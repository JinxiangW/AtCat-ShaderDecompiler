using System.Diagnostics;
using System.Text.Json;

namespace Ruri.ShaderDecompiler.Testing.Compilation;

internal static class SpirvReflectionMetadataExtractor
{
    public static ShaderSymbolData Extract(string spirvPath, string spirvCrossExePath)
    {
        string json = RunReflect(spirvPath, spirvCrossExePath);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        var metadata = new ShaderSymbolData
        {
            EntryPoint = TryReadEntryPoint(root, out ShaderStage stage) ?? "main",
            Stage = stage,
            DebugName = Path.GetFileNameWithoutExtension(spirvPath),
        };

        Dictionary<string, JsonElement> types = ReadTypes(root);

        AddUniformBuffers(metadata, root, types);
        AddResources(metadata, root, "separate_images", ShaderResourceType.Texture, 't');
        AddResources(metadata, root, "separate_samplers", ShaderResourceType.Sampler, 's');
        AddResources(metadata, root, "images", ShaderResourceType.StorageImage, 'u');
        AddStorageBuffers(metadata, root);

        return metadata;
    }

    private static string RunReflect(string spirvPath, string spirvCrossExePath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = spirvCrossExePath,
            Arguments = $"\"{spirvPath}\" --reflect",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(spirvCrossExePath)!,
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {spirvCrossExePath}");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"spirv-cross reflection failed for {spirvPath}\n{stdout}\n{stderr}");
        }

        if (string.IsNullOrWhiteSpace(stdout))
        {
            throw new InvalidOperationException($"spirv-cross reflection produced no JSON for {spirvPath}");
        }

