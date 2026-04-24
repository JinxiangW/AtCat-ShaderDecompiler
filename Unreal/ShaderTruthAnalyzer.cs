using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Ruri.ShaderTools.Unreal;

internal static class ShaderTruthAnalyzer
{
    public static int Analyze(string unifiedMetadataPath, string materialPath)
    {
        if (string.IsNullOrWhiteSpace(unifiedMetadataPath))
        {
            throw new ArgumentException("Missing unified metadata path.", nameof(unifiedMetadataPath));
        }

        if (string.IsNullOrWhiteSpace(materialPath))
        {
            throw new ArgumentException("Missing material path.", nameof(materialPath));
        }

        string fullMetadataPath = Path.GetFullPath(unifiedMetadataPath);
        if (!File.Exists(fullMetadataPath))
        {
            throw new FileNotFoundException($"Unified metadata file not found: {fullMetadataPath}");
        }

        string normalizedMaterialPath = NormalizeMaterialPath(materialPath);
        string exportRoot = Path.GetDirectoryName(fullMetadataPath) ?? throw new InvalidOperationException("Failed to resolve export root.");

        using JsonDocument metadataDocument = JsonDocument.Parse(File.ReadAllText(fullMetadataPath));
        JsonElement root = metadataDocument.RootElement;
        JsonElement materialEntry = FindMaterialInterface(root, normalizedMaterialPath);

        var report = new StringBuilder();
        report.AppendLine($"Material: {normalizedMaterialPath}");
        report.AppendLine($"UnifiedMetadata: {fullMetadataPath}");
        report.AppendLine();

        List<MaterialAssetNode> materialChain = LoadMaterialAssetChain(exportRoot, normalizedMaterialPath);
        AppendMaterialChain(report, materialChain);

        JsonElement loadedShaderMaps = GetRequiredProperty(materialEntry, "LoadedShaderMaps");
        int shaderMapIndex = 0;
        foreach (JsonElement shaderMap in loadedShaderMaps.EnumerateArray())
        {
            report.AppendLine($"[LoadedShaderMap {shaderMapIndex}]");
            report.AppendLine($"CookedShaderMapIdHash: {ReadRequiredString(shaderMap, "CookedShaderMapIdHash")}");
            report.AppendLine($"ShaderContentHash: {ReadOptionalString(shaderMap, "ShaderContentHash") ?? "<null>"}");
            report.AppendLine($"ShaderPlatform: {ReadOptionalString(shaderMap, "ShaderPlatform") ?? "<null>"}");

            JsonElement materialShaderMapContent = GetRequiredProperty(shaderMap, "MaterialShaderMapContent");
            JsonElement uniformExpressionSet = GetRequiredProperty(materialShaderMapContent, "UniformExpressionSet");

            AppendMaterialUniformBufferTruth(report, uniformExpressionSet);
            AppendMaterialCollectionTruth(report, exportRoot, materialChain, uniformExpressionSet);
            AppendShaderBindingTruth(report, materialShaderMapContent);
            report.AppendLine();
            shaderMapIndex++;
        }

        Console.Write(report.ToString());
        return 0;
    }

    private static void AppendMaterialChain(StringBuilder report, List<MaterialAssetNode> materialChain)
    {
        report.AppendLine("[MaterialChain]");
        foreach (MaterialAssetNode node in materialChain)
        {
            report.AppendLine($"Asset: {node.MaterialPath}");
            if (!string.IsNullOrWhiteSpace(node.ParentPath))
            {
                report.AppendLine($"Parent: {node.ParentPath}");
            }

            if (node.ParameterCollectionInfos.Count > 0)
            {
                foreach (ParameterCollectionInfo collectionInfo in node.ParameterCollectionInfos)
                {
                    report.AppendLine($"ParameterCollectionInfo: {collectionInfo.StateId} -> {collectionInfo.CollectionPath}");
                }
            }
        }

        report.AppendLine();
    }

