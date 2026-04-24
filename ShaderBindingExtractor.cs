using System.Collections.Concurrent;
using System.Text.Json;
using Ruri.ShaderTools.Unreal;

namespace Ruri.ShaderTools;

public static class ShaderBindingExtractor
{
    public static Dictionary<int, string> ScanAndRestore(string materialExportsDir, string shaderArchiveDir)
    {
        Dictionary<string, List<int>> shaderMapHashes = LoadShaderArchive(shaderArchiveDir);
        if (shaderMapHashes.Count == 0)
        {
            return new Dictionary<int, string>();
        }

        Dictionary<int, string> nameMap = new();
        Dictionary<string, List<int>> restoredMappings = new(StringComparer.OrdinalIgnoreCase);
        ConcurrentDictionary<string, Guid> textureGuidCache = new(StringComparer.OrdinalIgnoreCase);

        foreach (string file in Directory.GetFiles(materialExportsDir, "*.json", SearchOption.AllDirectories))
        {
            if (ShouldSkipMaterialFile(file))
            {
                continue;
            }

            try
            {
                FMaterialShaderMapId? id = ExtractMaterialShaderMapId(file, materialExportsDir, textureGuidCache);
                if (id is null)
                {
                    continue;
                }

                id.GetMaterialHash(out byte[] hashBytes);
                string hash = BitConverter.ToString(hashBytes).Replace("-", string.Empty).ToLowerInvariant();
                if (!shaderMapHashes.TryGetValue(hash, out List<int>? shaderIndices))
                {
                    continue;
                }

                string materialName = Path.GetFileNameWithoutExtension(file);
                if (!restoredMappings.TryGetValue(materialName, out List<int>? mappedIndices))
                {
                    mappedIndices = new List<int>();
                    restoredMappings[materialName] = mappedIndices;
                }

                mappedIndices.AddRange(shaderIndices);
                foreach (int index in shaderIndices)
                {
                    nameMap[index] = materialName;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error processing {file}: {ex.Message}");
            }
        }

        string outputPath = Path.Combine(materialExportsDir, "ShaderMappings.json");
        JsonSerializerOptions options = new() { WriteIndented = true };
        File.WriteAllText(outputPath, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["RestoredCount"] = restoredMappings.Count,
            ["Mappings"] = restoredMappings,
        }, options));

