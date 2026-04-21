using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Ruri.ShaderDecompiler.Unreal;

internal sealed class UnifiedShaderMetadataResolver
{
    private readonly Dictionary<string, UnifiedMaterialEntry> _materials;
    private readonly Dictionary<string, List<string>> _materialToShaderMapHashes;

    private UnifiedShaderMetadataResolver(
        Dictionary<string, UnifiedMaterialEntry> materials,
        Dictionary<string, List<string>> materialToShaderMapHashes)
    {
        _materials = materials;
        _materialToShaderMapHashes = materialToShaderMapHashes;
    }

    public static UnifiedShaderMetadataResolver? LoadFromFile(string jsonPath)
    {
        if (!File.Exists(jsonPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(jsonPath);
            var root = JsonSerializer.Deserialize<UnifiedShaderMetadataRoot>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            });

            if (root == null)
            {
                return null;
            }

            return new UnifiedShaderMetadataResolver(
                root.Materials ?? new Dictionary<string, UnifiedMaterialEntry>(StringComparer.OrdinalIgnoreCase),
                root.MaterialToShaderMapHashes ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    public Dictionary<string, HashSet<string>> BuildHashToMaterialsMap(string? normalizedMaterialFilter = null)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in _materialToShaderMapHashes)
        {
            string materialPath = kvp.Key.Replace('\\', '/');
            if (!string.IsNullOrWhiteSpace(normalizedMaterialFilter) &&
                !string.Equals(materialPath, normalizedMaterialFilter, StringComparison.OrdinalIgnoreCase) &&
                !materialPath.EndsWith(normalizedMaterialFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (string hash in kvp.Value)
            {
                if (!result.TryGetValue(hash, out HashSet<string>? materials))
                {
                    materials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    result[hash] = materials;
                }

                materials.Add(materialPath);
            }
        }

        return result;
    }

    public UnifiedShaderMapEntry? FindShaderMap(string materialPath, string? shaderMapHash)
    {
        if (string.IsNullOrWhiteSpace(materialPath) || string.IsNullOrWhiteSpace(shaderMapHash))
        {
            return null;
        }

        string normalized = materialPath.Replace('\\', '/');
        if (!_materials.TryGetValue(normalized, out UnifiedMaterialEntry? material) || material.ShaderMaps == null)
        {
            return null;
        }

        foreach (UnifiedShaderMapEntry shaderMap in material.ShaderMaps)
        {
            if (string.Equals(shaderMap.ShaderMapIdHash, shaderMapHash, StringComparison.OrdinalIgnoreCase))
            {
                return shaderMap;
            }
        }

        return null;
    }
}

internal sealed class UnifiedShaderMetadataRoot
{
    public Dictionary<string, List<string>>? MaterialToShaderMapHashes { get; set; }
    public Dictionary<string, UnifiedMaterialEntry>? Materials { get; set; }
}

internal sealed class UnifiedMaterialEntry
{
    public string? MaterialPath { get; set; }
    public List<string>? ShaderMapHashes { get; set; }
    public List<UnifiedShaderMapEntry>? ShaderMaps { get; set; }
}

internal sealed class UnifiedShaderMapEntry
{
    public string? ShaderPlatform { get; set; }
    public string? ShaderMapIdHash { get; set; }
}
