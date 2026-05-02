namespace Ruri.ShaderTools.Spirv.Rewriter.Models;

// State carried between Pass080 (rewrites the OpAccessChain) and Pass090 (rewrites
// downstream Load/Bitcast/CompositeExtract chains). The `Translation` is null for
// access chains that took the composite-extract fallback path — those don't translate to
// a single member directly but their downstream extracts do, so we keep the original path
// so Pass090 can fork it per-extract.
internal sealed class RewrittenAccessChainInfo
{
    public uint AccessChainResultId { get; set; }
    public uint BaseVariableId { get; set; }
    public ushort InstructionOpCode { get; set; }
    public BufferRewritePlan Plan { get; set; } = null!;
    public FlatAccessPath OriginalAccessPath { get; set; } = null!;
    public StructuredAccessTranslation? Translation { get; set; }
}
