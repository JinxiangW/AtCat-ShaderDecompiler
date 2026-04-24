using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Ruri.ShaderTools.Unreal;

internal sealed class UnifiedShaderMetadataResolver
{
    private readonly Dictionary<string, UnifiedMaterialEntry> _materialInterfaces;
    private readonly Dictionary<string, List<string>> _packageShaderMapHashes;

    private UnifiedShaderMetadataResolver(
        Dictionary<string, UnifiedMaterialEntry> materialInterfaces,
        Dictionary<string, List<string>> packageShaderMapHashes)
    {
        _materialInterfaces = materialInterfaces;
        _packageShaderMapHashes = packageShaderMapHashes;
    }

    public static UnifiedShaderMetadataResolver? LoadFromFile(string jsonPath)
    {
        if (!File.Exists(jsonPath))
        {
            return null;
        }

        var json = File.ReadAllText(jsonPath);
        var root = JsonSerializer.Deserialize<UnifiedShaderMetadataRoot>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        });

        if (root == null)
        {
            throw new InvalidDataException($"Failed to deserialize unified shader metadata: {jsonPath}");
        }

        return new UnifiedShaderMetadataResolver(
            root.MaterialInterfaces ?? new Dictionary<string, UnifiedMaterialEntry>(StringComparer.OrdinalIgnoreCase),
            root.PackageShaderMapHashes ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase));
    }

    public Dictionary<string, HashSet<string>> BuildPackageShaderMapHashToMaterialsMap(string? normalizedMaterialFilter = null)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        HashSet<string>? filterVariants = BuildMaterialPathVariants(normalizedMaterialFilter);

        foreach (var kvp in _packageShaderMapHashes)
        {
            string materialPath = NormalizePathSlashes(kvp.Key);
            if (!MatchesMaterialFilter(materialPath, filterVariants))
            {
                continue;
            }

            foreach (string hash in kvp.Value)
            {
                AddHashMaterialMapping(result, hash, materialPath);
            }
        }

        foreach (var kvp in _materialInterfaces)
        {
            string materialPath = NormalizeMaterialPathKey(kvp.Key);
            if (!MatchesMaterialFilter(materialPath, filterVariants))
            {
                continue;
            }

            List<UnifiedShaderMapEntry>? loadedShaderMaps = kvp.Value.LoadedShaderMaps;
            if (loadedShaderMaps == null)
            {
                continue;
            }

            foreach (UnifiedShaderMapEntry shaderMap in loadedShaderMaps)
            {
                if (!string.IsNullOrWhiteSpace(shaderMap.CookedShaderMapIdHash))
                {
                    AddHashMaterialMapping(result, shaderMap.CookedShaderMapIdHash!, materialPath);
                }

                if (!string.IsNullOrWhiteSpace(shaderMap.ShaderContentHash))
                {
                    AddHashMaterialMapping(result, shaderMap.ShaderContentHash!, materialPath);
                }
            }
        }

        return result;
    }

    private static void AddHashMaterialMapping(Dictionary<string, HashSet<string>> result, string hash, string materialPath)
    {
        if (!result.TryGetValue(hash, out HashSet<string>? materials))
        {
            materials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            result[hash] = materials;
        }

        materials.Add(materialPath);
    }

    private static bool MatchesMaterialFilter(string materialPath, HashSet<string>? filterVariants)
    {
        if (filterVariants == null || filterVariants.Count == 0)
        {
            return true;
        }

        HashSet<string> materialVariants = BuildMaterialPathVariants(materialPath);
        return materialVariants.Overlaps(filterVariants);
    }

    private static HashSet<string> BuildMaterialPathVariants(string? materialPath)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(materialPath))
        {
            return result;
        }

        string normalized = NormalizePathSlashes(materialPath!);
        AddVariant(result, normalized);

        if (normalized.StartsWith("/", StringComparison.Ordinal))
        {
            AddVariant(result, normalized[1..]);
        }
        else
        {
            AddVariant(result, "/" + normalized);
        }

        int dotIndex = normalized.LastIndexOf('.');
        int slashIndex = normalized.LastIndexOf('/');
        if (dotIndex > slashIndex)
        {
            AddVariant(result, normalized[..dotIndex]);
        }

        foreach (string current in result.ToArray())
        {
            int contentMarkerIndex = current.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
            if (contentMarkerIndex >= 0)
            {
                string trimmed = current[(contentMarkerIndex + "/Content/".Length)..];
                AddVariant(result, trimmed);
                AddVariant(result, "/" + trimmed);
            }
            else if (current.StartsWith("Content/", StringComparison.OrdinalIgnoreCase))
            {
                string trimmed = current["Content/".Length..];
                AddVariant(result, trimmed);
                AddVariant(result, "/" + trimmed);
            }
        }

        return result;
    }

    private static string NormalizeMaterialPathKey(string materialPath)
    {
        string normalized = NormalizePathSlashes(materialPath);
        int dotIndex = normalized.LastIndexOf('.');
        int slashIndex = normalized.LastIndexOf('/');
        if (dotIndex > slashIndex)
        {
            normalized = normalized[..dotIndex];
        }

        return normalized;
    }

    private static string NormalizePathSlashes(string path)
    {
        return path.Replace('\\', '/');
    }

    private static void AddVariant(HashSet<string> variants, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            variants.Add(value!);
        }
    }
}

internal sealed class UnifiedShaderMetadataRoot
{
    public Dictionary<string, List<string>>? PackageShaderMapHashes { get; set; }
    public Dictionary<string, UnifiedMaterialEntry>? MaterialInterfaces { get; set; }
}

internal sealed class UnifiedMaterialEntry
{
    public string? MaterialPath { get; set; }
    public List<UnifiedShaderMapEntry>? LoadedShaderMaps { get; set; }
}

internal sealed class UnifiedShaderMapEntry
{
    public string? ShaderPlatform { get; set; }
    public string? CookedShaderMapIdHash { get; set; }
    public string? ShaderContentHash { get; set; }
}
