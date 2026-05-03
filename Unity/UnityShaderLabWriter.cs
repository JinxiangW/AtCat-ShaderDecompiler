using System.Text;

namespace Ruri.ShaderTools;

public static class UnityShaderLabWriter
{
    public static string Write(UnityShaderMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        IndentedStringBuilder sb = new();
        sb.AppendLine($"Shader \"{metadata.m_Name}\" {{");
        sb.Indent();

        if (metadata.m_ParsedForm.m_PropInfo.m_Props.Count > 0)
        {
            sb.AppendLine("Properties {");
            sb.Indent();
            foreach (UnitySerializedProperty property in metadata.m_ParsedForm.m_PropInfo.m_Props)
            {
                string declaration = BuildPropertyDeclaration(property);
                if (!string.IsNullOrWhiteSpace(declaration))
                {
                    sb.AppendLine(declaration);
                }
            }
            sb.Unindent();
            sb.AppendLine("}");
        }

        foreach (UnitySerializedSubShader subShader in metadata.m_ParsedForm.m_SubShaders)
        {
            sb.AppendLine("SubShader {");
            sb.Indent();
            WriteTags(sb, subShader.m_Tags.tags);
            if (subShader.m_LOD != 0)
            {
                sb.AppendLine($"LOD {subShader.m_LOD}");
            }

            foreach (UnitySerializedPass pass in subShader.m_Passes)
            {
                if (!string.IsNullOrWhiteSpace(pass.m_UseName))
                {
                    sb.AppendLine($"UsePass \"{pass.m_UseName}\"");
                    continue;
                }

                sb.AppendLine("Pass {");
                sb.Indent();

                if (!string.IsNullOrWhiteSpace(pass.m_State.m_Name))
                {
                    sb.AppendLine($"Name \"{pass.m_State.m_Name}\"");
                }
                if (pass.m_State.m_LOD != 0)
                {
                    sb.AppendLine($"LOD {pass.m_State.m_LOD}");
                }
                foreach (string command in BuildStateCommands(pass.m_State))
                {
                    if (!string.IsNullOrWhiteSpace(command))
                    {
                        sb.AppendLine(command);
                    }
                }
                WriteTags(sb, pass.m_State.m_Tags.tags);

                if (pass.Programs.Count > 0)
                {
                    WriteProgramsBlock(sb, metadata.m_ParsedForm.m_KeywordNames, pass.Programs);
                }

                sb.Unindent();
                sb.AppendLine("}");
            }

            sb.Unindent();
            sb.AppendLine("}");
        }

        if (!string.IsNullOrWhiteSpace(metadata.m_FallbackName))
        {
            sb.AppendLine($"Fallback \"{metadata.m_FallbackName}\"");
        }

        sb.Unindent();
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string BuildPropertyDeclaration(UnitySerializedProperty property)
    {
        StringBuilder builder = new();
        foreach (string attribute in property.m_Attributes)
        {
            builder.Append('[').Append(attribute).Append("] ");
        }

        uint flags = property.m_Flags;
        if ((flags & 1u) != 0) builder.Append("[HideInInspector] ");
        if ((flags & 2u) != 0) builder.Append("[PerRendererData] ");
        if ((flags & 4u) != 0) builder.Append("[NoScaleOffset] ");
        if ((flags & 8u) != 0) builder.Append("[Normal] ");
        if ((flags & 0x10u) != 0) builder.Append("[HDR] ");
        if ((flags & 0x20u) != 0) builder.Append("[Gamma] ");

        string typeName = property.m_Type switch
        {
            0 => "Color",
            1 => "Vector",
            2 => "Float",
            3 => $"Range({FormatFloat(property.m_DefValue[1])}, {FormatFloat(property.m_DefValue[2])})",
            4 => property.m_DefTexture.m_TexDim switch
            {
                1 => "any",
                2 => "2D",
                3 => "3D",
                4 => "Cube",
                5 => "2DArray",
                6 => "CubeArray",
                _ => "2D",
            },
            5 => "Int",
            _ => "Float",
        };

        string value = property.m_Type switch
        {
            0 or 1 => $"({FormatFloat(property.m_DefValue[0])}, {FormatFloat(property.m_DefValue[1])}, {FormatFloat(property.m_DefValue[2])}, {FormatFloat(property.m_DefValue[3])})",
            2 or 3 or 5 => FormatFloat(property.m_DefValue[0]),
            4 => $"\"{property.m_DefTexture.m_DefaultName}\" {{}}",
            _ => FormatFloat(property.m_DefValue[0]),
        };

        builder.Append($"{property.m_Name} (\"{property.m_Description}\", {typeName}) = {value}");
        return builder.ToString();
    }

    private static IEnumerable<string> BuildStateCommands(UnitySerializedShaderState state)
    {
        if (state.rtSeparateBlend)
        {
            foreach (string command in BuildRtBlendCommands(state.rtBlend0, 0)) yield return command;
            foreach (string command in BuildRtBlendCommands(state.rtBlend1, 1)) yield return command;
            foreach (string command in BuildRtBlendCommands(state.rtBlend2, 2)) yield return command;
            foreach (string command in BuildRtBlendCommands(state.rtBlend3, 3)) yield return command;
            foreach (string command in BuildRtBlendCommands(state.rtBlend4, 4)) yield return command;
            foreach (string command in BuildRtBlendCommands(state.rtBlend5, 5)) yield return command;
            foreach (string command in BuildRtBlendCommands(state.rtBlend6, 6)) yield return command;
            foreach (string command in BuildRtBlendCommands(state.rtBlend7, 7)) yield return command;
        }
        else
        {
            foreach (string command in BuildRtBlendCommands(state.rtBlend0, -1)) yield return command;
        }

        if (state.alphaToMask.val > 0f || HasName(state.alphaToMask))
        {
            yield return HasName(state.alphaToMask) ? $"AlphaToMask [{state.alphaToMask.name}]" : "AlphaToMask On";
        }
        if ((int)state.zClip.val == 1 || HasName(state.zClip)) yield return $"ZClip {FormatNamedOrEnum(state.zClip, FormatZClip(state.zClip.val))}";
        if (((int)state.zTest.val != 0 && (int)state.zTest.val != 4) || HasName(state.zTest)) yield return $"ZTest {FormatNamedOrEnum(state.zTest, FormatZTest(state.zTest.val))}";
        if ((int)state.zWrite.val != 1 || HasName(state.zWrite)) yield return $"ZWrite {FormatNamedOrEnum(state.zWrite, FormatZWrite(state.zWrite.val))}";
        if ((int)state.culling.val != 2 || HasName(state.culling)) yield return $"Cull {FormatNamedOrEnum(state.culling, FormatCullMode(state.culling.val))}";
        if (state.offsetFactor.val != 0f || state.offsetUnits.val != 0f || HasName(state.offsetFactor) || HasName(state.offsetUnits)) yield return $"Offset {FormatNamedOrDecimal(state.offsetFactor)}, {FormatNamedOrDecimal(state.offsetUnits)}";

        foreach (string command in BuildStencilCommands(state)) yield return command;
        foreach (string command in BuildFogCommands(state)) yield return command;

        if (state.lighting)
        {
            yield return "Lighting On";
        }
    }

    private static IEnumerable<string> BuildRtBlendCommands(UnitySerializedShaderRTBlendState state, int index)
    {
        bool hasBlendName = HasName(state.srcBlend) || HasName(state.destBlend) || HasName(state.srcBlendAlpha) || HasName(state.destBlendAlpha);
        bool hasBlendOpName = HasName(state.blendOp) || HasName(state.blendOpAlpha);
        bool hasColMaskName = HasName(state.colMask);

        if ((int)state.srcBlend.val != 1 || (int)state.destBlend.val != 0 || (int)state.srcBlendAlpha.val != 1 || (int)state.destBlendAlpha.val != 0 || hasBlendName)
        {
            string command = index >= 0 ? $"Blend {index} " : "Blend ";
            command += $"{FormatNamedOrEnum(state.srcBlend, FormatBlendMode(state.srcBlend.val))} {FormatNamedOrEnum(state.destBlend, FormatBlendMode(state.destBlend.val))}";
            string alphaPart = (int)state.srcBlendAlpha.val != 1 || (int)state.destBlendAlpha.val != 0 || HasName(state.srcBlendAlpha) || HasName(state.destBlendAlpha)
                ? $", {FormatNamedOrEnum(state.srcBlendAlpha, FormatBlendMode(state.srcBlendAlpha.val))} {FormatNamedOrEnum(state.destBlendAlpha, FormatBlendMode(state.destBlendAlpha.val))}"
                : string.Empty;
            yield return command + alphaPart;
        }

        if ((int)state.blendOp.val != 0 || (int)state.blendOpAlpha.val != 0 || hasBlendOpName)
        {
            string command = index >= 0 ? $"BlendOp {index} " : "BlendOp ";
            command += FormatNamedOrEnum(state.blendOp, FormatBlendOp(state.blendOp.val));
            if ((int)state.blendOpAlpha.val != 0 || HasName(state.blendOpAlpha))
            {
                command += $", {FormatNamedOrEnum(state.blendOpAlpha, FormatBlendOp(state.blendOpAlpha.val))}";
            }
            yield return command;
        }

        if ((int)state.colMask.val != 15 || hasColMaskName)
        {
            string mask = hasColMaskName ? $"[{state.colMask.name}]" : ((int)state.colMask.val) == 0 ? "0" : BuildColorMask((int)state.colMask.val);
            yield return index >= 0 ? $"ColorMask {mask} {index}" : $"ColorMask {mask}";
        }
    }

    private static IEnumerable<string> BuildStencilCommands(UnitySerializedShaderState state)
    {
        bool hasNames = HasName(state.stencilRef) || HasName(state.stencilReadMask) || HasName(state.stencilWriteMask)
            || HasStencilNames(state.stencilOp) || HasStencilNames(state.stencilOpFront) || HasStencilNames(state.stencilOpBack);

        bool hasValues = state.stencilRef.val != 0f || state.stencilReadMask.val != 255f || state.stencilWriteMask.val != 255f
            || !IsDefaultStencilBlock(state.stencilOp, allowDisabledComp: false)
            || !IsDefaultStencilBlock(state.stencilOpFront, allowDisabledComp: false)
            || !IsDefaultStencilBlock(state.stencilOpBack, allowDisabledComp: false);

        if (!hasValues && !hasNames)
        {
            yield break;
        }

        yield return "Stencil {";
        if (state.stencilRef.val != 0f || HasName(state.stencilRef)) yield return $"    Ref {FormatNamedOrInt(state.stencilRef)}";
        if (state.stencilReadMask.val != 255f || HasName(state.stencilReadMask)) yield return $"    ReadMask {FormatNamedOrInt(state.stencilReadMask)}";
        if (state.stencilWriteMask.val != 255f || HasName(state.stencilWriteMask)) yield return $"    WriteMask {FormatNamedOrInt(state.stencilWriteMask)}";
        if (!IsDefaultStencilBlock(state.stencilOp, allowDisabledComp: true) || HasStencilNames(state.stencilOp))
        {
            yield return $"    Comp {FormatNamedOrEnum(state.stencilOp.comp, FormatStencilComp(state.stencilOp.comp.val))}";
            yield return $"    Pass {FormatNamedOrEnum(state.stencilOp.pass, FormatStencilOp(state.stencilOp.pass.val))}";
            yield return $"    Fail {FormatNamedOrEnum(state.stencilOp.fail, FormatStencilOp(state.stencilOp.fail.val))}";
            yield return $"    ZFail {FormatNamedOrEnum(state.stencilOp.zFail, FormatStencilOp(state.stencilOp.zFail.val))}";
        }
        if (!IsDefaultStencilBlock(state.stencilOpFront, allowDisabledComp: true) || HasStencilNames(state.stencilOpFront))
        {
            yield return $"    CompFront {FormatNamedOrEnum(state.stencilOpFront.comp, FormatStencilComp(state.stencilOpFront.comp.val))}";
            yield return $"    PassFront {FormatNamedOrEnum(state.stencilOpFront.pass, FormatStencilOp(state.stencilOpFront.pass.val))}";
            yield return $"    FailFront {FormatNamedOrEnum(state.stencilOpFront.fail, FormatStencilOp(state.stencilOpFront.fail.val))}";
            yield return $"    ZFailFront {FormatNamedOrEnum(state.stencilOpFront.zFail, FormatStencilOp(state.stencilOpFront.zFail.val))}";
        }
        if (!IsDefaultStencilBlock(state.stencilOpBack, allowDisabledComp: true) || HasStencilNames(state.stencilOpBack))
        {
            yield return $"    CompBack {FormatNamedOrEnum(state.stencilOpBack.comp, FormatStencilComp(state.stencilOpBack.comp.val))}";
            yield return $"    PassBack {FormatNamedOrEnum(state.stencilOpBack.pass, FormatStencilOp(state.stencilOpBack.pass.val))}";
            yield return $"    FailBack {FormatNamedOrEnum(state.stencilOpBack.fail, FormatStencilOp(state.stencilOpBack.fail.val))}";
            yield return $"    ZFailBack {FormatNamedOrEnum(state.stencilOpBack.zFail, FormatStencilOp(state.stencilOpBack.zFail.val))}";
        }
        yield return "}";
    }

    private static IEnumerable<string> BuildFogCommands(UnitySerializedShaderState state)
    {
        int fogMode = (int)state.fogMode;
        bool needsFog = fogMode != -1 || state.fogDensity.val != 0f || state.fogStart.val != 0f || state.fogEnd.val != 0f
            || state.fogColor.x.val != 0f || state.fogColor.y.val != 0f || state.fogColor.z.val != 0f || state.fogColor.w.val != 0f;
        if (!needsFog)
        {
            yield break;
        }

        yield return "Fog {";
        if (fogMode != -1)
        {
            yield return $"    Mode {FormatFogMode(state.fogMode)}";
        }
        if (state.fogColor.x.val != 0f || state.fogColor.y.val != 0f || state.fogColor.z.val != 0f || state.fogColor.w.val != 0f)
        {
            yield return $"    Color ({FormatFloat(state.fogColor.x.val)},{FormatFloat(state.fogColor.y.val)},{FormatFloat(state.fogColor.z.val)},{FormatFloat(state.fogColor.w.val)})";
        }
        if (state.fogDensity.val != 0f)
        {
            yield return $"    Density {FormatFloat(state.fogDensity.val)}";
        }
        if (state.fogStart.val != 0f || state.fogEnd.val != 0f)
        {
            yield return $"    Range {FormatFloat(state.fogStart.val)}, {FormatFloat(state.fogEnd.val)}";
        }
        yield return "}";
    }

    private static string BuildColorMask(int mask)
    {
        StringBuilder builder = new();
        if ((mask & 2) != 0) builder.Append('R');
        if ((mask & 4) != 0) builder.Append('G');
        if ((mask & 8) != 0) builder.Append('B');
        if ((mask & 1) != 0) builder.Append('A');
        return builder.ToString();
    }

    private static bool HasName(UnitySerializedShaderFloatValue value)
    {
        return !string.IsNullOrWhiteSpace(value.name) && !string.Equals(value.name, "<noninit>", StringComparison.Ordinal);
    }

    private static bool HasStencilNames(UnitySerializedStencilOp op)
    {
        return HasName(op.pass) || HasName(op.fail) || HasName(op.zFail) || HasName(op.comp);
    }

    private static bool IsDefaultStencilBlock(UnitySerializedStencilOp op, bool allowDisabledComp)
    {
        int comp = (int)op.comp.val;
        bool defaultComp = comp == 8 || (allowDisabledComp && comp == 0);
        return (int)op.pass.val == 0 && (int)op.fail.val == 0 && (int)op.zFail.val == 0 && defaultComp;
    }

    private static string FormatNamedOrEnum(UnitySerializedShaderFloatValue value, string fallback)
    {
        return HasName(value) ? $"[{value.name}]" : fallback;
    }

    private static string FormatNamedOrInt(UnitySerializedShaderFloatValue value)
    {
        return HasName(value) ? $"[{value.name}]" : ((int)value.val).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string FormatNamedOrDecimal(UnitySerializedShaderFloatValue value)
    {
        return HasName(value) ? $"[{value.name}]" : FormatFloat(value.val);
    }

    private static string FormatCullMode(float value)
    {
        return ((int)value) switch
        {
            -1 => "Unknown",
            0 => "Off",
            1 => "Front",
            _ => "Back",
        };
    }

    private static string FormatZClip(float value) => (int)value == 0 ? "Off" : "On";

    private static string FormatZWrite(float value) => (int)value == 0 ? "Off" : "On";

    private static string FormatZTest(float value)
    {
        return (int)value switch
        {
            0 => "None",
            1 => "Unknown",
            2 => "Less",
            3 => "Equal",
            4 => "LEqual",
            5 => "Greater",
            6 => "NotEqual",
            7 => "GEqual",
            8 => "Always",
            _ => ((int)value).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    private static string FormatBlendMode(float value)
    {
        return (int)value switch
        {
            0 => "Zero",
            1 => "One",
            2 => "DstColor",
            3 => "SrcColor",
            4 => "OneMinusDstColor",
            5 => "SrcAlpha",
            6 => "OneMinusSrcColor",
            7 => "DstAlpha",
            8 => "OneMinusDstAlpha",
            9 => "SrcAlphaSaturate",
            10 => "OneMinusSrcAlpha",
            _ => ((int)value).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    private static string FormatBlendOp(float value)
    {
        return (int)value switch
        {
            0 => "Add",
            1 => "Sub",
            2 => "RevSub",
            3 => "Min",
            4 => "Max",
            5 => "LogicalClear",
            6 => "LogicalSet",
            7 => "LogicalCopy",
            8 => "LogicalCopyInverted",
            9 => "LogicalNoop",
            10 => "LogicalInvert",
            11 => "LogicalAnd",
            12 => "LogicalNand",
            13 => "LogicalOr",
            14 => "LogicalNor",
            15 => "LogicalXor",
            16 => "LogicalEquivalence",
            17 => "LogicalAndReverse",
            18 => "LogicalAndInverted",
            19 => "LogicalOrReverse",
            20 => "LogicalOrInverted",
            21 => "Multiply",
            22 => "Screen",
            23 => "Overlay",
            24 => "Darken",
            25 => "Lighten",
            26 => "ColorDodge",
            27 => "ColorBurn",
            28 => "HardLight",
            29 => "SoftLight",
            30 => "Difference",
            31 => "Exclusion",
            32 => "HSLHue",
            33 => "HSLSaturation",
            34 => "HSLColor",
            35 => "HSLLuminosity",
            _ => ((int)value).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    private static string FormatStencilComp(float value)
    {
        return (int)value switch
        {
            -1 => "Unknown",
            0 => "Disabled",
            1 => "Never",
            2 => "Less",
            3 => "Equal",
            4 => "LEqual",
            5 => "Greater",
            6 => "NotEqual",
            7 => "GEqual",
            8 => "Always",
            _ => ((int)value).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    private static string FormatStencilOp(float value)
    {
        return (int)value switch
        {
            0 => "Keep",
            1 => "Zero",
            2 => "Replace",
            3 => "IncrSat",
            4 => "DecrSat",
            5 => "Invert",
            6 => "IncrWrap",
            7 => "DecrWrap",
            _ => ((int)value).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    private static string FormatFogMode(float value)
    {
        return (int)value switch
        {
            -1 => "Unknown",
            0 => "Off",
            1 => "Linear",
            2 => "Exp",
            3 => "Exp2",
            _ => ((int)value).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    private static void WriteTags(IndentedStringBuilder sb, List<UnityTagMapEntry> tags)
    {
        if (tags.Count == 0)
        {
            return;
        }

        sb.AppendLine("Tags {");
        sb.Indent();
        foreach (UnityTagMapEntry tag in tags)
        {
            sb.AppendLine($"\"{tag.first}\"=\"{tag.second}\"");
        }
        sb.Unindent();
        sb.AppendLine("}");
    }

    private static string FormatFloat(float value)
    {
        return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void WriteProgramsBlock(IndentedStringBuilder sb, List<string> keywordNames, List<UnityProgramData> programs)
    {
        sb.AppendLine("CGPROGRAM");

        List<IGrouping<string, UnityProgramData>> stageGroups = programs
            .GroupBy(static p => p.Stage)
            .ToList();

        foreach (IGrouping<string, UnityProgramData> stageGroup in stageGroups)
        {
            if (TryGetStagePragma(stageGroup.Key, out string pragma))
            {
                sb.AppendLine($"{pragma} main");
            }
        }

        foreach (string keyword in BuildPassKeywordSymbols(keywordNames, programs))
        {
            sb.AppendLine($"#pragma multi_compile_local __ {keyword}");
        }

        sb.AppendLine(string.Empty);
        foreach (IGrouping<string, UnityProgramData> stageGroup in stageGroups)
        {
            WriteStagePrograms(sb, keywordNames, stageGroup.Key, stageGroup.ToList());
        }
        sb.AppendLine("ENDCG");
    }

    private static void WriteStagePrograms(IndentedStringBuilder sb, List<string> keywordNames, string stage, List<UnityProgramData> programs)
    {
        string? stageMacro = GetStageMacro(stage);
        if (!string.IsNullOrWhiteSpace(stageMacro))
        {
            sb.AppendLine($"#if defined({stageMacro})");
        }

        List<ushort> stageKeywordIndices = CollectDistinctKeywordIndices(programs);

        List<UnityProgramData> conditionalPrograms = [];
        List<UnityProgramData> unconditionalPrograms = [];

        foreach (UnityProgramData program in programs)
        {
            if (program.KeywordIndices.Count == 0)
            {
                unconditionalPrograms.Add(program);
            }
            else
            {
                conditionalPrograms.Add(program);
            }
        }

        bool wroteConditionalHeader = false;
        for (int i = 0; i < conditionalPrograms.Count; i++)
        {
            UnityProgramData program = conditionalPrograms[i];
            string keywordCondition = BuildKeywordCondition(keywordNames, stageKeywordIndices, program.KeywordIndices) ?? string.Empty;
            sb.AppendLine($"// Stage: {program.Stage}, Blob: {program.BlobIndex}, ParamBlob: {(program.ParameterBlobIndex.HasValue ? program.ParameterBlobIndex.Value.ToString() : "<none>")}, Language: {program.SourceLanguage}");
            sb.AppendLine($"{(wroteConditionalHeader ? "#elif" : "#if")} {keywordCondition}");
            wroteConditionalHeader = true;
            WriteProgramBody(sb, program);
            sb.AppendLine(string.Empty);
        }

        if (unconditionalPrograms.Count > 0)
        {
            for (int i = 0; i < unconditionalPrograms.Count; i++)
            {
                UnityProgramData program = unconditionalPrograms[i];
                sb.AppendLine($"// Stage: {program.Stage}, Blob: {program.BlobIndex}, ParamBlob: {(program.ParameterBlobIndex.HasValue ? program.ParameterBlobIndex.Value.ToString() : "<none>")}, Language: {program.SourceLanguage}");
                if (wroteConditionalHeader && i == 0)
                {
                    sb.AppendLine("#else");
                }
                WriteProgramBody(sb, program);
                sb.AppendLine(string.Empty);
            }
        }

        if (wroteConditionalHeader)
        {
            sb.AppendLine("#endif");
            sb.AppendLine(string.Empty);
        }

        if (!string.IsNullOrWhiteSpace(stageMacro))
        {
            sb.AppendLine("#endif");
            sb.AppendLine(string.Empty);
        }
    }

    private static void WriteProgramBody(IndentedStringBuilder sb, UnityProgramData program)
    {
        if (program.Success && !string.IsNullOrWhiteSpace(program.SourceCode))
        {
            WriteRawBlock(sb, TrimTrailingWhitespace(program.SourceCode!));
            return;
        }

        sb.AppendLine("// Decompile failed.");
        if (!string.IsNullOrWhiteSpace(program.ErrorMessage))
        {
            foreach (string line in SplitLines(program.ErrorMessage!))
            {
                sb.AppendLine($"// {line}");
            }
        }
    }

    private static string? BuildKeywordCondition(List<string> keywordNames, List<ushort> stageKeywordIndices, List<ushort> keywordIndices)
    {
        if (stageKeywordIndices.Count == 0)
        {
            return null;
        }

        HashSet<ushort> activeKeywords = keywordIndices.ToHashSet();
        List<string> conditions = [];
        foreach (ushort keywordIndex in stageKeywordIndices)
        {
            string keyword = BuildKeywordSymbol(keywordNames, keywordIndex);
            conditions.Add(activeKeywords.Contains(keywordIndex) ? $"defined({keyword})" : $"!defined({keyword})");
        }

        return string.Join(" && ", conditions);
    }

    private static List<string> BuildPassKeywordSymbols(List<string> keywordNames, List<UnityProgramData> programs)
    {
        return CollectDistinctKeywordIndices(programs)
            .Select(i => BuildKeywordSymbol(keywordNames, i))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static List<ushort> CollectDistinctKeywordIndices(List<UnityProgramData> programs)
    {
        List<ushort> result = [];
        HashSet<ushort> seen = [];
        foreach (UnityProgramData program in programs)
        {
            foreach (ushort keywordIndex in program.KeywordIndices)
            {
                if (seen.Add(keywordIndex))
                {
                    result.Add(keywordIndex);
                }
            }
        }

        return result;
    }

    private static string BuildKeywordSymbol(List<string> keywordNames, ushort keywordIndex)
    {
        if (keywordIndex >= keywordNames.Count)
        {
            return $"KEYWORD_{keywordIndex}";
        }

        string keyword = keywordNames[keywordIndex];
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return $"KEYWORD_{keywordIndex}";
        }

        return SanitizePreprocessorSymbol(keyword);
    }

    private static string SanitizePreprocessorSymbol(string value)
    {
        StringBuilder builder = new(value.Length);
        foreach (char c in value)
        {
            builder.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        }

        if (builder.Length == 0)
        {
            return "KEYWORD";
        }

        if (char.IsDigit(builder[0]))
        {
            builder.Insert(0, '_');
        }

        return builder.ToString();
    }

    private static bool TryGetStagePragma(string stage, out string pragma)
    {
        pragma = stage switch
        {
            "Vertex" => "#pragma vertex",
            "Fragment" => "#pragma fragment",
            "Geometry" => "#pragma geometry",
            "Hull" => "#pragma hull",
            "Domain" => "#pragma domain",
            _ => string.Empty,
        };

        return pragma.Length > 0;
    }

    private static string? GetStageMacro(string stage)
    {
        return stage switch
        {
            "Vertex" => "SHADER_STAGE_VERTEX",
            "Fragment" => "SHADER_STAGE_FRAGMENT",
            "Geometry" => "SHADER_STAGE_GEOMETRY",
            "Hull" => "SHADER_STAGE_HULL",
            "Domain" => "SHADER_STAGE_DOMAIN",
            _ => null,
        };
    }

    private static void WriteRawBlock(IndentedStringBuilder sb, string text)
    {
        foreach (string line in SplitLines(text))
        {
            sb.AppendLine(line);
        }
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
    }

    private static string TrimTrailingWhitespace(string text)
    {
        return text.TrimEnd(' ', '\t', '\r', '\n');
    }

    private sealed class IndentedStringBuilder
    {
        private readonly StringBuilder _builder = new();
        private int _indent;

        public void Indent() => _indent++;

        public void Unindent()
        {
            if (_indent > 0)
            {
                _indent--;
            }
        }

        public void AppendLine(string text)
        {
            if (text.Length == 0)
            {
                _builder.AppendLine();
                return;
            }

            _builder.Append(' ', _indent * 4);
            _builder.AppendLine(text);
        }

        public override string ToString() => _builder.ToString();
    }
}
