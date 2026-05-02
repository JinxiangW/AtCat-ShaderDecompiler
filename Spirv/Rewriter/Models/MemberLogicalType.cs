namespace Ruri.ShaderTools.Spirv.Rewriter.Models;

// Engine-internal description of a single cbuffer member's logical shape, derived from the
// metadata (NumericShaderParameter / StructParameter). The downstream type factory turns this
// into the actual SPIR-V type ids; the access translator uses it to decide whether a flat
// access drills into a vec4 .y or selects a matrix column or indexes into an array.
internal sealed class MemberLogicalType
{
    public LogicalTypeKind Kind { get; set; }
    public ScalarKind ScalarKind { get; set; }
    public int Rows { get; set; }
    public int Columns { get; set; }
    public int ArrayLength { get; set; }
    public int SecondaryArrayLength { get; set; }
    public int DeclaredByteSize { get; set; }
    public int UscIndex { get; set; }
    public bool IsMatrix { get; set; }
    public List<StructuredMemberLayout>? StructMembers { get; set; }
    public int StructByteSize { get; set; }
    public string StructName { get; set; } = string.Empty;
}
