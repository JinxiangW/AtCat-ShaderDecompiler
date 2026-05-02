namespace Ruri.ShaderTools.Spirv.Rewriter.Models;

// One metadata constant buffer paired with the SPIR-V variable it describes. Pass020
// finds these matches; later passes consume them.
//
// `ArrayLength` / `ArrayStride` reflect the SPIR-V flat array view — `cb.float4 _m0[N]` —
// and Pass031 uses them to validate the structured layout fits inside.
internal sealed class FlatUniformBufferInfo
{
    public uint VariableId { get; set; }
    public uint PointerTypeId { get; set; }
    public uint StructTypeId { get; set; }
    public uint ArrayTypeId { get; set; }
    public uint ElementTypeId { get; set; }
    public int ArrayLength { get; set; }
    public int ArrayStride { get; set; }
    public FlatResourceBinding Metadata { get; set; } = null!;
    public ConstantBuffer ConstantBuffer { get; set; } = null!;
}
