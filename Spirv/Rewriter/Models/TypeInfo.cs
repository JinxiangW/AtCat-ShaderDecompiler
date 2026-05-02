namespace Ruri.ShaderTools.Spirv.Rewriter.Models;

// Cache of the module's primitive scalar / vector / matrix type ids. Lazily populated by
// the type factory: if a type isn't in the module yet (e.g. uint scalar in a shader that
// only uses float), the factory inserts it on demand and records the new id here.
internal sealed class TypeInfo
{
    public uint FloatTypeId { get; set; }
    public uint IntTypeId { get; set; }
    public uint UIntTypeId { get; set; }
    public Dictionary<int, uint> FloatVectorTypeIds { get; } = new();
    public Dictionary<int, uint> IntVectorTypeIds { get; } = new();
    public Dictionary<int, uint> UIntVectorTypeIds { get; } = new();
    public Dictionary<(int Rows, int Columns), uint> MatrixTypeIds { get; } = new();
}
