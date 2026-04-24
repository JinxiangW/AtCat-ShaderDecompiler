namespace Ruri.ShaderTools;

public class ShaderSymbolMetadata
{
    public List<ResourceSymbol> Resources { get; set; } = new();
    public string? EntryPoint { get; set; }
}

public class ResourceSymbol
{
    public string Name { get; set; } = string.Empty;
    public int Set { get; set; }
    public int Binding { get; set; }
    public ResourceType Type { get; set; }
    public int? Slot { get; set; }
}

public enum ResourceType
{
    UniformBuffer = 0,
    Texture,
    Sampler,
    UAV,
    StructuredBuffer,
    RWTexture,
    RWBuffer,
}
