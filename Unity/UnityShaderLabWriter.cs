using System.Text;

namespace Ruri.ShaderTools;

// Render result: the main `.shader` file plus a per-variant `<key>.hlsl` map
// the caller writes to a sibling folder named after the .shader stem. The
// .shader file references those bodies via `#include` so the file stays
// compact (and individual variants stay diffable in isolation).
public sealed record UnityShaderLabResult(string ShaderText, IReadOnlyDictionary<string, string> VariantFiles);

public static class UnityShaderLabWriter
{
    // Backwards-compat overload for callers that don't want per-variant
    // splitting. The variant bodies stay inlined inside the .shader file.
    public static string Write(UnityShaderMetadata metadata)
        => WriteCore(metadata, variantFolderStem: null).ShaderText;

    // Variant-splitting form: each subprogram's HLSL body lands in
    // `<variantFolderStem>/<key>.hlsl`, the .shader file references them
    // via `#include`. Caller is responsible for materialising the dictionary
    // entries to disk under that folder name.
    public static UnityShaderLabResult WriteSplit(UnityShaderMetadata metadata, string variantFolderStem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variantFolderStem);
        return WriteCore(metadata, variantFolderStem);
    }

    private static UnityShaderLabResult WriteCore(UnityShaderMetadata metadata, string? variantFolderStem)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        Dictionary<string, string> variantFiles = new(StringComparer.Ordinal);
        WriteContext ctx = new(variantFolderStem, variantFiles);

        IndentedStringBuilder sb = new();
        sb.AppendLine($"Shader \"{metadata.Name}\" {{");
        sb.Indent();

        if (metadata.ParsedForm.PropInfo.Props.Count > 0)
        {
            sb.AppendLine("Properties {");
            sb.Indent();
            foreach (UnitySerializedProperty property in metadata.ParsedForm.PropInfo.Props)
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

        for (int subShaderIndex = 0; subShaderIndex < metadata.ParsedForm.SubShaders.Count; subShaderIndex++)
        {
            UnitySerializedSubShader subShader = metadata.ParsedForm.SubShaders[subShaderIndex];
            sb.AppendLine("SubShader {");
            sb.Indent();
            WriteTags(sb, subShader.Tags.Tags);
            if (subShader.LOD != 0)
            {
                sb.AppendLine($"LOD {subShader.LOD}");
            }

            for (int passIndex = 0; passIndex < subShader.Passes.Count; passIndex++)
            {
                UnitySerializedPass pass = subShader.Passes[passIndex];
                if (!string.IsNullOrWhiteSpace(pass.UseName))
                {
                    sb.AppendLine($"UsePass \"{pass.UseName}\"");
                    continue;
                }

                sb.AppendLine("Pass {");
                sb.Indent();

                if (!string.IsNullOrWhiteSpace(pass.State.Name))
                {
                    sb.AppendLine($"Name \"{pass.State.Name}\"");
                }
                if (pass.State.LOD != 0)
                {
                    sb.AppendLine($"LOD {pass.State.LOD}");
                }
                foreach (string command in BuildStateCommands(pass.State))
                {
                    if (!string.IsNullOrWhiteSpace(command))
                    {
                        sb.AppendLine(command);
                    }
                }
                WriteTags(sb, pass.State.Tags.Tags);
                if (pass.Tags.Tags.Count > 0)
                {
                    WriteTags(sb, pass.Tags.Tags);
                }

                if (HasAnyProgram(pass))
                {
                    WriteProgramsBlock(sb, metadata.ParsedForm.KeywordNames, pass, subShaderIndex, passIndex, ctx);
                }

                sb.Unindent();
                sb.AppendLine("}");
            }

            sb.Unindent();
            sb.AppendLine("}");
        }

        if (!string.IsNullOrWhiteSpace(metadata.ParsedForm.FallbackName))
        {
            sb.AppendLine($"Fallback \"{metadata.ParsedForm.FallbackName}\"");
        }

        if (!string.IsNullOrWhiteSpace(metadata.ParsedForm.CustomEditorName))
        {
            sb.AppendLine($"CustomEditor \"{metadata.ParsedForm.CustomEditorName}\"");
        }

        sb.Unindent();
        sb.AppendLine("}");
        return new UnityShaderLabResult(sb.ToString(), variantFiles);
    }

    // Per-emit context. Carries the variant-folder stem used in `#include`
    // paths plus the dictionary that collects (filename -> body) entries
    // so the caller can flush them to disk after Write returns. When the
    // stem is null, every variant body stays inlined in the .shader text
    // and the dictionary stays empty.
    private sealed class WriteContext
    {
        public string? VariantFolderStem { get; }
        public Dictionary<string, string> VariantFiles { get; }
        public WriteContext(string? variantFolderStem, Dictionary<string, string> variantFiles)
        {
            VariantFolderStem = variantFolderStem;
            VariantFiles = variantFiles;
        }
    }

    private static bool HasAnyProgram(UnitySerializedPass pass)
    {
        foreach ((_, UnitySerializedProgram program) in pass.EnumerateProgramSlots())
        {
            if (program.SubPrograms.Count > 0) return true;
        }
        return false;
    }

    private static string BuildPropertyDeclaration(UnitySerializedProperty property)
    {
        StringBuilder builder = new();
        foreach (string attribute in property.Attributes)
        {
            builder.Append('[').Append(attribute).Append("] ");
        }

        uint flags = property.Flags;
        if ((flags & 1u) != 0) builder.Append("[HideInInspector] ");
        if ((flags & 2u) != 0) builder.Append("[PerRendererData] ");
        if ((flags & 4u) != 0) builder.Append("[NoScaleOffset] ");
        if ((flags & 8u) != 0) builder.Append("[Normal] ");
        if ((flags & 0x10u) != 0) builder.Append("[HDR] ");
        if ((flags & 0x20u) != 0) builder.Append("[Gamma] ");

        string typeName = property.Type switch
        {
            0 => "Color",
            1 => "Vector",
            2 => "Float",
            3 => $"Range({FormatFloat(property.DefValue[1])}, {FormatFloat(property.DefValue[2])})",
            4 => property.DefTexture.TexDim switch
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

        string value = property.Type switch
        {
            0 or 1 => $"({FormatFloat(property.DefValue[0])}, {FormatFloat(property.DefValue[1])}, {FormatFloat(property.DefValue[2])}, {FormatFloat(property.DefValue[3])})",
            2 or 3 or 5 => FormatFloat(property.DefValue[0]),
            4 => $"\"{property.DefTexture.DefaultName}\" {{}}",
            _ => FormatFloat(property.DefValue[0]),
        };

        builder.Append($"{property.Name} (\"{property.Description}\", {typeName}) = {value}");
        return builder.ToString();
    }

    private static IEnumerable<string> BuildStateCommands(UnitySerializedShaderState state)
    {
        if (state.RtSeparateBlend)
        {
            foreach (string command in BuildRtBlendCommands(state.RtBlend0, 0)) yield return command;
            foreach (string command in BuildRtBlendCommands(state.RtBlend1, 1)) yield return command;
            foreach (string command in BuildRtBlendCommands(state.RtBlend2, 2)) yield return command;
            foreach (string command in BuildRtBlendCommands(state.RtBlend3, 3)) yield return command;
            foreach (string command in BuildRtBlendCommands(state.RtBlend4, 4)) yield return command;
            foreach (string command in BuildRtBlendCommands(state.RtBlend5, 5)) yield return command;
            foreach (string command in BuildRtBlendCommands(state.RtBlend6, 6)) yield return command;
            foreach (string command in BuildRtBlendCommands(state.RtBlend7, 7)) yield return command;
        }
        else
        {
            foreach (string command in BuildRtBlendCommands(state.RtBlend0, -1)) yield return command;
        }

        if (state.AlphaToMask.Val > 0f || HasName(state.AlphaToMask))
        {
            yield return HasName(state.AlphaToMask) ? $"AlphaToMask [{state.AlphaToMask.Name}]" : "AlphaToMask On";
        }
        if ((int)state.ZClip.Val == 1 || HasName(state.ZClip)) yield return $"ZClip {FormatNamedOrEnum(state.ZClip, FormatZClip(state.ZClip.Val))}";
        if (((int)state.ZTest.Val != 0 && (int)state.ZTest.Val != 4) || HasName(state.ZTest)) yield return $"ZTest {FormatNamedOrEnum(state.ZTest, FormatZTest(state.ZTest.Val))}";
        if ((int)state.ZWrite.Val != 1 || HasName(state.ZWrite)) yield return $"ZWrite {FormatNamedOrEnum(state.ZWrite, FormatZWrite(state.ZWrite.Val))}";
        if ((int)state.Culling.Val != 2 || HasName(state.Culling)) yield return $"Cull {FormatNamedOrEnum(state.Culling, FormatCullMode(state.Culling.Val))}";
        if ((int)state.Conservative.Val != 0 || HasName(state.Conservative)) yield return $"Conservative {FormatNamedOrEnum(state.Conservative, ((int)state.Conservative.Val) == 1 ? "True" : "False")}";
        if (state.OffsetFactor.Val != 0f || state.OffsetUnits.Val != 0f || HasName(state.OffsetFactor) || HasName(state.OffsetUnits)) yield return $"Offset {FormatNamedOrDecimal(state.OffsetFactor)}, {FormatNamedOrDecimal(state.OffsetUnits)}";

        foreach (string command in BuildStencilCommands(state)) yield return command;
        foreach (string command in BuildFogCommands(state)) yield return command;

        if (state.Lighting)
        {
            yield return "Lighting On";
        }
    }

    private static IEnumerable<string> BuildRtBlendCommands(UnitySerializedShaderRTBlendState state, int index)
    {
        bool hasBlendName = HasName(state.SrcBlend) || HasName(state.DestBlend) || HasName(state.SrcBlendAlpha) || HasName(state.DestBlendAlpha);
        bool hasBlendOpName = HasName(state.BlendOp) || HasName(state.BlendOpAlpha);
        bool hasColMaskName = HasName(state.ColMask);

        if ((int)state.SrcBlend.Val != 1 || (int)state.DestBlend.Val != 0 || (int)state.SrcBlendAlpha.Val != 1 || (int)state.DestBlendAlpha.Val != 0 || hasBlendName)
        {
            string command = index >= 0 ? $"Blend {index} " : "Blend ";
            command += $"{FormatNamedOrEnum(state.SrcBlend, FormatBlendMode(state.SrcBlend.Val))} {FormatNamedOrEnum(state.DestBlend, FormatBlendMode(state.DestBlend.Val))}";
            string alphaPart = (int)state.SrcBlendAlpha.Val != 1 || (int)state.DestBlendAlpha.Val != 0 || HasName(state.SrcBlendAlpha) || HasName(state.DestBlendAlpha)
                ? $", {FormatNamedOrEnum(state.SrcBlendAlpha, FormatBlendMode(state.SrcBlendAlpha.Val))} {FormatNamedOrEnum(state.DestBlendAlpha, FormatBlendMode(state.DestBlendAlpha.Val))}"
                : string.Empty;
            yield return command + alphaPart;
        }

        if ((int)state.BlendOp.Val != 0 || (int)state.BlendOpAlpha.Val != 0 || hasBlendOpName)
        {
            string command = index >= 0 ? $"BlendOp {index} " : "BlendOp ";
            command += FormatNamedOrEnum(state.BlendOp, FormatBlendOp(state.BlendOp.Val));
            if ((int)state.BlendOpAlpha.Val != 0 || HasName(state.BlendOpAlpha))
            {
                command += $", {FormatNamedOrEnum(state.BlendOpAlpha, FormatBlendOp(state.BlendOpAlpha.Val))}";
            }
            yield return command;
        }

        if ((int)state.ColMask.Val != 15 || hasColMaskName)
        {
            string mask = hasColMaskName ? $"[{state.ColMask.Name}]" : ((int)state.ColMask.Val) == 0 ? "0" : BuildColorMask((int)state.ColMask.Val);
            yield return index >= 0 ? $"ColorMask {mask} {index}" : $"ColorMask {mask}";
        }
    }

    private static IEnumerable<string> BuildStencilCommands(UnitySerializedShaderState state)
    {
        bool hasNames = HasName(state.StencilRef) || HasName(state.StencilReadMask) || HasName(state.StencilWriteMask)
            || HasStencilNames(state.StencilOp) || HasStencilNames(state.StencilOpFront) || HasStencilNames(state.StencilOpBack);

        bool hasValues = state.StencilRef.Val != 0f || state.StencilReadMask.Val != 255f || state.StencilWriteMask.Val != 255f
            || !IsDefaultStencilBlock(state.StencilOp, allowDisabledComp: false)
            || !IsDefaultStencilBlock(state.StencilOpFront, allowDisabledComp: false)
            || !IsDefaultStencilBlock(state.StencilOpBack, allowDisabledComp: false);

        if (!hasValues && !hasNames)
        {
            yield break;
        }

        yield return "Stencil {";
        if (state.StencilRef.Val != 0f || HasName(state.StencilRef)) yield return $"    Ref {FormatNamedOrInt(state.StencilRef)}";
        if (state.StencilReadMask.Val != 255f || HasName(state.StencilReadMask)) yield return $"    ReadMask {FormatNamedOrInt(state.StencilReadMask)}";
        if (state.StencilWriteMask.Val != 255f || HasName(state.StencilWriteMask)) yield return $"    WriteMask {FormatNamedOrInt(state.StencilWriteMask)}";
        if (!IsDefaultStencilBlock(state.StencilOp, allowDisabledComp: true) || HasStencilNames(state.StencilOp))
        {
            yield return $"    Comp {FormatNamedOrEnum(state.StencilOp.Comp, FormatStencilComp(state.StencilOp.Comp.Val))}";
            yield return $"    Pass {FormatNamedOrEnum(state.StencilOp.Pass, FormatStencilOp(state.StencilOp.Pass.Val))}";
            yield return $"    Fail {FormatNamedOrEnum(state.StencilOp.Fail, FormatStencilOp(state.StencilOp.Fail.Val))}";
            yield return $"    ZFail {FormatNamedOrEnum(state.StencilOp.ZFail, FormatStencilOp(state.StencilOp.ZFail.Val))}";
        }
        if (!IsDefaultStencilBlock(state.StencilOpFront, allowDisabledComp: true) || HasStencilNames(state.StencilOpFront))
        {
            yield return $"    CompFront {FormatNamedOrEnum(state.StencilOpFront.Comp, FormatStencilComp(state.StencilOpFront.Comp.Val))}";
            yield return $"    PassFront {FormatNamedOrEnum(state.StencilOpFront.Pass, FormatStencilOp(state.StencilOpFront.Pass.Val))}";
            yield return $"    FailFront {FormatNamedOrEnum(state.StencilOpFront.Fail, FormatStencilOp(state.StencilOpFront.Fail.Val))}";
            yield return $"    ZFailFront {FormatNamedOrEnum(state.StencilOpFront.ZFail, FormatStencilOp(state.StencilOpFront.ZFail.Val))}";
        }
        if (!IsDefaultStencilBlock(state.StencilOpBack, allowDisabledComp: true) || HasStencilNames(state.StencilOpBack))
        {
            yield return $"    CompBack {FormatNamedOrEnum(state.StencilOpBack.Comp, FormatStencilComp(state.StencilOpBack.Comp.Val))}";
            yield return $"    PassBack {FormatNamedOrEnum(state.StencilOpBack.Pass, FormatStencilOp(state.StencilOpBack.Pass.Val))}";
            yield return $"    FailBack {FormatNamedOrEnum(state.StencilOpBack.Fail, FormatStencilOp(state.StencilOpBack.Fail.Val))}";
            yield return $"    ZFailBack {FormatNamedOrEnum(state.StencilOpBack.ZFail, FormatStencilOp(state.StencilOpBack.ZFail.Val))}";
        }
        yield return "}";
    }

    private static IEnumerable<string> BuildFogCommands(UnitySerializedShaderState state)
    {
        int fogMode = state.FogMode;
        bool needsFog = fogMode != -1 || state.FogDensity.Val != 0f || state.FogStart.Val != 0f || state.FogEnd.Val != 0f
            || state.FogColor.X.Val != 0f || state.FogColor.Y.Val != 0f || state.FogColor.Z.Val != 0f || state.FogColor.W.Val != 0f;
        if (!needsFog)
        {
            yield break;
        }

        yield return "Fog {";
        if (fogMode != -1)
        {
            yield return $"    Mode {FormatFogMode(fogMode)}";
        }
        if (state.FogColor.X.Val != 0f || state.FogColor.Y.Val != 0f || state.FogColor.Z.Val != 0f || state.FogColor.W.Val != 0f)
        {
            yield return $"    Color ({FormatFloat(state.FogColor.X.Val)},{FormatFloat(state.FogColor.Y.Val)},{FormatFloat(state.FogColor.Z.Val)},{FormatFloat(state.FogColor.W.Val)})";
        }
        if (state.FogDensity.Val != 0f)
        {
            yield return $"    Density {FormatFloat(state.FogDensity.Val)}";
        }
        if (state.FogStart.Val != 0f || state.FogEnd.Val != 0f)
        {
            yield return $"    Range {FormatFloat(state.FogStart.Val)}, {FormatFloat(state.FogEnd.Val)}";
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
        return !string.IsNullOrWhiteSpace(value.Name) && !string.Equals(value.Name, "<noninit>", StringComparison.Ordinal);
    }

    private static bool HasStencilNames(UnitySerializedStencilOp op)
    {
        return HasName(op.Pass) || HasName(op.Fail) || HasName(op.ZFail) || HasName(op.Comp);
    }

    private static bool IsDefaultStencilBlock(UnitySerializedStencilOp op, bool allowDisabledComp)
    {
        int comp = (int)op.Comp.Val;
        bool defaultComp = comp == 8 || (allowDisabledComp && comp == 0);
        return (int)op.Pass.Val == 0 && (int)op.Fail.Val == 0 && (int)op.ZFail.Val == 0 && defaultComp;
    }

    private static string FormatNamedOrEnum(UnitySerializedShaderFloatValue value, string fallback)
    {
        return HasName(value) ? $"[{value.Name}]" : fallback;
    }

    private static string FormatNamedOrInt(UnitySerializedShaderFloatValue value)
    {
        return HasName(value) ? $"[{value.Name}]" : ((int)value.Val).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string FormatNamedOrDecimal(UnitySerializedShaderFloatValue value)
    {
        return HasName(value) ? $"[{value.Name}]" : FormatFloat(value.Val);
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

    private static string FormatFogMode(int value)
    {
        return value switch
        {
            -1 => "Unknown",
            0 => "Off",
            1 => "Linear",
            2 => "Exp",
            3 => "Exp2",
            _ => value.ToString(System.Globalization.CultureInfo.InvariantCulture),
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
            sb.AppendLine($"\"{tag.First}\"=\"{tag.Second}\"");
        }
        sb.Unindent();
        sb.AppendLine("}");
    }

    private static string FormatFloat(float value)
    {
        return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // Walks ProgVertex/Fragment/Geometry/Hull/Domain/RayTracing in order and writes one
    // HLSLPROGRAM block per pass. SubPrograms within each prog* slot share the stage
    // pragma but get split across `#if defined(KEYWORD)` blocks when their KeywordIndices
    // differ. Decompile output (Success/SourceCode/ErrorMessage) lives on each
    // SubProgram directly, populated by ShaderRuriDecompileExporter after the decompiler
    // returns.
    //
    // When `ctx.VariantFolderStem` is set, every concrete subprogram body is offloaded
    // to a sibling `<stem>/<variantKey>.hlsl` file and the .shader gets a single
    // `#include "<stem>/<variantKey>.hlsl"` line per variant. The variant key is built
    // from (subShaderIndex, passIndex, stage, keyword combo, blob index) so two binaries
    // with the same active-keyword set in the same pass slot still get distinct files.
    private static void WriteProgramsBlock(IndentedStringBuilder sb, List<string> keywordNames, UnitySerializedPass pass, int subShaderIndex, int passIndex, WriteContext ctx)
    {
        sb.AppendLine("HLSLPROGRAM");

        // The decompiled HLSL uses SM5.1+ features (register(spaceN), templated
        // ByteAddressBuffer.Load<T>(), etc.) that Unity's default FXC path
        // doesn't accept. Enabling DXC routes the program through the modern
        // compiler stack and lifts the target to 5.0 (Unity's max for DX11),
        // which together cover the syntactic surface SPIRV-Cross emits.
        sb.AppendLine("#pragma target 5.0");
        sb.AppendLine("#pragma use_dxc");

        foreach ((string stage, _) in pass.EnumerateProgramSlots())
        {
            if (TryGetStagePragma(stage, out string pragma))
            {
                sb.AppendLine($"{pragma} main");
            }
        }

        // Single-variant mode: each stage emits only the first SubProgram,
        // so the chain-of-`#if defined(VARIANT_*)` no longer applies and we
        // skip the cross-product multi_compile_local pragmas entirely. With
        // them in place Unity generates 2^N variant permutations and the
        // ones where neither stage's `main` is defined fail to compile.
        sb.AppendLine(string.Empty);
        foreach ((string stage, UnitySerializedProgram program) in pass.EnumerateProgramSlots())
        {
            WriteStageSubPrograms(sb, keywordNames, stage, program.SubPrograms, subShaderIndex, passIndex, ctx);
        }
        sb.AppendLine("ENDHLSL");
    }

    private static void WriteStageSubPrograms(IndentedStringBuilder sb, List<string> keywordNames, string stage, List<UnitySerializedSubProgram> subPrograms, int subShaderIndex, int passIndex, WriteContext ctx)
    {
        if (subPrograms.Count == 0)
        {
            return;
        }

        sb.AppendLine($"// ============================================================");
        sb.AppendLine($"// Stage: {stage}");
        sb.AppendLine($"// ============================================================");

        // Wrap each stage body in `#ifdef SHADER_STAGE_*` so VS-only and PS-only
        // declarations (entry `main`, SPIRV_Cross_Input/Output structs, statics,
        // cbuffers) don't collide when Unity compiles a single TU per stage.
        // SHADER_STAGE_VERTEX/FRAGMENT/etc are Unity-provided macros set per
        // stage compile, so the preprocessor naturally strips the other stage's
        // declarations.
        string? stageMacro = GetShaderStageMacro(stage);
        if (stageMacro != null)
        {
            sb.AppendLine($"#ifdef {stageMacro}");
        }

        // Single-variant mode for v0 of Unity output: each stage emits only
        // its FIRST SubProgram. Multi-variant requires either per-pass pairing
        // (vertex+fragment paired into individual Pass blocks) or stage-aware
        // `multi_compile_local` keyword sets, which still produce most of an
        // invalid cross-product. v0 keeps the dev loop tight by trading variant
        // coverage for a clean compile.
        UnitySerializedSubProgram primary = subPrograms[0];
        sb.AppendLine($"// Stage: {stage}, Blob: {primary.BlobIndex}, ParamBlob: {(primary.ParameterBlobIndex.HasValue ? primary.ParameterBlobIndex.Value.ToString() : "<none>")}, Language: {primary.SourceLanguage}");
        if (subPrograms.Count > 1)
        {
            sb.AppendLine($"// Note: {subPrograms.Count - 1} additional variant(s) elided (single-variant emit mode).");
        }

        bool effectiveSplit = !string.IsNullOrEmpty(ctx.VariantFolderStem) && subPrograms.Count > 1;
        WriteSubProgramBody(sb, stage, primary, keywordNames, subShaderIndex, passIndex, ctx, effectiveSplit);
        sb.AppendLine(string.Empty);

        if (stageMacro != null)
        {
            sb.AppendLine($"#endif");
            sb.AppendLine(string.Empty);
        }
    }

    private static string? GetShaderStageMacro(string stage) => stage switch
    {
        "Vertex" => "SHADER_STAGE_VERTEX",
        "Fragment" => "SHADER_STAGE_FRAGMENT",
        "Geometry" => "SHADER_STAGE_GEOMETRY",
        "Hull" => "SHADER_STAGE_HULL",
        "Domain" => "SHADER_STAGE_DOMAIN",
        "RayTracing" => "SHADER_STAGE_RAY_TRACING",
        _ => null,
    };

    private static void WriteSubProgramBody(IndentedStringBuilder sb, string stage, UnitySerializedSubProgram sp, List<string> keywordNames, int subShaderIndex, int passIndex, WriteContext ctx, bool effectiveSplit)
    {
        if (effectiveSplit && sp.Success && !string.IsNullOrWhiteSpace(sp.SourceCode))
        {
            string variantKey = BuildVariantKey(subShaderIndex, passIndex, stage, sp, keywordNames);
            string fileName = variantKey + (string.IsNullOrWhiteSpace(sp.SourceFileExtension) ? ".hlsl" : sp.SourceFileExtension);
            string includePath = $"{ctx.VariantFolderStem}/{fileName}";
            ctx.VariantFiles[fileName] = BuildVariantFileContent(stage, sp, variantKey, AdaptHlslForUnity(TrimTrailingWhitespace(sp.SourceCode!)));
            sb.AppendLine($"#include \"{includePath}\"");
            return;
        }

        if (sp.Success && !string.IsNullOrWhiteSpace(sp.SourceCode))
        {
            WriteRawBlock(sb, AdaptHlslForUnity(TrimTrailingWhitespace(sp.SourceCode!)));
            return;
        }

        sb.AppendLine("// Decompile failed.");
        if (!string.IsNullOrWhiteSpace(sp.ErrorMessage))
        {
            foreach (string line in SplitLines(sp.ErrorMessage!))
            {
                sb.AppendLine($"// {line}");
            }
        }
    }

    // Adapts spirv-cross emitted HLSL so Unity's ShaderLab pipeline accepts it
    // without further hand-edits:
    //   * Texture bindings named `Material_<X>` → `_<X>` so the Properties
    //     declaration (Unity uses `_X` convention) auto-binds to the HLSL var.
    //   * Sampler bindings `Material_<X>Sampler` → `sampler_<X>` so Unity's
    //     "must match a texture or contain inline mode names" heuristic accepts
    //     them and pairs them with the matching texture.
    //   * Aliased ByteAddressBuffer dup `T<N>_<M>` at the SAME slot as `T<N>`
    //     — spirv-cross emits both names when two SSA values touch the same
    //     descriptor; collapse the alias declaration and rewrite call sites.
    //
    // The replacements preserve cbuffer member names that happen to share the
    // `Material_<X>` prefix by anchoring on the texture/sampler type token.
    private static readonly System.Text.RegularExpressions.Regex MaterialSamplerDeclRegex =
        new(@"\bMaterial_(?<n>[A-Za-z0-9_]+)Sampler\b", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex MaterialTextureDeclRegex =
        new(@"(?<t>Texture(?:2D|2DArray|Cube|CubeArray|3D)(?:<[^>]+>)?)\s+Material_(?<n>[A-Za-z0-9_]+)\s*:\s*register", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex AliasedByteAddressDeclRegex =
        new(@"^\s*ByteAddressBuffer\s+T(?<n>\d+)_\d+\s*:\s*register\(t\k<n>[^\)]*\);\s*$",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.Multiline);
    private static readonly System.Text.RegularExpressions.Regex AliasedByteAddressRefRegex =
        new(@"\bT(\d+)_\d+\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static string AdaptHlslForUnity(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return body;
        }

        // Pass 1 — sampler decl + refs.
        body = MaterialSamplerDeclRegex.Replace(body, "sampler_${n}");

        // Pass 2 — texture decl. Collect the renamed roots so we only rewrite
        // body references that correspond to a renamed texture (cbuffer scalar
        // members like `Material_SelectionColor` must NOT get renamed to
        // `_SelectionColor` — Unity properties exist for textures only here).
        HashSet<string> renamedTextures = new(StringComparer.Ordinal);
        body = MaterialTextureDeclRegex.Replace(body, m =>
        {
            renamedTextures.Add(m.Groups["n"].Value);
            return $"{m.Groups["t"].Value} _{m.Groups["n"].Value} : register";
        });
        foreach (string name in renamedTextures)
        {
            string from = "Material_" + name;
            string to = "_" + name;
            body = System.Text.RegularExpressions.Regex.Replace(body, $@"\b{System.Text.RegularExpressions.Regex.Escape(from)}\b", to);
        }

        // Pass 3 — drop aliased ByteAddressBuffer redeclarations (same register
        // as the primary T<N>) and rewrite T<N>_<M> references to T<N>.
        body = AliasedByteAddressDeclRegex.Replace(body, string.Empty);
        body = AliasedByteAddressRefRegex.Replace(body, "T$1");

        return body;
    }

    // Variant key rules:
    //   Sub<N>_Pass<M>_<Stage>_<KeywordCombo>_b<BlobIndex>
    //
    // KeywordCombo is `_`-joined active keyword names. When no keywords
    // are set we use `DEFAULT` so the file path stays human-readable.
    // BlobIndex tiebreaks across distinct binaries that share the same
    // (subshader, pass, stage, keyword set) — happens when Unity emits
    // platform variants under one keyword combo. Without that tail we'd
    // get filename collisions and the dictionary write would lose data.
    private static string BuildVariantKey(int subShaderIndex, int passIndex, string stage, UnitySerializedSubProgram sp, List<string> keywordNames)
    {
        StringBuilder sb = new();
        sb.Append("Sub").Append(subShaderIndex);
        sb.Append("_Pass").Append(passIndex);
        sb.Append('_').Append(stage);
        sb.Append('_');
        if (sp.KeywordIndices.Count == 0)
        {
            sb.Append("DEFAULT");
        }
        else
        {
            // Sorted to keep filename stable across reorderings of
            // KeywordIndices that may differ between exporter runs.
            List<string> kws = new(sp.KeywordIndices.Count);
            foreach (ushort idx in sp.KeywordIndices)
            {
                kws.Add(BuildKeywordSymbol(keywordNames, idx));
            }
            kws.Sort(StringComparer.Ordinal);
            sb.Append(string.Join('_', kws));
        }
        sb.Append("_b").Append(sp.BlobIndex);
        return SanitizeFileStem(sb.ToString());
    }

    private static string BuildVariantFileContent(string stage, UnitySerializedSubProgram sp, string variantKey, string body)
    {
        StringBuilder sb = new();
        sb.AppendLine("// =============================================================");
        sb.AppendLine($"// Variant: {variantKey}");
        sb.AppendLine($"// Stage: {stage}");
        sb.AppendLine($"// Blob: {sp.BlobIndex}");
        sb.AppendLine($"// ParamBlob: {(sp.ParameterBlobIndex.HasValue ? sp.ParameterBlobIndex.Value.ToString() : "<none>")}");
        sb.AppendLine($"// Language: {sp.SourceLanguage}");
        sb.AppendLine("// =============================================================");
        sb.AppendLine();
        sb.Append(body);
        if (!body.EndsWith('\n')) sb.AppendLine();
        return sb.ToString();
    }

    // Filename-safe form of the variant key. Builds a stem that survives
    // both Windows and POSIX file-system rules; the variant key itself is
    // already alnum+underscore but we belt-and-braces strip path separators
    // and any other invalid char that future enums might inject.
    private static string SanitizeFileStem(string raw)
    {
        char[] invalid = System.IO.Path.GetInvalidFileNameChars();
        StringBuilder sb = new(raw.Length);
        foreach (char c in raw)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }
        return sb.ToString();
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

    private static List<string> BuildPassKeywordSymbols(List<string> keywordNames, UnitySerializedPass pass)
    {
        List<UnitySerializedSubProgram> all = new();
        foreach ((_, UnitySerializedProgram program) in pass.EnumerateProgramSlots())
        {
            all.AddRange(program.SubPrograms);
        }
        return CollectDistinctKeywordIndices(all)
            .Select(i => BuildKeywordSymbol(keywordNames, i))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static List<ushort> CollectDistinctKeywordIndices(List<UnitySerializedSubProgram> subPrograms)
    {
        List<ushort> result = [];
        HashSet<ushort> seen = [];
        foreach (UnitySerializedSubProgram sp in subPrograms)
        {
            foreach (ushort keywordIndex in sp.KeywordIndices)
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
