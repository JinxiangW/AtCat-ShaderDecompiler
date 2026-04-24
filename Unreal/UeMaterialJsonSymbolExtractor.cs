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

    public UeMaterialSymbolInfo? GetBestMaterial(IEnumerable<string> materialPaths, string? shaderPlatform = null)
    {
        UeMaterialSymbolInfo? best = null;

        foreach (string materialPath in materialPaths)
        {
            UeMaterialSymbolInfo? candidate = GetMaterial(materialPath, shaderPlatform);
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

    public UeMaterialSymbolInfo? GetMaterial(string materialPath, string? shaderPlatform = null)
    {
        string normalizedPath = materialPath.Replace('\\', '/');
        string cacheKey = string.IsNullOrWhiteSpace(shaderPlatform)
            ? normalizedPath
            : normalizedPath + "|" + shaderPlatform;
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

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(jsonPath));
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
        {
            _cache[cacheKey] = null;
            return null;
        }

        JsonElement asset = root[0];
        UeMaterialSymbolInfo info = BuildSymbolInfo(normalizedPath, asset, shaderPlatform);
        _cache[cacheKey] = info;
        return info;
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

    private UeMaterialSymbolInfo BuildSymbolInfo(string materialPath, JsonElement asset, string? shaderPlatform)
    {
        var metadata = new ShaderSymbolData
        {
            DebugName = materialPath
        };

        List<string> fMaterialParameterInfoLines = new();
        List<string> fMaterialTextureParameterInfoLines = new();
        List<string> fRHIUniformBufferLayoutInitializerResourceLines = new();
        List<string> shaderSideBindingLines = new();
        Dictionary<int, string> referencedTexturesByTextureIndex = BuildReferencedTextureIdentityMap(materialPath, asset);
        if (referencedTexturesByTextureIndex.Count > 0)
        {
            Console.WriteLine($"[纹理真理] {materialPath} referencedTextures={referencedTexturesByTextureIndex.Count}");
            foreach (KeyValuePair<int, string> pair in referencedTexturesByTextureIndex.OrderBy(pair => pair.Key).Take(8))
            {
                Console.WriteLine($"[纹理真理]   tex{pair.Key} -> {pair.Value}");
            }
        }
        bool usedLoadedResources = false;
        if (asset.TryGetProperty("LoadedMaterialResources", out JsonElement loadedResources) &&
            loadedResources.ValueKind == JsonValueKind.Array &&
            TryExtractFromLoadedMaterialResourcesUniformExpressionSet(metadata, loadedResources, shaderPlatform, fMaterialParameterInfoLines, fMaterialTextureParameterInfoLines, fRHIUniformBufferLayoutInitializerResourceLines, shaderSideBindingLines, referencedTexturesByTextureIndex))
        {
            usedLoadedResources = true;
        }

        if (asset.TryGetProperty("Properties", out JsonElement properties) && properties.ValueKind == JsonValueKind.Object)
        {
            if (!usedLoadedResources)
            {
            }

            ExtractFallbackMaterialParameterInfos(properties, fMaterialParameterInfoLines);
        }

        DeduplicateResources(metadata);
        string header = BuildHeader(materialPath, shaderPlatform, usedLoadedResources, fMaterialParameterInfoLines, fMaterialTextureParameterInfoLines, fRHIUniformBufferLayoutInitializerResourceLines, shaderSideBindingLines);
        int score = usedLoadedResources ? 2 : metadata.Resources.Count > 0 || fMaterialParameterInfoLines.Count > 0 ? 1 : 0;

        return new UeMaterialSymbolInfo(materialPath, metadata, header, score, usedLoadedResources);
    }

    private static bool TryExtractFromLoadedMaterialResourcesUniformExpressionSet(
        ShaderSymbolData metadata,
        JsonElement loadedResources,
        string? shaderPlatform,
        List<string> fMaterialParameterInfoLines,
        List<string> fMaterialTextureParameterInfoLines,
        List<string> fRHIUniformBufferLayoutInitializerResourceLines,
        List<string> shaderSideBindingLines,
        Dictionary<int, string> referencedTexturesByTextureIndex)
    {
        JsonElement? selectedResource = null;

        foreach (JsonElement resource in loadedResources.EnumerateArray())
        {
            if (!resource.TryGetProperty("LoadedShaderMap", out JsonElement loadedShaderMap) ||
                loadedShaderMap.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? candidateShaderPlatform = ReadString(loadedShaderMap, "ShaderPlatform");
            if (!string.IsNullOrWhiteSpace(shaderPlatform) &&
                !string.Equals(candidateShaderPlatform, shaderPlatform, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            selectedResource = resource;
            break;
        }

        if (!selectedResource.HasValue)
        {
            foreach (JsonElement resource in loadedResources.EnumerateArray())
            {
                selectedResource = resource;
                break;
            }
        }

        if (!selectedResource.HasValue || !TryGetUniformExpressionSet(selectedResource.Value, out JsonElement uniformExpressionSet))
        {
            return false;
        }

        AddProvenMaterialConstantBuffer(metadata, uniformExpressionSet);
        ExtractUniformNumericParameterInfos(uniformExpressionSet, fMaterialParameterInfoLines);
        ExtractFMaterialTextureParameterInfos(uniformExpressionSet, fMaterialTextureParameterInfoLines, referencedTexturesByTextureIndex);
        ExtractMaterialUniformBufferResourceMembers(uniformExpressionSet, fRHIUniformBufferLayoutInitializerResourceLines);

        if (selectedResource.Value.TryGetProperty("LoadedShaderMap", out JsonElement selectedLoadedShaderMap) &&
            selectedLoadedShaderMap.ValueKind == JsonValueKind.Object)
        {
            ExtractShaderSideBindingLines(selectedLoadedShaderMap, shaderSideBindingLines);
        }

        return true;
    }

    private static void ExtractShaderSideBindingLines(JsonElement loadedShaderMap, List<string> shaderSideBindingLines)
    {
        string? shaderPlatform = ReadString(loadedShaderMap, "ShaderPlatform");
        string platformLabel = string.IsNullOrWhiteSpace(shaderPlatform) ? "<unknown>" : shaderPlatform;

        JsonElement materialShaderMapContent;
        if (loadedShaderMap.TryGetProperty("MaterialShaderMapContent", out JsonElement directMaterialShaderMapContent) &&
            directMaterialShaderMapContent.ValueKind == JsonValueKind.Object)
        {
            materialShaderMapContent = directMaterialShaderMapContent;
        }
        else if (loadedShaderMap.TryGetProperty("Content", out JsonElement content) && content.ValueKind == JsonValueKind.Object)
        {
            materialShaderMapContent = content;
        }
        else
        {
            shaderSideBindingLines.Add($"Shader-side binding overview: ShaderPlatform={platformLabel} MaterialShaderMapContent=<missing>");
            return;
        }

        int scannedShaderCount = 0;
        int nonEmptyShaderCount = 0;

        if (materialShaderMapContent.TryGetProperty("Shaders", out JsonElement directShaders) && directShaders.ValueKind == JsonValueKind.Array)
        {
            int shaderIndex = 0;
            foreach (JsonElement shader in directShaders.EnumerateArray())
            {
                AppendSingleShaderSideBindingLine(shaderSideBindingLines, $"DirectShaders[{shaderIndex}]", shader, ref scannedShaderCount, ref nonEmptyShaderCount);
                shaderIndex++;
            }
        }

        if (materialShaderMapContent.TryGetProperty("OrderedMeshShaderMaps", out JsonElement orderedMeshShaderMaps) && orderedMeshShaderMaps.ValueKind == JsonValueKind.Array)
        {
            int meshIndex = 0;
            foreach (JsonElement meshShaderMap in orderedMeshShaderMaps.EnumerateArray())
            {
                if (!meshShaderMap.TryGetProperty("Shaders", out JsonElement meshShaders) || meshShaders.ValueKind != JsonValueKind.Array)
                {
                    meshIndex++;
                    continue;
                }

                int shaderIndex = 0;
                foreach (JsonElement shader in meshShaders.EnumerateArray())
                {
                    AppendSingleShaderSideBindingLine(shaderSideBindingLines, $"OrderedMeshShaderMaps[{meshIndex}].Shaders[{shaderIndex}]", shader, ref scannedShaderCount, ref nonEmptyShaderCount);
                    shaderIndex++;
                }

                meshIndex++;
            }
        }

        shaderSideBindingLines.Insert(0, $"Shader-side binding overview: ShaderPlatform={platformLabel} ScannedShaders={scannedShaderCount} NonEmptyShaders={nonEmptyShaderCount}");
        if (nonEmptyShaderCount == 0)
        {
            shaderSideBindingLines.Add("All scanned shader-side Bindings.ResourceParameters / Bindings.BindlessResourceParameters / Bindings.GraphUniformBuffers / Bindings.ParameterReferences / ParameterMapInfo.TextureSamplers / SRVs are empty for this material/platform.");
        }
    }

    private static void AppendSingleShaderSideBindingLine(
        List<string> shaderSideBindingLines,
        string shaderLabel,
        JsonElement shader,
        ref int scannedShaderCount,
        ref int nonEmptyShaderCount)
    {
        scannedShaderCount++;

        if (!shader.TryGetProperty("Bindings", out JsonElement bindings) || bindings.ValueKind != JsonValueKind.Object ||
            !shader.TryGetProperty("ParameterMapInfo", out JsonElement parameterMapInfo) || parameterMapInfo.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        int parameterCount = bindings.TryGetProperty("Parameters", out JsonElement parameters) && parameters.ValueKind == JsonValueKind.Array
            ? parameters.GetArrayLength()
            : 0;
        int resourceParameterCount = bindings.TryGetProperty("ResourceParameters", out JsonElement resourceParameters) && resourceParameters.ValueKind == JsonValueKind.Array
            ? resourceParameters.GetArrayLength()
            : 0;
        int bindlessResourceParameterCount = bindings.TryGetProperty("BindlessResourceParameters", out JsonElement bindlessResourceParameters) && bindlessResourceParameters.ValueKind == JsonValueKind.Array
            ? bindlessResourceParameters.GetArrayLength()
            : 0;
        int graphUniformBufferCount = bindings.TryGetProperty("GraphUniformBuffers", out JsonElement graphUniformBuffers) && graphUniformBuffers.ValueKind == JsonValueKind.Array
            ? graphUniformBuffers.GetArrayLength()
            : 0;
        int parameterReferenceCount = bindings.TryGetProperty("ParameterReferences", out JsonElement parameterReferences) && parameterReferences.ValueKind == JsonValueKind.Array
            ? parameterReferences.GetArrayLength()
            : 0;
        int textureSamplerCount = parameterMapInfo.TryGetProperty("TextureSamplers", out JsonElement textureSamplers) && textureSamplers.ValueKind == JsonValueKind.Array
            ? textureSamplers.GetArrayLength()
            : 0;
        int srvCount = parameterMapInfo.TryGetProperty("SRVs", out JsonElement srvs) && srvs.ValueKind == JsonValueKind.Array
            ? srvs.GetArrayLength()
            : 0;
        int looseParameterBufferCount = parameterMapInfo.TryGetProperty("LooseParameterBuffers", out JsonElement looseParameterBuffers) && looseParameterBuffers.ValueKind == JsonValueKind.Array
            ? looseParameterBuffers.GetArrayLength()
            : 0;

        if (parameterCount == 0 && resourceParameterCount == 0 && bindlessResourceParameterCount == 0 && graphUniformBufferCount == 0 && parameterReferenceCount == 0 && textureSamplerCount == 0 && srvCount == 0 && looseParameterBufferCount == 0)
        {
            return;
        }

        nonEmptyShaderCount++;
        int resourceIndex = shader.TryGetProperty("ResourceIndex", out JsonElement resourceIndexElement) && resourceIndexElement.ValueKind == JsonValueKind.Number
            ? resourceIndexElement.GetInt32()
            : -1;
        shaderSideBindingLines.Add($"{shaderLabel}: ResourceIndex={resourceIndex} Bindings.Parameters={parameterCount} Bindings.ResourceParameters={resourceParameterCount} Bindings.BindlessResourceParameters={bindlessResourceParameterCount} Bindings.GraphUniformBuffers={graphUniformBufferCount} Bindings.ParameterReferences={parameterReferenceCount} ParameterMapInfo.TextureSamplers={textureSamplerCount} ParameterMapInfo.SRVs={srvCount} ParameterMapInfo.LooseParameterBuffers={looseParameterBufferCount}");

        foreach (JsonElement parameter in parameters.EnumerateArray())
        {
            shaderSideBindingLines.Add($"  Parameter BufferIndex={ReadUInt32(parameter, "BufferIndex")} BaseIndex={ReadUInt32(parameter, "BaseIndex")} ByteOffset={ReadUInt32(parameter, "ByteOffset")} ByteSize={ReadUInt32(parameter, "ByteSize")}");
        }

        foreach (JsonElement parameter in resourceParameters.EnumerateArray())
        {
            shaderSideBindingLines.Add($"  ResourceParameter ByteOffset={ReadUInt32(parameter, "ByteOffset")} BaseIndex={ReadUInt32(parameter, "BaseIndex")} BaseType={ReadString(parameter, "BaseType") ?? "unknown"}");
        }

        foreach (JsonElement parameter in bindlessResourceParameters.EnumerateArray())
        {
            shaderSideBindingLines.Add($"  BindlessResourceParameter ByteOffset={ReadUInt32(parameter, "ByteOffset")} GlobalConstantOffset={ReadUInt32(parameter, "GlobalConstantOffset")} BaseType={ReadString(parameter, "BaseType") ?? "unknown"}");
        }

        foreach (JsonElement parameter in graphUniformBuffers.EnumerateArray())
        {
            shaderSideBindingLines.Add($"  GraphUniformBuffer BufferIndex={ReadUInt32(parameter, "BufferIndex")} ByteOffset={ReadUInt32(parameter, "ByteOffset")}");
        }

        foreach (JsonElement parameter in parameterReferences.EnumerateArray())
        {
            shaderSideBindingLines.Add($"  ParameterReference BufferIndex={ReadUInt32(parameter, "BufferIndex")} ByteOffset={ReadUInt32(parameter, "ByteOffset")}");
        }

        foreach (JsonElement parameter in textureSamplers.EnumerateArray())
        {
            shaderSideBindingLines.Add($"  TextureSampler BaseIndex={ReadUInt32(parameter, "BaseIndex")} Size={ReadUInt32(parameter, "Size")} BufferIndex={ReadUInt32(parameter, "BufferIndex")} Type={ReadUInt32(parameter, "Type")}");
        }

        foreach (JsonElement parameter in srvs.EnumerateArray())
        {
            shaderSideBindingLines.Add($"  SRV BaseIndex={ReadUInt32(parameter, "BaseIndex")} Size={ReadUInt32(parameter, "Size")} BufferIndex={ReadUInt32(parameter, "BufferIndex")} Type={ReadUInt32(parameter, "Type")}");
        }

        foreach (JsonElement buffer in looseParameterBuffers.EnumerateArray())
        {
            uint baseIndex = ReadUInt32(buffer, "BaseIndex");
            uint size = ReadUInt32(buffer, "Size");
            JsonElement looseParameters = buffer.TryGetProperty("Parameters", out JsonElement looseParametersElement) && looseParametersElement.ValueKind == JsonValueKind.Array
                ? looseParametersElement
                : default;
            int looseParameterCount = looseParameters.ValueKind == JsonValueKind.Array ? looseParameters.GetArrayLength() : 0;
            shaderSideBindingLines.Add($"  LooseParameterBuffer BaseIndex={baseIndex} Size={size} Parameters={looseParameterCount}");
            if (looseParameters.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement parameter in looseParameters.EnumerateArray())
            {
                shaderSideBindingLines.Add($"    LooseParameter BaseIndex={ReadUInt32(parameter, "BaseIndex")} Size={ReadUInt32(parameter, "Size")}");
            }
        }
    }

    private static void ExtractFMaterialTextureParameterInfos(
        JsonElement uniformExpressionSet,
        List<string> fMaterialTextureParameterInfoLines,
        Dictionary<int, string> referencedTexturesByTextureIndex)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (uniformExpressionSet.TryGetProperty("UniformTextureParameters", out JsonElement uniformTextureParameters) &&
            uniformTextureParameters.ValueKind == JsonValueKind.Array)
        {
            int materialTextureParameterTypeIndex = 0;
            foreach (JsonElement typedArray in uniformTextureParameters.EnumerateArray())
            {
                if (typedArray.ValueKind != JsonValueKind.Array)
                {
                    materialTextureParameterTypeIndex++;
                    continue;
                }

                int typedIndex = 0;
                foreach (JsonElement parameter in typedArray.EnumerateArray())
                {
                    FMaterialTextureParameterInfo? fMaterialTextureParameterInfo = ParseFMaterialTextureParameterInfo(parameter);
                    if (fMaterialTextureParameterInfo != null)
                    {
                        AddFMaterialTextureParameterInfoLine(fMaterialTextureParameterInfo, fMaterialTextureParameterInfoLines, referencedTexturesByTextureIndex, seen, "TextureParameter", $"Material.{GetMaterialTextureParameterBaseName(materialTextureParameterTypeIndex)}_{typedIndex}");
                    }
                    typedIndex++;
                }

                materialTextureParameterTypeIndex++;
            }
        }

        if (uniformExpressionSet.TryGetProperty("UniformExternalTextureParameters", out JsonElement uniformExternalTextureParameters) &&
            uniformExternalTextureParameters.ValueKind == JsonValueKind.Array)
        {
            int externalTextureIndex = 0;
            foreach (JsonElement parameter in uniformExternalTextureParameters.EnumerateArray())
            {
                FMaterialTextureParameterInfo? fMaterialTextureParameterInfo = ParseFMaterialTextureParameterInfo(parameter);
                if (fMaterialTextureParameterInfo != null)
                {
                    AddFMaterialTextureParameterInfoLine(fMaterialTextureParameterInfo, fMaterialTextureParameterInfoLines, referencedTexturesByTextureIndex, seen, "ExternalTextureParameter", $"Material.ExternalTexture_{externalTextureIndex}");
                }
                externalTextureIndex++;
            }
        }
    }

    private static string GetMaterialTextureParameterBaseName(int materialTextureParameterTypeIndex)
    {
        return materialTextureParameterTypeIndex switch
        {
            0 => "Texture2D",
            1 => "TextureCube",
            2 => "Texture2DArray",
            3 => "TextureCubeArray",
            4 => "VolumeTexture",
            5 => "VirtualTexturePhysical",
            _ => "UnknownTextureType"
        };
    }

    private static void ExtractMaterialUniformBufferResourceMembers(JsonElement uniformExpressionSet, List<string> fRHIUniformBufferLayoutInitializerResourceLines)
    {
        if (!uniformExpressionSet.TryGetProperty("UniformBufferLayoutInitializer", out JsonElement uniformBufferLayoutInitializer) ||
            uniformBufferLayoutInitializer.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        string? bufferName = ReadString(uniformBufferLayoutInitializer, "Name");
        if (!string.Equals(bufferName, "Material", StringComparison.Ordinal))
        {
            return;
        }

        if (!uniformBufferLayoutInitializer.TryGetProperty("Resources", out JsonElement resources) ||
            resources.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        Queue<MaterialUniformBufferExpectedMember> fRHIUniformBufferLayoutInitializerExpectedMembers = BuildMaterialUniformBufferExpectedMembers(uniformExpressionSet);
        if (fRHIUniformBufferLayoutInitializerExpectedMembers.Count == 0)
        {
            return;
        }

        foreach (JsonElement resource in resources.EnumerateArray())
        {
            string? memberType = ReadString(resource, "MemberType");
            if (string.IsNullOrWhiteSpace(memberType))
            {
                continue;
            }

            if (fRHIUniformBufferLayoutInitializerExpectedMembers.Count == 0)
            {
                break;
            }

            MaterialUniformBufferExpectedMember expectedMember = fRHIUniformBufferLayoutInitializerExpectedMembers.Peek();
            if (!IsMatchingMaterialUniformBufferMemberType(expectedMember.MemberType, memberType))
            {
                continue;
            }

            uint memberOffset = ReadUInt32(resource, "MemberOffset");
            fRHIUniformBufferLayoutInitializerResourceLines.Add($"Material.{expectedMember.MemberName} @ byte {memberOffset} ({memberType})");
            fRHIUniformBufferLayoutInitializerExpectedMembers.Dequeue();
        }
    }

    private static Queue<MaterialUniformBufferExpectedMember> BuildMaterialUniformBufferExpectedMembers(JsonElement uniformExpressionSet)
    {
        var result = new Queue<MaterialUniformBufferExpectedMember>();

        EnqueueTextureMembers(uniformExpressionSet, result, "UniformTextureParameters", 0, "Texture2D");
        EnqueueTextureMembers(uniformExpressionSet, result, "UniformTextureParameters", 1, "TextureCube");
        EnqueueTextureMembers(uniformExpressionSet, result, "UniformTextureParameters", 2, "Texture2DArray");
        EnqueueTextureMembers(uniformExpressionSet, result, "UniformTextureParameters", 3, "TextureCubeArray");
        EnqueueTextureMembers(uniformExpressionSet, result, "UniformTextureParameters", 4, "VolumeTexture");
        EnqueueTextureMembers(uniformExpressionSet, result, "UniformExternalTextureParameters", null, "ExternalTexture");
        EnqueueVirtualTexturePageTableMembers(uniformExpressionSet, result);
        EnqueueTextureMembers(uniformExpressionSet, result, "UniformTextureParameters", 5, "VirtualTexturePhysical", "UBMT_SRV");
        result.Enqueue(new MaterialUniformBufferExpectedMember("Wrap_WorldGroupSettings", "UBMT_SAMPLER"));
        result.Enqueue(new MaterialUniformBufferExpectedMember("Clamp_WorldGroupSettings", "UBMT_SAMPLER"));

        return result;
    }

    private static void EnqueueTextureMembers(
        JsonElement uniformExpressionSet,
        Queue<MaterialUniformBufferExpectedMember> result,
        string propertyName,
        int? typedArrayIndex,
        string baseName,
        string resourceMemberType = "UBMT_TEXTURE")
    {
        if (!uniformExpressionSet.TryGetProperty(propertyName, out JsonElement container))
        {
            return;
        }

        JsonElement parameterArray;
        if (typedArrayIndex.HasValue)
        {
            if (container.ValueKind != JsonValueKind.Array || typedArrayIndex.Value >= container.GetArrayLength())
            {
                return;
            }

            parameterArray = container[typedArrayIndex.Value];
        }
        else
        {
            parameterArray = container;
        }

        if (parameterArray.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        int index = 0;
        foreach (JsonElement _ in parameterArray.EnumerateArray())
        {
            result.Enqueue(new MaterialUniformBufferExpectedMember($"{baseName}_{index}", resourceMemberType));
            result.Enqueue(new MaterialUniformBufferExpectedMember($"{baseName}_{index}Sampler", "UBMT_SAMPLER"));
            index++;
        }
    }

    private static void EnqueueVirtualTexturePageTableMembers(JsonElement uniformExpressionSet, Queue<MaterialUniformBufferExpectedMember> result)
    {
        if (!uniformExpressionSet.TryGetProperty("VTStacks", out JsonElement vtStacks) || vtStacks.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        int vtStackIndex = 0;
        foreach (JsonElement vtStack in vtStacks.EnumerateArray())
        {
            result.Enqueue(new MaterialUniformBufferExpectedMember($"VirtualTexturePageTable0_{vtStackIndex}", "UBMT_TEXTURE"));

            int numLayers = vtStack.TryGetProperty("NumLayers", out JsonElement numLayersElement) && numLayersElement.ValueKind == JsonValueKind.Number
                ? numLayersElement.GetInt32()
                : 0;
            if (numLayers > 4)
            {
                result.Enqueue(new MaterialUniformBufferExpectedMember($"VirtualTexturePageTable1_{vtStackIndex}", "UBMT_TEXTURE"));
            }

            result.Enqueue(new MaterialUniformBufferExpectedMember($"VirtualTexturePageTableIndirection_{vtStackIndex}", "UBMT_TEXTURE"));
            vtStackIndex++;
        }
    }

    private static bool IsMatchingMaterialUniformBufferMemberType(string expectedMemberType, string actualMemberType)
    {
        if (string.Equals(expectedMemberType, actualMemberType, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(expectedMemberType, "UBMT_TEXTURE", StringComparison.Ordinal) &&
            string.Equals(actualMemberType, "EUniformBufferBaseType_NumBits", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private sealed record MaterialUniformBufferExpectedMember(string MemberName, string MemberType);

    private static void AddFMaterialTextureParameterInfoLine(
        FMaterialTextureParameterInfo fMaterialTextureParameterInfo,
        List<string> fMaterialTextureParameterInfoLines,
        Dictionary<int, string> referencedTexturesByTextureIndex,
        HashSet<string> seen,
        string sourceKind,
        string materialUniformBufferMemberName)
    {
        string fMaterialParameterInfoText = FormatFMaterialParameterInfo(fMaterialTextureParameterInfo.ParameterInfo);

        string line;
        if (fMaterialTextureParameterInfo.TextureIndex >= 0)
        {
            int textureIndex = fMaterialTextureParameterInfo.TextureIndex;
            string referencedIdentity = referencedTexturesByTextureIndex.TryGetValue(textureIndex, out string? identity)
                ? identity
                : "unknown";
            line = $"{fMaterialParameterInfoText} -> TextureIndex={textureIndex}, SamplerSource={fMaterialTextureParameterInfo.SamplerSource}, VirtualTextureLayerIndex={fMaterialTextureParameterInfo.VirtualTextureLayerIndex} -> {materialUniformBufferMemberName} -> {referencedIdentity} ({sourceKind})";
        }
        else
        {
            line = $"{fMaterialParameterInfoText} -> {materialUniformBufferMemberName} ({sourceKind})";
        }

        if (seen.Add(line))
        {
            fMaterialTextureParameterInfoLines.Add(line);
        }
    }

    private static bool TryGetUniformExpressionSet(JsonElement loadedResource, out JsonElement uniformExpressionSet)
    {
        uniformExpressionSet = default;

        if (!loadedResource.TryGetProperty("LoadedShaderMap", out JsonElement loadedShaderMap) ||
            loadedShaderMap.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (loadedShaderMap.TryGetProperty("MaterialShaderMapContent", out JsonElement materialShaderMapContent) &&
            materialShaderMapContent.ValueKind == JsonValueKind.Object &&
            materialShaderMapContent.TryGetProperty("UniformExpressionSet", out uniformExpressionSet))
        {
            return true;
        }

        if (loadedShaderMap.TryGetProperty("Content", out JsonElement content) &&
            content.ValueKind == JsonValueKind.Object &&
            content.TryGetProperty("MaterialCompilationOutput", out JsonElement materialCompilationOutput) &&
            materialCompilationOutput.ValueKind == JsonValueKind.Object &&
            materialCompilationOutput.TryGetProperty("UniformExpressionSet", out uniformExpressionSet))
        {
            return true;
        }

        return false;
    }

    private static void AddProvenMaterialConstantBuffer(ShaderSymbolData metadata, JsonElement uniformExpressionSet)
    {
        if (!uniformExpressionSet.TryGetProperty("UniformBufferLayoutInitializer", out JsonElement uniformBufferLayoutInitializer) ||
            uniformBufferLayoutInitializer.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        string? bufferName = ReadString(uniformBufferLayoutInitializer, "Name");
        if (!string.Equals(bufferName, "Material", StringComparison.Ordinal))
        {
            return;
        }

        uint constantBufferSize = ReadUInt32(uniformBufferLayoutInitializer, "ConstantBufferSize");

        if (!uniformExpressionSet.TryGetProperty("UniformPreshaders", out JsonElement uniformPreshaders) ||
            uniformPreshaders.ValueKind != JsonValueKind.Array ||
            !uniformExpressionSet.TryGetProperty("UniformPreshaderFields", out JsonElement uniformPreshaderFields) ||
            uniformPreshaderFields.ValueKind != JsonValueKind.Array ||
            !uniformExpressionSet.TryGetProperty("UniformNumericParameters", out JsonElement uniformNumericParameters) ||
            uniformNumericParameters.ValueKind != JsonValueKind.Array ||
            !uniformExpressionSet.TryGetProperty("UniformPreshaderData", out JsonElement uniformPreshaderData) ||
            uniformPreshaderData.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        string? encodedData = ReadString(uniformPreshaderData, "Data");
        if (string.IsNullOrWhiteSpace(encodedData))
        {
            return;
        }

        byte[] opcodeData = Convert.FromBase64String(encodedData);
        ConstantBuffer materialBuffer = new()
        {
            Name = "Material",
            Size = checked((int)constantBufferSize)
        };

        var seenOffsets = new HashSet<int>();
        foreach (JsonElement preshader in uniformPreshaders.EnumerateArray())
        {
            uint opcodeOffset = ReadUInt32(preshader, "OpcodeOffset");
            uint opcodeSize = ReadUInt32(preshader, "OpcodeSize");
            uint fieldIndex = ReadUInt32(preshader, "FieldIndex");
            uint numFields = ReadUInt32(preshader, "NumFields");

            if (numFields != 1 || !IsSingleParameterSlice(opcodeData, opcodeOffset, opcodeSize))
            {
                continue;
            }

            ushort parameterIndex = BitConverter.ToUInt16(opcodeData, checked((int)opcodeOffset + 1));
            if (parameterIndex >= uniformNumericParameters.GetArrayLength() || fieldIndex >= uniformPreshaderFields.GetArrayLength())
            {
                continue;
            }

            JsonElement parameter = uniformNumericParameters[parameterIndex];
            string? parameterName = ReadNestedString(parameter, "ParameterInfo", "Name");
            string? parameterType = ReadString(parameter, "ParameterType");
            if (string.IsNullOrWhiteSpace(parameterName) || string.IsNullOrWhiteSpace(parameterType))
            {
                continue;
            }

            JsonElement field = uniformPreshaderFields[checked((int)fieldIndex)];
            string? fieldType = ReadString(field, "Type");
            if (!TryMapFieldTypeToLayout(fieldType, out int rows, out int columns))
            {
                continue;
            }

            int byteOffset = checked((int)ReadUInt32(field, "BufferOffset") * 4);
            if (!seenOffsets.Add(byteOffset))
            {
                continue;
            }

            materialBuffer.CBParams.Add(new ConstantBufferParameter
            {
                ParamName = parameterName,
                ParamType = ShaderParamType.Float,
                Rows = rows,
                Columns = columns,
                IsMatrix = false,
                ArraySize = 1,
                Index = byteOffset
            });
        }

        if (materialBuffer.CBParams.Count == 0)
        {
            return;
        }

        materialBuffer.CBParams.Sort((left, right) => left.Index.CompareTo(right.Index));
        metadata.ConstantBuffers.RemoveAll(cb => string.Equals(cb.Name, materialBuffer.Name, StringComparison.Ordinal));
        metadata.ConstantBuffers.Add(materialBuffer);
    }

    private static bool IsSingleParameterSlice(byte[] opcodeData, uint opcodeOffset, uint opcodeSize)
    {
        if (opcodeSize != 3 || opcodeOffset + opcodeSize > opcodeData.Length)
        {
            return false;
        }

        return opcodeData[opcodeOffset] == 3;
    }

    private static bool TryMapFieldTypeToLayout(string? fieldType, out int rows, out int columns)
    {
        rows = 0;
        columns = 1;
        switch (fieldType)
        {
            case "Float1":
                rows = 1;
                return true;
            case "Float2":
                rows = 2;
                return true;
            case "Float3":
                rows = 3;
                return true;
            case "Float4":
                rows = 4;
                return true;
            default:
                return false;
        }
    }

    private static void ExtractUniformNumericParameterInfos(JsonElement uniformExpressionSet, List<string> fMaterialParameterInfoLines)
    {
        if (!uniformExpressionSet.TryGetProperty("UniformNumericParameters", out JsonElement numericParameters) ||
            numericParameters.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement parameter in numericParameters.EnumerateArray())
        {
            string? fMaterialParameterInfoText = FormatFMaterialParameterInfoText(parameter);
            if (!string.IsNullOrWhiteSpace(fMaterialParameterInfoText))
            {
                fMaterialParameterInfoLines.Add(fMaterialParameterInfoText);
            }
        }
    }

    private static void ExtractFallbackMaterialParameterInfos(JsonElement properties, List<string> fMaterialParameterInfoLines)
    {
        AddMaterialParameterInfos(properties, "ScalarParameterValues", fMaterialParameterInfoLines);
        AddMaterialParameterInfos(properties, "VectorParameterValues", fMaterialParameterInfoLines);
        AddMaterialParameterInfos(properties, "DoubleVectorParameterValues", fMaterialParameterInfoLines);
    }

    private static void AddMaterialParameterInfos(JsonElement properties, string propertyName, List<string> fMaterialParameterInfoLines)
    {
        if (!properties.TryGetProperty(propertyName, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement entry in array.EnumerateArray())
        {
            string? fMaterialParameterInfoText = FormatFMaterialParameterInfoText(entry);
            if (!string.IsNullOrWhiteSpace(fMaterialParameterInfoText))
            {
                fMaterialParameterInfoLines.Add(fMaterialParameterInfoText);
            }
        }
    }

    private static FMaterialTextureParameterInfo? ParseFMaterialTextureParameterInfo(JsonElement element)
    {
        FMaterialParameterInfo? fMaterialParameterInfo = ParseFMaterialParameterInfo(element);
        if (fMaterialParameterInfo == null)
        {
            return null;
        }

        return new FMaterialTextureParameterInfo
        {
            ParameterInfo = fMaterialParameterInfo,
            TextureIndex = element.TryGetProperty("TextureIndex", out JsonElement textureIndexElement) && textureIndexElement.ValueKind == JsonValueKind.Number
                ? textureIndexElement.GetInt32()
                : -1,
            SamplerSource = ReadString(element, "SamplerSource") ?? string.Empty,
            VirtualTextureLayerIndex = element.TryGetProperty("VirtualTextureLayerIndex", out JsonElement virtualTextureLayerIndexElement) && virtualTextureLayerIndexElement.ValueKind == JsonValueKind.Number
                ? virtualTextureLayerIndexElement.GetInt32()
                : 0
        };
    }

    private Dictionary<int, string> BuildReferencedTextureIdentityMap(string materialPath, JsonElement asset)
    {
        var result = new Dictionary<int, string>();
        string? currentMaterialPath = materialPath;
        JsonElement currentAsset = asset;

        while (!string.IsNullOrWhiteSpace(currentMaterialPath))
        {
            Console.WriteLine($"[纹理真理] scan material={currentMaterialPath}");
            AppendReferencedTextures(result, currentAsset);

            if (!TryGetParentMaterialPath(currentAsset, out string? parentMaterialPath) || string.IsNullOrWhiteSpace(parentMaterialPath))
            {
                break;
            }

            string normalizedParent = NormalizeAssetPathForExport(parentMaterialPath);
            string? parentJsonPath = ResolveMaterialJsonPath(normalizedParent);
            if (parentJsonPath == null || !File.Exists(parentJsonPath))
            {
                break;
            }

            using JsonDocument parentDocument = JsonDocument.Parse(File.ReadAllText(parentJsonPath));
            JsonElement parentRoot = parentDocument.RootElement;
            if (parentRoot.ValueKind != JsonValueKind.Array || parentRoot.GetArrayLength() == 0)
            {
                break;
            }

            currentMaterialPath = normalizedParent;
            currentAsset = parentRoot[0].Clone();
        }

        return result;
    }

    private static void AppendReferencedTextures(Dictionary<int, string> result, JsonElement asset)
    {
        if (!asset.TryGetProperty("CachedExpressionData", out JsonElement cachedExpressionData) ||
            cachedExpressionData.ValueKind != JsonValueKind.Object ||
            !cachedExpressionData.TryGetProperty("ReferencedTextures", out JsonElement referencedTextures) ||
            referencedTextures.ValueKind != JsonValueKind.Array)
        {
            Console.WriteLine("[纹理真理]   ReferencedTextures=<missing>");
            return;
        }

        Console.WriteLine($"[纹理真理]   ReferencedTextures.Count={referencedTextures.GetArrayLength()}");

        int textureIndex = 0;
        foreach (JsonElement referencedTexture in referencedTextures.EnumerateArray())
        {
            if (!result.ContainsKey(textureIndex))
            {
                string identity = ReadReferencedTextureIdentity(referencedTexture);
                if (!string.IsNullOrWhiteSpace(identity))
                {
                    result[textureIndex] = identity;
                }
            }

            textureIndex++;
        }
    }
    private static bool TryGetParentMaterialPath(JsonElement asset, out string? parentMaterialPath)
    {
        parentMaterialPath = null;
        if (!asset.TryGetProperty("Properties", out JsonElement properties) || properties.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!properties.TryGetProperty("Parent", out JsonElement parent) || parent.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        parentMaterialPath = ReadString(parent, "ObjectPath");
        return !string.IsNullOrWhiteSpace(parentMaterialPath);
    }

    private static string ReadReferencedTextureIdentity(JsonElement referencedTexture)
    {
        string? objectName = ReadString(referencedTexture, "ObjectName");
        if (!string.IsNullOrWhiteSpace(objectName))
        {
            int quoteIndex = objectName.IndexOf('\'', StringComparison.Ordinal);
            if (quoteIndex >= 0 && quoteIndex + 1 < objectName.Length)
            {
            int endQuoteIndex = objectName.LastIndexOf('\'');
                if (endQuoteIndex > quoteIndex)
                {
                    return objectName[(quoteIndex + 1)..endQuoteIndex];
                }
            }

            return objectName;
        }

        string? objectPath = ReadString(referencedTexture, "ObjectPath");
        return NormalizeIdentityObjectPath(objectPath ?? string.Empty);
    }

    private string NormalizeAssetPathForExport(string objectPath)
    {
        string normalized = objectPath.Replace('\\', '/');
        int dotIndex = normalized.LastIndexOf('.');
        if (dotIndex > 0)
        {
            normalized = normalized[..dotIndex];
        }

        normalized = normalized.TrimStart('/');
        if (normalized.StartsWith("Game/", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(_exportRootName)
                ? "Content/" + normalized[5..]
                : _exportRootName + "/Content/" + normalized[5..];
        }

        return normalized;
    }

    private static string NormalizeIdentityObjectPath(string objectPath)
    {
        string normalized = objectPath.Replace('\\', '/');
        int slashIndex = normalized.LastIndexOf('/');
        if (slashIndex >= 0 && slashIndex + 1 < normalized.Length)
        {
            normalized = normalized[(slashIndex + 1)..];
        }

        int dotIndex = normalized.LastIndexOf('.');
        if (dotIndex > 0)
        {
            normalized = normalized[..dotIndex];
        }

        return normalized;
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

    private static string BuildHeader(
        string materialPath,
        string? shaderPlatform,
        bool usedLoadedResources,
        List<string> fMaterialParameterInfoLines,
        List<string> fMaterialTextureParameterInfoLines,
        List<string> fRHIUniformBufferLayoutInitializerResourceLines,
        List<string> shaderSideBindingLines)
    {
        var sb = new StringBuilder();
        sb.AppendLine("/*");
        sb.AppendLine(" * UE Runtime Metadata");
        sb.AppendLine($" * Material: {materialPath}");
        if (!string.IsNullOrWhiteSpace(shaderPlatform))
        {
            sb.AppendLine($" * ShaderPlatform: {shaderPlatform}");
        }
        sb.AppendLine($" * Source: {(usedLoadedResources ? "LoadedMaterialResources.UniformExpressionSet" : "Material Properties Fallback")}");
        sb.AppendLine(" * Texture binding/resource truth is not yet closed; canonical metadata does not consume material-side texture names.");

        List<string> distinctTextureTruth = fMaterialTextureParameterInfoLines.Distinct(StringComparer.Ordinal).Take(16).ToList();
        if (distinctTextureTruth.Count > 0)
        {
            sb.AppendLine(" * Proven Texture Params:");
            foreach (string textureTruthLine in distinctTextureTruth)
            {
                sb.AppendLine($" *   {textureTruthLine}");
            }
        }

        List<string> distinctMaterialResources = fRHIUniformBufferLayoutInitializerResourceLines.Distinct(StringComparer.Ordinal).Take(16).ToList();
        if (distinctMaterialResources.Count > 0)
        {
            sb.AppendLine(" * FRHIUniformBufferLayoutInitializer.Resources:");
            foreach (string materialResourceTruthLine in distinctMaterialResources)
            {
                sb.AppendLine($" *   {materialResourceTruthLine}");
            }
        }

        List<string> distinctShaderSideBindings = shaderSideBindingLines.Distinct(StringComparer.Ordinal).Take(32).ToList();
        if (distinctShaderSideBindings.Count > 0)
        {
            sb.AppendLine(" * Shader-side Bindings/ParameterMapInfo:");
            foreach (string shaderSideBindingLine in distinctShaderSideBindings)
            {
                sb.AppendLine($" *   {shaderSideBindingLine}");
            }
            sb.AppendLine(" *   LooseParameterBuffers are shader loose-data ranges (BufferIndex/BaseIndex/Size), not proven texture/sampler/SRV bindings.");
        }

        List<string> distinctNumeric = fMaterialParameterInfoLines.Distinct(StringComparer.Ordinal).Take(16).ToList();
        if (distinctNumeric.Count > 0)
        {
            sb.AppendLine(" * FMaterialParameterInfo:");
            foreach (string materialParameterInfoText in distinctNumeric)
            {
                sb.AppendLine($" *   {materialParameterInfoText}");
            }
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

    private static string? FormatFMaterialParameterInfoText(JsonElement element)
    {
        FMaterialParameterInfo? fMaterialParameterInfo = ParseFMaterialParameterInfo(element);
        if (fMaterialParameterInfo != null)
        {
            return FormatFMaterialParameterInfo(fMaterialParameterInfo);
        }

        string? parameterName = ReadString(element, "ParameterName");
        if (string.IsNullOrWhiteSpace(parameterName) || string.Equals(parameterName, "None", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return $"Name={parameterName}";
    }

    private static FMaterialParameterInfo? ParseFMaterialParameterInfo(JsonElement element)
    {
        if (!element.TryGetProperty("ParameterInfo", out JsonElement parameterInfo) || parameterInfo.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? name = ReadString(parameterInfo, "Name");
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name, "None", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        EMaterialParameterAssociation association = ParseEMaterialParameterAssociation(ReadString(parameterInfo, "Association"));
        int index = parameterInfo.TryGetProperty("Index", out JsonElement indexElement) && indexElement.ValueKind == JsonValueKind.Number
            ? indexElement.GetInt32()
            : -1;
        return new FMaterialParameterInfo(name, association, index);
    }

    private static string FormatFMaterialParameterInfo(FMaterialParameterInfo parameterInfo)
    {
        return $"Name={parameterInfo.Name}, Association={parameterInfo.Association}, Index={parameterInfo.Index}";
    }

    private static EMaterialParameterAssociation ParseEMaterialParameterAssociation(string? associationText)
    {
        return associationText switch
        {
            "EMaterialParameterAssociation::LayerParameter" => EMaterialParameterAssociation.LayerParameter,
            "EMaterialParameterAssociation::BlendParameter" => EMaterialParameterAssociation.BlendParameter,
            _ => EMaterialParameterAssociation.GlobalParameter
        };
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static uint ReadUInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind != JsonValueKind.Number)
        {
            throw new InvalidDataException($"Missing numeric property: {propertyName}");
        }

        return value.GetUInt32();
    }
}

internal sealed record UeMaterialSymbolInfo(
    string MaterialPath,
    ShaderSymbolData Metadata,
    string Header,
    int Score,
    bool UsedLoadedResources);
