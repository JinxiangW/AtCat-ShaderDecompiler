namespace Ruri.ShaderTools.Spirv.Patcher.Analysis;

// Mutable state passed through the binding-analysis sub-pipeline. Pass010 fills the maps,
// Pass020 reads them to assemble the final SpirvBindingInfo list. Kept separate from the
// rewriter's RewriterPipelineState because the patcher's analysis is independent — it
// runs both before patching (so callers can build their name lists) and after the
// rewriter (so the symbol injector sees the post-rewrite struct shape).
internal sealed class BindingAnalysisState
{
    public BindingAnalysisState(byte[] inputSpirv)
    {
        InputSpirv = inputSpirv;
        Words = Array.Empty<uint>();
    }

    public byte[] InputSpirv { get; }
    public uint[] Words { get; set; }

    // Pass010 outputs.
    public Dictionary<uint, (int? Set, int? Binding)> IdToSetBinding { get; } = new();
    public Dictionary<uint, (uint StorageClass, uint PointedType)> TypePointerMap { get; } = new();
    public Dictionary<uint, uint> VariableTypeMap { get; } = new();
    public HashSet<uint> StructTypeIds { get; } = new();
    public Dictionary<uint, int> StructMemberCounts { get; } = new();
    public Dictionary<uint, Dictionary<int, uint>> StructMemberOffsets { get; } = new();
    public HashSet<uint> ImageTypeIds { get; } = new();
    public HashSet<uint> SamplerTypeIds { get; } = new();
    public HashSet<uint> SampledImageTypeIds { get; } = new();
    public Dictionary<uint, string> IdToName { get; } = new();

    // Existing OpMemberName decorations: (structTypeId, memberIndex) -> name.
    // Captured during Pass010 so callers (e.g. ShaderDecompiler.BuildMemberPatches)
    // can preserve cook-baked names that our seed CB doesn't override AND
    // ensure that EVERY member-index of a covered cbuffer type gets a NEW
    // patched name (otherwise dxil-spirv's pre-existing OpMemberName with
    // duplicate / unsanitised values would leak through into the HLSL).
    public Dictionary<(uint StructTypeId, int MemberIndex), string> MemberNames { get; } = new();

    // Pass020 output.
    public List<SpirvBindingInfo> Bindings { get; set; } = new();
}
