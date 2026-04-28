namespace Ruri.ShaderTools;

public class ShaderSymbolData
{
    public List<ConstantBuffer> ConstantBuffers { get; set; } = new();
    public List<BufferBinding> ConstantBufferBindings { get; set; } = new();
    public List<TextureParameter> TextureParameters { get; set; } = new();
    public List<SamplerParameter> Samplers { get; set; } = new();
    public List<UAVParameter> UAVs { get; set; } = new();
    public string EntryPoint { get; set; } = "main";
    public string? DebugName { get; set; }
    public List<string> UsedMaterials { get; set; } = new();

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

        foreach (UAVParameter uav in UAVs)
        {
            yield return (uav.Name, uav.Index, uav.Set, ShaderResourceType.UAV, 'u');
        }
    }

    public int GetResourceBindingCount()
    {
        return ConstantBufferBindings.Count + TextureParameters.Count + Samplers.Count + UAVs.Count;
    }

    public ConstantBuffer? GetConstantBufferByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        List<ConstantBuffer> matches = ConstantBuffers
            .Where(cb => string.Equals(cb.Name, name, StringComparison.Ordinal))
            .ToList();
        if (matches.Count == 0)
        {
            return null;
        }

        if (matches.Count == 1)
        {
            return matches[0];
        }

        return BuildMergedConstantBuffer(matches);
    }

    private static ConstantBuffer BuildMergedConstantBuffer(List<ConstantBuffer> matches)
    {
        ConstantBuffer first = matches[0];
        return new ConstantBuffer
        {
            Name = first.Name,
            NameIndex = matches.Select(static cb => cb.NameIndex).FirstOrDefault(static index => index >= 0),
            Size = matches.Max(static cb => cb.Size),
            IsPartialCB = matches.Any(static cb => cb.IsPartialCB),
            MatrixParams = MergeNumericParameters(matches.SelectMany(static cb => cb.MatrixParams)),
            VectorParams = MergeNumericParameters(matches.SelectMany(static cb => cb.VectorParams)),
            StructParams = MergeStructParameters(matches.SelectMany(static cb => cb.StructParams)),
        };
    }

    private static T[] MergeNumericParameters<T>(IEnumerable<T> parameters) where T : NumericShaderParameter
    {
        return parameters
            .GroupBy(static parameter => new NumericParameterKey(
                parameter.Name ?? string.Empty,
                parameter.ByteOffset,
                parameter.ArraySize,
                parameter.Type,
                parameter.RowCount,
                parameter.ColumnCount,
                parameter.IsMatrix))
            .Select(static group => group.First())
            .OrderBy(static parameter => parameter.ByteOffset)
            .ThenBy(static parameter => parameter.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static StructParameter[] MergeStructParameters(IEnumerable<StructParameter> parameters)
    {
        return parameters
            .GroupBy(static parameter => new StructParameterKey(
                parameter.Name,
                parameter.Index,
                parameter.ArraySize,
                parameter.StructSize))
            .Select(static group =>
            {
                StructParameter first = group.First();
                return new StructParameter
                {
                    Name = first.Name,
                    NameIndex = group.Select(static parameter => parameter.NameIndex).FirstOrDefault(static index => index >= 0),
                    Index = first.Index,
                    ArraySize = group.Max(static parameter => parameter.ArraySize),
                    StructSize = group.Max(static parameter => parameter.StructSize),
                    VectorMembers = MergeNumericParameters(group.SelectMany(static parameter => parameter.VectorMembers)),
                    MatrixMembers = MergeNumericParameters(group.SelectMany(static parameter => parameter.MatrixMembers)),
                };
            })
            .OrderBy(static parameter => parameter.Index)
            .ThenBy(static parameter => parameter.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private readonly record struct NumericParameterKey(string Name, int ByteOffset, int ArraySize, ShaderParamType Type, byte Rows, byte Columns, bool IsMatrix);
    private readonly record struct StructParameterKey(string Name, int Index, int ArraySize, int StructSize);
}
