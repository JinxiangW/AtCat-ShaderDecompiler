using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Ruri.ShaderDecompiler.Unreal;

internal sealed class UeMaterialJsonSymbolExtractor
{
    private readonly string _exportRoot;
    private readonly string _exportRootName;
    private readonly UnifiedShaderMetadataResolver? _contextResolver;
    private readonly Dictionary<string, UeMaterialSymbolInfo?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public UeMaterialJsonSymbolExtractor(string exportRoot, UnifiedShaderMetadataResolver? contextResolver = null)
    {
        _exportRoot = exportRoot;
        _exportRootName = Path.GetFileName(exportRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        _contextResolver = contextResolver;
    }

    public UeMaterialSymbolInfo? GetBestMaterial(IEnumerable<string> materialPaths, string? shaderMapHash = null)
    {
        UeMaterialSymbolInfo? best = null;

        foreach (string materialPath in materialPaths)
        {
            UeMaterialSymbolInfo? candidate = GetMaterial(materialPath, shaderMapHash);
            if (candidate == null)
            {
                continue;
            }

            if (best == null || candidate.Score > best.Score)
            {
                best = candidate;
            }
        }

        return best;
    }

    public UeMaterialSymbolInfo? GetMaterial(string materialPath, string? shaderMapHash = null)
    {
        string normalizedPath = materialPath.Replace('\\', '/');
        string cacheKey = string.IsNullOrWhiteSpace(shaderMapHash)
            ? normalizedPath
            : normalizedPath + "|" + shaderMapHash;
        if (_cache.TryGetValue(cacheKey, out UeMaterialSymbolInfo? cached))
        {
            return cached;
        }

        string? jsonPath = ResolveMaterialJsonPath(normalizedPath);
        if (jsonPath == null || !File.Exists(jsonPath))
        {
            _cache[cacheKey] = null;
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(jsonPath));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            {
                _cache[normalizedPath] = null;
                return null;
            }

            JsonElement asset = root[0];
            UeMaterialSymbolInfo info = BuildSymbolInfo(normalizedPath, asset, shaderMapHash);
            _cache[cacheKey] = info;
            return info;
        }
        catch
        {
            _cache[cacheKey] = null;
            return null;
        }
    }

