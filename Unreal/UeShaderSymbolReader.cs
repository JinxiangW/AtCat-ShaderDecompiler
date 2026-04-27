using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Ruri.ShaderTools.Unreal;

internal sealed class UeShaderSymbolReader
{
    private readonly string _exportRoot;
    private readonly string _exportRootName;
    private readonly Dictionary<string, UeShaderSymbolSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public UeShaderSymbolReader(string exportRoot)
    {
        _exportRoot = exportRoot;
        _exportRootName = Path.GetFileName(exportRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    public UeShaderSymbolSource? GetBestSource(IEnumerable<string> materialPaths, string? shaderPlatform = null)
    {
        UeShaderSymbolSource? best = null;
        foreach (string materialPath in materialPaths)
        {
            UeShaderSymbolSource? candidate = GetSource(materialPath, shaderPlatform);
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

    public UeShaderSymbolSource? GetSource(string materialPath, string? shaderPlatform = null)
    {
        string normalizedPath = materialPath.Replace('\\', '/');
        string cacheKey = string.IsNullOrWhiteSpace(shaderPlatform)
            ? normalizedPath
            : normalizedPath + "|" + shaderPlatform;
        if (_cache.TryGetValue(cacheKey, out UeShaderSymbolSource? cached))
        {
            return cached;
        }

        string? jsonPath = ResolveMaterialJsonPath(normalizedPath);
        if (jsonPath == null || !File.Exists(jsonPath))
        {
            _cache[cacheKey] = null;
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(jsonPath));
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
        {
            _cache[cacheKey] = null;
            return null;
        }

        UeShaderSymbolInputs? inputs = UeShaderSymbolInputsReader.Read(normalizedPath, shaderPlatform, root[0]);
        if (inputs == null)
        {
            _cache[cacheKey] = null;
            return null;
        }

        ShaderSymbolData metadata = UeShaderSymbolBuilder.Build(inputs);
        string header = UeShaderSymbolHeaderWriter.Build(inputs);
        int score = inputs.UsedLoadedMaterialResources ? 2 : inputs.NumericParameterInfos.Count > 0 ? 1 : 0;
        UeMaterialUniformBufferLayout? materialLayout = inputs.MaterialResourceCounts != null
            ? new UeMaterialUniformBufferLayout(inputs.MaterialResourceCounts)
            : null;
        UeShaderSymbolSource source = new(normalizedPath, metadata, header, score, inputs.UsedLoadedMaterialResources, materialLayout);
        _cache[cacheKey] = source;
        return source;
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
}

internal sealed record UeShaderSymbolSource(
    string MaterialPath,
    ShaderSymbolData Metadata,
    string Header,
    int Score,
    bool UsedLoadedMaterialResources,
    UeMaterialUniformBufferLayout? MaterialLayout);
