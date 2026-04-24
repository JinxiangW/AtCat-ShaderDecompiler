using System.Text;

namespace Ruri.ShaderDecompiler.Spirv;

/// <summary>
/// Detailed binding information for a SPIR-V variable.
/// </summary>
public class SpirvBindingInfo
{
    public uint Id { get; set; }
    public int Set { get; set; }
    public int Binding { get; set; }
    public string? DescriptorType { get; set; }
    public uint? StructTypeId { get; set; }
    public int StructMemberCount { get; set; }
    public Dictionary<int, uint> MemberOffsets { get; set; } = new();
    public string? CurrentName { get; set; }
}

/// <summary>
/// Patches SPIR-V binaries to inject symbol names.
/// </summary>
public class SpirvPatcher
{
    /// <summary>
    /// Analyzes a SPIR-V binary and returns detailed binding information.
    /// </summary>
    public List<SpirvBindingInfo> AnalyzeBindingsDetailed(byte[] spirvBytes)
    {
        uint[] words = BytesToWords(spirvBytes);
        
        var idToSetBinding = new Dictionary<uint, (int? Set, int? Binding)>();
        var typePointerMap = new Dictionary<uint, (uint StorageClass, uint PointedType)>();
        var variableTypeMap = new Dictionary<uint, uint>();
        var structTypeIds = new HashSet<uint>();
        var structMemberCounts = new Dictionary<uint, int>();
        var structMemberOffsets = new Dictionary<uint, Dictionary<int, uint>>();
        var imageTypeIds = new HashSet<uint>();
        var samplerTypeIds = new HashSet<uint>();
        var sampledImageTypeIds = new HashSet<uint>();
        var idToName = new Dictionary<uint, string>();

        int offset = SpvOpCode.HeaderWordCount;
        while (offset < words.Length)
        {
            uint instrWord = words[offset];
            ushort opCode = SpvOpCode.GetOpCode(instrWord);
            ushort wordCount = SpvOpCode.GetWordCount(instrWord);
            if (wordCount == 0) break;

            switch (opCode)
            {
                case SpvOpCode.OpDecorate when wordCount >= 4:
                    {
                        uint targetId = words[offset + 1];
                        uint decoration = words[offset + 2];
                        if (decoration == SpvOpCode.DecorationDescriptorSet)
                        {
                            int set = (int)words[offset + 3];
                            if (!idToSetBinding.ContainsKey(targetId))
                                idToSetBinding[targetId] = (set, null);
                            else
                                idToSetBinding[targetId] = (set, idToSetBinding[targetId].Binding);
                        }
                        else if (decoration == SpvOpCode.DecorationBinding)
                        {
                            int binding = (int)words[offset + 3];
                            if (!idToSetBinding.ContainsKey(targetId))
                                idToSetBinding[targetId] = (null, binding);
                            else
                                idToSetBinding[targetId] = (idToSetBinding[targetId].Set, binding);
                        }
                        break;
                    }
                case SpvOpCode.OpMemberDecorate when wordCount >= 5:
                    {
                        uint structId = words[offset + 1];
                        int memberIndex = (int)words[offset + 2];
                        uint decoration = words[offset + 3];
                        if (decoration == SpvOpCode.DecorationOffset)
                        {
                            if (!structMemberOffsets.TryGetValue(structId, out Dictionary<int, uint>? memberOffsets))
                            {
                                memberOffsets = new Dictionary<int, uint>();
                                structMemberOffsets[structId] = memberOffsets;
                            }

                            memberOffsets[memberIndex] = words[offset + 4];
                        }

                        break;
                    }
                case SpvOpCode.OpTypePointer when wordCount >= 4:
                    {
                        uint resultId = words[offset + 1];
                        uint storageClass = words[offset + 2];
                        uint pointedTypeId = words[offset + 3];
                        typePointerMap[resultId] = (storageClass, pointedTypeId);
                        break;
                    }
                case SpvOpCode.OpTypeStruct:
                    {
                        uint structId = words[offset + 1];
                        structTypeIds.Add(structId);
                        structMemberCounts[structId] = wordCount >= 2 ? wordCount - 2 : 0;
                        break;
                    }
                case SpvOpCode.OpTypeImage:
                    imageTypeIds.Add(words[offset + 1]);
                    break;
                case SpvOpCode.OpTypeSampler:
                    samplerTypeIds.Add(words[offset + 1]);
                    break;
                case SpvOpCode.OpTypeSampledImage:
                    sampledImageTypeIds.Add(words[offset + 1]);
                    break;
                case SpvOpCode.OpName when wordCount >= 3:
                    {
                        uint targetId = words[offset + 1];
                        string? name = ReadLiteralString(words, offset + 2, wordCount - 2);
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            idToName[targetId] = name;
                        }

                        break;
                    }
                case SpvOpCode.OpVariable when wordCount >= 4:
                    {
                        uint pointerTypeId = words[offset + 1];
                        uint resultId = words[offset + 2];
                        variableTypeMap[resultId] = pointerTypeId;
                        break;
                    }
            }

            offset += wordCount;
        }