    private string? ResolveMaterialJsonPath(string materialPath)
    {
        string normalized = materialPath.TrimStart('/');
        if (!string.IsNullOrEmpty(_exportRootName) &&
            normalized.StartsWith(_exportRootName + "/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[(_exportRootName.Length + 1)..];
        }

        string relative = normalized.Replace('/', Path.DirectorySeparatorChar);
        string direct = Path.Combine(_exportRoot, relative + ".json");
        if (File.Exists(direct))
        {
            return direct;
        }

        int dotIndex = relative.LastIndexOf('.');
        if (dotIndex > 0)
        {
            string withoutObjectSuffix = relative[..dotIndex];
            string alias = Path.Combine(_exportRoot, withoutObjectSuffix + ".json");
            if (File.Exists(alias))
            {
                return alias;
            }
        }

        return null;
    }

    private UeMaterialSymbolInfo BuildSymbolInfo(string materialPath, JsonElement asset, string? shaderMapHash)
    {
        var metadata = new ShaderSymbolData
        {
            DebugName = materialPath
        };

        List<string> numericNames = new();
        List<string> textureNames = new();
        bool usedLoadedResources = false;
        UnifiedShaderMapEntry? targetShaderMap = _contextResolver?.FindShaderMap(materialPath, shaderMapHash);

        if (asset.TryGetProperty("LoadedMaterialResources", out JsonElement loadedResources) &&
            loadedResources.ValueKind == JsonValueKind.Array &&
            TryExtractFromLoadedMaterialResources(metadata, loadedResources, numericNames, textureNames, targetShaderMap))
        {
            usedLoadedResources = true;
        }

        if (asset.TryGetProperty("Properties", out JsonElement properties) && properties.ValueKind == JsonValueKind.Object)
        {
            if (!usedLoadedResources)
            {
                ExtractFallbackTextureProperties(metadata, properties, textureNames);
            }

            ExtractFallbackNumericProperties(properties, numericNames);
        }

        DeduplicateResources(metadata);
        string header = BuildHeader(materialPath, usedLoadedResources, numericNames, textureNames);
        int score = usedLoadedResources ? 2 : metadata.Resources.Count > 0 || numericNames.Count > 0 ? 1 : 0;

        return new UeMaterialSymbolInfo(materialPath, metadata, header, score, usedLoadedResources);
    }

    private static bool TryExtractFromLoadedMaterialResources(
        ShaderSymbolData metadata,
        JsonElement loadedResources,
        List<string> numericNames,
        List<string> textureNames,
        UnifiedShaderMapEntry? targetShaderMap)
    {
        bool foundAny = false;

        foreach (JsonElement resource in loadedResources.EnumerateArray())
        {
            if (targetShaderMap != null)
            {
                string? candidateHash = ReadNestedString(resource, "LoadedShaderMap", "ShaderMapId", "CookedShaderMapIdHash", "Hash");
                if (!string.Equals(candidateHash, targetShaderMap.ShaderMapIdHash, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            if (!resource.TryGetProperty("LoadedShaderMap", out JsonElement loadedShaderMap) ||
                !loadedShaderMap.TryGetProperty("Content", out JsonElement content) ||
                !content.TryGetProperty("MaterialCompilationOutput", out JsonElement compilationOutput) ||
                !compilationOutput.TryGetProperty("UniformExpressionSet", out JsonElement uniformExpressionSet))
            {
                continue;
            }

            ExtractUniformNumericParameters(uniformExpressionSet, numericNames);
            ExtractUniformTextureParameters(metadata, uniformExpressionSet, textureNames);
            foundAny = true;
            break;
        }

        return foundAny;
    }

    private static void ExtractUniformNumericParameters(JsonElement uniformExpressionSet, List<string> numericNames)
    {
        if (!uniformExpressionSet.TryGetProperty("UniformNumericParameters", out JsonElement numericParameters) ||
            numericParameters.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement parameter in numericParameters.EnumerateArray())
        {
            string? name = ReadNestedString(parameter, "ParameterInfo", "Name");
            if (!string.IsNullOrWhiteSpace(name) && !string.Equals(name, "None", StringComparison.OrdinalIgnoreCase))
            {
                numericNames.Add(name);
            }
        }
    }

    private static void ExtractUniformTextureParameters(ShaderSymbolData metadata, JsonElement uniformExpressionSet, List<string> textureNames)
    {
        if (!uniformExpressionSet.TryGetProperty("UniformTextureParameters", out JsonElement uniformTextureParameters) ||
            uniformTextureParameters.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        string[] prefixes =
        {
            "Texture2D",
            "TextureCube",
            "Texture2DArray",
            "TextureCubeArray",
            "VolumeTexture",
            "VirtualTexturePhysical"
        };

        ShaderResourceType[] resourceTypes =
        {
            ShaderResourceType.Texture2D,
            ShaderResourceType.TextureCube,
            ShaderResourceType.Texture2DArray,
            ShaderResourceType.TextureCubeArray,
            ShaderResourceType.Texture3D,
            ShaderResourceType.Texture
        };

        int typeIndex = 0;
        foreach (JsonElement typedArray in uniformTextureParameters.EnumerateArray())
        {
            if (typedArray.ValueKind != JsonValueKind.Array)
            {
                typeIndex++;
                continue;
            }

            int binding = 0;
            foreach (JsonElement entry in typedArray.EnumerateArray())
            {
                string? name = ReadNestedString(entry, "ParameterInfo", "Name");
                string prefix = prefixes[Math.Min(typeIndex, prefixes.Length - 1)];
                ShaderResourceType resourceType = resourceTypes[Math.Min(typeIndex, resourceTypes.Length - 1)];

                if (!string.IsNullOrWhiteSpace(name) && !string.Equals(name, "None", StringComparison.OrdinalIgnoreCase))
                {
                    AddTextureResource(metadata, name, binding, prefix, resourceType);
                    textureNames.Add(name);
                }

                binding++;
            }

            typeIndex++;
        }

        if (uniformExpressionSet.TryGetProperty("UniformExternalTextureParameters", out JsonElement externalTextures) &&
            externalTextures.ValueKind == JsonValueKind.Array)
        {
            int binding = 0;
            foreach (JsonElement entry in externalTextures.EnumerateArray())
            {
                string? name = ReadString(entry, "ParameterName");
                if (!string.IsNullOrWhiteSpace(name) && !string.Equals(name, "None", StringComparison.OrdinalIgnoreCase))
                {
                    AddTextureResource(metadata, name, binding, "ExternalTexture", ShaderResourceType.Texture);
                    textureNames.Add(name);
                }

                binding++;
            }
        }
    }

    private static void ExtractFallbackTextureProperties(ShaderSymbolData metadata, JsonElement properties, List<string> textureNames)
    {
        if (!properties.TryGetProperty("TextureParameterValues", out JsonElement textureParameters) ||
            textureParameters.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        int binding = 0;
        foreach (JsonElement entry in textureParameters.EnumerateArray())
        {
            string? name = ReadNestedString(entry, "ParameterInfo", "Name");
            if (!string.IsNullOrWhiteSpace(name) && !string.Equals(name, "None", StringComparison.OrdinalIgnoreCase))
            {
                AddTextureResource(metadata, name, binding, "Texture2D", ShaderResourceType.Texture2D);
                textureNames.Add(name);
            }

            binding++;
        }
    }

    private static void ExtractFallbackNumericProperties(JsonElement properties, List<string> numericNames)
    {
        AddParameterNames(properties, "ScalarParameterValues", numericNames);
        AddParameterNames(properties, "VectorParameterValues", numericNames);
        AddParameterNames(properties, "DoubleVectorParameterValues", numericNames);
    }

    private static void AddParameterNames(JsonElement properties, string propertyName, List<string> names)
    {
        if (!properties.TryGetProperty(propertyName, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement entry in array.EnumerateArray())
        {
            string? name = ReadNestedString(entry, "ParameterInfo", "Name");
            if (!string.IsNullOrWhiteSpace(name) && !string.Equals(name, "None", StringComparison.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }
    }

    private static void AddTextureResource(ShaderSymbolData metadata, string name, int binding, string prefix, ShaderResourceType textureType)
    {
        metadata.Resources.Add(new ResourceBinding
        {
            Name = name,
            Binding = binding,
            Set = 0,
            Type = textureType,
            RegisterType = 't'
        });

        metadata.Resources.Add(new ResourceBinding
        {
            Name = name + "Sampler",
            Binding = binding,
            Set = 0,
            Type = ShaderResourceType.Sampler,
            RegisterType = 's'
        });
    }

    private static void DeduplicateResources(ShaderSymbolData metadata)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        metadata.Resources.RemoveAll(resource =>
        {
            string key = $"{resource.Set}:{resource.Binding}:{resource.RegisterType}:{resource.Name}";
            return !seen.Add(key);
        });
    }

    private static string BuildHeader(string materialPath, bool usedLoadedResources, List<string> numericNames, List<string> textureNames)
    {
        var sb = new StringBuilder();
        sb.AppendLine("/*");
        sb.AppendLine(" * UE Runtime Metadata");
        sb.AppendLine($" * Material: {materialPath}");
        sb.AppendLine($" * Source: {(usedLoadedResources ? "LoadedMaterialResources.UniformExpressionSet" : "Material Properties Fallback")}");

        List<string> distinctTextures = textureNames.Distinct(StringComparer.Ordinal).Take(16).ToList();
        if (distinctTextures.Count > 0)
        {
            sb.AppendLine(" * Texture Params: " + string.Join(", ", distinctTextures));
        }

        List<string> distinctNumeric = numericNames.Distinct(StringComparer.Ordinal).Take(16).ToList();
        if (distinctNumeric.Count > 0)
        {
            sb.AppendLine(" * Numeric Params: " + string.Join(", ", distinctNumeric));
        }

        sb.AppendLine(" */");
        sb.AppendLine();
        return sb.ToString();
    }

    private static string? ReadNestedString(JsonElement element, string objectProperty, string valueProperty)
    {
        if (!element.TryGetProperty(objectProperty, out JsonElement nested) || nested.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ReadString(nested, valueProperty);
    }

    private static string? ReadNestedString(JsonElement element, params string[] path)
    {
        JsonElement current = element;
        foreach (string segment in path)
        {
            if (!current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }
}

internal sealed record UeMaterialSymbolInfo(
    string MaterialPath,
    ShaderSymbolData Metadata,
    string Header,
    int Score,
    bool UsedLoadedResources);
