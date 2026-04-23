namespace Ruri.ShaderDecompiler;

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
    public int SamplerIndex { get; set; }
    public bool MultiSampled { get; set; }
    public byte Dim { get; set; }
}

public sealed record class SamplerParameter
{
    public uint Sampler { get; set; }
    public int Index { get; set; }
}

public sealed record class UAVParameter
{
    public string Name { get; set; } = string.Empty;
    public int NameIndex { get; set; }
    public int Index { get; set; }
    public int OriginalIndex { get; set; }
}

public sealed record class BufferBinding
{
    public string Name { get; set; } = string.Empty;
    public int NameIndex { get; set; }
    public int Index { get; set; }
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
