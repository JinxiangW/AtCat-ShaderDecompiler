namespace Ruri.ShaderTools.Spirv.Rewriter.Models;

// Result of translating a flat access path through a structured layout: the index list to
// feed back into OpAccessChain plus the SPIR-V type id of the addressed member. `Indices`
// already includes the wrapper member index (i.e. the new struct's member id), the per-
// member array/column/component indices, and any dynamic-id passthroughs.
internal sealed class StructuredAccessTranslation
{
    public List<uint> Indices { get; set; } = new();
    public uint MemberTypeId { get; set; }
}
