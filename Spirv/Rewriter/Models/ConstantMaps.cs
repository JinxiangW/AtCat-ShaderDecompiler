namespace Ruri.ShaderTools.Spirv.Rewriter.Models;

// Caches OpConstant id↔value mappings plus an id→defining-instruction lookup. Built once
// per module (Pass010) and extended after we synthesise translation constants in Pass040.
internal sealed class ConstantMaps
{
    public Dictionary<uint, uint> IdToValue { get; } = new();
    public Dictionary<uint, uint> ValueToId { get; } = new();
    public Dictionary<uint, SpirvInstruction> Definitions { get; } = new();
}