        var result = new List<SpirvBindingInfo>();
        
        foreach (var kvp in idToSetBinding)
        {
            if (!kvp.Value.Set.HasValue || !kvp.Value.Binding.HasValue)
                continue;

            var info = new SpirvBindingInfo
            {
                Id = kvp.Key,
                Set = kvp.Value.Set.Value,
                Binding = kvp.Value.Binding.Value
            };

            if (idToName.TryGetValue(kvp.Key, out string? currentName))
            {
                info.CurrentName = currentName;
            }

            // Determine type
            if (variableTypeMap.TryGetValue(kvp.Key, out uint ptrTypeId))
            {
                if (typePointerMap.TryGetValue(ptrTypeId, out var ptrInfo))
                {
                    uint pointedType = ptrInfo.PointedType;
                    
                    if (structTypeIds.Contains(pointedType))
                    {
                        info.DescriptorType = ptrInfo.StorageClass == 2 ? "UniformBuffer" : "StorageBuffer";
                        info.StructTypeId = pointedType;
                        info.StructMemberCount = structMemberCounts.TryGetValue(pointedType, out int memberCount) ? memberCount : 0;
                        if (structMemberOffsets.TryGetValue(pointedType, out Dictionary<int, uint>? offsets))
                        {
                            info.MemberOffsets = offsets;
                        }
                    }
                    else if (samplerTypeIds.Contains(pointedType))
                        info.DescriptorType = "Sampler";
                    // IMPORTANT:
                    // dxil-spirv frequently emits texture resources as plain OpTypeImage instead of
                    // the shape we see from direct dxc -spirv. Treat both forms as sampled images for
                    // symbol restoration, otherwise DXIL routes keep machine names like _8 / _14.
                    else if (sampledImageTypeIds.Contains(pointedType) || imageTypeIds.Contains(pointedType))
                        info.DescriptorType = "SampledImage";
                    else
                        info.DescriptorType = "Unknown";
                }
            }

            result.Add(info);
        }