    private static void AppendMaterialUniformBufferTruth(StringBuilder report, JsonElement uniformExpressionSet)
    {
        report.AppendLine("[MaterialUniformBuffer]");

        JsonElement uniformBufferLayoutInitializer = GetRequiredProperty(uniformExpressionSet, "UniformBufferLayoutInitializer");
        report.AppendLine($"UniformBufferLayoutInitializer.Name: {ReadRequiredString(uniformBufferLayoutInitializer, "Name")}");
        report.AppendLine($"UniformBufferLayoutInitializer.ConstantBufferSize: {ReadRequiredUInt32(uniformBufferLayoutInitializer, "ConstantBufferSize")}");

        JsonElement resources = GetRequiredProperty(uniformBufferLayoutInitializer, "Resources");
        report.AppendLine($"UniformBufferLayoutInitializer.Resources.Count: {resources.GetArrayLength()}");

        JsonElement uniformPreshaderFields = GetRequiredProperty(uniformExpressionSet, "UniformPreshaderFields");
        JsonElement uniformPreshaders = GetRequiredProperty(uniformExpressionSet, "UniformPreshaders");
        JsonElement uniformNumericParameters = GetRequiredProperty(uniformExpressionSet, "UniformNumericParameters");
        JsonElement uniformPreshaderData = GetRequiredProperty(uniformExpressionSet, "UniformPreshaderData");

        List<string> names = ReadStringArray(GetRequiredProperty(uniformPreshaderData, "Names"));
        byte[] opcodeData = Convert.FromBase64String(ReadRequiredString(uniformPreshaderData, "Data"));
        List<PreshaderSliceAnalysis> analyses = AnalyzePreshaderSlices(uniformPreshaders, uniformPreshaderFields, uniformNumericParameters, names, opcodeData);

        report.AppendLine($"UniformPreshaders.Count: {analyses.Count}");
        foreach (PreshaderSliceAnalysis analysis in analyses)
        {
            string fieldOffsets = string.Join(", ", analysis.WrittenFields.Select(field => $"{field.BufferOffset}:{field.Type}"));
            string parameterRefs = analysis.ParameterReferences.Count == 0
                ? "<none>"
                : string.Join(", ", analysis.ParameterReferences.Select(parameter => $"{parameter.ParameterIndex}:{parameter.ParameterName}:{parameter.ParameterType}"));

            report.AppendLine($"Slice[{analysis.SliceIndex}] OpcodeOffset={analysis.OpcodeOffset} OpcodeSize={analysis.OpcodeSize} Fields={fieldOffsets}");
            report.AppendLine($"  ReferencedNumericParameters: {parameterRefs}");

            if (analysis.DirectParameterWrite != null)
            {
                report.AppendLine($"  ProvenDirectParameterWrite: {analysis.DirectParameterWrite.ParameterName} -> BufferOffset {analysis.DirectParameterWrite.BufferOffset} ({analysis.DirectParameterWrite.Type})");
            }
        }

        report.AppendLine();
    }

