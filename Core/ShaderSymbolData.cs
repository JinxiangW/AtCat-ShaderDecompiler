namespace Ruri.ShaderTools;

public class ShaderSymbolData
{
    public List<ConstantBuffer> ConstantBuffers { get; set; } = new();
    public List<BufferBinding> ConstantBufferBindings { get; set; } = new();
    public List<TextureParameter> TextureParameters { get; set; } = new();
    public List<SamplerParameter> Samplers { get; set; } = new();
    public List<UAVParameter> UAVs { get; set; } = new();
    // Mirrors Unity SerializedProgramParameters.m_DescriptorSetParams. The
    // single source of truth for descriptor-set membership: per-resource
    // records (BufferBinding / TextureParameter / SamplerParameter /
    // UAVParameter) hold the binding slot, the set id is recovered from here
    // by matching (BindingIndex, DescriptorType).
    public List<DescriptorSetParameter> DescriptorSetParams { get; set; } = new();
    public string EntryPoint { get; set; } = "main";
    public string? DebugName { get; set; }
    public List<string> UsedMaterials { get; set; } = new();

    public IEnumerable<(string Name, int Binding, int Set, ShaderResourceType Type, char RegisterType)> EnumerateResourceBindings()
    {
        foreach (BufferBinding binding in ConstantBufferBindings)
        {
            yield return (binding.Name, binding.Index, GetSetIdFor(binding.Index, ShaderResourceType.ConstantBuffer), ShaderResourceType.ConstantBuffer, 'b');
        }

        foreach (TextureParameter texture in TextureParameters)
        {
            yield return (texture.Name, texture.Index, GetSetIdFor(texture.Index, ShaderResourceType.Texture), ShaderResourceType.Texture, 't');
        }

        foreach (SamplerParameter sampler in Samplers)
        {
            string name = string.IsNullOrWhiteSpace(sampler.Name)
                ? $"sampler_{sampler.Index}"
                : sampler.Name!;
            yield return (name, sampler.Index, GetSetIdFor(sampler.Index, ShaderResourceType.Sampler), ShaderResourceType.Sampler, 's');
        }

        foreach (UAVParameter uav in UAVs)
        {
            yield return (uav.Name, uav.Index, GetSetIdFor(uav.Index, ShaderResourceType.UAV), ShaderResourceType.UAV, 'u');
        }
    }

    // Resolve the descriptor-set id that owns `(bindingIndex, kind)`. Returns 0
    // when no entry matches — matches the legacy "default set 0" behaviour
    // from before Set lived on individual records.
    public int GetSetIdFor(int bindingIndex, ShaderResourceType kind)
    {
        DescriptorBindingType descriptorType = ClassifyDescriptorBindingType(kind);
        return GetSetIdFor(bindingIndex, descriptorType);
    }

    public int GetSetIdFor(int bindingIndex, DescriptorBindingType descriptorType)
    {
        int wireType = (int)descriptorType;
        foreach (DescriptorSetParameter set in DescriptorSetParams)
        {
            foreach (SetBinding binding in set.Bindings)
            {
                if (binding.BindingIndex != bindingIndex)
                {
                    continue;
                }
                if (descriptorType == DescriptorBindingType.Unknown || binding.DescriptorType == wireType)
                {
                    return set.SetId;
                }
            }
        }
        return 0;
    }

    // Add or update a descriptor-set entry for `(setId, bindingIndex, kind)`.
    // Used by hooks that decode packed binding indices (see
    // EndFieldShaderBindingHook.DecodePackedBindPoint) and need to write the
    // recovered set id back into the symbol table.
    public void RegisterSetBinding(int setId, int bindingIndex, ShaderResourceType kind, string? name = null)
    {
        if (setId < 0 || bindingIndex < 0)
        {
            return;
        }

        DescriptorBindingType descriptorType = ClassifyDescriptorBindingType(kind);
        DescriptorSetParameter? set = DescriptorSetParams.FirstOrDefault(s => s.SetId == setId);
        if (set is null)
        {
            set = new DescriptorSetParameter(string.Empty, setId);
            DescriptorSetParams.Add(set);
        }

        SetBinding? existing = set.Bindings.FirstOrDefault(b => b.BindingIndex == bindingIndex && b.DescriptorType == (int)descriptorType);
        if (existing is null)
        {
            set.Bindings.Add(new SetBinding(name ?? string.Empty, bindingIndex, descriptorType));
        }
        else if (!string.IsNullOrEmpty(name) && string.IsNullOrEmpty(existing.Name))
        {
            existing.Name = name;
        }

        if (bindingIndex > set.MaxBindingIndex)
        {
            set.MaxBindingIndex = bindingIndex;
        }
    }

    public static DescriptorBindingType ClassifyDescriptorBindingType(ShaderResourceType kind) => kind switch
    {
        ShaderResourceType.ConstantBuffer => DescriptorBindingType.UniformBuffer,
        ShaderResourceType.Sampler or ShaderResourceType.SamplerComparison => DescriptorBindingType.Sampler,
        ShaderResourceType.Texture
            or ShaderResourceType.SampledImage
            or ShaderResourceType.SRV
            or ShaderResourceType.Texture2D
            or ShaderResourceType.Texture2DArray
            or ShaderResourceType.Texture3D
            or ShaderResourceType.TextureCube
            or ShaderResourceType.TextureCubeArray
            or ShaderResourceType.Texture2DMS
            or ShaderResourceType.Buffer
            or ShaderResourceType.StructuredBuffer
            or ShaderResourceType.ByteAddressBuffer => DescriptorBindingType.SampledImage,
        ShaderResourceType.UAV
            or ShaderResourceType.RWBuffer
            or ShaderResourceType.RWStructuredBuffer
            or ShaderResourceType.RWByteAddressBuffer
            or ShaderResourceType.StorageBuffer => DescriptorBindingType.StorageBuffer,
        ShaderResourceType.RWTexture2D
            or ShaderResourceType.RWTexture2DArray
            or ShaderResourceType.RWTexture3D
            or ShaderResourceType.StorageImage => DescriptorBindingType.StorageImage,
        _ => DescriptorBindingType.Unknown,
    };

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
