namespace Ruri.ShaderTools;

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Normalizes DXBC-derived HLSL for D3D11 recompilation, including duplicate
/// SPIR-V views, input semantics, immediate constant bit views, and structured
/// SRV declarations whose view type must match the original bytecode.
/// </summary>
internal static partial class DxbcHlslNormalizer
{
    public static string Normalize(string hlsl, ReadOnlySpan<byte> dxbc)
    {
        hlsl = PreserveImmediateConstantBitcasts(hlsl);
        hlsl = InputStructRegex().Replace(hlsl, NormalizePixelInputStruct);

        var cbuffers = new Dictionary<string, CbufferLayout>(StringComparer.Ordinal);
        string normalized = CbufferRegex().Replace(hlsl, match =>
        {
            if (!TryParseCbuffer(match, out string key, out List<string> members))
            {
                return match.Value;
            }

            if (!cbuffers.TryGetValue(key, out CbufferLayout? canonical))
            {
                cbuffers.Add(key, new CbufferLayout(members));
                return match.Value;
            }

            var aliases = new StringBuilder();
            for (int i = 0; i < members.Count; i++)
            {
                if (!string.Equals(members[i], canonical.Members[i], StringComparison.Ordinal))
                {
                    aliases.Append("#define ").Append(members[i]).Append(' ')
                        .Append(canonical.Members[i]).Append('\n');
                }
            }
            return aliases.ToString();
        });

        var resources = new Dictionary<string, string>(StringComparer.Ordinal);
        normalized = ResourceRegex().Replace(normalized, match =>
        {
            string key = $"{match.Groups["type"].Value}|{match.Groups["register"].Value}|{match.Groups["space"].Value.Replace(" ", string.Empty)}";
            string name = match.Groups["name"].Value;
            if (!resources.TryGetValue(key, out string? canonical))
            {
                resources.Add(key, name);
                return match.Value;
            }

            return string.Equals(name, canonical, StringComparison.Ordinal)
                ? string.Empty
                : $"#define {name} {canonical}";
        });

        return PreserveStructuredSrvDeclarations(normalized, dxbc);
    }

    private static string PreserveStructuredSrvDeclarations(string hlsl, ReadOnlySpan<byte> dxbc)
    {
        Dictionary<(int Slot, int Space), int> structuredSrvs = ReadStructuredSrvStrides(dxbc);
        foreach (((int slot, int space), int stride) in structuredSrvs)
        {
            string spacePattern = space == 0 ? @"(?:\s*,\s*space0)?" : $@"\s*,\s*space{space}";
            Match declaration = Regex.Match(
                hlsl,
                $@"^ByteAddressBuffer\s+(?<name>[A-Za-z_]\w*)\s*:\s*register\(t{slot}{spacePattern}\);$",
                RegexOptions.Multiline);
            if (!declaration.Success || stride <= 0 || (stride & 3) != 0)
            {
                continue;
            }

            string resourceName = declaration.Groups["name"].Value;
            var viewNames = new HashSet<string>(StringComparer.Ordinal) { resourceName };
            foreach (Match alias in ResourceAliasRegex().Matches(hlsl))
            {
                if (string.Equals(alias.Groups["target"].Value, resourceName, StringComparison.Ordinal))
                {
                    viewNames.Add(alias.Groups["alias"].Value);
                }
            }

            string helperName = $"RuriDxbcLoadT{slot}S{space}";
            foreach (string viewName in viewNames)
            {
                for (int width = 4; width >= 2; width--)
                {
                    hlsl = Regex.Replace(
                        hlsl,
                        $@"\b{Regex.Escape(viewName)}\s*\.\s*Load{width}\s*\(",
                        helperName + "x" + width + "(");
                }
                hlsl = Regex.Replace(
                    hlsl,
                    $@"\b{Regex.Escape(viewName)}\s*\.\s*Load\s*\(",
                    helperName + "(");
            }

            int wordCount = stride / sizeof(uint);
            string declarationText;
            string loadFunction;
            if (wordCount <= 4)
            {
                string elementType = wordCount == 1 ? "uint" : $"uint{wordCount}";
                string elementAccess = wordCount == 1
                    ? $"{resourceName}[byteOffset / {stride}u]"
                    : $"{resourceName}[byteOffset / {stride}u][(byteOffset % {stride}u) >> 2u]";
                string spaceSuffix = space == 0 ? string.Empty : $", space{space}";
                declarationText = $"StructuredBuffer<{elementType}> {resourceName} : register(t{slot}{spaceSuffix});";
                loadFunction = $"uint {helperName}(uint byteOffset) {{ return {elementAccess}; }}";
            }
            else
            {
                string elementType = $"RuriDxbcStructuredT{slot}S{space}Element";
                var fields = new StringBuilder();
                var cases = new StringBuilder();
                for (int word = 0; word < wordCount; word++)
                {
                    fields.Append("    uint word").Append(word).Append(";\n");
                    cases.Append(" case ").Append(word).Append(": value = ")
                        .Append(resourceName).Append("[elementIndex].word").Append(word).Append("; break;");
                }

                declarationText = $"struct {elementType}\n{{\n{fields}}};\n"
                    + $"StructuredBuffer<{elementType}> {resourceName} : register(t{slot}{(space == 0 ? string.Empty : $", space{space}")});";
                loadFunction = $"uint {helperName}(uint byteOffset) {{ uint elementIndex = byteOffset / {stride}u; uint value = 0u;"
                    + $" switch ((byteOffset % {stride}u) >> 2u) {{{cases} }} return value; }}";
            }

            string helpers = declarationText + "\n"
                + loadFunction + "\n"
                + $"uint2 {helperName}x2(uint byteOffset) {{ return uint2({helperName}(byteOffset), {helperName}(byteOffset + 4u)); }}\n"
                + $"uint3 {helperName}x3(uint byteOffset) {{ return uint3({helperName}(byteOffset), {helperName}(byteOffset + 4u), {helperName}(byteOffset + 8u)); }}\n"
                + $"uint4 {helperName}x4(uint byteOffset) {{ return uint4({helperName}(byteOffset), {helperName}(byteOffset + 4u), {helperName}(byteOffset + 8u), {helperName}(byteOffset + 12u)); }}";

            hlsl = hlsl[..declaration.Index] + helpers + hlsl[(declaration.Index + declaration.Length)..];
        }

        return hlsl;
    }

