using Newtonsoft.Json;

namespace Ruri.ShaderTools;

public sealed record class SamplerParameter
{
    public SamplerParameter() { }

    public SamplerParameter(uint sampler, int index)
    {
        Sampler = sampler;
        Index = index;
    }

    public uint Sampler { get; set; }
    public int Index { get; set; }

    // Optional resolved name (e.g. "Material_Texture2D_0Sampler" when the
    // sampler is SRT-bound to a Material UB resource). Null when we don't
    // have a source-truth name for this slot; the consumer falls back to
    // "sampler_<Index>". Engine-UB-bound samplers get a placeholder
    // ("View_Sampler39"); loose samplers stay null.
    public string? Name { get; set; }
}
