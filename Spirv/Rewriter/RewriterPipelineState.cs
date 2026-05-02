using Ruri.ShaderTools.Spirv.Rewriter.Models;

namespace Ruri.ShaderTools.Spirv.Rewriter;

// Mutable state that flows through the rewriter pipeline. Each pass reads what earlier
// passes filled in and writes its own outputs. Keeping it as one explicit object (rather
// than threaded parameters) makes the pass signatures uniform — every pass is just
// `DoPass(RewriterPipelineState state)` — and matches the AssetRipper SharedState pattern,
// scoped to a single shader rewrite instead of a process-wide singleton.
internal sealed class RewriterPipelineState
{
    public RewriterPipelineState(byte[] inputSpirv, ShaderSymbolData metadata)
    {
        InputSpirv = inputSpirv;
        Metadata = metadata;
    }

    // Inputs (immutable for the pipeline run).
    public byte[] InputSpirv { get; }
    public ShaderSymbolData Metadata { get; }

    // Diagnostic log accumulated by every pass. Surfaced through
    // `StructuredCBufferRewriter.LastRewriteSummary` and the per-shader `rewrite-summary.txt`
    // failure dump. Lines should describe the decision the pass made (e.g.
    // "[CB] rewrite planned with N members" / "...validation failed: <reason>").
    public List<string> Summary { get; } = new();

    // Pass010 outputs.
    public SpirvModule Module { get; set; } = null!;
    public ModuleAnalysis Analysis { get; set; } = null!;
    public ConstantMaps Constants { get; set; } = null!;
    public TypeInfo Types { get; set; } = null!;

    // Pass020 output.
    public List<FlatUniformBufferInfo> FlatBuffers { get; set; } = new();

    // Built progressively by passes 030..060. After Pass060 the list contains exactly the
    // CBs that survived layout / type / access-chain validation and will get their SPIR-V
    // mutated by the later passes.
    public List<BufferRewritePlan> Plans { get; set; } = new();

    // Pass080 outputs (consumed by Pass090 + Pass100):
    //   * RewrittenChains   — every OpAccessChain we rewrote (translated == direct
    //                         translation) or recorded for the composite-extract fallback
    //                         (translated == null, downstream extracts pick the member).
    //   * UniformPointerTypes — cache of `member-type-id → ptr-uniform-type-id` for the
    //                         new pointer types we either reused or just emitted.
    public Dictionary<uint, RewrittenAccessChainInfo> RewrittenChains { get; set; } = new();
    public Dictionary<uint, uint> UniformPointerTypes { get; set; } = new();

    // Pass090 outputs (consumed by Pass100):
    //   * TrackedLoads      — Loads consuming a rewritten access chain.
    //   * ProcessedBitcasts — Bitcasts that participated in a rewrite (Load → Bitcast →
    //                         CompositeExtract chains). After Pass090 their result is
    //                         dead and Pass100 NOPs them.
    public Dictionary<uint, RewrittenLoadInfo> TrackedLoads { get; set; } = new();
    public Dictionary<uint, SpirvInstruction> ProcessedBitcasts { get; set; } = new();

    // Output: name lookup for downstream patcher (set, binding) → resolved CB name.
    public Dictionary<(int Set, int Binding), string> ResolvedBufferNames { get; } = new();

    // Set true by Pass110 when at least one rewrite landed in the module.
    public bool RewriteApplied { get; set; }

    // Final SPIR-V bytes, populated by Pass110.
    public byte[]? OutputSpirv { get; set; }
}
