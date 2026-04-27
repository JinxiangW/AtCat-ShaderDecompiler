using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ruri.ShaderTools.Engine;

namespace Ruri.ShaderTools.Unreal;

internal static class UeShaderLibraryAssetIndexReader
{
    public static UeShaderLibraryAssetIndex Read(
        string shaderLibraryPath,
        UnrealShaderLibraryReader.ShaderLibrary lib,
        string unifiedMetadataPath,
        string? materialFilter)
    {
        Dictionary<int, HashSet<string>> usageByShaderIndex = new();
        Dictionary<int, string> displayNameByShaderIndex = new();

        string? normalizedMaterialFilter = string.IsNullOrWhiteSpace(materialFilter)
            ? null
            : materialFilter.Replace('\\', '/');

        ShaderLibraryTruthSidecarResolver sidecarResolver = ShaderLibraryTruthSidecarResolver.Load(shaderLibraryPath);
        AddSidecarUsage(lib, sidecarResolver, usageByShaderIndex);

        if (File.Exists(unifiedMetadataPath))
        {
            UnifiedShaderMetadataResolver? unifiedResolver = UnifiedShaderMetadataResolver.LoadFromFile(unifiedMetadataPath);
            if (unifiedResolver != null)
            {
                AddUnifiedMetadataUsage(lib, unifiedResolver, normalizedMaterialFilter, usageByShaderIndex, displayNameByShaderIndex);
            }
        }

        return new UeShaderLibraryAssetIndex(usageByShaderIndex, displayNameByShaderIndex);
    }

    private static void AddSidecarUsage(
        UnrealShaderLibraryReader.ShaderLibrary lib,
        ShaderLibraryTruthSidecarResolver sidecarResolver,
        Dictionary<int, HashSet<string>> usageByShaderIndex)
    {
        for (int shaderMapIndex = 0; shaderMapIndex < Math.Min(lib.ShaderMapEntries.Length, lib.ShaderMapHashes.Count); shaderMapIndex++)
        {
            IReadOnlyCollection<string> assets = sidecarResolver.GetAssetsForShaderMap(lib.ShaderMapHashes[shaderMapIndex]);
            if (assets.Count == 0)
            {
                continue;
            }

            UnrealShaderLibraryReader.FShaderMapEntry shaderMapEntry = lib.ShaderMapEntries[shaderMapIndex];
            foreach (string asset in assets)
            {
                for (uint i = 0; i < shaderMapEntry.NumShaders; i++)
                {
                    long shaderIndicesOffset = shaderMapEntry.ShaderIndicesOffset + i;
                    if (shaderIndicesOffset < 0 || shaderIndicesOffset >= lib.ShaderIndices.Length)
                    {
                        continue;
                    }

                    int shaderIndex = (int)lib.ShaderIndices[shaderIndicesOffset];
                    AddUsage(usageByShaderIndex, shaderIndex, asset);
                }
            }
        }

        for (int shaderIndex = 0; shaderIndex < lib.ShaderHashes.Count && shaderIndex < lib.ShaderEntries.Length; shaderIndex++)
        {
            IReadOnlyCollection<string> assets = sidecarResolver.GetAssetsForShaderHash(lib.ShaderHashes[shaderIndex], lib.ShaderEntries[shaderIndex].Frequency);
            foreach (string asset in assets)
            {
                AddUsage(usageByShaderIndex, shaderIndex, asset);
            }
        }
    }

    private static void AddUnifiedMetadataUsage(
        UnrealShaderLibraryReader.ShaderLibrary lib,
        UnifiedShaderMetadataResolver unifiedResolver,
        string? normalizedMaterialFilter,
        Dictionary<int, HashSet<string>> usageByShaderIndex,
        Dictionary<int, string> displayNameByShaderIndex)
    {
        Dictionary<string, HashSet<string>> hashToMaterials = unifiedResolver.BuildPackageShaderMapHashToMaterialsMap(normalizedMaterialFilter);

        int mapCount = Math.Min(lib.ShaderMapEntries.Length, lib.ShaderMapHashes.Count);
        for (int shaderMapIndex = 0; shaderMapIndex < mapCount; shaderMapIndex++)
        {
            string hash = lib.ShaderMapHashes[shaderMapIndex];
            if (!hashToMaterials.TryGetValue(hash, out HashSet<string>? materials))
            {
                continue;
            }

            UnrealShaderLibraryReader.FShaderMapEntry shaderMapEntry = lib.ShaderMapEntries[shaderMapIndex];
            string representativeMaterial = materials.FirstOrDefault() ?? "Unknown";
            string displayName = Path.GetFileNameWithoutExtension(representativeMaterial);
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = "UnknownMaterial";
            }

            for (uint i = 0; i < shaderMapEntry.NumShaders; i++)
            {
                long shaderIndicesOffset = shaderMapEntry.ShaderIndicesOffset + i;
                if (shaderIndicesOffset < 0 || shaderIndicesOffset >= lib.ShaderIndices.Length)
                {
                    continue;
                }

                int shaderIndex = (int)lib.ShaderIndices[shaderIndicesOffset];
                foreach (string material in materials)
                {
                    AddUsage(usageByShaderIndex, shaderIndex, material);
                }

                if (!displayNameByShaderIndex.ContainsKey(shaderIndex))
                {
                    displayNameByShaderIndex[shaderIndex] = displayName;
                }
            }
        }
    }

    private static void AddUsage(Dictionary<int, HashSet<string>> usageByShaderIndex, int shaderIndex, string asset)
    {
        if (!usageByShaderIndex.TryGetValue(shaderIndex, out HashSet<string>? materials))
        {
            materials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            usageByShaderIndex[shaderIndex] = materials;
        }

        materials.Add(asset);
    }
}

internal sealed class UeShaderLibraryAssetIndex
{
    public UeShaderLibraryAssetIndex(Dictionary<int, HashSet<string>> usageByShaderIndex, Dictionary<int, string> displayNameByShaderIndex)
    {
        UsageByShaderIndex = usageByShaderIndex;
        DisplayNameByShaderIndex = displayNameByShaderIndex;
    }

    public Dictionary<int, HashSet<string>> UsageByShaderIndex { get; }
    public Dictionary<int, string> DisplayNameByShaderIndex { get; }
}
