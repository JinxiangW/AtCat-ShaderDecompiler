namespace Ruri.ShaderTools.Spirv;

// Public output of `SpirvPatcher.AnalyzeBindingsDetailed`. One record per (set, binding)
// uniform variable in the SPIR-V module — what kind of descriptor it is, what struct type
// it backs (for cbuffers), and the byte offsets of its struct members. Consumers
// (ShaderDecompiler.BuildNamePatches / BuildMemberPatches) match this against the metadata
// to decide which OpName / OpMemberName to inject.
//
// `DescriptorType` is a string ("UniformBuffer", "Sampler", "SampledImage", "StorageBuffer",
// "Unknown") rather than an enum so the matching code can keep the simple string-key shape
// the existing call sites already use; the patcher's analysis builds it from the SPIR-V
// type chain (variable → pointer → struct/sampler/image).
public class SpirvBindingInfo
{
    public uint Id { get; set; }
    public int Set { get; set; }
    public int Binding { get; set; }
    public string? DescriptorType { get; set; }
    public uint? StructTypeId { get; set; }
    public int StructMemberCount { get; set; }
    public Dictionary<int, uint> MemberOffsets { get; set; } = new();
    public string? CurrentName { get; set; }
}