        return stdout;
    }

    private static Dictionary<string, JsonElement> ReadTypes(JsonElement root)
    {
        var types = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (!root.TryGetProperty("types", out JsonElement typesElement) || typesElement.ValueKind != JsonValueKind.Object)
        {
            return types;
        }

        foreach (JsonProperty property in typesElement.EnumerateObject())
        {
            types[property.Name] = property.Value;
        }

        return types;
    }

    private static string? TryReadEntryPoint(JsonElement root, out ShaderStage stage)
    {
        stage = ShaderStage.Unknown;
        if (!root.TryGetProperty("entryPoints", out JsonElement entryPoints) || entryPoints.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        JsonElement first = entryPoints.EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? entryName = first.TryGetProperty("name", out JsonElement nameElement) ? nameElement.GetString() : null;
        string? mode = first.TryGetProperty("mode", out JsonElement modeElement) ? modeElement.GetString() : null;
        stage = mode switch
        {
            "vert" => ShaderStage.Vertex,
            "frag" => ShaderStage.Pixel,
            "comp" => ShaderStage.Compute,
            "geom" => ShaderStage.Geometry,
            "tesc" => ShaderStage.TessellationControl,
            "tese" => ShaderStage.TessellationEvaluation,
            _ => ShaderStage.Unknown,
        };
        return entryName;
    }

    private static void AddUniformBuffers(ShaderSymbolData metadata, JsonElement root, Dictionary<string, JsonElement> types)
    {
        if (!root.TryGetProperty("ubos", out JsonElement ubos) || ubos.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement ubo in ubos.EnumerateArray())
        {
            string typeId = ubo.TryGetProperty("type", out JsonElement typeElement) ? typeElement.GetString() ?? string.Empty : string.Empty;
            string reflectedName = ubo.TryGetProperty("name", out JsonElement nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
            string resourceName = NormalizeTypePrefixedName(reflectedName);
            var cbuffer = new ConstantBuffer
            {
                Name = resourceName,
                UsedSize = ReadIntProperty(ubo, "block_size"),
                Partial = false,
                CBParams = new List<ConstantBufferParameter>(),
            };

            int binding = ReadIntProperty(ubo, "binding");
            metadata.Resources.Add(new ResourceBinding
            {
                Name = resourceName,
                Set = 0,
                Binding = binding,
                Type = ShaderResourceType.ConstantBuffer,
                RegisterType = 'b',
            });

            if (types.TryGetValue(typeId, out JsonElement typeInfo) && typeInfo.TryGetProperty("members", out JsonElement members) && members.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (JsonElement member in members.EnumerateArray())
                {
                    int offset = ReadIntProperty(member, "offset");
                    string typeName = NormalizeMemberTypeName(member.TryGetProperty("type", out JsonElement memberType) ? memberType.GetString() ?? string.Empty : string.Empty);
                    ParseUscLayout(typeName, out ShaderParamType paramType, out int rows, out int columns, out bool isMatrix, out int arraySize);
                    cbuffer.CBParams.Add(new ConstantBufferParameter
                    {
                        ParamName = member.TryGetProperty("name", out JsonElement memberName) ? memberName.GetString() ?? $"Member{index}" : $"Member{index}",
                        Index = offset,
                        ParamType = paramType,
                        Rows = rows,
                        Columns = columns,
                        IsMatrix = isMatrix,
                        ArraySize = arraySize,
                    });
                    index++;
                }
            }

            metadata.ConstantBuffers.Add(cbuffer);
        }
    }

    private static void AddResources(ShaderSymbolData metadata, JsonElement root, string propertyName, ShaderResourceType resourceType, char registerType)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement resources) || resources.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement resource in resources.EnumerateArray())
        {
            string name = resource.TryGetProperty("name", out JsonElement nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            metadata.Resources.Add(new ResourceBinding
            {
                Name = NormalizeTypePrefixedName(name),
                Set = 0,
                Binding = ReadIntProperty(resource, "binding"),
                Type = InferConcreteResourceType(resourceType, resource),
                RegisterType = registerType,
            });
        }
    }

    private static void AddStorageBuffers(ShaderSymbolData metadata, JsonElement root)
    {
        if (!root.TryGetProperty("ssbos", out JsonElement buffers) || buffers.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement buffer in buffers.EnumerateArray())
        {
            bool isReadonly = buffer.TryGetProperty("readonly", out JsonElement readonlyElement) && readonlyElement.ValueKind == JsonValueKind.True;
            string name = buffer.TryGetProperty("name", out JsonElement nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
            string typeName = buffer.TryGetProperty("type", out JsonElement typeElement) ? typeElement.GetString() ?? string.Empty : string.Empty;

            metadata.Resources.Add(new ResourceBinding
            {
                Name = NormalizeTypePrefixedName(name),
                Set = 0,
                Binding = ReadIntProperty(buffer, "binding"),
                Type = InferStorageBufferType(isReadonly, typeName),
                RegisterType = isReadonly ? 't' : 'u',
            });
        }
    }

    private static ShaderResourceType InferConcreteResourceType(ShaderResourceType fallbackType, JsonElement resource)
    {
        string type = resource.TryGetProperty("type", out JsonElement typeElement) ? typeElement.GetString() ?? string.Empty : string.Empty;
        return type switch
        {
            "texture2D" => ShaderResourceType.Texture2D,
            "texture3D" => ShaderResourceType.Texture3D,
            "textureCube" => ShaderResourceType.TextureCube,
            "sampler" => ShaderResourceType.Sampler,
            "image2D" => ShaderResourceType.RWTexture2D,
            _ => fallbackType,
        };
    }

    private static ShaderResourceType InferStorageBufferType(bool isReadonly, string typeName)
    {
        if (typeName.Contains("ByteAddressBuffer", StringComparison.Ordinal))
        {
            return isReadonly ? ShaderResourceType.ByteAddressBuffer : ShaderResourceType.RWByteAddressBuffer;
        }

        if (typeName.Contains("StructuredBuffer", StringComparison.Ordinal))
        {
            return isReadonly ? ShaderResourceType.StructuredBuffer : ShaderResourceType.RWStructuredBuffer;
        }

        return isReadonly ? ShaderResourceType.StructuredBuffer : ShaderResourceType.RWStructuredBuffer;
    }

    private static string NormalizeTypePrefixedName(string name)
    {
        const string prefix = "type.";
        if (name.StartsWith(prefix, StringComparison.Ordinal))
        {
            return name.Substring(prefix.Length);
        }

        return name;
    }

    private static int ReadIntProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement valueElement))
        {
            return 0;
        }

        return valueElement.ValueKind == JsonValueKind.Number ? valueElement.GetInt32() : 0;
    }

    private static void ParseUscLayout(string typeName, out ShaderParamType paramType, out int rows, out int columns, out bool isMatrix, out int arraySize)
    {
        paramType = ShaderParamType.Float;
        rows = 0;
        columns = 0;
        isMatrix = false;
        arraySize = 1;

        if (typeName.StartsWith("float", StringComparison.Ordinal))
        {
            string suffix = typeName.Substring("float".Length);
            int separatorIndex = suffix.IndexOf('x');
            if (separatorIndex > 0)
            {
                rows = int.Parse(suffix.Substring(0, separatorIndex));
                columns = int.Parse(suffix[(separatorIndex + 1)..]);
                isMatrix = true;
                return;
            }

            rows = suffix.Length == 0 ? 1 : int.Parse(suffix);
            columns = 1;
            return;
        }

        if (typeName.StartsWith("int", StringComparison.Ordinal))
        {
            paramType = ShaderParamType.Int;
            string suffix = typeName.Substring("int".Length);
            rows = suffix.Length == 0 ? 1 : int.Parse(suffix);
            columns = 1;
            return;
        }

        if (typeName.StartsWith("uint", StringComparison.Ordinal) || typeName.StartsWith("bool", StringComparison.Ordinal))
        {
            paramType = typeName.StartsWith("bool", StringComparison.Ordinal) ? ShaderParamType.Bool : ShaderParamType.UInt;
            string prefix = typeName.StartsWith("bool", StringComparison.Ordinal) ? "bool" : "uint";
            string suffix = typeName.Substring(prefix.Length);
            rows = suffix.Length == 0 ? 1 : int.Parse(suffix);
            columns = 1;
            return;
        }

        throw new InvalidOperationException($"Unsupported reflected USC type '{typeName}'.");
    }

    private static string NormalizeMemberTypeName(string typeName)
    {
        return typeName switch
        {
            "mat2" => "float2x2",
            "mat2x3" => "float2x3",
            "mat2x4" => "float2x4",
            "mat3x2" => "float3x2",
            "mat3" => "float3x3",
            "mat3x4" => "float3x4",
            "mat4x2" => "float4x2",
            "mat4x3" => "float4x3",
            "mat4" => "float4x4",
            "vec2" => "float2",
            "vec3" => "float3",
            "vec4" => "float4",
            "uvec2" => "uint2",
            "uvec3" => "uint3",
            "uvec4" => "uint4",
            "ivec2" => "int2",
            "ivec3" => "int3",
            "ivec4" => "int4",
            "bool" => "bool",
            _ => typeName,
        };
    }
}
