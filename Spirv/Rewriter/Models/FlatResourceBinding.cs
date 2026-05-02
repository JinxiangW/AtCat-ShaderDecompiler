namespace Ruri.ShaderTools.Spirv.Rewriter.Models;

// Minimal descriptor identifying a metadata constant buffer once we've matched it back to
// a SPIR-V uniform variable. Just (Name, set, binding) — kept separate from
// FlatUniformBufferInfo so the resolved-name cache can survive past the module instance.
internal sealed class FlatResourceBinding
{
    public string Name { get; set; } = string.Empty;
    public int Binding { get; set; }
    public int Set { get; set; }
}
