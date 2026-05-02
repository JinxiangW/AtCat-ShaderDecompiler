namespace Ruri.ShaderTools.Spirv.Rewriter.Models;

// Decomposed form of an OpAccessChain index expression. DXC frequently emits indices like
// `(instanceId << 4) | constantOffset` for instancing access; we split them into
// `dynamicIndexId * dynamicIndexStride + constantRegisterOffset` so the layout-aware
// translator can recognise dynamic-array members independently of the literal SPIR-V form.
internal sealed class SlotExpression
{
    public int ConstantRegisterOffset { get; set; }
    public uint DynamicIndexId { get; set; }
    public int DynamicIndexStride { get; set; }
}