    private static Dictionary<(int Slot, int Space), int> ReadStructuredSrvStrides(ReadOnlySpan<byte> dxbc)
    {
        var result = new Dictionary<(int Slot, int Space), int>();
        if (dxbc.Length < 32 || !dxbc[..4].SequenceEqual("DXBC"u8))
        {
            return result;
        }

        uint chunkCount = BinaryPrimitives.ReadUInt32LittleEndian(dxbc[28..]);
        if (chunkCount > (uint)((dxbc.Length - 32) / sizeof(uint)))
        {
            return result;
        }

        int chunks = (int)chunkCount;
        for (int chunkIndex = 0; chunkIndex < chunks; chunkIndex++)
        {
            int tableOffset = 32 + (chunkIndex * sizeof(uint));
            uint rawChunkOffset = BinaryPrimitives.ReadUInt32LittleEndian(dxbc[tableOffset..]);
            if (rawChunkOffset > int.MaxValue)
            {
                continue;
            }

            int chunkOffset = (int)rawChunkOffset;
            if (chunkOffset < 0 || chunkOffset > dxbc.Length - 8)
            {
                continue;
            }

            ReadOnlySpan<byte> fourCc = dxbc.Slice(chunkOffset, 4);
            if (!fourCc.SequenceEqual("SHDR"u8) && !fourCc.SequenceEqual("SHEX"u8))
            {
                continue;
            }

            uint rawChunkSize = BinaryPrimitives.ReadUInt32LittleEndian(dxbc[(chunkOffset + 4)..]);
            if (rawChunkSize > int.MaxValue || (rawChunkSize & 3) != 0)
            {
                continue;
            }

            int chunkSize = (int)rawChunkSize;
            int dataOffset = chunkOffset + 8;
            if (chunkSize < 8 || dataOffset > dxbc.Length - chunkSize)
            {
                continue;
            }

            ReadOnlySpan<byte> program = dxbc.Slice(dataOffset, chunkSize);
            int wordCount = chunkSize / sizeof(uint);
            int declaredWords = (int)Math.Min(
                BinaryPrimitives.ReadUInt32LittleEndian(program[4..]),
                (uint)wordCount);
            int shaderVersion = (int)(BinaryPrimitives.ReadUInt32LittleEndian(program) & 0xffu);

            for (int word = 2; word < declaredWords;)
            {
                uint opcodeToken = ReadWord(program, word);
                int opcode = (int)(opcodeToken & 0x7ffu);
                int instructionWords = (int)((opcodeToken >> 24) & 0x7fu);
                if (opcode == 53 && word + 1 < declaredWords)
                {
                    instructionWords = (int)ReadWord(program, word + 1);
                }
                if (instructionWords <= 0 || instructionWords > declaredWords - word)
                {
                    break;
                }

                // D3D11_SB_OPCODE_DCL_RESOURCE_STRUCTURED. SM5.0 uses one
                // immediate register index; SM5.1 adds identifier/range indices
                // and a register-space token.
                if (opcode == 162)
                {
                    bool shaderModel51 = shaderVersion == 0x51;
                    int slotWord = word + (shaderModel51 ? 3 : 2);
                    int strideWord = word + (shaderModel51 ? 5 : 3);
                    if (strideWord < word + instructionWords)
                    {
                        int slot = (int)ReadWord(program, slotWord);
                        int stride = (int)ReadWord(program, strideWord);
                        int space = shaderModel51 && strideWord + 1 < word + instructionWords
                            ? (int)ReadWord(program, strideWord + 1)
                            : 0;
                        if (slot >= 0 && stride > 0)
                        {
                            result[(slot, space)] = stride;
                        }
                    }
                }

                word += instructionWords;
            }
        }

        return result;
    }

