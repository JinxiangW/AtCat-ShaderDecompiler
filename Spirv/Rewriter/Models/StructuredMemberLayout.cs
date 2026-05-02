namespace Ruri.ShaderTools.Spirv.Rewriter.Models;

internal sealed class StructuredMemberLayout
{
    public string Name { get; set; } = string.Empty;
    public int ByteOffset { get; set; }
    public NumericShaderParameter Metadata { get; set; } = null!;
    public MemberLogicalType LogicalType { get; set; } = null!;
    public int RegisterOffset { get; set; }
    public int RegisterCount { get; set; }
    public uint ResolvedTypeId { get; set; }

    // Scalar component type id for non-scalar members. Required so that a per-component access
    // chain into a vec4 / matrix member ends up with the correct ptr-scalar result type instead
    // of inheriting the parent vec4 / matrix type id (which would produce an invalid module that
    // crashes spirv-cross with "Cannot subdivide a scalar value").
    public uint ScalarTypeId { get; set; }

    // Column vector type id for matrix members. `matrix[col]` returns a vec(rowCount), and the
    // access chain that selects a column needs ptr-vec(rowCount), not ptr-matrix.
    public uint ColumnVectorTypeId { get; set; }
}
