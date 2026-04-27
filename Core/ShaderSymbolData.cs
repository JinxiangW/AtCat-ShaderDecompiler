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

    public void RefreshCompatibilityViews()
    {
        foreach (ConstantBuffer constantBuffer in ConstantBuffers)
        {
            List<ConstantBufferParameter> regeneratedParameters = constantBuffer.AllNumericParams
                .Select(ToCompatibilityParameter)
                .OrderBy(static parameter => parameter.ByteOffset)
                .ToList();

            if (regeneratedParameters.Count > 0)
            {
                constantBuffer.CBParams = regeneratedParameters;
            }
            else if (constantBuffer.CBParams.Count > 1)
            {
                constantBuffer.CBParams = constantBuffer.CBParams
                    .OrderBy(static parameter => parameter.ByteOffset)
                    .ToList();
            }

            foreach (StructParameter structParameter in constantBuffer.StructParams)
            {
                List<ConstantBufferParameter> regeneratedStructParameters = structParameter.AllNumericMembers
                    .Select(ToCompatibilityParameter)
                    .OrderBy(static parameter => parameter.ByteOffset)
                    .ToList();

                if (regeneratedStructParameters.Count > 0)
                {
                    structParameter.CBParams = regeneratedStructParameters;
                }
                else if (structParameter.CBParams.Count > 1)
                {
                    structParameter.CBParams = structParameter.CBParams
                        .OrderBy(static parameter => parameter.ByteOffset)
                        .ToList();
                }
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
            ByteOffset = parameter.ByteOffset,
        };
    }

    private static ConstantBuffer BuildMergedConstantBuffer(List<ConstantBuffer> matches)
    {
        ConstantBuffer first = matches[0];
        var merged = new ConstantBuffer
        {
            Name = first.Name,
            NameIndex = matches.Select(static cb => cb.NameIndex).FirstOrDefault(static index => index >= 0),
            Size = matches.Max(static cb => cb.Size),
            IsPartialCB = matches.Any(static cb => cb.IsPartialCB),
            MatrixParams = MergeNumericParameters(matches.SelectMany(static cb => cb.MatrixParams)),
            VectorParams = MergeNumericParameters(matches.SelectMany(static cb => cb.VectorParams)),
            StructParams = MergeStructParameters(matches.SelectMany(static cb => cb.StructParams)),
            CBParams = MergeCompatibilityParameters(matches.SelectMany(static cb => cb.CBParams))
        };

        return merged;
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
                    CBParams = MergeCompatibilityParameters(group.SelectMany(static parameter => parameter.CBParams))
                };
            })
            .OrderBy(static parameter => parameter.Index)
            .ThenBy(static parameter => parameter.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static List<ConstantBufferParameter> MergeCompatibilityParameters(IEnumerable<ConstantBufferParameter> parameters)
    {
        return parameters
            .GroupBy(static parameter => new ConstantBufferParameterKey(
                parameter.ParamName,
                parameter.ByteOffset,
                parameter.ArraySize,
                parameter.ParamType,
                parameter.Rows,
                parameter.Columns,
                parameter.IsMatrix))
            .Select(static group => group.First())
            .OrderBy(static parameter => parameter.ByteOffset)
            .ThenBy(static parameter => parameter.ParamName, StringComparer.Ordinal)
            .ToList();
    }

    private readonly record struct NumericParameterKey(string Name, int ByteOffset, int ArraySize, ShaderParamType Type, byte Rows, byte Columns, bool IsMatrix);
    private readonly record struct StructParameterKey(string Name, int Index, int ArraySize, int StructSize);
    private readonly record struct ConstantBufferParameterKey(string Name, int ByteOffset, int ArraySize, ShaderParamType Type, int Rows, int Columns, bool IsMatrix);
}
