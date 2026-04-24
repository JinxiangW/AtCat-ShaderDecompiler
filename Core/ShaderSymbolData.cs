namespace Ruri.ShaderTools;

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

    public IEnumerable<(string Name, int Binding, int Set, ShaderResourceType Type, char RegisterType)> EnumerateResourceBindings()
    {
        foreach (BufferBinding binding in ConstantBufferBindings)
        {
            yield return (binding.Name, binding.Index, binding.Set, ShaderResourceType.ConstantBuffer, 'b');
        }

        foreach (TextureParameter texture in TextureParameters)
        {
            yield return (texture.Name, texture.Index, texture.Set, ShaderResourceType.Texture, 't');
        }

        foreach (SamplerParameter sampler in Samplers)
        {
            yield return ($"sampler_{sampler.Index}", sampler.Index, sampler.Set, ShaderResourceType.Sampler, 's');
        }

        foreach (BufferBinding buffer in Buffers)
        {
            yield return (buffer.Name, buffer.Index, buffer.Set, ShaderResourceType.StructuredBuffer, 't');
        }

        foreach (UAVParameter uav in UAVs)
        {
            yield return (uav.Name, uav.Index, uav.Set, ShaderResourceType.UAV, 'u');
        }
    }

    public int GetResourceBindingCount()
    {
        return ConstantBufferBindings.Count + TextureParameters.Count + Samplers.Count + Buffers.Count + UAVs.Count;
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
