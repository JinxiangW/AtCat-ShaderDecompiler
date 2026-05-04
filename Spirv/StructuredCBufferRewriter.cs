using Ruri.ShaderTools.Spirv.Rewriter;
using Ruri.ShaderTools.Spirv.Rewriter.Passes;

namespace Ruri.ShaderTools.Spirv;

// Top-level orchestrator for the structured-cbuffer rewriter pipeline. Mirrors the
// AssetRipper.AssemblyDumper.Program style: each step is a self-contained `PassXXX_*` class
// with a `DoPass(state)` entry point, and this class strings them together and surfaces
// results back to the caller.
//
// Pipeline (read top-to-bottom):
//
//   Pass010 — parse SPV bytes, walk module to build analysis / constants / type caches
//   Pass020 — match every metadata constant buffer to a SPIR-V uniform variable
//   Pass030 — turn matched flat buffers into structured layouts (metadata-only, no SPV)
//   Pass040 — resolve member SPV type ids; ensure translation constants exist
//   Pass050 — validate every access chain can be rewritten (else drop the whole CB)
//   Pass060 — allocate new struct + pointer ids; record (set, binding) → name map
//   Pass070 — emit OpTypeStruct + OpTypePointer + decorations + OpName / OpMemberName
//   Pass080 — switch each variable's pointer type; rewrite OpAccessChains in place
//   Pass090 — replace Load/Bitcast/CompositeExtract chains with direct member loads
//   Pass100 — structural dead-code cleanup of obsolete Bitcasts/Loads/AccessChains
//   Pass110 — serialise the mutated module back to bytes
//
// Each pass reads from `RewriterPipelineState` and writes its own outputs there. Adding a
// new pass means: write a new file, drop a `DoPass(state)` call in the right slot below.
//
// Bug-fixing methodology lives in `SPIRV_DEBUG_PLAYBOOK.md` and refers to passes by their
// number — keep these in sync if you re-number anything.
internal sealed class StructuredCBufferRewriter
{
    private readonly Dictionary<(int Set, int Binding), string> _resolvedBufferNames = new();

    public bool LastRewriteApplied { get; private set; }
    public string LastRewriteSummary { get; private set; } = string.Empty;

    public string? GetResolvedBufferName(int set, int binding)
    {
        return _resolvedBufferNames.TryGetValue((set, binding), out string? name) ? name : null;
    }

    public byte[] Rewrite(byte[] spirv, SerializedProgramData metadata)
    {
        LastRewriteApplied = false;
        _resolvedBufferNames.Clear();

        var state = new RewriterPipelineState(spirv, metadata);

        Pass010_AnalyzeModule.DoPass(state);
        Pass020_MatchFlatBuffers.DoPass(state);
        if (state.FlatBuffers.Count == 0)
        {
            return Finalize(state, spirv, "No flat uniform buffers matched metadata bindings.");
        }

        Pass030_BuildLayouts.DoPass(state);
        Pass040_PrepareNewTypes.DoPass(state);
        Pass050_ValidateAccessChains.DoPass(state);
        Pass060_AllocateRewrites.DoPass(state);
        if (state.Plans.Count == 0)
        {
            return Finalize(state, spirv, "No rewrites planned.");
        }

        Pass070_InsertStructuredTypes.DoPass(state);
        Pass080_RewriteAccessChains.DoPass(state);
        Pass090_RewriteCompositeExtractChains.DoPass(state);
        Pass100_CleanupDeadCode.DoPass(state);
        Pass110_SerializeModule.DoPass(state);

        LastRewriteApplied = state.RewriteApplied;
        LastRewriteSummary = string.Join(Environment.NewLine, state.Summary);
        foreach (var kvp in state.ResolvedBufferNames)
        {
            _resolvedBufferNames[kvp.Key] = kvp.Value;
        }

        return state.OutputSpirv ?? spirv;
    }

    private byte[] Finalize(RewriterPipelineState state, byte[] inputSpirv, string emptySummaryFallback)
    {
        LastRewriteApplied = false;
        LastRewriteSummary = state.Summary.Count == 0
            ? emptySummaryFallback
            : string.Join(Environment.NewLine, state.Summary);
        foreach (var kvp in state.ResolvedBufferNames)
        {
            _resolvedBufferNames[kvp.Key] = kvp.Value;
        }
        return inputSpirv;
    }
}
