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
    public int Set { get; set; }
}
