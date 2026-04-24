using Newtonsoft.Json;

namespace Ruri.ShaderDecompiler;

public class ShaderSymbolData
{
    public List<ConstantBuffer> ConstantBuffers { get; set; } = new();
    public List<BufferBinding> ConstantBufferBindings { get; set; } = new();
    public List<TextureParameter> TextureParameters { get; set; } = new();
    public List<SamplerParameter> Samplers { get; set; } = new();
    public List<BufferBinding> Buffers { get; set; } = new();
    public List<UAVParameter> UAVs { get; set; } = new();
    public string EntryPoint { get; set; } = "main";
    public ShaderStage Stage { get; set; } = ShaderStage.Unknown;
    public string? DebugName { get; set; }

    [JsonIgnore]
    public List<ResourceBinding> Resources { get; set; } = new();

    public IEnumerable<(string Name, int Index, char RegisterType)> EnumerateBindings()
    {
        foreach (BufferBinding binding in ConstantBufferBindings)
        {
            yield return (binding.Name, binding.Index, 'b');
        }

        foreach (TextureParameter texture in TextureParameters)
        {
            yield return (texture.Name, texture.Index, 't');
        }

        foreach (SamplerParameter sampler in Samplers)
        {
            yield return ($"sampler_{sampler.Index}", sampler.Index, 's');
        }

        foreach (BufferBinding buffer in Buffers)
        {
            yield return (buffer.Name, buffer.Index, 't');
        }

        foreach (UAVParameter uav in UAVs)
        {
            yield return (uav.Name, uav.Index, 'u');
        }
    }

    public bool HasAnyBindings()
    {
        return ConstantBufferBindings.Count > 0
            || TextureParameters.Count > 0
            || Samplers.Count > 0
            || Buffers.Count > 0
            || UAVs.Count > 0;
    }

    public void RefreshCompatibilityViews()
    {
        foreach (ConstantBuffer constantBuffer in ConstantBuffers)
        {
            constantBuffer.CBParams = constantBuffer.AllNumericParams
                .Select(ToCompatibilityParameter)
                .OrderBy(static parameter => parameter.Index)
                .ToList();

            foreach (StructParameter structParameter in constantBuffer.StructParams)
            {
                structParameter.CBParams = structParameter.AllNumericMembers
                    .Select(ToCompatibilityParameter)
                    .OrderBy(static parameter => parameter.Index)
                    .ToList();
            }
        }

        Resources.Clear();
        Resources.AddRange(ConstantBufferBindings.Select(static binding => new ResourceBinding
        {
            Name = binding.Name,
            Binding = binding.Index,
            Set = binding.Set,
            Type = ShaderResourceType.ConstantBuffer,
            RegisterType = 'b',
        }));
        Resources.AddRange(TextureParameters.Select(static texture => new ResourceBinding
        {
            Name = texture.Name,
            Binding = texture.Index,
            Set = texture.Set,
            Type = ShaderResourceType.Texture,
            RegisterType = 't',
        }));
        Resources.AddRange(Samplers.Select(static sampler => new ResourceBinding
        {
            Name = $"sampler_{sampler.Index}",
            Binding = sampler.Index,
            Set = sampler.Set,
            Type = ShaderResourceType.Sampler,
            RegisterType = 's',
        }));
        Resources.AddRange(Buffers.Select(static buffer => new ResourceBinding
        {
            Name = buffer.Name,
            Binding = buffer.Index,
            Set = buffer.Set,
            Type = ShaderResourceType.StructuredBuffer,
            RegisterType = 't',
        }));
        Resources.AddRange(UAVs.Select(static uav => new ResourceBinding
        {
            Name = uav.Name,
            Binding = uav.Index,
            Set = uav.Set,
            Type = ShaderResourceType.UAV,
            RegisterType = 'u',
        }));
    }

    private static ConstantBufferParameter ToCompatibilityParameter(NumericShaderParameter parameter)
    {
        return new ConstantBufferParameter
        {
            ParamName = parameter.Name ?? string.Empty,
            ParamType = parameter.Type,
            Rows = parameter.RowCount,
            Columns = parameter.ColumnCount,
            IsMatrix = parameter.IsMatrix,
            ArraySize = parameter.ArraySize,
            Index = parameter.Index,
        };
    }
}
