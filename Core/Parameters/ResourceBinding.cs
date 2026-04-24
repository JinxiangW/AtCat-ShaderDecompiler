namespace Ruri.ShaderDecompiler;

public class ResourceBinding
{
    public string Name { get; set; } = string.Empty;
    public int Binding { get; set; }
    public int Set { get; set; }
    public ShaderResourceType Type { get; set; } = ShaderResourceType.Unknown;
    public int Tag { get; set; }
    public char RegisterType { get; set; }
}
