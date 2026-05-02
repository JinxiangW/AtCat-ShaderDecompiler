using Ruri.ShaderTools.Spirv.Patcher.Analysis;
using Ruri.ShaderTools.Spirv.Patcher.Patch;

namespace Ruri.ShaderTools.Spirv;

// Top-level orchestrator for SPIR-V symbol patching. Two independent sub-pipelines:
//
//   Analysis  (Patcher/Analysis):
//     Pass010 — scan SPV words, fill (set, binding) / type / name maps
//     Pass020 — turn the maps into a List<SpirvBindingInfo> sorted by (set, binding)
//     Used by callers (ShaderDecompiler.BuildNamePatches / BuildMemberPatches) to decide
//     which OpName / OpMemberName to inject.
//
//   Patch     (Patcher/Patch):
//     Pass010 — copy the input SPV, dropping any OpName / OpMemberName whose target is
//               about to be replaced (avoids dxil-spirv's machine names winning the name-
//               uniquify race in spirv-cross)
//     Pass020 — encode the (id, name) and (typeId, memberIndex, name) overrides as
//               ready-to-splice instruction word arrays
//     Pass030 — splice them into the debug section of the filtered SPV, return new bytes
//
// External callers see the same surface as before: AnalyzeBindingsDetailed / AnalyzeBindings
// for read-only queries, and PatchByIds for symbol injection. The legacy `Patch(spirv,
// symbols)` shim still works (matches existing test fixtures) and just orchestrates the
// pipelines internally.
public class SpirvPatcher
{
    public List<SpirvBindingInfo> AnalyzeBindingsDetailed(byte[] spirvBytes)
    {
        var state = new BindingAnalysisState(spirvBytes);
        Pass010_ScanModule.DoPass(state);
        Pass020_BuildBindingInfos.DoPass(state);
        return state.Bindings;
    }

    public Dictionary<(int Set, int Binding), uint> AnalyzeBindings(byte[] spirvBytes)
    {
        List<SpirvBindingInfo> detailed = AnalyzeBindingsDetailed(spirvBytes);
        var result = new Dictionary<(int Set, int Binding), uint>();
        foreach (SpirvBindingInfo b in detailed)
        {
            (int Set, int Binding) key = (b.Set, b.Binding);
            if (!result.ContainsKey(key))
            {
                result[key] = b.Id;
            }
        }
        return result;
    }

    public byte[] PatchByIds(
        byte[] spirvBytes,
        List<(uint Id, string Name)> names,
        List<(uint TypeId, uint MemberIndex, string Name)>? memberNames = null)
    {
        var state = new PatchPipelineState(spirvBytes, names, memberNames);

        Pass010_FilterReplacedNames.DoPass(state);
        Pass020_BuildNewInstructions.DoPass(state);
        Pass030_InsertAndSerialize.DoPass(state);

        return state.OutputSpirv ?? spirvBytes;
    }

    // Legacy convenience: combines analyse + match-by-byte-offset + patch in one call. Kept
    // because it's still referenced by some test paths; the modern pipeline calls the two
    // entry points above directly via the higher-level ShaderDecompiler.
    public byte[] Patch(byte[] spirvBytes, ShaderSymbolData symbols)
    {
        var memberNames = new List<(uint TypeId, uint MemberIndex, string Name)>();
        List<SpirvBindingInfo> detailed = AnalyzeBindingsDetailed(spirvBytes);

        foreach (BufferBinding resource in symbols.ConstantBufferBindings)
        {
            ConstantBuffer? constantBuffer = symbols.GetConstantBufferByName(resource.Name);
            if (constantBuffer == null)
            {
                continue;
            }

            int resourceSet = symbols.GetSetIdFor(resource.Index, ShaderResourceType.ConstantBuffer);
            SpirvBindingInfo? match = detailed.FirstOrDefault(b => b.Set == resourceSet && b.Binding == resource.Index && b.StructTypeId.HasValue);
            if (match?.StructTypeId == null)
            {
                continue;
            }

            CollectStructMemberNames(constantBuffer, match, memberNames);
            CollectScalarMemberNames(constantBuffer, match, memberNames);
        }

        return memberNames.Count > 0 ? PatchByIds(spirvBytes, [], memberNames) : spirvBytes;
    }

    private static void CollectStructMemberNames(ConstantBuffer constantBuffer, SpirvBindingInfo match, List<(uint, uint, string)> output)
    {
        foreach (StructParameter structParameter in constantBuffer.StructParams.Where(static s => !string.IsNullOrWhiteSpace(s.Name)))
        {
            if (TryFindMemberByByteOffset(match, structParameter.Index) is int targetIndex)
            {
                output.Add((match.StructTypeId!.Value, (uint)targetIndex, structParameter.Name));
            }
        }
    }

    private static void CollectScalarMemberNames(ConstantBuffer constantBuffer, SpirvBindingInfo match, List<(uint, uint, string)> output)
    {
        foreach (NumericShaderParameter parameter in constantBuffer.AllNumericParams.Where(static p => !string.IsNullOrWhiteSpace(p.Name)))
        {
            if (TryFindMemberByByteOffset(match, parameter.ByteOffset) is int targetIndex)
            {
                output.Add((match.StructTypeId!.Value, (uint)targetIndex, parameter.Name!));
            }
        }
    }

    private static int? TryFindMemberByByteOffset(SpirvBindingInfo match, int byteOffset)
    {
        if (byteOffset < 0 || match.MemberOffsets.Count == 0)
        {
            return null;
        }

        foreach (KeyValuePair<int, uint> kvp in match.MemberOffsets)
        {
            if (kvp.Value == (uint)byteOffset)
            {
                return kvp.Key;
            }
        }
        return null;
    }
}