        return result.OrderBy(x => x.Set).ThenBy(x => x.Binding).ToList();
    }

    /// <summary>
    /// Patches SPIR-V names using binding analysis plus typed metadata.
    /// </summary>
    public byte[] PatchByIds(byte[] spirvBytes, List<(uint Id, string Name)> names, List<(uint TypeId, uint MemberIndex, string Name)>? memberNames = null)
    {
        if (spirvBytes.Length < SpvOpCode.HeaderWordCount * 4)
            throw new ArgumentException("Invalid SPIR-V binary");

        uint[] words = BytesToWords(spirvBytes);
        if (words[0] != SpvOpCode.MagicNumber)
            throw new ArgumentException("Invalid SPIR-V magic");

        // IMPORTANT:
        // We replace existing OpName / OpMemberName entries instead of appending duplicates.
        // dxil-spirv often already contains machine debug names; if they are left in place,
        // spirv-cross may keep preferring them and the HLSL output regresses even though the
        // metadata names were injected later.
        var replacedIds = names.Select(x => x.Id).ToHashSet();
        var replacedMembers = memberNames?
            .Select(x => (x.TypeId, x.MemberIndex))
            .ToHashSet() ?? new HashSet<(uint TypeId, uint MemberIndex)>();

        var filteredWords = new List<uint>(words.Length);
        for (int i = 0; i < SpvOpCode.HeaderWordCount; i++)
        {
            filteredWords.Add(words[i]);
        }

        int offset = SpvOpCode.HeaderWordCount;
        while (offset < words.Length)
        {
            uint instrWord = words[offset];
            ushort opCode = SpvOpCode.GetOpCode(instrWord);
            ushort wordCount = SpvOpCode.GetWordCount(instrWord);
            if (wordCount == 0)
            {
                break;
            }

            bool skip = false;
            if (opCode == SpvOpCode.OpName && wordCount >= 2)
            {
                skip = replacedIds.Contains(words[offset + 1]);
            }
            else if (opCode == SpvOpCode.OpMemberName && wordCount >= 3)
            {
                skip = replacedMembers.Contains((words[offset + 1], words[offset + 2]));
            }

            if (!skip)
            {
                for (int i = 0; i < wordCount; i++)
                {
                    filteredWords.Add(words[offset + i]);
                }
            }

            offset += wordCount;
        }

        words = filteredWords.ToArray();

        var instructions = new List<uint[]>();
        foreach (var (id, name) in names)
        {
            instructions.Add(CreateOpName(id, name));
        }

        if (memberNames != null)
        {
            foreach (var (typeId, memberIndex, name) in memberNames)
            {
                instructions.Add(CreateOpMemberName(typeId, memberIndex, name));
            }
        }

        if (instructions.Count == 0)
            return spirvBytes;

        int insertOffset = FindDebugInsertionPoint(words);
        int additionalWords = instructions.Sum(arr => arr.Length);
        uint[] newWords = new uint[words.Length + additionalWords];

        Array.Copy(words, 0, newWords, 0, insertOffset);

        int writeOffset = insertOffset;
        foreach (var instr in instructions)
        {
            Array.Copy(instr, 0, newWords, writeOffset, instr.Length);
            writeOffset += instr.Length;
        }

        Array.Copy(words, insertOffset, newWords, writeOffset, words.Length - insertOffset);

        return WordsToBytes(newWords);
    }

    /// <summary>
    /// Legacy compatibility entry point. The current pipeline uses AnalyzeBindingsDetailed()
    /// plus PatchByIds(); typed metadata no longer carries direct SPIR-V ids.
    /// </summary>
    public byte[] Patch(byte[] spirvBytes, ShaderSymbolData symbols)
    {
        var memberNames = new List<(uint TypeId, uint MemberIndex, string Name)>();
        var detailed = AnalyzeBindingsDetailed(spirvBytes);
        foreach (BufferBinding resource in symbols.ConstantBufferBindings)
        {
            ConstantBuffer? constantBuffer = symbols.ConstantBuffers.FirstOrDefault(cb => string.Equals(cb.Name, resource.Name, StringComparison.Ordinal));
            if (constantBuffer == null)
            {
                continue;
            }

            var match = detailed.FirstOrDefault(b => b.Set == resource.Set && b.Binding == resource.Index && b.StructTypeId.HasValue);
            if (match?.StructTypeId == null)
            {
                continue;
            }

            foreach (StructParameter structParameter in constantBuffer.StructParams.Where(s => !string.IsNullOrWhiteSpace(s.Name)))
            {
                int? structTargetIndex = null;

                if (structParameter.Index >= 0 && match.MemberOffsets.Count > 0)
                {
                    foreach (var offsetKvp in match.MemberOffsets)
                    {
                        if (offsetKvp.Value == (uint)structParameter.Index)
                        {
                            structTargetIndex = offsetKvp.Key;
                            break;
                        }
                    }
                }

                if (structTargetIndex.HasValue)
                {
                    memberNames.Add((match.StructTypeId.Value, (uint)structTargetIndex.Value, structParameter.Name));
                }
            }

            foreach (var parameter in constantBuffer.CBParams.Where(p => !string.IsNullOrWhiteSpace(p.ParamName)))
            {
                int? targetIndex = null;

                if (parameter.Index >= 0 && match.MemberOffsets.Count > 0)
                {
                    foreach (var offsetKvp in match.MemberOffsets)
                    {
                        if (offsetKvp.Value == (uint)parameter.Index)
                        {
                            targetIndex = offsetKvp.Key;
                            break;
                        }
                    }
                }

				if (targetIndex.HasValue)
				{
					memberNames.Add((match.StructTypeId.Value, (uint)targetIndex.Value, parameter.ParamName));
				}
            }
        }
        
        if (memberNames.Count > 0)
            return PatchByIds(spirvBytes, [], memberNames);

        return spirvBytes;
    }

    public Dictionary<(int Set, int Binding), uint> AnalyzeBindings(byte[] spirvBytes)
    {
        var detailed = AnalyzeBindingsDetailed(spirvBytes);
        var result = new Dictionary<(int Set, int Binding), uint>();
        foreach (var b in detailed)
        {
            var key = (b.Set, b.Binding);
            if (!result.ContainsKey(key))
                result[key] = b.Id;
        }
        return result;
    }

    private int FindDebugInsertionPoint(uint[] words)
    {
        int offset = SpvOpCode.HeaderWordCount;
        int lastDebugEnd = SpvOpCode.HeaderWordCount;

        while (offset < words.Length)
        {
            uint instrWord = words[offset];
            ushort opCode = SpvOpCode.GetOpCode(instrWord);
            ushort wordCount = SpvOpCode.GetWordCount(instrWord);
            if (wordCount == 0) break;

            if (opCode >= 3 && opCode <= 10)
                lastDebugEnd = offset + wordCount;
            else if (opCode == SpvOpCode.OpDecorate || opCode >= SpvOpCode.OpTypeVoid)
                break;

            offset += wordCount;
        }

        return lastDebugEnd;
    }

    private uint[] CreateOpName(uint id, string name)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        int paddedLength = (nameBytes.Length + 1 + 3) / 4 * 4;
        byte[] paddedName = new byte[paddedLength];
        Array.Copy(nameBytes, paddedName, nameBytes.Length);

        int wordCount = 2 + paddedLength / 4;
        uint[] instr = new uint[wordCount];

        instr[0] = SpvOpCode.MakeInstructionWord(SpvOpCode.OpName, (ushort)wordCount);
        instr[1] = id;

        for (int i = 0; i < paddedLength / 4; i++)
            instr[2 + i] = BitConverter.ToUInt32(paddedName, i * 4);

        return instr;
    }

    private uint[] CreateOpMemberName(uint typeId, uint memberIndex, string name)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        int paddedLength = (nameBytes.Length + 1 + 3) / 4 * 4;
        byte[] paddedName = new byte[paddedLength];
        Array.Copy(nameBytes, paddedName, nameBytes.Length);

        int wordCount = 3 + paddedLength / 4;
        uint[] instr = new uint[wordCount];

        instr[0] = SpvOpCode.MakeInstructionWord(SpvOpCode.OpMemberName, (ushort)wordCount);
        instr[1] = typeId;
        instr[2] = memberIndex;

        for (int i = 0; i < paddedLength / 4; i++)
            instr[3 + i] = BitConverter.ToUInt32(paddedName, i * 4);

        return instr;
    }

    private static uint[] BytesToWords(byte[] bytes)
    {
        uint[] words = new uint[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, words, 0, bytes.Length);
        return words;
    }

    private static byte[] WordsToBytes(uint[] words)
    {
        byte[] bytes = new byte[words.Length * 4];
        Buffer.BlockCopy(words, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static string? ReadLiteralString(uint[] words, int start, int wordCount)
    {
        byte[] bytes = new byte[wordCount * 4];
        Buffer.BlockCopy(words, start * 4, bytes, 0, bytes.Length);
        int nullIndex = Array.IndexOf(bytes, (byte)0);
        if (nullIndex < 0)
        {
            nullIndex = bytes.Length;
        }

        return Encoding.UTF8.GetString(bytes, 0, nullIndex);
    }
}
