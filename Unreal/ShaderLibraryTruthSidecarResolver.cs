using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Ruri.ShaderTools.Unreal;

internal sealed class ShaderLibraryTruthSidecarResolver
{
    private readonly Dictionary<string, HashSet<string>> _shaderMapToAssets;
    private readonly Dictionary<string, Dictionary<string, HashSet<byte>>> _shaderHashToAssetsByFrequency;

    private ShaderLibraryTruthSidecarResolver(
        Dictionary<string, HashSet<string>> shaderMapToAssets,
        Dictionary<string, Dictionary<string, HashSet<byte>>> shaderHashToAssetsByFrequency)
    {
        _shaderMapToAssets = shaderMapToAssets;
        _shaderHashToAssetsByFrequency = shaderHashToAssetsByFrequency;
    }

    public static ShaderLibraryTruthSidecarResolver Load(string shaderLibraryPath)
    {
        return new ShaderLibraryTruthSidecarResolver(
            LoadAssetInfo(shaderLibraryPath + ".assetinfo.json"),
            LoadStableInfo(shaderLibraryPath + ".stableinfo.json"));
    }

    public IReadOnlyCollection<string> GetAssetsForShaderMap(string shaderMapHash)
    {
        if (string.IsNullOrWhiteSpace(shaderMapHash) || !_shaderMapToAssets.TryGetValue(shaderMapHash, out HashSet<string>? assets))
        {
            return Array.Empty<string>();
        }

        return assets.ToArray();
    }

    public IReadOnlyCollection<string> GetAssetsForShaderHash(string shaderHash, byte frequency)
    {
        if (string.IsNullOrWhiteSpace(shaderHash) || !_shaderHashToAssetsByFrequency.TryGetValue(shaderHash, out Dictionary<string, HashSet<byte>>? assetMap))
        {
            return Array.Empty<string>();
        }

        List<string> results = new();
        foreach ((string asset, HashSet<byte> frequencies) in assetMap)
        {
            if (frequencies.Contains(frequency))
            {
                results.Add(asset);
            }
        }

        return results;
    }

    private static Dictionary<string, HashSet<string>> LoadAssetInfo(string path)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return result;
        }

        var root = JsonSerializer.Deserialize<AssetInfoRoot>(File.ReadAllText(path), CreateJsonOptions());
        if (root == null)
        {
            throw new InvalidDataException($"Failed to deserialize asset info sidecar: {path}");
        }

        if (root.ShaderCodeToAssets == null)
        {
            throw new InvalidDataException($"Asset info sidecar is missing ShaderCodeToAssets: {path}");
        }

        foreach (AssetInfoEntry entry in root.ShaderCodeToAssets)
        {
            if (string.IsNullOrWhiteSpace(entry.ShaderMapHash) || entry.Assets == null)
            {
                continue;
            }

            if (!result.TryGetValue(entry.ShaderMapHash, out HashSet<string>? assets))
            {
                assets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                result[entry.ShaderMapHash] = assets;
            }

            foreach (string asset in entry.Assets.Where(a => !string.IsNullOrWhiteSpace(a)))
            {
                assets.Add(asset.Replace('\\', '/'));
            }
        }

        return result;
    }

    private static Dictionary<string, Dictionary<string, HashSet<byte>>> LoadStableInfo(string path)
    {
        var result = new Dictionary<string, Dictionary<string, HashSet<byte>>>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return result;
        }

        var root = JsonSerializer.Deserialize<StableInfoRoot>(File.ReadAllText(path), CreateJsonOptions());
        if (root == null)
        {
            throw new InvalidDataException($"Failed to deserialize stable info sidecar: {path}");
        }

        if (root.ShaderMaps == null)
        {
            throw new InvalidDataException($"Stable info sidecar is missing ShaderMaps: {path}");
        }

        foreach (StableInfoEntry shaderMap in root.ShaderMaps)
        {
            if (shaderMap.ShaderHashes == null || shaderMap.Frequencies == null || shaderMap.Assets == null)
            {
                continue;
            }

            int count = Math.Min(shaderMap.ShaderHashes.Count, shaderMap.Frequencies.Count);
            for (int i = 0; i < count; i++)
            {
                string shaderHash = shaderMap.ShaderHashes[i];
                if (string.IsNullOrWhiteSpace(shaderHash))
                {
                    continue;
                }

                if (!result.TryGetValue(shaderHash, out Dictionary<string, HashSet<byte>>? assetMap))
                {
                    assetMap = new Dictionary<string, HashSet<byte>>(StringComparer.OrdinalIgnoreCase);
                    result[shaderHash] = assetMap;
                }

                foreach (string asset in shaderMap.Assets.Where(a => !string.IsNullOrWhiteSpace(a)))
                {
                    string normalizedAsset = asset.Replace('\\', '/');
                    if (!assetMap.TryGetValue(normalizedAsset, out HashSet<byte>? frequencies))
                    {
                        frequencies = new HashSet<byte>();
                        assetMap[normalizedAsset] = frequencies;
                    }

                    frequencies.Add(shaderMap.Frequencies[i]);
                }
            }
        }

        return result;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };
    }

    private sealed class AssetInfoRoot
    {
        public List<AssetInfoEntry>? ShaderCodeToAssets { get; set; }
    }

    private sealed class AssetInfoEntry
    {
        public string? ShaderMapHash { get; set; }
        public List<string>? Assets { get; set; }
    }

    private sealed class StableInfoRoot
    {
        public List<StableInfoEntry>? ShaderMaps { get; set; }
    }

    private sealed class StableInfoEntry
    {
        public string? ShaderMapHash { get; set; }
        public List<string>? Assets { get; set; }
        public List<string>? ShaderHashes { get; set; }
        public List<byte>? Frequencies { get; set; }
    }
}