    private static uint ReadWord(ReadOnlySpan<byte> program, int word)
        => BinaryPrimitives.ReadUInt32LittleEndian(program[(word * sizeof(uint))..]);

    private static string PreserveImmediateConstantBitcasts(string hlsl)
    {
        var arrays = new List<(string Name, string BitsName)>();
        string normalized = ImmediateConstantArrayRegex().Replace(hlsl, match =>
        {
            string name = match.Groups["name"].Value;
            string access = $@"(?:asuint|asint)\(\s*{Regex.Escape(name)}\[";
            if (!Regex.IsMatch(hlsl, access))
            {
                return match.Value;
            }

            string bitsName = name + "_bits";
            while (Regex.IsMatch(hlsl, $@"\b{Regex.Escape(bitsName)}\b"))
            {
                bitsName += "_";
            }

            if (!TryBuildBitArray(match, bitsName, out string bitArray))
            {
                return match.Value;
            }

            arrays.Add((name, bitsName));
            return match.Value + "\n" + bitArray;
        });

        foreach ((string name, string bitsName) in arrays)
        {
            string access = $@"{Regex.Escape(name)}\[(?<index>[^\]]+)\](?<swizzle>\.[xyzw]{{1,4}})?";
            normalized = Regex.Replace(
                normalized,
                $@"asuint\(\s*{access}\s*\)",
                match => bitsName + "[" + match.Groups["index"].Value + "]" + match.Groups["swizzle"].Value);
            normalized = Regex.Replace(
                normalized,
                $@"asint\(\s*{access}\s*\)",
                match => "asint(" + bitsName + "[" + match.Groups["index"].Value + "]" + match.Groups["swizzle"].Value + ")");
        }

        return normalized;
    }

    private static bool TryBuildBitArray(Match match, string bitsName, out string bitArray)
    {
        bitArray = string.Empty;
        List<string> items = SplitTopLevel(match.Groups["initializer"].Value);
        if (items.Count != int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture))
        {
            return false;
        }

        var vectors = new List<string>(items.Count);
        foreach (string item in items)
        {
            List<string> components;
            string trimmed = item.Trim();
            if (trimmed.StartsWith("float4(", StringComparison.Ordinal) && trimmed.EndsWith(')'))
            {
                components = SplitTopLevel(trimmed[7..^1]);
            }
            else if (trimmed.EndsWith(".xxxx", StringComparison.Ordinal))
            {
                string scalar = trimmed[..^5];
                components = [scalar, scalar, scalar, scalar];
            }
            else
            {
                return false;
            }

            if (components.Count != 4)
            {
                return false;
            }

            var bits = new uint[4];
            for (int i = 0; i < components.Count; i++)
            {
                string literal = components[i].Trim().TrimEnd('f', 'F');
                if (!float.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
                {
                    return false;
                }
                bits[i] = BitConverter.SingleToUInt32Bits(value);
            }

            vectors.Add($"uint4(0x{bits[0]:x8}u, 0x{bits[1]:x8}u, 0x{bits[2]:x8}u, 0x{bits[3]:x8}u)");
        }

        string count = match.Groups["count"].Value;
        bitArray = $"static const uint4 {bitsName}[{count}] = {{ {string.Join(", ", vectors)} }};";
        return true;
    }

