namespace Ruri.ShaderTools.Spirv.Rewriter.Models;

// Single-pass walk of the SPIR-V module produces this. Every later pass reads from it
// without re-walking the module: who has which (set, binding) decoration, who points at
// what, what shape each struct/array/vector type has, etc.
internal sealed class ModuleAnalysis
{
    public Dictionary<uint, (int? Set, int? Binding)> SetBindingById { get; } = new();
    public Dictionary<uint, (uint StorageClass, uint TypeId)> PointerTypes { get; } = new();
    public Dictionary<uint, uint> VariablePointerTypes { get; } = new();
    public Dictionary<uint, uint[]> StructMembers { get; } = new();
    public Dictionary<uint, (uint ComponentTypeId, uint ComponentCount)> VectorShapes { get; } = new();
    public Dictionary<uint, (uint ElementTypeId, uint LengthId)> ArrayTypes { get; } = new();
    public Dictionary<uint, uint> Constants { get; } = new();
    public Dictionary<uint, uint> ArrayStrides { get; } = new();

    public bool TryGetVectorShape(uint vectorTypeId, out uint componentTypeId, out uint componentCount)
    {
        if (VectorShapes.TryGetValue(vectorTypeId, out (uint ComponentTypeId, uint ComponentCount) shape))
        {
            componentTypeId = shape.ComponentTypeId;
            componentCount = shape.ComponentCount;
            return true;
        }

        componentTypeId = 0;
        componentCount = 0;
        return false;
    }
}
