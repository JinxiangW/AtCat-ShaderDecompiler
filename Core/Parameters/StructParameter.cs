using Newtonsoft.Json;

namespace Ruri.ShaderTools;

// Mirrors Unity AssetRipper StructParameter exactly.
// `Index` is the byte offset of this struct within the parent CB (Unity wire
// name kept for compat — semantically a byte offset, not a bind slot).
public sealed class StructParameter
{
    public StructParameter() { }

    public StructParameter(string name, int index, int arraySize, int structSize, VectorParameter[] vectors, MatrixParameter[] matrices)
    {
        Name = name;
        NameIndex = -1;
        Index = index;
        ArraySize = arraySize;
        StructSize = structSize;
        VectorMembers = vectors;
        MatrixMembers = matrices;
    }

    public string Name { get; set; } = string.Empty;
    public int NameIndex { get; set; }
    public int Index { get; set; }
    public int ArraySize { get; set; }
    public int StructSize { get; set; }
    public VectorParameter[] VectorMembers { get; set; } = Array.Empty<VectorParameter>();
    public MatrixParameter[] MatrixMembers { get; set; } = Array.Empty<MatrixParameter>();

    [JsonIgnore]
    public NumericShaderParameter[] AllNumericMembers
    {
        get
        {
            NumericShaderParameter[] shaderParams = new NumericShaderParameter[MatrixMembers.Length + VectorMembers.Length];
            MatrixMembers.CopyTo(shaderParams, 0);
            VectorMembers.CopyTo(shaderParams, MatrixMembers.Length);
            return shaderParams;
        }
    }
}