        return nameMap;
    }

    private static bool ShouldSkipMaterialFile(string filePath)
    {
        return filePath.Contains("ShaderArchive", StringComparison.OrdinalIgnoreCase)
            || filePath.EndsWith(".json.json", StringComparison.OrdinalIgnoreCase)
            || filePath.Contains("ShaderMappings.json", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, List<int>> LoadShaderArchive(string directory)
    {
        Dictionary<string, List<int>> map = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.GetFiles(directory, "ShaderArchive-*.json"))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                JsonElement shaderSection = root;
                if (root.TryGetProperty("SerializedShaders", out JsonElement serializedShaders))
                {
                    shaderSection = serializedShaders;
                }

                if (!shaderSection.TryGetProperty("ShaderMapHashes", out JsonElement hashes) || hashes.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                int index = 0;
                foreach (JsonElement hashElement in hashes.EnumerateArray())
                {
                    string? raw = hashElement.GetString();
                    if (!string.IsNullOrEmpty(raw))
                    {
                        string hash = TryDecodeHash(raw);
                        if (!map.TryGetValue(hash, out List<int>? indices))
                        {
                            indices = new List<int>();
                            map[hash] = indices;
                        }

                        indices.Add(index);
                    }

                    index++;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to load {file}: {ex.Message}");
            }
        }

        return map;
    }

    private static string TryDecodeHash(string raw)
    {
        try
        {
            byte[] bytes = Convert.FromBase64String(raw);
            return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
        }
        catch (FormatException)
        {
            return raw.ToLowerInvariant();
        }
    }

    private static FMaterialShaderMapId? ExtractMaterialShaderMapId(string jsonPath, string rootDir, ConcurrentDictionary<string, Guid> textureCache)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
        {
            return null;
        }

        JsonElement material = root[0];
        if (!material.TryGetProperty("Type", out JsonElement typeElement))
        {
            return null;
        }

        string? type = typeElement.GetString();
        if (!string.Equals(type, "Material", StringComparison.Ordinal) && !string.Equals(type, "MaterialInstanceConstant", StringComparison.Ordinal))
        {
            return null;
        }

        if (!material.TryGetProperty("Properties", out JsonElement properties))
        {
            return null;
        }

        FMaterialShaderMapId id = new()
        {
            Usage = EMaterialShaderMapUsage.Default,
            QualityLevel = ReadQualityLevel(properties),
            FeatureLevel = ReadFeatureLevel(properties),
        };

        Guid stateId = ReadStateId(properties);
        if (stateId == Guid.Empty && properties.TryGetProperty("Parent", out JsonElement parentProperty))
        {
            string parentPath = GetPathFromObject(parentProperty);
            if (!string.IsNullOrEmpty(parentPath))
            {
                stateId = ResolveParentStateId(parentPath, rootDir);
            }
        }

        id.BaseMaterialId = stateId;
        id.ReferencedTextures = ReadReferencedTextures(material, properties, rootDir, textureCache);
        return id;
    }

    private static EMaterialQualityLevel ReadQualityLevel(JsonElement properties)
    {
        if (!properties.TryGetProperty("QualityLevel", out JsonElement qualityElement))
        {
            return EMaterialQualityLevel.High;
        }

        string value = qualityElement.GetString() ?? string.Empty;
        if (value.Contains("Low", StringComparison.OrdinalIgnoreCase))
        {
            return EMaterialQualityLevel.Low;
        }

        if (value.Contains("Medium", StringComparison.OrdinalIgnoreCase))
        {
            return EMaterialQualityLevel.Medium;
        }

        if (value.Contains("Epic", StringComparison.OrdinalIgnoreCase))
        {
            return EMaterialQualityLevel.Epic;
        }

        return EMaterialQualityLevel.High;
    }

    private static int ReadFeatureLevel(JsonElement properties)
    {
        if (!properties.TryGetProperty("FeatureLevel", out JsonElement featureElement))
        {
            return (int)ERHIFeatureLevel.SM6;
        }

        string value = featureElement.GetString() ?? string.Empty;
        if (value.Contains("SM5", StringComparison.OrdinalIgnoreCase))
        {
            return (int)ERHIFeatureLevel.SM5;
        }

        return (int)ERHIFeatureLevel.SM6;
    }

    private static Guid ReadStateId(JsonElement properties)
    {
        if (!properties.TryGetProperty("StateId", out JsonElement stateElement))
        {
            return Guid.Empty;
        }

        return Guid.TryParse(stateElement.GetString(), out Guid stateId) ? stateId : Guid.Empty;
    }

    private static List<Guid> ReadReferencedTextures(JsonElement material, JsonElement properties, string rootDir, ConcurrentDictionary<string, Guid> textureCache)
    {
        List<Guid> result = new();
        AddReferencedTextures(result, properties, "ReferencedTextures", rootDir, textureCache);
        if (material.TryGetProperty("CachedExpressionData", out JsonElement cachedExpressionData))
        {
            AddReferencedTextures(result, cachedExpressionData, "ReferencedTextures", rootDir, textureCache);
        }

        return result;
    }

    private static void AddReferencedTextures(List<Guid> target, JsonElement container, string propertyName, string rootDir, ConcurrentDictionary<string, Guid> textureCache)
    {
        if (!container.TryGetProperty(propertyName, out JsonElement textures) || textures.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement texture in textures.EnumerateArray())
        {
            string texturePath = GetPathFromObject(texture);
            if (string.IsNullOrEmpty(texturePath))
            {
                continue;
            }

            Guid lightingGuid = GetTextureLightingGuid(texturePath, rootDir, textureCache);
            if (lightingGuid != Guid.Empty && !target.Contains(lightingGuid))
            {
                target.Add(lightingGuid);
            }
        }
    }

    private static string GetPathFromObject(JsonElement obj)
    {
        if (!obj.TryGetProperty("ObjectPath", out JsonElement objectPathElement))
        {
            return string.Empty;
        }

        string? objectPath = objectPathElement.GetString();
        if (string.IsNullOrEmpty(objectPath))
        {
            return string.Empty;
        }

        int dotIndex = objectPath.LastIndexOf('.');
        return dotIndex > 0 ? objectPath[..dotIndex] : objectPath;
    }

    private static Guid ResolveParentStateId(string parentPath, string rootDir)
    {
        string fullPath = Path.Combine(rootDir, parentPath.Replace("/Game/", string.Empty, StringComparison.Ordinal) + ".json");
        if (!File.Exists(fullPath))
        {
            return Guid.Empty;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(fullPath));
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            {
                return Guid.Empty;
            }

            JsonElement material = root[0];
            if (!material.TryGetProperty("Properties", out JsonElement properties))
            {
                return Guid.Empty;
            }

            Guid stateId = ReadStateId(properties);
            if (stateId != Guid.Empty)
            {
                return stateId;
            }

            if (material.TryGetProperty("Type", out JsonElement typeElement)
                && string.Equals(typeElement.GetString(), "MaterialInstanceConstant", StringComparison.Ordinal)
                && properties.TryGetProperty("Parent", out JsonElement parentElement))
            {
                string nextParentPath = GetPathFromObject(parentElement);
                if (!string.IsNullOrEmpty(nextParentPath))
                {
                    return ResolveParentStateId(nextParentPath, rootDir);
                }
            }
        }
        catch
        {
        }

        return Guid.Empty;
    }

    private static Guid GetTextureLightingGuid(string objectPath, string rootDir, ConcurrentDictionary<string, Guid> cache)
    {
        if (cache.TryGetValue(objectPath, out Guid cached))
        {
            return cached;
        }

        string fullPath = Path.Combine(rootDir, objectPath.Replace("/Game/", string.Empty, StringComparison.Ordinal) + ".json");
        if (!File.Exists(fullPath))
        {
            return Guid.Empty;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(fullPath));
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            {
                return Guid.Empty;
            }

            JsonElement texture = root[0];
            if (texture.TryGetProperty("Properties", out JsonElement properties)
                && properties.TryGetProperty("LightingGuid", out JsonElement lightingGuidElement)
                && Guid.TryParse(lightingGuidElement.GetString(), out Guid lightingGuid))
            {
                cache[objectPath] = lightingGuid;
                return lightingGuid;
            }
        }
        catch
        {
        }

        return Guid.Empty;
    }
}
