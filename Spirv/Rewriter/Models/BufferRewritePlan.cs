namespace Ruri.ShaderTools.Spirv.Rewriter.Models;

// One planned cbuffer rewrite. Built up across passes 030..060: layout from metadata,
// resolved member SPV type ids, then freshly-allocated struct + pointer type ids.
// The actual mutation of OpVariable / OpAccessChain happens in passes 070..100 against
// these plans.
internal sealed class BufferRewritePlan
{
    public FlatUniformBufferInfo Info { get; set; } = null!;
    public StructuredBufferLayout Layout { get; set; } = null!;
    public uint NewStructTypeId { get; set; }
    public uint NewPointerTypeId { get; set; }
    public List<uint> MemberTypeIds { get; set; } = new();
}