    private static List<string> SplitTopLevel(string text)
    {
        var result = new List<string>();
        int depth = 0;
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    result.Add(text[start..i]);
                    start = i + 1;
                    break;
            }
        }

        result.Add(text[start..]);
        return result;
    }

    private static string NormalizePixelInputStruct(Match match)
    {
        string body = match.Groups["body"].Value;
        string[] lines = body.Split('\n');
        int positionIndex = Array.FindIndex(lines, static line => line.Contains(": SV_Position;", StringComparison.Ordinal));
        if (positionIndex < 0)
        {
            return match.Value;
        }

        var texcoordLines = new Dictionary<int, string>();
        foreach (string line in lines)
        {
            Match field = TexcoordFieldRegex().Match(line);
            if (!field.Success)
            {
                continue;
            }

            int expectedIndex = field.Groups["nameIndex"].Success
                ? int.Parse(field.Groups["nameIndex"].Value, CultureInfo.InvariantCulture)
                : 0;
            int emittedIndex = int.Parse(field.Groups["semanticIndex"].Value, CultureInfo.InvariantCulture);
            if (emittedIndex != expectedIndex + 1 || !texcoordLines.TryAdd(expectedIndex, line))
            {
                return match.Value;
            }
        }

        if (texcoordLines.Count == 0)
        {
            return match.Value;
        }

        string position = lines[positionIndex];
        var normalized = new List<string>(lines.Length) { position };
        for (int i = 0; i < lines.Length; i++)
        {
            if (i == positionIndex)
            {
                continue;
            }

            normalized.Add(TexcoordFieldRegex().Replace(lines[i], field =>
            {
                int index = field.Groups["nameIndex"].Success
                    ? int.Parse(field.Groups["nameIndex"].Value, CultureInfo.InvariantCulture)
                    : 0;
                return field.Groups["prefix"].Value + $"TEXCOORD{index};";
            }));
        }

        int bodyOffset = match.Groups["body"].Index - match.Index;
        return match.Value[..bodyOffset]
            + string.Join('\n', normalized)
            + match.Value[(bodyOffset + match.Groups["body"].Length)..];
    }

    private static bool TryParseCbuffer(
        Match match,
        out string key,
        out List<string> members)
    {
        key = string.Empty;
        members = new List<string>();
        var signatures = new List<string>();

        foreach (string line in match.Groups["body"].Value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            Match member = MemberRegex().Match(line);
            if (!member.Success)
            {
                return false;
            }

            members.Add(member.Groups["name"].Value);
            signatures.Add($"{member.Groups["type"].Value.Trim()}|{member.Groups["array"].Value}|{member.Groups["pack"].Value}");
        }

        if (members.Count == 0)
        {
            return false;
        }

        key = $"b{match.Groups["slot"].Value}|{string.Join(";", signatures)}";
        return true;
    }

    private sealed record CbufferLayout(List<string> Members);

    [GeneratedRegex(
        @"^static const float4 (?<name>[A-Za-z_]\w*)\[(?<count>\d+)\] = \{ (?<initializer>.*) \};$",
        RegexOptions.Multiline)]
    private static partial Regex ImmediateConstantArrayRegex();

    [GeneratedRegex(
        @"^struct\s+SPIRV_Cross_Input\s*\r?\n\{\r?\n(?<body>.*?)^\};",
        RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex InputStructRegex();

    [GeneratedRegex(
        @"^(?<prefix>\s*.+?\s+TEXCOORD(?:_(?<nameIndex>\d+))?\s*:\s*)TEXCOORD(?<semanticIndex>\d+);\s*$")]
    private static partial Regex TexcoordFieldRegex();

    [GeneratedRegex(
        @"^cbuffer\s+[A-Za-z_]\w*\s*:\s*register\(b(?<slot>\d+)\)\s*\r?\n\{\r?\n(?<body>.*?)^\};[ \t]*(?:\r?\n)?",
        RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex CbufferRegex();

    [GeneratedRegex(
        @"^\s*(?<type>.+?)\s+(?<name>[A-Za-z_]\w*)(?<array>\[[^\]]+\])?\s*:\s*packoffset\((?<pack>[^)]+)\);\s*$")]
    private static partial Regex MemberRegex();

    [GeneratedRegex(
        @"^(?<type>[A-Za-z_]\w*(?:<[^>]+>)?)\s+(?<name>[A-Za-z_]\w*)\s*:\s*register\((?<register>[tsu]\d+)(?<space>\s*,\s*space\d+)?\);$",
        RegexOptions.Multiline)]
    private static partial Regex ResourceRegex();

    [GeneratedRegex(
        @"^#define\s+(?<alias>[A-Za-z_]\w*)\s+(?<target>[A-Za-z_]\w*)\s*$",
        RegexOptions.Multiline)]
    private static partial Regex ResourceAliasRegex();
}
