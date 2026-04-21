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
            var binding = new ResourceBinding
            {
                Name = resourceName,
                Set = 0,
                Binding = ReadIntProperty(ubo, "binding"),
                Type = ShaderResourceType.ConstantBuffer,
                RegisterType = 'b',
                Members = new List<StructMember>(),
            };

            if (types.TryGetValue(typeId, out JsonElement typeInfo) && typeInfo.TryGetProperty("members", out JsonElement members) && members.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (JsonElement member in members.EnumerateArray())
                {
                    int offset = ReadIntProperty(member, "offset");
                    string typeName = NormalizeMemberTypeName(member.TryGetProperty("type", out JsonElement memberType) ? memberType.GetString() ?? string.Empty : string.Empty);
                    binding.Members.Add(new StructMember
                    {
                        Name = member.TryGetProperty("name", out JsonElement memberName) ? memberName.GetString() ?? $"Member{index}" : $"Member{index}",
                        Index = index,
                        ByteOffset = offset,
                        ByteSize = EstimateByteSize(member, typeName, index < members.GetArrayLength() - 1 ? ReadIntProperty(members[index + 1], "offset") : -1),
                        TypeName = typeName,
                    });
                    index++;
                }
            }

            metadata.Resources.Add(binding);
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

    private static int EstimateByteSize(JsonElement member, string typeName, int nextOffset)
    {
        if (nextOffset > 0)
        {
            int offset = ReadIntProperty(member, "offset");
            int span = nextOffset - offset;
            if (span > 0)
            {
                return span;
            }
        }

        return typeName switch
        {
            "float" or "uint" or "int" or "bool" => 4,
            "float2" or "uint2" or "int2" => 8,
            "float3" or "uint3" or "int3" => 12,
            "float4" or "uint4" or "int4" => 16,
            "float2x2" => 32,
            "float3x3" => 48,
            "float4x4" => 64,
            _ => 16,
        };
    }

    private static string NormalizeMemberTypeName(string typeName)
    {
        return typeName switch
        {
            "mat2" => "float2x2",
            "mat3" => "float3x3",
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