    private static void AppendMaterialCollectionTruth(StringBuilder report, string exportRoot, List<MaterialAssetNode> materialChain, JsonElement uniformExpressionSet)
    {
        report.AppendLine("[MaterialCollections]");

        JsonElement parameterCollections = GetRequiredProperty(uniformExpressionSet, "ParameterCollections");
        if (parameterCollections.GetArrayLength() == 0)
        {
            report.AppendLine("UniformExpressionSet.ParameterCollections: <empty>");
            report.AppendLine();
            return;
        }

        Dictionary<string, ParameterCollectionInfo> collectionInfosByStateId = materialChain
            .SelectMany(node => node.ParameterCollectionInfos)
            .GroupBy(info => NormalizeGuidString(info.StateId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        report.AppendLine($"MaterialChain.ParameterCollectionInfos.Count: {collectionInfosByStateId.Count}");

        if (collectionInfosByStateId.Count == 0)
        {
            List<ParameterCollectionInfo> scannedFromChain = ScanParameterCollectionInfosFromChain(exportRoot, materialChain);
            report.AppendLine($"ScannedFromChain.ParameterCollectionInfos.Count: {scannedFromChain.Count}");
            foreach (ParameterCollectionInfo candidate in scannedFromChain)
            {
                report.AppendLine($"ScannedFromChain.Candidate: {candidate.StateId} -> {candidate.CollectionPath}");
            }

            collectionInfosByStateId = scannedFromChain
                .GroupBy(info => NormalizeGuidString(info.StateId), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        }

        if (collectionInfosByStateId.Count == 0)
        {
            var requestedStateIds = parameterCollections.EnumerateArray()
                .Select(element => NormalizeGuidString(element.GetString() ?? throw new InvalidDataException("ParameterCollections entry is not a string.")))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            List<ParameterCollectionInfo> scannedFromExportRoot = ScanParameterCollectionInfosFromExportRoot(exportRoot, requestedStateIds);
            report.AppendLine($"ScannedFromExportRoot.ParameterCollectionInfos.Count: {scannedFromExportRoot.Count}");
            foreach (ParameterCollectionInfo candidate in scannedFromExportRoot)
            {
                report.AppendLine($"ScannedFromExportRoot.Candidate: {candidate.StateId} -> {candidate.CollectionPath}");
            }

            collectionInfosByStateId = scannedFromExportRoot
                .GroupBy(info => NormalizeGuidString(info.StateId), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        }

        int collectionIndex = 0;
        foreach (JsonElement guidElement in parameterCollections.EnumerateArray())
        {
            string stateId = guidElement.GetString() ?? throw new InvalidDataException("ParameterCollections entry is not a string.");
            report.AppendLine($"ParameterCollections[{collectionIndex}]: {stateId}");

            if (!collectionInfosByStateId.TryGetValue(NormalizeGuidString(stateId), out ParameterCollectionInfo? collectionInfo))
            {
                report.AppendLine("  ProvenCollectionAsset: <missing in material parent chain>");
                collectionIndex++;
                continue;
            }

            report.AppendLine($"  ProvenCollectionAsset: {collectionInfo.CollectionPath}");
            CollectionAssetTruth collectionTruth = LoadCollectionAssetTruth(exportRoot, collectionInfo.CollectionPath);
            report.AppendLine($"  CollectionAssetStateId: {collectionTruth.StateId}");
            report.AppendLine($"  ScalarParameters.Count: {collectionTruth.ScalarParameterNames.Count}");
            report.AppendLine($"  VectorParameters.Count: {collectionTruth.VectorParameterNames.Count}");
            report.AppendLine($"  ScalarParameters: {string.Join(", ", collectionTruth.ScalarParameterNames)}");
            report.AppendLine($"  VectorParameters: {string.Join(", ", collectionTruth.VectorParameterNames)}");
            report.AppendLine("  NotProven: shader symbol name MaterialCollectionN to ParameterCollections[index] numbering");
            collectionIndex++;
        }

        report.AppendLine();
    }

    private static void AppendShaderBindingTruth(StringBuilder report, JsonElement materialShaderMapContent)
    {
        report.AppendLine("[ShaderBindingTruth]");

        if (materialShaderMapContent.TryGetProperty("Shaders", out JsonElement shaders) && shaders.ValueKind == JsonValueKind.Array)
        {
            report.AppendLine($"MaterialShaderMapContent.Shaders.Count: {shaders.GetArrayLength()}");
            int shaderIndex = 0;
            foreach (JsonElement shader in shaders.EnumerateArray())
            {
                AppendSingleShaderBindingTruth(report, $"MaterialShaderMapContent.Shaders[{shaderIndex}]", shader);
                shaderIndex++;
            }
        }
        else
        {
            report.AppendLine("MaterialShaderMapContent.Shaders: <missing>");
        }

        if (materialShaderMapContent.TryGetProperty("OrderedMeshShaderMaps", out JsonElement orderedMeshShaderMaps) &&
            orderedMeshShaderMaps.ValueKind == JsonValueKind.Array)
        {
            report.AppendLine($"MaterialShaderMapContent.OrderedMeshShaderMaps.Count: {orderedMeshShaderMaps.GetArrayLength()}");
            int meshShaderMapIndex = 0;
            foreach (JsonElement meshShaderMap in orderedMeshShaderMaps.EnumerateArray())
            {
                report.AppendLine($"OrderedMeshShaderMaps[{meshShaderMapIndex}].MeshShaderMapHash: {ReadOptionalString(meshShaderMap, "MeshShaderMapHash") ?? "<null>"}");
                report.AppendLine($"OrderedMeshShaderMaps[{meshShaderMapIndex}].VertexFactoryTypeHash: {ReadOptionalString(meshShaderMap, "VertexFactoryTypeHash") ?? "<null>"}");

                if (meshShaderMap.TryGetProperty("Shaders", out JsonElement meshShaders) && meshShaders.ValueKind == JsonValueKind.Array)
                {
                    report.AppendLine($"OrderedMeshShaderMaps[{meshShaderMapIndex}].Shaders.Count: {meshShaders.GetArrayLength()}");
                    int shaderIndex = 0;
                    foreach (JsonElement shader in meshShaders.EnumerateArray())
                    {
                        AppendSingleShaderBindingTruth(report, $"OrderedMeshShaderMaps[{meshShaderMapIndex}].Shaders[{shaderIndex}]", shader);
                        shaderIndex++;
                    }
                }
                else
                {
                    report.AppendLine($"OrderedMeshShaderMaps[{meshShaderMapIndex}].Shaders: <missing>");
                }

                meshShaderMapIndex++;
            }
        }
        else
        {
            report.AppendLine("MaterialShaderMapContent.OrderedMeshShaderMaps: <missing>");
        }

        report.AppendLine();
    }

    private static void AppendSingleShaderBindingTruth(StringBuilder report, string shaderLabel, JsonElement shader)
    {
        report.AppendLine($"{shaderLabel}.ResourceIndex: {ReadRequiredInt32(shader, "ResourceIndex")}");
        report.AppendLine($"{shaderLabel}.NumInstructions: {ReadRequiredUInt32(shader, "NumInstructions")}");
        report.AppendLine($"{shaderLabel}.SortKey: {ReadRequiredUInt32(shader, "SortKey")}");
        report.AppendLine($"{shaderLabel}.TypeHash: {ReadOptionalString(shader, "TypeHash") ?? "<null>"}");
        report.AppendLine($"{shaderLabel}.VertexFactoryTypeHash: {ReadOptionalString(shader, "VertexFactoryTypeHash") ?? "<null>"}");

        JsonElement bindings = GetRequiredProperty(shader, "Bindings");
        JsonElement parameters = GetRequiredProperty(bindings, "Parameters");
        JsonElement resourceParameters = GetRequiredProperty(bindings, "ResourceParameters");
        JsonElement bindlessResourceParameters = GetRequiredProperty(bindings, "BindlessResourceParameters");
        JsonElement graphUniformBuffers = GetRequiredProperty(bindings, "GraphUniformBuffers");
        JsonElement parameterReferences = GetRequiredProperty(bindings, "ParameterReferences");

        report.AppendLine($"  Bindings.Parameters.Count: {parameters.GetArrayLength()}");
        foreach (JsonElement parameter in parameters.EnumerateArray())
        {
            report.AppendLine($"    Parameter BufferIndex={ReadRequiredUInt32(parameter, "BufferIndex")} BaseIndex={ReadRequiredUInt32(parameter, "BaseIndex")} ByteOffset={ReadRequiredUInt32(parameter, "ByteOffset")} ByteSize={ReadRequiredUInt32(parameter, "ByteSize")}");
        }

        report.AppendLine($"  Bindings.ResourceParameters.Count: {resourceParameters.GetArrayLength()}");
        foreach (JsonElement parameter in resourceParameters.EnumerateArray())
        {
            report.AppendLine($"    ResourceParameter ByteOffset={ReadRequiredUInt32(parameter, "ByteOffset")} BaseIndex={ReadRequiredUInt32(parameter, "BaseIndex")} BaseType={ReadRequiredString(parameter, "BaseType")}");
        }

        report.AppendLine($"  Bindings.BindlessResourceParameters.Count: {bindlessResourceParameters.GetArrayLength()}");
        foreach (JsonElement parameter in bindlessResourceParameters.EnumerateArray())
        {
            report.AppendLine($"    BindlessResourceParameter ByteOffset={ReadRequiredUInt32(parameter, "ByteOffset")} GlobalConstantOffset={ReadRequiredUInt32(parameter, "GlobalConstantOffset")} BaseType={ReadRequiredString(parameter, "BaseType")}");
        }

        report.AppendLine($"  Bindings.GraphUniformBuffers.Count: {graphUniformBuffers.GetArrayLength()}");
        foreach (JsonElement parameter in graphUniformBuffers.EnumerateArray())
        {
            report.AppendLine($"    GraphUniformBuffer BufferIndex={ReadRequiredUInt32(parameter, "BufferIndex")} ByteOffset={ReadRequiredUInt32(parameter, "ByteOffset")}");
        }

        report.AppendLine($"  Bindings.ParameterReferences.Count: {parameterReferences.GetArrayLength()}");
        foreach (JsonElement parameter in parameterReferences.EnumerateArray())
        {
            report.AppendLine($"    ParameterReference BufferIndex={ReadRequiredUInt32(parameter, "BufferIndex")} ByteOffset={ReadRequiredUInt32(parameter, "ByteOffset")}");
        }

        report.AppendLine($"  Bindings.StructureLayoutHash: {ReadRequiredUInt32(bindings, "StructureLayoutHash")}");
        report.AppendLine($"  Bindings.RootParameterBufferIndex: {ReadRequiredUInt32(bindings, "RootParameterBufferIndex")}");

        JsonElement parameterMapInfo = GetRequiredProperty(shader, "ParameterMapInfo");
        JsonElement uniformBuffers = GetRequiredProperty(parameterMapInfo, "UniformBuffers");
        JsonElement textureSamplers = GetRequiredProperty(parameterMapInfo, "TextureSamplers");
        JsonElement srvs = GetRequiredProperty(parameterMapInfo, "SRVs");
        JsonElement looseParameterBuffers = GetRequiredProperty(parameterMapInfo, "LooseParameterBuffers");

        report.AppendLine($"  ParameterMapInfo.UniformBuffers.Count: {uniformBuffers.GetArrayLength()}");
        foreach (JsonElement parameter in uniformBuffers.EnumerateArray())
        {
            report.AppendLine($"    UniformBuffer BaseIndex={ReadRequiredUInt32(parameter, "BaseIndex")} Size={ReadRequiredUInt32(parameter, "Size")}");
        }

        report.AppendLine($"  ParameterMapInfo.TextureSamplers.Count: {textureSamplers.GetArrayLength()}");
        foreach (JsonElement parameter in textureSamplers.EnumerateArray())
        {
            report.AppendLine($"    TextureSampler BaseIndex={ReadRequiredUInt32(parameter, "BaseIndex")} Size={ReadRequiredUInt32(parameter, "Size")} BufferIndex={ReadRequiredUInt32(parameter, "BufferIndex")} Type={ReadRequiredUInt32(parameter, "Type")}");
        }

        report.AppendLine($"  ParameterMapInfo.SRVs.Count: {srvs.GetArrayLength()}");
        foreach (JsonElement parameter in srvs.EnumerateArray())
        {
            report.AppendLine($"    SRV BaseIndex={ReadRequiredUInt32(parameter, "BaseIndex")} Size={ReadRequiredUInt32(parameter, "Size")} BufferIndex={ReadRequiredUInt32(parameter, "BufferIndex")} Type={ReadRequiredUInt32(parameter, "Type")}");
        }

        report.AppendLine($"  ParameterMapInfo.LooseParameterBuffers.Count: {looseParameterBuffers.GetArrayLength()}");
        foreach (JsonElement buffer in looseParameterBuffers.EnumerateArray())
        {
            report.AppendLine($"    LooseParameterBuffer BaseIndex={ReadRequiredUInt32(buffer, "BaseIndex")} Size={ReadRequiredUInt32(buffer, "Size")}");
            JsonElement looseParameters = GetRequiredProperty(buffer, "Parameters");
            foreach (JsonElement parameter in looseParameters.EnumerateArray())
            {
                report.AppendLine($"      LooseParameter BaseIndex={ReadRequiredUInt32(parameter, "BaseIndex")} Size={ReadRequiredUInt32(parameter, "Size")}");
            }
        }

        report.AppendLine($"  ParameterMapInfo.Hash: {ReadOptionalString(parameterMapInfo, "Hash") ?? "<null>"}");
    }

    private static List<ParameterCollectionInfo> ScanParameterCollectionInfosFromChain(string exportRoot, List<MaterialAssetNode> materialChain)
    {
        var result = new List<ParameterCollectionInfo>();
        foreach (MaterialAssetNode node in materialChain)
        {
            string jsonPath = ResolveExportJsonPath(exportRoot, node.MaterialPath);
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(jsonPath));
            JsonElement assetRoot = document.RootElement;
            if (assetRoot.ValueKind != JsonValueKind.Array || assetRoot.GetArrayLength() == 0)
            {
                continue;
            }

            JsonElement infos = FindFirstPropertyRecursive(assetRoot[0], "ParameterCollectionInfos");
            if (infos.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement info in infos.EnumerateArray())
            {
                JsonElement collection = GetRequiredProperty(info, "ParameterCollection");
                result.Add(new ParameterCollectionInfo(
                    ReadRequiredString(info, "StateId"),
                    NormalizeObjectPath(ReadRequiredString(collection, "ObjectPath"))));
            }
        }

        return result;
    }

    private static List<ParameterCollectionInfo> ScanParameterCollectionInfosFromExportRoot(string exportRoot, HashSet<string> requestedStateIds)
    {
        var result = new List<ParameterCollectionInfo>();
        if (requestedStateIds.Count == 0)
        {
            return result;
        }

        string contentRoot = Path.Combine(exportRoot, "Content");
        string scanRoot = Directory.Exists(contentRoot) ? contentRoot : exportRoot;

        foreach (string jsonPath in Directory.EnumerateFiles(scanRoot, "*.json", SearchOption.AllDirectories))
        {
            string fileText = File.ReadAllText(jsonPath);
            if (!fileText.Contains("ParameterCollectionInfos", StringComparison.Ordinal) ||
                !requestedStateIds.Any(stateId =>
                    fileText.Contains(stateId, StringComparison.OrdinalIgnoreCase) ||
                    fileText.Contains(FormatGuidDashed(stateId), StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            using JsonDocument document = JsonDocument.Parse(fileText);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            {
                continue;
            }

            JsonElement infos = FindFirstPropertyRecursive(root[0], "ParameterCollectionInfos");
            if (infos.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement info in infos.EnumerateArray())
            {
                string stateId = NormalizeGuidString(ReadRequiredString(info, "StateId"));
                if (!requestedStateIds.Contains(stateId))
                {
                    continue;
                }

                JsonElement collection = GetRequiredProperty(info, "ParameterCollection");
                result.Add(new ParameterCollectionInfo(
                    ReadRequiredString(info, "StateId"),
                    NormalizeObjectPath(ReadRequiredString(collection, "ObjectPath"))));
            }
        }

        return result;
    }

    private static string FormatGuidDashed(string normalizedGuid)
    {
        if (normalizedGuid.Length != 32)
        {
            return normalizedGuid;
        }

        return string.Create(36, normalizedGuid, static (span, value) =>
        {
            value.AsSpan(0, 8).CopyTo(span);
            span[8] = '-';
            value.AsSpan(8, 4).CopyTo(span.Slice(9));
            span[13] = '-';
            value.AsSpan(12, 4).CopyTo(span.Slice(14));
            span[18] = '-';
            value.AsSpan(16, 4).CopyTo(span.Slice(19));
            span[23] = '-';
            value.AsSpan(20, 12).CopyTo(span.Slice(24));
        });
    }

    private static List<PreshaderSliceAnalysis> AnalyzePreshaderSlices(JsonElement uniformPreshaders, JsonElement uniformPreshaderFields, JsonElement uniformNumericParameters, List<string> names, byte[] opcodeData)
    {
        var analyses = new List<PreshaderSliceAnalysis>();
        int sliceIndex = 0;
        foreach (JsonElement preshader in uniformPreshaders.EnumerateArray())
        {
            int opcodeOffset = checked((int)ReadRequiredUInt32(preshader, "OpcodeOffset"));
            int opcodeSize = checked((int)ReadRequiredUInt32(preshader, "OpcodeSize"));
            int fieldIndex = checked((int)ReadRequiredUInt32(preshader, "FieldIndex"));
            int numFields = checked((int)ReadRequiredUInt32(preshader, "NumFields"));

            if (opcodeOffset < 0 || opcodeSize < 0 || opcodeOffset + opcodeSize > opcodeData.Length)
            {
                throw new InvalidDataException($"Invalid preshader slice range: offset={opcodeOffset}, size={opcodeSize}, dataLength={opcodeData.Length}");
            }

            var analysis = new PreshaderSliceAnalysis
            {
                SliceIndex = sliceIndex,
                OpcodeOffset = opcodeOffset,
                OpcodeSize = opcodeSize,
                WrittenFields = ReadWrittenFields(uniformPreshaderFields, fieldIndex, numFields),
                ParameterReferences = AnalyzeParameterReferences(uniformNumericParameters, names, opcodeData, opcodeOffset, opcodeSize)
            };

            if (analysis.WrittenFields.Count == 1 && analysis.ParameterReferences.Count == 1 && SliceIsSingleParameterRead(opcodeData, opcodeOffset, opcodeSize))
            {
                analysis.DirectParameterWrite = new DirectParameterWrite(
                    analysis.ParameterReferences[0].ParameterName,
                    analysis.ParameterReferences[0].ParameterType,
                    analysis.WrittenFields[0].BufferOffset,
                    analysis.WrittenFields[0].Type);
            }

            analyses.Add(analysis);
            sliceIndex++;
        }

        return analyses;
    }

    private static bool SliceIsSingleParameterRead(byte[] opcodeData, int opcodeOffset, int opcodeSize)
    {
        int cursor = opcodeOffset;
        int end = opcodeOffset + opcodeSize;
        int opcodeCount = 0;
        while (cursor < end)
        {
            byte opcode = ReadByte(opcodeData, ref cursor, end);
            opcodeCount++;
            if (opcode != 3)
            {
                return false;
            }

            ReadUInt16(opcodeData, ref cursor, end);
        }

        return opcodeCount == 1 && cursor == end;
    }

    private static List<ReferencedNumericParameter> AnalyzeParameterReferences(JsonElement uniformNumericParameters, List<string> names, byte[] opcodeData, int opcodeOffset, int opcodeSize)
    {
        var result = new List<ReferencedNumericParameter>();
        if (!SliceContainsOnlyParameterOpcodes(opcodeData, opcodeOffset, opcodeSize))
        {
            return result;
        }

        int cursor = opcodeOffset;
        int end = opcodeOffset + opcodeSize;
        while (cursor < end)
        {
            byte opcode = ReadByte(opcodeData, ref cursor, end);
            if (opcode != 3)
            {
                throw new InvalidDataException($"Unexpected opcode {opcode} in parameter-only slice.");
            }

            ushort parameterIndex = ReadUInt16(opcodeData, ref cursor, end);
            result.Add(ReadReferencedNumericParameter(uniformNumericParameters, parameterIndex));
        }

        if (cursor != end)
        {
            throw new InvalidDataException($"Preshader slice decode ended at {cursor}, expected {end}.");
        }

        return result;
    }

    private static bool SliceContainsOnlyParameterOpcodes(byte[] opcodeData, int opcodeOffset, int opcodeSize)
    {
        if (opcodeSize <= 0 || opcodeSize % 3 != 0)
        {
            return false;
        }

        int cursor = opcodeOffset;
        int end = opcodeOffset + opcodeSize;
        while (cursor < end)
        {
            if (opcodeData[cursor] != 3)
            {
                return false;
            }

            cursor += 3;
        }

        return true;
    }

    private static ReferencedNumericParameter ReadReferencedNumericParameter(JsonElement uniformNumericParameters, ushort parameterIndex)
    {
        JsonElement parameterElement = uniformNumericParameters[parameterIndex];
        return new ReferencedNumericParameter(
            parameterIndex,
            ReadRequiredString(parameterElement, "ParameterName"),
            ReadRequiredString(parameterElement, "ParameterType"),
            ReadRequiredUInt32(parameterElement, "DefaultValueOffset"));
    }

    private static List<WrittenField> ReadWrittenFields(JsonElement uniformPreshaderFields, int fieldIndex, int numFields)
    {
        var result = new List<WrittenField>(numFields);
        for (int i = 0; i < numFields; i++)
        {
            JsonElement field = uniformPreshaderFields[fieldIndex + i];
            result.Add(new WrittenField(
                ReadRequiredUInt32(field, "BufferOffset"),
                ReadRequiredUInt32(field, "ComponentIndex"),
                ReadRequiredString(field, "Type")));
        }

        return result;
    }

    private static byte ReadByte(byte[] opcodeData, ref int cursor, int end)
    {
        if (cursor >= end)
        {
            throw new InvalidDataException("Preshader data truncated while reading byte.");
        }

        return opcodeData[cursor++];
    }

    private static ushort ReadUInt16(byte[] opcodeData, ref int cursor, int end)
    {
        if (cursor + 2 > end)
        {
            throw new InvalidDataException("Preshader data truncated while reading uint16.");
        }

        ushort value = BitConverter.ToUInt16(opcodeData, cursor);
        cursor += 2;
        return value;
    }

    private static List<string> ReadStringArray(JsonElement array)
    {
        var result = new List<string>();
        foreach (JsonElement element in array.EnumerateArray())
        {
            result.Add(element.GetString() ?? throw new InvalidDataException("Expected string array element."));
        }

        return result;
    }

    private static List<MaterialAssetNode> LoadMaterialAssetChain(string exportRoot, string materialPath)
    {
        var result = new List<MaterialAssetNode>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? currentPath = NormalizeAssetPathForExport(exportRoot, materialPath);
        while (!string.IsNullOrWhiteSpace(currentPath) && visited.Add(currentPath))
        {
            MaterialAssetNode node = LoadMaterialAssetNode(exportRoot, currentPath);
            result.Add(node);
            currentPath = node.ParentPath != null ? NormalizeAssetPathForExport(exportRoot, node.ParentPath) : null;
        }

        return result;
    }

    private static MaterialAssetNode LoadMaterialAssetNode(string exportRoot, string materialPath)
    {
        string jsonPath = ResolveExportJsonPath(exportRoot, materialPath);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(jsonPath));
        JsonElement assetRoot = document.RootElement;
        if (assetRoot.ValueKind != JsonValueKind.Array || assetRoot.GetArrayLength() == 0)
        {
            throw new InvalidDataException($"Material asset JSON is not a non-empty array: {jsonPath}");
        }

        JsonElement asset = assetRoot[0];
        JsonElement properties = GetRequiredProperty(asset, "Properties");
        string? parentPath = null;
        if (properties.TryGetProperty("Parent", out JsonElement parent) && parent.ValueKind == JsonValueKind.Object)
        {
            parentPath = NormalizeObjectPath(ReadRequiredString(parent, "ObjectPath"));
        }

        List<ParameterCollectionInfo> parameterCollectionInfos = new();
        JsonElement infos = FindFirstPropertyRecursive(asset, "ParameterCollectionInfos");
        if (infos.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement info in infos.EnumerateArray())
            {
                JsonElement collection = GetRequiredProperty(info, "ParameterCollection");
                parameterCollectionInfos.Add(new ParameterCollectionInfo(
                    ReadRequiredString(info, "StateId"),
                    NormalizeObjectPath(ReadRequiredString(collection, "ObjectPath"))));
            }
        }

        return new MaterialAssetNode(materialPath, parentPath, parameterCollectionInfos);
    }

    private static JsonElement FindFirstPropertyRecursive(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(propertyName, out JsonElement direct))
            {
                return direct;
            }

            foreach (JsonProperty property in element.EnumerateObject())
            {
                JsonElement nested = FindFirstPropertyRecursive(property.Value, propertyName);
                if (nested.ValueKind != JsonValueKind.Undefined)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement entry in element.EnumerateArray())
            {
                JsonElement nested = FindFirstPropertyRecursive(entry, propertyName);
                if (nested.ValueKind != JsonValueKind.Undefined)
                {
                    return nested;
                }
            }
        }

        return default;
    }

    private static CollectionAssetTruth LoadCollectionAssetTruth(string exportRoot, string collectionPath)
    {
        string jsonPath = ResolveExportJsonPath(exportRoot, collectionPath);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(jsonPath));
        JsonElement assetRoot = document.RootElement;
        if (assetRoot.ValueKind != JsonValueKind.Array || assetRoot.GetArrayLength() == 0)
        {
            throw new InvalidDataException($"Collection asset JSON is not a non-empty array: {jsonPath}");
        }

        JsonElement asset = assetRoot[0];
        JsonElement properties = GetRequiredProperty(asset, "Properties");
        string stateId = ReadRequiredString(properties, "StateId");
        List<string> scalarNames = ReadCollectionParameterNames(properties, "ScalarParameters");
        List<string> vectorNames = ReadCollectionParameterNames(properties, "VectorParameters");
        return new CollectionAssetTruth(stateId, scalarNames, vectorNames);
    }

    private static List<string> ReadCollectionParameterNames(JsonElement properties, string propertyName)
    {
        if (!properties.TryGetProperty(propertyName, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            return new List<string>();
        }

        var result = new List<string>();
        foreach (JsonElement entry in array.EnumerateArray())
        {
            result.Add(ReadRequiredString(entry, "ParameterName"));
        }

        return result;
    }

    private static JsonElement FindMaterialInterface(JsonElement root, string materialPath)
    {
        JsonElement materialInterfaces = GetRequiredProperty(root, "MaterialInterfaces");
        if (materialInterfaces.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("MaterialInterfaces is not an object.");
        }

        foreach (JsonProperty property in materialInterfaces.EnumerateObject())
        {
            if (string.Equals(NormalizeMaterialPath(property.Name), materialPath, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        throw new InvalidDataException($"MaterialInterfaces entry not found for {materialPath}");
    }

    private static string ResolveExportJsonPath(string exportRoot, string objectPath)
    {
        string normalized = objectPath.TrimStart('/');
        string exportRootName = Path.GetFileName(exportRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.IsNullOrWhiteSpace(exportRootName) && normalized.StartsWith(exportRootName + "/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Substring(exportRootName.Length + 1);
        }

        if (normalized.StartsWith("Game/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = Path.Combine("Content", normalized.Substring(5).Replace('/', Path.DirectorySeparatorChar));
        }
        else if (!normalized.StartsWith("Content/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Replace('/', Path.DirectorySeparatorChar);
        }
        else
        {
            normalized = normalized.Replace('/', Path.DirectorySeparatorChar);
        }

        string fullPath = Path.Combine(exportRoot, normalized + ".json");
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Export JSON not found for object path {objectPath}: {fullPath}");
        }

        return fullPath;
    }

    private static string NormalizeObjectPath(string objectPath)
    {
        string normalized = (objectPath ?? throw new InvalidDataException("ObjectPath is null.")).Replace('\\', '/');
        int dotIndex = normalized.LastIndexOf('.');
        if (dotIndex > 0)
        {
            normalized = normalized.Substring(0, dotIndex);
        }

        return normalized;
    }

    private static string NormalizeMaterialPath(string materialPath)
    {
        return materialPath.Replace('\\', '/').Trim();
    }

    private static string NormalizeAssetPathForExport(string exportRoot, string assetPath)
    {
        string normalized = NormalizeMaterialPath(assetPath).TrimStart('/');
        string exportRootName = Path.GetFileName(exportRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (normalized.StartsWith("Game/", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(exportRootName)
                ? "Content/" + normalized.Substring(5)
                : exportRootName + "/Content/" + normalized.Substring(5);
        }

        return normalized;
    }

    private static string NormalizeGuidString(string value)
    {
        if (!Guid.TryParse(value, out Guid guid))
        {
            return value.Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        }

        return guid.ToString("N").ToUpperInvariant();
    }

    private static JsonElement GetRequiredProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            throw new InvalidDataException($"Missing required property: {propertyName}");
        }

        return value;
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        JsonElement value = GetRequiredProperty(element, propertyName);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"Property {propertyName} is not a string.");
        }

        return value.GetString() ?? throw new InvalidDataException($"Property {propertyName} is null.");
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static uint ReadRequiredUInt32(JsonElement element, string propertyName)
    {
        JsonElement value = GetRequiredProperty(element, propertyName);
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetUInt32(),
            _ => throw new InvalidDataException($"Property {propertyName} is not a uint32.")
        };
    }

    private static int ReadRequiredInt32(JsonElement element, string propertyName)
    {
        JsonElement value = GetRequiredProperty(element, propertyName);
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetInt32(),
            _ => throw new InvalidDataException($"Property {propertyName} is not an int32.")
        };
    }

    private sealed record MaterialAssetNode(string MaterialPath, string? ParentPath, List<ParameterCollectionInfo> ParameterCollectionInfos);
    private sealed record ParameterCollectionInfo(string StateId, string CollectionPath);
    private sealed record CollectionAssetTruth(string StateId, List<string> ScalarParameterNames, List<string> VectorParameterNames);
    private sealed record WrittenField(uint BufferOffset, uint ComponentIndex, string Type);
    private sealed record ReferencedNumericParameter(ushort ParameterIndex, string ParameterName, string ParameterType, uint DefaultValueOffset);
    private sealed record DirectParameterWrite(string ParameterName, string ParameterType, uint BufferOffset, string Type);
    private sealed class PreshaderSliceAnalysis
    {
        public int SliceIndex { get; init; }
        public int OpcodeOffset { get; init; }
        public int OpcodeSize { get; init; }
        public List<WrittenField> WrittenFields { get; init; } = new();
        public List<ReferencedNumericParameter> ParameterReferences { get; init; } = new();
        public DirectParameterWrite? DirectParameterWrite { get; set; }
    }
}
