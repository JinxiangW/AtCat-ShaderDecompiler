namespace Ruri.ShaderTools.Spirv.Rewriter.Models;

// Tracking record for a Load that consumes a rewritten access chain. We hold a direct
// reference to the SpirvInstruction (not its index) because Pass090 inserts new
// instructions while iterating — index-based caches go stale, but the class reference
// stays valid across List.Insert.
internal sealed class RewrittenLoadInfo
{
    public SpirvInstruction Instruction { get; set; } = null!;
    public uint ResultId { get; set; }
    public uint OriginalResultTypeId { get; set; }
    public bool HasCompositeExtractUsers { get; set; }
    public RewrittenAccessChainInfo AccessChain { get; set; } = null!;
}
