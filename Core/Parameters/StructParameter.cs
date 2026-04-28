using Newtonsoft.Json;

namespace Ruri.ShaderTools;

// Mirrors Unity's AssetRipper ShaderBlob.Parameters.StructParameter (typed-only).
// `Index` is the *byte offset* of this struct within the parent CB — the field
// keeps the historical Unity name for wire compatibility with Unity-side
// metadata.json (AssetRipper exporter writes `"Index"`). Semantically it is a
// byte offset, not a bind slot.
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
    public IEnumerable<NumericShaderParameter> AllNumericMembers
    {
        get
        {
            foreach (MatrixParameter matrix in MatrixMembers)
            {
                yield return matrix;
            }

            foreach (VectorParameter vector in VectorMembers)
            {
                yield return vector;
            }
        }
    }
}
