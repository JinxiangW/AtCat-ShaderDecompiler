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
    // Existing OpMemberName decorations from the input module, keyed by
    // member-index. Populated by the analysis pipeline so the symbol
    // patcher (which writes NEW OpMemberName entries) can read what the
    // cook / dxil-spirv had baked in and either preserve it (when our
    // metadata doesn't override) or override it (when seed naming wins).
    // Critical for cbuffers where the cook emitted duplicate member
    // names (e.g. two `AO ` parameters that sanitise to the same `AO_`
    // HLSL identifier) — without seeing the cook's existing decorations
    // we couldn't blanket-dedupe across them at patch time.
    public Dictionary<int, string> CurrentMemberNames { get; set; } = new();
    public string? CurrentName { get; set; }
}
