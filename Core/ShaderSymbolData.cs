using Newtonsoft.Json;

namespace Ruri.ShaderDecompiler;

public enum ShaderResourceType
{
    Unknown = 0,
    Texture,
    SampledImage,
    SRV,
    UAV,
    Sampler,
    SamplerComparison,
    ConstantBuffer,
    Buffer,
    StructuredBuffer,
    ByteAddressBuffer,
    RWBuffer,
    RWStructuredBuffer,
    RWByteAddressBuffer,
    Texture2D,
    Texture2DArray,
    Texture3D,
    TextureCube,
    TextureCubeArray,
    Texture2DMS,
    RWTexture2D,
    RWTexture2DArray,
    RWTexture3D,
    RaytracingAccelerationStructure,
    StorageImage,
    StorageBuffer,
    InputAttachment,
}

public class ResourceBinding
{
    public string Name { get; set; } = string.Empty;
    public int Binding { get; set; }
    public int Set { get; set; }
    public ShaderResourceType Type { get; set; } = ShaderResourceType.Unknown;
    public int Tag { get; set; }
    public char RegisterType { get; set; }
}

public class ConstantBufferParameter
{
    public string ParamName = string.Empty;
    public ShaderParamType ParamType;
    public int Rows;
    public int Columns;
    public bool IsMatrix;
    public int ArraySize;
    public int Index;
}

public enum ShaderParamType
{
    Float = 0,
    Int = 1,
    Bool = 2,
    Half = 3,
    Short = 4,
    UInt = 5,
    TypeCount = 6,
}

public class NumericShaderParameter
{
    public string? Name { get; set; }
    public int NameIndex { get; set; }
    public int Index { get; set; }
    public int ArraySize { get; set; }
    public ShaderParamType Type { get; set; }
    public byte RowCount { get; set; }
    public byte ColumnCount { get; set; }
    public bool IsMatrix { get; set; }

    [JsonIgnore]
    public ShaderParamType ParamType
    {
        get => Type;
        set => Type = value;
    }

    [JsonIgnore]
    public int Rows
    {
        get => RowCount;
        set => RowCount = unchecked((byte)value);
    }

    [JsonIgnore]
    public int Columns
    {
        get => ColumnCount;
        set => ColumnCount = unchecked((byte)value);
    }
}

public sealed class VectorParameter : NumericShaderParameter
{
    public byte Dim
    {
        get => RowCount;
        set => RowCount = value;
    }
}

public sealed class MatrixParameter : NumericShaderParameter
{
}

public sealed class StructParameter
{
    public string Name { get; set; } = string.Empty;
    public int NameIndex { get; set; }
    public int Index { get; set; }
    public int ArraySize { get; set; }
    public int StructSize { get; set; }
    public VectorParameter[] VectorMembers { get; set; } = Array.Empty<VectorParameter>();
    public MatrixParameter[] MatrixMembers { get; set; } = Array.Empty<MatrixParameter>();

    [JsonIgnore]
    public int Size
    {
        get => StructSize;
        set => StructSize = value;
    }

    [JsonIgnore]
    public List<ConstantBufferParameter> CBParams { get; set; } = new();

    public IEnumerable<NumericShaderParameter> AllNumericMembers
    {
        get
        {
            foreach (MatrixParameter matrix in MatrixMembers)
            {
                yield return matrix;
            }

            foreach (VectorParameter vector in VectorMembers)
            {
                yield return vector;
            }
        }
    }
}

public sealed class ConstantBuffer
{
    public string Name { get; set; } = string.Empty;
    public int NameIndex { get; set; }
    public MatrixParameter[] MatrixParams { get; set; } = Array.Empty<MatrixParameter>();
    public VectorParameter[] VectorParams { get; set; } = Array.Empty<VectorParameter>();
    public StructParameter[] StructParams { get; set; } = Array.Empty<StructParameter>();
    public int Size { get; set; }
    public bool IsPartialCB { get; set; }

    [JsonIgnore]
    public int UsedSize
    {
        get => Size;
        set => Size = value;
    }

    [JsonIgnore]
    public bool Partial
    {
        get => IsPartialCB;
        set => IsPartialCB = value;
    }

    [JsonIgnore]
    public List<ConstantBufferParameter> CBParams { get; set; } = new();

    public IEnumerable<NumericShaderParameter> AllNumericParams
    {
        get
        {
            foreach (MatrixParameter matrix in MatrixParams)
            {
                yield return matrix;
            }

            foreach (VectorParameter vector in VectorParams)
            {
                yield return vector;
            }
        }
    }
}

public sealed record class TextureParameter
{
    public string Name { get; set; } = string.Empty;
    public int NameIndex { get; set; }
    public int Index { get; set; }
    public int Set { get; set; }
    public int SamplerIndex { get; set; }
    public bool MultiSampled { get; set; }
    public byte Dim { get; set; }
}

public sealed record class SamplerParameter
{
    public uint Sampler { get; set; }
    public int Index { get; set; }
    public int Set { get; set; }

    [JsonIgnore]
    public int BindPoint
    {
        get => Index;
        set => Index = value;
    }
}

public sealed record class UAVParameter
{
    public string Name { get; set; } = string.Empty;
    public int NameIndex { get; set; }
    public int Index { get; set; }
    public int Set { get; set; }
    public int OriginalIndex { get; set; }
}

public sealed record class BufferBinding
{
    public string Name { get; set; } = string.Empty;
    public int NameIndex { get; set; }
    public int Index { get; set; }
    public int Set { get; set; }
    public int ArraySize { get; set; }
}

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

public enum ShaderStage
{
    Unknown = 0,
    Vertex,
    Pixel,
    Compute,
    Geometry,
    TessellationControl,
    TessellationEvaluation,
    RayGeneration,
    RayClosestHit,
    RayMiss,
    RayAnyHit,
    RayIntersection,
    Callable,
    Task,
    Mesh,
}
