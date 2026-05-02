namespace Ruri.ShaderTools.Spirv.Rewriter.Models;

internal sealed class StructuredBufferLayout
{
    public List<StructuredMemberLayout> Members { get; } = new();

    // Smallest register count that fits all member byte offsets + sizes (incl. padding for
    // hidden tail bytes). Used to assert the new struct layout still fits the original flat
    // float4 array without overshooting it.
    public int RequiredRegisterCount { get; set; }

    // Largest register actually referenced by any member, ignoring synthetic padding. Used as
    // a sanity check vs. the SPIR-V flat array length.
    public int MaxUsedRegisterCount { get; set; }
}
