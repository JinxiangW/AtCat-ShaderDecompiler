using Newtonsoft.Json;

namespace Ruri.ShaderTools;

// Mirrors the Unity intermediate (AssetRipper ShaderBlob.Parameters.ConstantBuffer)
// — typed-only structure. There is intentionally no flat compatibility list:
// readers fill VectorParams / MatrixParams / StructParams once, the SPIR-V
// rewriter and patcher consume those views directly. AllNumericParams is a
// read-only iteration helper, never a separate storage.
public sealed class ConstantBuffer
{
    public ConstantBuffer() { }

    public ConstantBuffer(string name, MatrixParameter[] matrices, VectorParameter[] vectors, StructParameter[] structs, int usedSize)
    {
        Name = name;
        NameIndex = -1;
        MatrixParams = matrices;
        VectorParams = vectors;
        StructParams = structs;
        Size = usedSize;
        IsPartialCB = false;
    }

    public string Name { get; set; } = string.Empty;
    public int NameIndex { get; set; }
    public MatrixParameter[] MatrixParams { get; set; } = Array.Empty<MatrixParameter>();
    public VectorParameter[] VectorParams { get; set; } = Array.Empty<VectorParameter>();
    public StructParameter[] StructParams { get; set; } = Array.Empty<StructParameter>();
    public int Size { get; set; }
    public bool IsPartialCB { get; set; }

    [JsonIgnore]
    public IEnumerable<NumericShaderParameter> AllNumericParams
    {
        get
        {
            foreach (MatrixParameter matrix in MatrixParams)
            {
                yield return matrix;
            }

            foreach (VectorParameter vector in VectorParams)
            {
                yield return vector;
            }
        }
    }
}
