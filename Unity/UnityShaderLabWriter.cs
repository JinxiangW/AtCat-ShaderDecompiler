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
                // SubShader-level Tags inherit into every Pass per ShaderLab
                // semantics, so a Pass-level Tags entry whose key+value matches
                // the SubShader's is pure noise. Dedup before writing — Pass
                // emits only the entries it actually adds or overrides (key
                // present in SubShader with a different value). Same dedup is
                // applied to both `pass.State.Tags` (state-block tags) and
                // `pass.Tags` (extra non-state tags) since they share the
                // inheritance rule.
                WriteTags(sb, FilterRedundantTags(pass.State.Tags.Tags, subShader.Tags.Tags));
                if (pass.Tags.Tags.Count > 0)
                {
                    WriteTags(sb, FilterRedundantTags(pass.Tags.Tags, subShader.Tags.Tags));
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

    // Strip entries from `passTags` whose key+value already appears in
    // `subShaderTags`. Pass-level tags inherit from SubShader-level tags in
    // ShaderLab semantics, so a literal duplicate is dead text. An entry whose
    // key exists in SubShader with a *different* value still survives — that's
    // a real override (e.g. SubShader sets `"Queue"="Geometry"` and one Pass
    // overrides to `"Queue"="Transparent+10"`). Tag-key matching is case-
    // insensitive because Unity treats `"QUEUE"` and `"Queue"` as the same
    // tag.
    private static List<UnityTagMapEntry> FilterRedundantTags(
        List<UnityTagMapEntry> passTags,
        List<UnityTagMapEntry> subShaderTags)
    {
        if (passTags.Count == 0 || subShaderTags.Count == 0) return passTags;

        Dictionary<string, string> inheritedByKey = new(StringComparer.OrdinalIgnoreCase);
        foreach (UnityTagMapEntry t in subShaderTags)
        {
            inheritedByKey[t.First] = t.Second;
        }

        List<UnityTagMapEntry> result = new(passTags.Count);
        foreach (UnityTagMapEntry t in passTags)
        {
            if (inheritedByKey.TryGetValue(t.First, out string? inherited)
                && string.Equals(inherited, t.Second, StringComparison.Ordinal))
            {
                continue; // already in SubShader with same value — skip.
            }
            result.Add(t);
        }
        return result;
    }

    private static string FormatFloat(float value)
    {
        return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // Writes one `HLSLPROGRAM { … } ENDHLSL` block per pass. Each pass interleaves
    // every stage's variants in declaration order. Within a stage we emit *all*
    // SubPrograms (not just the first) so material assignment can pick the right
    // keyword combo at runtime; each variant body is wrapped in
    // `#if <keyword condition>` derived from its <c>KeywordIndices</c> against the
    // pass's keyword universe so exactly one variant per stage is visible per
    // compile permutation.
    //
    // Per-stage entry-point renaming (`main` → `vertMain` / `fragMain` / …) keeps
    // multiple `main` definitions from colliding inside the same translation unit
    // when both stages are textually present. The corresponding `#pragma vertex
    // vertMain` / `#pragma fragment fragMain` pin Unity's stage compile to the
    // right entry. Old design had `#ifdef SHADER_STAGE_*` wrapping each stage —
    // dropped, the entry-point rename is enough and the macro guards just
    // hid all-but-one variant of each stage.
    //
    // `#pragma multi_compile_local` is emitted from the union of keywords any
    // variant references, so Unity actually compiles the cross-product. Keywords
    // that appear in zero variants are omitted. With one variant total the pragma
    // is omitted entirely.
    //
    // When `ctx.VariantFolderStem` is set, every concrete subprogram body is
    // offloaded to a sibling `<stem>/<variantKey>.hlsl` file and the .shader gets
    // a single `#include "<stem>/<variantKey>.hlsl"` line per variant.
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
        // HLSLSupport.cginc is what defines `SHADER_STAGE_VERTEX/FRAGMENT/...`
        // (see Built-in pipeline source ~line 290). Without including it, Unity
        // does NOT set those macros for plain HLSLPROGRAM blocks, so any
        // `#ifdef SHADER_STAGE_*` guard hides every stage's code and the d3d11
        // compiler errors with "Did not find shader kernel 'main'". The file is
        // tiny (just preprocessor symbols + a few API helpers) and ships with
        // every Unity install, so the include cost is negligible.
        sb.AppendLine("#include \"HLSLSupport.cginc\"");

        foreach ((string stage, _) in pass.EnumerateProgramSlots())
        {
            if (TryGetStagePragma(stage, out string pragma))
            {
                sb.AppendLine($"{pragma} {GetStageEntryName(stage)}");
            }
        }

        // multi_compile_local: union of keywords any variant in any stage of this
        // pass references. Unity compiles each combination into a separate shader
        // permutation; the `#if defined(KEYWORD)` guards on each variant's body
        // ensure exactly one fires per permutation.
        List<string> passKeywords = BuildPassKeywordSymbols(keywordNames, pass);
        if (passKeywords.Count > 0)
        {
            // One `multi_compile_local` line per keyword: each is treated as a
            // toggle (on/off). Bundling them as `#pragma multi_compile_local A B C`
            // would make them mutually exclusive, which is wrong for unrelated
            // toggles.
            foreach (string kw in passKeywords)
            {
                sb.AppendLine($"#pragma multi_compile_local _ {kw}");
            }
        }

        sb.AppendLine(string.Empty);
        // Cross-stage isolation is handled by the `#ifdef SHADER_STAGE_*`
        // guard inside WriteStageSubPrograms (made reliable by the
        // `#include "HLSLSupport.cginc"` above). No cross-stage cbuffer or
        // resource deduplication is needed — each stage's compile pass only
        // sees its own block, and within a stage's `#if/#elif/#else` chain
        // exactly one variant's declarations are visible per permutation.
        foreach ((string stage, UnitySerializedProgram program) in pass.EnumerateProgramSlots())
        {
            WriteStageSubPrograms(sb, keywordNames, stage, program.SubPrograms, subShaderIndex, passIndex, ctx, passKeywords);
        }
        sb.AppendLine("ENDHLSL");
    }

    private static readonly System.Text.RegularExpressions.Regex CBufferBlockRegex =
        new(@"(?<indent>[ \t]*)cbuffer\s+(?<name>[A-Za-z_][\w]*)(?<header>\s*(?::\s*register\([^)]*\))?)\s*\{(?<body>[^}]*)\}\s*;?",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex ResourceDeclRegex =
        new(@"(?<indent>[ \t]*)(?<type>(?:Texture(?:1D|2D|3D|Cube)(?:Array)?|TextureCubeArray|RWTexture(?:1D|2D|3D)|Buffer|RWBuffer|StructuredBuffer|RWStructuredBuffer|ByteAddressBuffer|RWByteAddressBuffer|SamplerState|SamplerComparisonState)(?:<[^>]+>)?)\s+(?<name>[A-Za-z_][\w]*)\s*:\s*register\([^)]*\)\s*;",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // Collapse duplicate `cbuffer X { ... }` and `Texture/Sampler/Buffer Name :
    // register(...)` declarations to a single instance each — Unity's HLSL
    // compiler treats both stages of a Pass as one translation unit, so a
    // verbatim duplicate from spirv-cross's per-stage emit is a redefinition
    // error. We keep the first occurrence in declaration order; for cbuffers
    // we union the member lists so a stage that needs members the first stage
    // didn't reference still has access to them.
    private static string DeduplicateCrossStageDeclarations(string text)
    {
        // Resource decls (textures / samplers / buffers): hoist out of every
        // `#if` branch they currently sit in, dedup by name, and emit once at
        // the top of the HLSLPROGRAM block. Just dropping later occurrences
        // would leave the surviving declaration inside a branch that may not
        // be active for the current compile permutation, breaking other
        // variants that reference the resource. Hoisting puts them at file
        // scope where every variant sees them.
        var resourceOrder = new List<string>();
        var resourceDecls = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match m in ResourceDeclRegex.Matches(text))
        {
            string key = m.Groups["type"].Value + " " + m.Groups["name"].Value;
            if (resourceDecls.ContainsKey(key)) continue;
            resourceOrder.Add(key);
            // Strip leading indent so the hoisted decl uses the outer indent.
            resourceDecls[key] = m.Value.TrimStart();
        }
        text = ResourceDeclRegex.Replace(text, "");

        // cbuffers: build a member-union per name, then emit one merged block
        // at the first occurrence; subsequent matches drop entirely. We keep
        // the first occurrence's header (including register binding) so the
        // bind point doesn't change.
        var cbufferOrder = new List<string>();
        var cbufferHeaders = new Dictionary<string, string>(StringComparer.Ordinal);
        var cbufferIndents = new Dictionary<string, string>(StringComparer.Ordinal);
        var cbufferMembers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var cbufferSeenMembers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (System.Text.RegularExpressions.Match m in CBufferBlockRegex.Matches(text))
        {
            string name = m.Groups["name"].Value;
            if (!cbufferOrder.Contains(name))
            {
                cbufferOrder.Add(name);
                cbufferHeaders[name] = m.Groups["header"].Value;
                cbufferIndents[name] = m.Groups["indent"].Value;
                cbufferMembers[name] = new List<string>();
                cbufferSeenMembers[name] = new HashSet<string>(StringComparer.Ordinal);
            }
            // Members: split the body into trimmed declaration lines, dedup by
            // exact line content (different stages may emit the same member with
            // different packoffset/no packoffset — keep both as distinct).
            foreach (string raw in m.Groups["body"].Value.Split('\n'))
            {
                string trimmed = raw.TrimEnd();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;
                if (cbufferSeenMembers[name].Add(trimmed.Trim()))
                {
                    cbufferMembers[name].Add(trimmed);
                }
            }
        }

        // Strip every cbuffer occurrence from the body. We hoist the merged
        // declarations above the stage chains rather than keeping them inline
        // because the inline location of the *first* cbuffer might be inside
        // an `#if` branch — re-emitting the merged set there would scope all
        // the cbuffers to that branch's preprocessor condition and hide them
        // from the other branches' code.
        text = CBufferBlockRegex.Replace(text, "");

        // Assemble the hoisted block: deduplicated resources first (textures,
        // samplers, buffers) followed by the merged cbuffers. Both groups are
        // outdented to a 12-space common indent that matches the HLSLPROGRAM
        // block's body indentation in `WriteRawBlock` output. We prepend the
        // hoisted block to the existing text so every `#if` branch sees the
        // declarations at file scope.
        if (resourceOrder.Count == 0 && cbufferOrder.Count == 0) return text;

        System.Text.StringBuilder all = new();
        const string hoistIndent = "            ";
        foreach (string key in resourceOrder)
        {
            all.Append(hoistIndent).AppendLine(resourceDecls[key]);
        }
        foreach (string orderedName in cbufferOrder)
        {
            all.Append(hoistIndent).Append("cbuffer ").Append(orderedName).Append(cbufferHeaders[orderedName]).AppendLine().Append(hoistIndent).AppendLine("{");
            foreach (string member in cbufferMembers[orderedName])
            {
                all.AppendLine(member);
            }
            all.Append(hoistIndent).AppendLine("};");
        }
        return all.ToString() + text;
    }

    private static void WriteStageSubPrograms(IndentedStringBuilder sb, List<string> keywordNames, string stage, List<UnitySerializedSubProgram> subPrograms, int subShaderIndex, int passIndex, WriteContext ctx, List<string> passKeywords)
    {
        if (subPrograms.Count == 0)
        {
            return;
        }

        sb.AppendLine($"// ============================================================");
        sb.AppendLine($"// Stage: {stage}");
        sb.AppendLine($"// ============================================================");

        // OUTER guard: only the matching stage's compile sees this block.
        // Without this, the file-scope declarations spirv-cross emits per
        // stage (`static <type> <gl_Foo|TEXCOORD|…>;`,
        // `struct SPIRV_Cross_Input/Output`, `cbuffer SPIRV_Cross_VertexInfo`,
        // and the per-stage cbuffers spirv-cross dead-codes against each
        // stage's actual usage) collide. The `#include "HLSLSupport.cginc"`
        // emitted by WriteProgramsBlock makes SHADER_STAGE_* available; with
        // the include in place the guard cleanly hides wrong-stage decls
        // from each compile pass without needing the per-stage rename trick.
        //
        // INNER guards (per variant): inside the stage block we walk every
        // SubProgram and gate it on its `KeywordIndices` so only one variant
        // is active per (stage, keyword combination) compile permutation.
        // The pass-level `#pragma multi_compile_local _ <KW>` lines (emitted
        // by WriteProgramsBlock) tell Unity to actually iterate the cross-
        // product; an `#if/#elif/#else` chain (closed below) ensures every
        // permutation hits a body so `Did not find shader kernel 'main'`
        // never fires.
        string? stageMacro = GetShaderStageMacro(stage);
        if (stageMacro != null)
        {
            sb.AppendLine($"#ifdef {stageMacro}");
        }

        // Pre-compute the keyword universe relevant to *this* stage so the per-
        // variant condition only mentions keywords this stage actually uses.
        List<ushort> stageKeywordIndices = CollectDistinctKeywordIndices(subPrograms);

        // Deduplicate variants by (keyword combo): SubPrograms with identical
        // KeywordIndices but different blob indices are platform variants of the
        // same logical compile. Pick the first-listed one.
        var grouped = subPrograms
            .GroupBy(sp => string.Join(",", sp.KeywordIndices.OrderBy(i => i)))
            .ToList();

        // Single-variant fast path — no condition chain, no `#if/#endif`.
        if (grouped.Count <= 1)
        {
            UnitySerializedSubProgram only = grouped[0].First();
            sb.AppendLine($"// Stage: {stage}, Blob: {only.BlobIndex}, ParamBlob: {(only.ParameterBlobIndex.HasValue ? only.ParameterBlobIndex.Value.ToString() : "<none>")}, Language: {only.SourceLanguage}");
            WriteSubProgramBody(sb, stage, only, keywordNames, subShaderIndex, passIndex, ctx, effectiveSplit: false);
            sb.AppendLine(string.Empty);
            if (stageMacro != null)
            {
                sb.AppendLine("#endif");
                sb.AppendLine(string.Empty);
            }
            return;
        }

        // Multi-variant: emit an `#if/#elif/.../#else/#endif` chain. Every
        // permutation Unity generates from `multi_compile_local _ KW` lands in
        // exactly one branch — the matching variant if one exists, otherwise
        // the catch-all `#else` body so vertex/fragment compile always finds a
        // `main` (Unity errors out with "Did not find shader kernel 'main'"
        // otherwise). Pick the catch-all variant in priority order:
        //   1. The variant with empty KeywordIndices (the original game's
        //      "no-keyword" state).
        //   2. Otherwise the first-listed variant — it's the highest blob
        //      index, usually the closest thing to a default the game ships.
        // The catch-all body is functionally a fallback; runtime keyword state
        // determines which branch Unity actually executes, so picking a
        // suboptimal default only affects unused permutations.
        int defaultIdx = grouped.FindIndex(g => g.First().KeywordIndices.Count == 0);
        if (defaultIdx < 0) defaultIdx = 0;

        // Order: every non-default variant first as `#if`/`#elif`, default
        // last as `#else`. This guarantees every permutation matches a body.
        List<int> emitOrder = new();
        for (int i = 0; i < grouped.Count; i++) if (i != defaultIdx) emitOrder.Add(i);
        emitOrder.Add(defaultIdx);

        for (int oi = 0; oi < emitOrder.Count; oi++)
        {
            var group = grouped[emitOrder[oi]];
            UnitySerializedSubProgram primary = group.First();
            bool isLast = oi == emitOrder.Count - 1;

            string directive;
            if (oi == 0)
            {
                directive = "#if " + (BuildKeywordCondition(keywordNames, stageKeywordIndices, primary.KeywordIndices.ToList()) ?? "1");
            }
            else if (isLast)
            {
                directive = "#else";
            }
            else
            {
                directive = "#elif " + (BuildKeywordCondition(keywordNames, stageKeywordIndices, primary.KeywordIndices.ToList()) ?? "1");
            }
            sb.AppendLine(directive);

            sb.AppendLine($"// Stage: {stage}, Blob: {primary.BlobIndex}, ParamBlob: {(primary.ParameterBlobIndex.HasValue ? primary.ParameterBlobIndex.Value.ToString() : "<none>")}, Language: {primary.SourceLanguage}");
            if (group.Count() > 1)
            {
                sb.AppendLine($"// Note: {group.Count() - 1} additional platform variant(s) collapsed into this combo.");
            }
            if (isLast)
            {
                sb.AppendLine("// Catch-all variant (no other branch matched).");
            }

            // Force per-variant include splitting when there's more than one
            // variant: keeping all bodies inline blows up the .shader file fast
            // and makes diffs impossible.
            bool effectiveSplit = !string.IsNullOrEmpty(ctx.VariantFolderStem);
            WriteSubProgramBody(sb, stage, primary, keywordNames, subShaderIndex, passIndex, ctx, effectiveSplit);
        }
        sb.AppendLine("#endif");
        if (stageMacro != null)
        {
            sb.AppendLine("#endif");
        }
        sb.AppendLine(string.Empty);
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

    // Every stage's `main` entry stays named `main`. With `SHADER_STAGE_*`
    // guards (made reliable via the HLSLSupport.cginc include) wrapping each
    // stage block, only one stage's `main` is visible per compile permutation
    // so there's nothing to disambiguate. Unity's d3d11 compiler also chokes
    // on some non-`main` entry names ("Did not find shader kernel
    // 'fragMain'") in plain HLSLPROGRAM blocks, so the rename was a net loss.
    private static string GetStageEntryName(string stage) => "main";

    private static void WriteSubProgramBody(IndentedStringBuilder sb, string stage, UnitySerializedSubProgram sp, List<string> keywordNames, int subShaderIndex, int passIndex, WriteContext ctx, bool effectiveSplit)
    {
        if (effectiveSplit && sp.Success && !string.IsNullOrWhiteSpace(sp.SourceCode))
        {
            // Name the file by (subshader, pass, stage, blob) only — short and
            // bounded — so the path never trips Windows' 255-char filename /
            // 260-char MAX_PATH limit. The keyword combo used to be baked into
            // the name and ran to hundreds of chars on heavily multi_compile'd
            // shaders (e.g. the CharacterNPR fragment), crashing the export with
            // ERROR_INVALID_NAME. It now lives in the file's `// Keywords:`
            // header, so a plain-text search for a keyword still finds the file.
            string ext = string.IsNullOrWhiteSpace(sp.SourceFileExtension) ? ".hlsl" : sp.SourceFileExtension;
            string fileName = ResolveUniqueVariantFileName(ctx, BuildVariantKey(subShaderIndex, passIndex, stage, sp), ext);
            string includePath = $"{ctx.VariantFolderStem}/{fileName}";
            string keywordList = BuildVariantKeywordList(sp, keywordNames);
            ctx.VariantFiles[fileName] = BuildVariantFileContent(stage, sp, System.IO.Path.GetFileNameWithoutExtension(fileName), keywordList, AdaptHlslForUnity(TrimTrailingWhitespace(sp.SourceCode!), stage));
            sb.AppendLine($"#include \"{includePath}\"");
            return;
        }

        if (sp.Success && !string.IsNullOrWhiteSpace(sp.SourceCode))
        {
            WriteRawBlock(sb, AdaptHlslForUnity(TrimTrailingWhitespace(sp.SourceCode!), stage));
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

    // spirv-cross emits the stage entry as `<ReturnType> main(<Args>)` (no
    // attribute, no `static`). Match the function header so we only rename the
    // entry, not any nested `main` reference inside the body. The `lhs` group
    // holds everything up to and including the whitespace before `main`, the
    // `rhs` group is the trailing `(`, both are kept verbatim.
    private static readonly System.Text.RegularExpressions.Regex MainEntryDeclRegex =
        new(@"(?<lhs>(?:^|\n)\s*(?:[A-Za-z_][A-Za-z0-9_<>]*\s+)+)main(?<rhs>\s*\()",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // File-scope `static <type> <name>;` declaration. Captures the bare name in
    // group `n`; the type prefix can include vector / matrix template syntax so
    // we accept any `[A-Za-z_][\w<>]*` plus optional whitespace.
    private static readonly System.Text.RegularExpressions.Regex StaticGlobalDeclRegex =
        new(@"(?<lhs>^|\n)(?<indent>\s*)static\s+(?<type>[A-Za-z_][\w<>]*(?:\s*[A-Za-z_][\w<>]*)*)\s+(?<n>[A-Za-z_][A-Za-z0-9_]*)\s*;",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // Renames spirv-cross-generated identifiers that would collide if vertex
    // and fragment stages share a translation unit. Touches:
    //   - `main` entry → `<stage>Main`
    //   - `SPIRV_Cross_Input` / `SPIRV_Cross_Output` → +`<suffix>`
    //   - `cbuffer SPIRV_Cross_VertexInfo` body and member usage → +`<suffix>`
    //   - File-scope `static <T> <name>;` decls → +`<suffix>` (and every body
    //     reference). User-facing names (textures, samplers, cbuffer members
    //     written by symbol injection) are skipped — the static-decl pass only
    //     adds names it actually finds at file scope, and the rest of the
    //     identifier universe (textures, samplers, cbuffer keywords) is fixed
    //     by the explicit set below.
    private static string RenameStageScopedIdentifiers(string body, string entryName, string suffix)
    {
        if (string.IsNullOrEmpty(suffix)) return body;

        // 1. Entry rename: `... main(` → `... <entryName>(`.
        if (entryName != "main")
        {
            body = MainEntryDeclRegex.Replace(body, m => m.Groups["lhs"].Value + entryName + m.Groups["rhs"].Value);
        }

        // 2. Collect file-scope static names — only ones actually declared get
        // rewritten so we don't touch identical-named cbuffer fields (e.g.
        // `static float2 TEXCOORD;` becomes a rename target, but a cbuffer's
        // `float2 TEXCOORD : packoffset(c0);` is left alone since it's not a
        // static decl). The rename target set is body-scoped so two stages
        // never see each other's targets.
        HashSet<string> renameTargets = new(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match m in StaticGlobalDeclRegex.Matches(body))
        {
            renameTargets.Add(m.Groups["n"].Value);
        }

        // 3. Always-renamed identifiers that spirv-cross emits with a fixed
        // name across stages and that participate in the collision set. Add
        // these to the rename set unconditionally — if they aren't present in
        // the body, the regex below just won't fire.
        foreach (string fixedName in s_spirvCrossFixedIdentifiers)
        {
            renameTargets.Add(fixedName);
        }

        if (renameTargets.Count == 0) return body;

        // 4. Word-boundary rewrite of every reference. We compile a single
        // alternation so the scan is one pass; to keep the regex from matching
        // identifiers nested inside larger ones (`TEXCOORD_2` shouldn't match
        // when we want `TEXCOORD`) we frame each name in `\b…\b`.
        string pattern = @"\b(" + string.Join("|", renameTargets.Select(System.Text.RegularExpressions.Regex.Escape)) + @")\b";
        var renameRegex = new System.Text.RegularExpressions.Regex(pattern,
            System.Text.RegularExpressions.RegexOptions.Compiled);
        body = renameRegex.Replace(body, m => m.Value + suffix);

        return body;
    }

    // spirv-cross-generated identifiers that always collide between stages and
    // are present in *every* shader's HLSL output (or at least never refer to a
    // user name). Anything user-facing (textures/samplers/cbuffer members fed
    // by symbol injection) stays out of this list.
    private static readonly string[] s_spirvCrossFixedIdentifiers =
    {
        "SPIRV_Cross_Input",
        "SPIRV_Cross_Output",
        "SPIRV_Cross_VertexInfo",
        "SPIRV_Cross_BaseVertex",
        "SPIRV_Cross_BaseInstance",
        "stage_input",
        "stage_output",
    };

    // Backwards-compat overload — older callers / tests pass no stage. Defaults to
    // leaving `main` untouched (legacy single-stage behaviour).
    public static string AdaptHlslForUnity(string body) => AdaptHlslForUnity(body, stage: null);

    public static string AdaptHlslForUnity(string body, string? stage)
    {
        if (string.IsNullOrEmpty(body))
        {
            return body;
        }

        // No per-stage identifier rename is needed: stage isolation is
        // achieved via the `#ifdef SHADER_STAGE_*` guard around each stage
        // block in WriteStageSubPrograms. With `HLSLSupport.cginc` included
        // at the HLSLPROGRAM top, those macros are reliably set per stage
        // compile, so spirv-cross's `SPIRV_Cross_Input/Output` / file-scope
        // statics / etc never appear in the wrong stage's TU.
        //
        // Past attempts at per-stage suffixing (`_v` / `_f` / …) caused
        // cross-variant cbuffer member redefinitions when we then tried to
        // hoist+merge cbuffers across stages — different variants legitimately
        // declare the same cbuffer member at different packoffsets, and the
        // merged set kept both with conflicting offsets. Reverting to plain
        // SHADER_STAGE_* gating sidesteps the whole class of issues.

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

        // Pass 4 — deduplicate TEXCOORDN semantics inside SPIRV_Cross_Input /
        // SPIRV_Cross_Output struct blocks. spirv-cross's HLSL backend emits
        // separate `: TEXCOORDN` slots for each SPIR-V Output variable that
        // shares a Location with a different Component offset (e.g. a packed
        // `vec4` written via two `float2` writes). Unity's d3d11 compiler
        // rejects "Semantic 'TEXCOORD' overlap at N" — vertex output / fragment
        // input semantics must be unique.
        //
        // We renumber duplicates to the next-unused TEXCOORDN slot, walking
        // fields in declaration order. Because vertex output and fragment input
        // appear as separate spirv-cross emissions but agree on the duplicate
        // pattern (same Location reused at the same struct position by both
        // ends — spirv-cross is deterministic), the renumbering is consistent
        // across stages without needing cross-stage state.
        body = DeduplicateInterstageSemantics(body);

        // Pass 5 (vertex stage only) — rewrite vertex-input semantics so Unity's
        // mesh binding actually finds the right stream. spirv-cross emits every
        // SPIR-V Input as `<Name> : TEXCOORD<Location>`, e.g.
        //   float4 POSITION : TEXCOORD0;   <-- WRONG, Unity binds the first UV
        //                                       stream into the position input
        //   float3 NORMAL   : TEXCOORD2;   <-- WRONG
        //   float4 TANGENT  : TEXCOORD3;   <-- WRONG
        //   float4 COLOR    : TEXCOORD4;   <-- WRONG
        //   float2 TEXCOORD : TEXCOORD1;
        //   float4 TEXCOORD_3 : TEXCOORD7;
        // Unity's MeshRenderer feeds vertex attributes by *semantic name*, not
        // by location index. With the above, the position stream lands at the
        // TEXCOORD0 input slot — the shader renders complete garbage.
        //
        // The field name is already the original SPIR-V variable name (HLSL/
        // GLSL author named it `POSITION` / `NORMAL` / `TEXCOORD_<N>` etc.), so
        // we map field name → matching Unity vertex semantic and overwrite the
        // semantic only.
        if (string.Equals(stage, "Vertex", StringComparison.Ordinal))
        {
            body = FixVertexInputSemantics(body);
        }

        // Pass 6 — strip the cbuffer name prefix off its members. Our SPIR-V
        // symbol patcher names the cbuffer's struct type as `type.<Name>` and
        // the variable as `<Name>`; spirv-cross then prepends the variable
        // name onto every member during its uniquify pass, producing
        //   cbuffer UnityPerMaterial { float UnityPerMaterial_Color; … }
        // when the metadata supplied just `_Color`. Unity's SRP Batcher /
        // material-property binding matches cbuffer member names against
        // property block names verbatim, so `UnityPerMaterial_Color` never
        // receives the material's `_Color` value at runtime — the shader
        // compiles but renders with uninitialised material data.
        //
        // Strip the prefix so the member ends up at its original property
        // name (`_Color` / `_MainTex_ST` / `_Time` etc.). We only strip when
        // the cbuffer member literally starts with `<cbufferName>_` so we
        // don't accidentally truncate genuine identifiers.
        body = StripCbufferMemberPrefix(body);

        return body;
    }

    // Strip the cbuffer-name prefix from every member declaration AND every
    // body reference inside each `cbuffer Foo { … }` block (and any non-Foo
    // reference too, since the body uses the prefixed form everywhere).
    //
    //   cbuffer UnityPerMaterial : register(b0) {
    //       float UnityPerMaterial_Color;       →  float _Color;
    //       float4 UnityPerMaterial_MainTex_ST; →  float4 _MainTex_ST;
    //   };
    //   // body:
    //   sampled.rgb * UnityPerMaterial_Color    →  sampled.rgb * _Color
    //
    // The prefix shows up because the SPIR-V symbol patcher tags the cbuffer
    // struct as `type.<Name>` and the variable as `<Name>`; spirv-cross's
    // uniquify pass concatenates the variable name with each member to keep
    // it globally unique inside the TU. Unity's SRP Batcher binds material
    // properties by *member name match*, so the prefixed form silently breaks
    // every shader that consumes material properties.
    private static readonly System.Text.RegularExpressions.Regex CbufferDeclRegex =
        new(@"cbuffer\s+(?<name>[A-Za-z_][\w]*)\s*(?::\s*register\([^)]*\))?\s*\{(?<body>[^}]*)\}",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string StripCbufferMemberPrefix(string body)
    {
        // Collect the (cbufferName) → (memberNames) map by walking each cbuffer
        // declaration's body once. Members that don't carry the cbuffer name as
        // a prefix stay untouched (some Unity SRP cbuffers contain pre-stripped
        // names already).
        //
        // The cbuffer-declaration name often appears as `type_<Foo>` after
        // spirv-cross sanitises our `type.<Foo>` struct alias; the member
        // prefix is the *variable* name (`<Foo>`) not the struct-type alias.
        // Try both forms when looking for the prefix.
        Dictionary<string, List<string>> renamePairs = new();
        foreach (System.Text.RegularExpressions.Match m in CbufferDeclRegex.Matches(body))
        {
            string cbName = m.Groups["name"].Value;
            string cbBody = m.Groups["body"].Value;
            string varName = cbName.StartsWith("type_", StringComparison.Ordinal)
                ? cbName.Substring(5)
                : cbName;
            string prefix = varName + "_";
            foreach (string line in cbBody.Split('\n'))
            {
                // Each member line: `<type> <name>[<array>] : packoffset(...);`
                // We just need the identifier after the type tokens.
                var memberMatch = System.Text.RegularExpressions.Regex.Match(line,
                    @"\b(?<n>" + System.Text.RegularExpressions.Regex.Escape(prefix) + @"\w+)\b");
                if (!memberMatch.Success) continue;
                string name = memberMatch.Groups["n"].Value;
                if (!renamePairs.TryGetValue(cbName, out var list))
                {
                    list = new();
                    renamePairs[cbName] = list;
                }
                if (!list.Contains(name)) list.Add(name);
            }
        }
        if (renamePairs.Count == 0) return body;

        // Apply rewrites: for each prefixed name, rewrite ALL occurrences in
        // the body to the unprefixed form. We use a single alternation regex
        // so we don't accidentally rewrite the same site twice; word
        // boundaries keep nested identifiers safe (`UnityPerMaterial_Color2`
        // doesn't shorten to `_Color2` when we meant to strip `_Color`).
        List<string> allNames = renamePairs.Values.SelectMany(l => l).Distinct().ToList();
        // Longest-first so prefixes don't shadow longer names with the same root.
        allNames.Sort((a, b) => b.Length.CompareTo(a.Length));

        var sb = new System.Text.StringBuilder(body.Length);
        var pattern = new System.Text.RegularExpressions.Regex(
            @"\b(" + string.Join("|", allNames.Select(System.Text.RegularExpressions.Regex.Escape)) + @")\b",
            System.Text.RegularExpressions.RegexOptions.Compiled);
        body = pattern.Replace(body, m =>
        {
            string s = m.Value;
            int us = s.IndexOf('_');
            return us >= 0 ? s.Substring(us) : s;
        });
        return body;
    }

    // Map each spirv-cross input field name to its Unity-canonical vertex
    // semantic. Unrecognised names are left as TEXCOORD<location> — those are
    // typically game-specific instance attributes that the original GLSL/HLSL
    // author already named after their stream slot, so the existing semantic is
    // already correct.
    private static string FixVertexInputSemantics(string body)
    {
        return SpirvCrossStructRegex.Replace(body, m =>
        {
            // Only the *Input* struct of the vertex stage carries mesh-stream
            // bindings; SPIRV_Cross_Output's semantics are interstage and stay
            // as TEXCOORD<n> so vertex output and fragment input agree.
            if (m.Groups[1].Value != "Input") return m.Value;

            string structHeader = m.Value.Substring(0, m.Value.IndexOf('{') + 1);
            string structBody = m.Groups["body"].Value;
            string rewritten = StructFieldSemanticRegex.Replace(structBody, fm =>
            {
                string fieldName = fm.Groups["lhs"].Value.Trim();
                string? canon = MapVertexInputSemantic(fieldName);
                if (canon == null) return fm.Value;
                return $"{fm.Groups["lhs"].Value} : {canon}{fm.Groups["tail"].Value}";
            });
            return structHeader + rewritten + "}";
        });
    }

    // Returns the canonical Unity vertex-input semantic for a spirv-cross-
    // emitted field name, or null if the field's name doesn't map to a
    // standard slot (in which case we leave the semantic alone).
    private static string? MapVertexInputSemantic(string fieldName)
    {
        // Strip whitespace and any leading type tokens caught by the loose
        // `\b\w[\w\s]*\b` lhs match — only the trailing identifier matters.
        int lastSpace = fieldName.LastIndexOf(' ');
        string ident = lastSpace >= 0 ? fieldName.Substring(lastSpace + 1) : fieldName;

        // spirv-cross conventions:
        //   POSITION         → POSITION0
        //   NORMAL           → NORMAL0
        //   TANGENT          → TANGENT0
        //   COLOR            → COLOR0
        //   TEXCOORD         → TEXCOORD0   (first uv)
        //   TEXCOORD_<N>     → TEXCOORD<N> (additional uvs / generic streams)
        //   BLENDINDICES, BLENDWEIGHT — pass through with index 0
        //   PSIZE — same
        // Anything else (game-custom instance attributes) returns null — the
        // existing TEXCOORD<location> semantic is what the asset bundle's mesh
        // already streams, so leave it untouched.
        switch (ident)
        {
            case "POSITION": return "POSITION0";
            case "NORMAL": return "NORMAL0";
            case "TANGENT": return "TANGENT0";
            case "COLOR": return "COLOR0";
            case "TEXCOORD": return "TEXCOORD0";
            case "BLENDINDICES": return "BLENDINDICES0";
            case "BLENDWEIGHT": return "BLENDWEIGHT0";
            case "PSIZE": return "PSIZE0";
        }
        if (ident.StartsWith("TEXCOORD_", StringComparison.Ordinal)
            && int.TryParse(ident.AsSpan(9), out int n))
        {
            return "TEXCOORD" + n;
        }
        if (ident.StartsWith("COLOR_", StringComparison.Ordinal)
            && int.TryParse(ident.AsSpan(6), out int cn))
        {
            return "COLOR" + cn;
        }
        return null;
    }

    private static readonly System.Text.RegularExpressions.Regex SpirvCrossStructRegex =
        new(@"struct\s+SPIRV_Cross_(Input|Output)\s*\{(?<body>[^}]*)\}",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // Match `<field> : <SEMANTIC>;` within a struct body. The semantic is captured
    // verbatim (including any trailing index digits) so we can decide whether to
    // renumber it.
    private static readonly System.Text.RegularExpressions.Regex StructFieldSemanticRegex =
        new(@"(?<lhs>\b\w[\w\s]*\b)\s*:\s*(?<sem>TEXCOORD\d+)\s*(?<tail>;)",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string DeduplicateInterstageSemantics(string body)
    {
        return SpirvCrossStructRegex.Replace(body, structMatch =>
        {
            string structHeader = structMatch.Value.Substring(0, structMatch.Value.IndexOf('{') + 1);
            string structBody = structMatch.Groups["body"].Value;

            HashSet<int> usedSlots = new();
            // First pass: collect declared indices to know which slots are taken.
            foreach (System.Text.RegularExpressions.Match m in StructFieldSemanticRegex.Matches(structBody))
            {
                if (TryParseTexcoordIndex(m.Groups["sem"].Value, out int idx))
                {
                    usedSlots.Add(idx);
                }
            }

            // Second pass: walk declaration order, when we see a duplicate, hand it the
            // smallest unused index. We mutate a working `seen` set so subsequent
            // duplicates don't all get the same fresh index.
            HashSet<int> seen = new();
            string rewritten = StructFieldSemanticRegex.Replace(structBody, m =>
            {
                string sem = m.Groups["sem"].Value;
                if (!TryParseTexcoordIndex(sem, out int idx)) return m.Value;
                if (seen.Add(idx)) return m.Value; // first occurrence — keep
                int free = 0;
                while (usedSlots.Contains(free) || seen.Contains(free)) free++;
                seen.Add(free);
                usedSlots.Add(free);
                return $"{m.Groups["lhs"].Value} : TEXCOORD{free}{m.Groups["tail"].Value}";
            });

            return structHeader + rewritten + "}";
        });
    }

    private static bool TryParseTexcoordIndex(string semantic, out int index)
    {
        index = 0;
        if (!semantic.StartsWith("TEXCOORD", StringComparison.Ordinal)) return false;
        return int.TryParse(semantic.AsSpan(8), out index);
    }

    // Variant *file-name* stem. Deliberately short and bounded:
    //   Sub<N>_Pass<M>_<Stage>_b<BlobIndex>
    //
    // The keyword combo is intentionally NOT in the name. On shaders with many
    // multi_compile toggles it ran to hundreds of chars and pushed the path past
    // Windows' 255-char filename limit, crashing the export with
    // ERROR_INVALID_NAME ("文件名、目录名或卷标语法不正确"). The full combo is
    // recorded as a `// Keywords:` comment inside the file (see
    // BuildVariantFileContent) so it stays searchable.
    //
    // (Subshader, pass, stage, blob) uniquely identifies an emitted variant in
    // the common case. Every variant file for one shader shares a single folder,
    // so subshader+pass+stage must stay in the name to avoid cross-pass clashes;
    // BlobIndex separates the distinct binaries within one (sub, pass, stage).
    // On the rare blob collision (two keyword combos Unity deduped onto one
    // bytecode blob) ResolveUniqueVariantFileName appends a numeric tail.
    private static string BuildVariantKey(int subShaderIndex, int passIndex, string stage, UnitySerializedSubProgram sp)
    {
        StringBuilder sb = new();
        sb.Append("Sub").Append(subShaderIndex);
        sb.Append("_Pass").Append(passIndex);
        sb.Append('_').Append(stage);
        sb.Append("_b").Append(sp.BlobIndex);
        return SanitizeFileStem(sb.ToString());
    }

    // Resolve a collision-free file name for a variant. The (subshader, pass,
    // stage, blob) stem is unique per emitted variant in almost every shader;
    // the only way two variants land on the same stem is when Unity deduped two
    // distinct keyword combos onto one bytecode blob. In that case append `_v2`,
    // `_v3`, … so neither body is silently dropped from the VariantFiles map
    // (a dictionary keyed by file name). Single-threaded writer, so the
    // check-then-add below is race-free.
    private static string ResolveUniqueVariantFileName(WriteContext ctx, string stem, string ext)
    {
        string candidate = stem + ext;
        if (!ctx.VariantFiles.ContainsKey(candidate))
        {
            return candidate;
        }
        for (int n = 2; ; n++)
        {
            candidate = $"{stem}_v{n}{ext}";
            if (!ctx.VariantFiles.ContainsKey(candidate))
            {
                return candidate;
            }
        }
    }

    private static string BuildVariantFileContent(string stage, UnitySerializedSubProgram sp, string variantKey, string keywordList, string body)
    {
        StringBuilder sb = new();
        sb.AppendLine("// =============================================================");
        sb.AppendLine($"// Variant: {variantKey}");
        sb.AppendLine($"// Stage: {stage}");
        sb.AppendLine($"// Blob: {sp.BlobIndex}");
        sb.AppendLine($"// ParamBlob: {(sp.ParameterBlobIndex.HasValue ? sp.ParameterBlobIndex.Value.ToString() : "<none>")}");
        sb.AppendLine($"// Language: {sp.SourceLanguage}");
        // The full keyword combo lives here, not in the file name, so the
        // on-disk name stays short while a plain-text search for any keyword
        // still lands on this variant.
        sb.AppendLine($"// Keywords: {keywordList}");
        sb.AppendLine("// =============================================================");
        sb.AppendLine();
        sb.Append(body);
        if (!body.EndsWith('\n')) sb.AppendLine();
        return sb.ToString();
    }

    // Space-joined active keyword symbols for the in-file `// Keywords:` header.
    // Each symbol is a standalone, plain-text-searchable token; sorted for stable
    // diffs across exporter runs. An empty keyword set is the default / catch-all
    // variant and renders as a sentinel.
    private static string BuildVariantKeywordList(UnitySerializedSubProgram sp, List<string> keywordNames)
    {
        if (sp.KeywordIndices.Count == 0)
        {
            return "<none> (default / catch-all variant)";
        }
        List<string> kws = new(sp.KeywordIndices.Count);
        foreach (ushort idx in sp.KeywordIndices)
        {
            kws.Add(BuildKeywordSymbol(keywordNames, idx));
        }
        kws.Sort(StringComparer.Ordinal);
        return string.Join(' ', kws);
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
