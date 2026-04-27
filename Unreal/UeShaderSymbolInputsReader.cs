using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Ruri.ShaderTools.Unreal;

internal static class UeShaderSymbolInputsReader
{
    public static UeShaderSymbolInputs? Read(string materialPath, string? shaderPlatform, JsonElement asset)
    {
        UeShaderSymbolInputs inputs = new()
        {
            MaterialPath = materialPath,
            ShaderPlatform = shaderPlatform,
        };

        JsonElement? selectedLoadedResource = SelectLoadedMaterialResource(asset, shaderPlatform, ref inputs);
        JsonElement? uniformExpressionSet = ResolveUniformExpressionSet(selectedLoadedResource);

        if (uniformExpressionSet.HasValue)
        {
            inputs.MaterialConstantBuffer = ReadMaterialConstantBuffer(uniformExpressionSet.Value);
            ReadUniformNumericParameters(uniformExpressionSet.Value, inputs.NumericParameterInfos);
            inputs.MaterialResourceCounts = ReadMaterialResourceCounts(uniformExpressionSet.Value);
        }

        ReadFallbackNumericParameters(asset, inputs.NumericParameterInfos);

        return inputs.NumericParameterInfos.Count == 0 && inputs.MaterialConstantBuffer == null
            ? null
            : inputs;
    }

    private static JsonElement? SelectLoadedMaterialResource(JsonElement asset, string? shaderPlatform, ref UeShaderSymbolInputs inputs)
    {
        if (!asset.TryGetProperty("LoadedMaterialResources", out JsonElement loadedResources) || loadedResources.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement resource in loadedResources.EnumerateArray())
        {
            if (!resource.TryGetProperty("LoadedShaderMap", out JsonElement loadedShaderMap) || loadedShaderMap.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? candidateShaderPlatform = ReadString(loadedShaderMap, "ShaderPlatform");
            if (!string.IsNullOrWhiteSpace(shaderPlatform) &&
                !string.Equals(candidateShaderPlatform, shaderPlatform, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            inputs.UsedLoadedMaterialResources = true;
            return resource.Clone();
        }

        foreach (JsonElement resource in loadedResources.EnumerateArray())
        {
            inputs.UsedLoadedMaterialResources = true;
            return resource.Clone();
        }

        return null;
    }

    private static JsonElement? ResolveUniformExpressionSet(JsonElement? loadedResource)
    {
        if (!loadedResource.HasValue)
        {
            return null;
        }

        JsonElement resource = loadedResource.Value;
        if (!resource.TryGetProperty("LoadedShaderMap", out JsonElement loadedShaderMap) || loadedShaderMap.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (loadedShaderMap.TryGetProperty("MaterialShaderMapContent", out JsonElement materialShaderMapContent) &&
            materialShaderMapContent.ValueKind == JsonValueKind.Object &&
            materialShaderMapContent.TryGetProperty("UniformExpressionSet", out JsonElement uniformExpressionSet))
        {
            return uniformExpressionSet.Clone();
        }

        if (loadedShaderMap.TryGetProperty("Content", out JsonElement content) &&
            content.ValueKind == JsonValueKind.Object &&
            content.TryGetProperty("MaterialCompilationOutput", out JsonElement materialCompilationOutput) &&
            materialCompilationOutput.ValueKind == JsonValueKind.Object &&
            materialCompilationOutput.TryGetProperty("UniformExpressionSet", out JsonElement nestedUniformExpressionSet))
        {
            return nestedUniformExpressionSet.Clone();
        }

        return null;
    }

    private static ConstantBuffer? ReadMaterialConstantBuffer(JsonElement uniformExpressionSet)
    {
        if (!uniformExpressionSet.TryGetProperty("UniformBufferLayoutInitializer", out JsonElement uniformBufferLayoutInitializer) ||
            uniformBufferLayoutInitializer.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? bufferName = ReadString(uniformBufferLayoutInitializer, "Name");
        if (!string.Equals(bufferName, "Material", StringComparison.Ordinal))
        {
            return null;
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
            return null;
        }

        string? encodedData = ReadString(uniformPreshaderData, "Data");
        if (string.IsNullOrWhiteSpace(encodedData))
        {
            return null;
        }

        byte[] opcodeData = Convert.FromBase64String(encodedData);
        ConstantBuffer materialBuffer = new()
        {
            Name = "Material",
            Size = checked((int)constantBufferSize)
        };

        HashSet<int> seenOffsets = new();
        foreach (JsonElement preshader in uniformPreshaders.EnumerateArray())
        {
            uint opcodeOffset = ReadUInt32(preshader, "OpcodeOffset");
            uint opcodeSize = ReadUInt32(preshader, "OpcodeSize");
            uint fieldIndex = ReadUInt32(preshader, "FieldIndex");
            uint numFields = ReadUInt32(preshader, "NumFields");

            if (!IsSingleParameterWrite(opcodeData, opcodeOffset, opcodeSize, numFields))
            {
                continue;
            }

            ushort parameterIndex = BitConverter.ToUInt16(opcodeData, checked((int)opcodeOffset + 1));
            if (parameterIndex >= uniformNumericParameters.GetArrayLength() || fieldIndex >= uniformPreshaderFields.GetArrayLength())
            {
                continue;
            }

            FMaterialParameterInfo? parameterInfo = ParseMaterialParameterInfo(uniformNumericParameters[parameterIndex]);
            if (parameterInfo == null)
            {
                continue;
            }

            JsonElement field = uniformPreshaderFields[checked((int)fieldIndex)];
            if (!TryMapFieldType(ReadString(field, "Type"), out int rows, out int columns))
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
                ParamName = parameterInfo.Name,
                ParamType = ShaderParamType.Float,
                Rows = rows,
                Columns = columns,
                IsMatrix = false,
                ArraySize = 1,
                ByteOffset = byteOffset
            });
        }

        if (materialBuffer.CBParams.Count == 0)
        {
            return null;
        }

        materialBuffer.CBParams.Sort((left, right) => left.ByteOffset.CompareTo(right.ByteOffset));
        return materialBuffer;
    }

    private static UeMaterialUniformBufferLayout.MaterialResourceCounts? ReadMaterialResourceCounts(JsonElement uniformExpressionSet)
    {
        if (!uniformExpressionSet.TryGetProperty("UniformTextureParameters", out JsonElement textureParams) || textureParams.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        // EMaterialTextureParameterType ordering matches FUniformExpressionSet::CreateBufferStruct:
        //   0 = Standard2D, 1 = Cube, 2 = Array2D, 3 = ArrayCube, 4 = Volume, 5 = Virtual.
        // External textures are a separate top-level array on the expression set.
        int Standard2D = ReadTypedArrayLength(textureParams, 0);
        int Cube = ReadTypedArrayLength(textureParams, 1);
        int Array2D = ReadTypedArrayLength(textureParams, 2);
        int ArrayCube = ReadTypedArrayLength(textureParams, 3);
        int Volume = ReadTypedArrayLength(textureParams, 4);
        int VirtualPhysical = ReadTypedArrayLength(textureParams, 5);

        int External = 0;
        if (uniformExpressionSet.TryGetProperty("UniformExternalTextureParameters", out JsonElement externalParams) && externalParams.ValueKind == JsonValueKind.Array)
        {
            External = externalParams.GetArrayLength();
        }

        // Page-table SRVs are emitted alongside virtual-texture physical pairs by CreateBufferStruct.
        return new UeMaterialUniformBufferLayout.MaterialResourceCounts(
            Standard2D: Standard2D,
            Cube: Cube,
            Array2D: Array2D,
            ArrayCube: ArrayCube,
            Volume: Volume,
            External: External,
            VirtualPhysical: VirtualPhysical,
            VirtualPageTable: VirtualPhysical);
    }

    private static int ReadTypedArrayLength(JsonElement arrayOfArrays, int index)
    {
        if (index < 0 || index >= arrayOfArrays.GetArrayLength())
        {
            return 0;
        }

        JsonElement inner = arrayOfArrays[index];
        return inner.ValueKind == JsonValueKind.Array ? inner.GetArrayLength() : 0;
    }

    private static void ReadUniformNumericParameters(JsonElement uniformExpressionSet, List<FMaterialParameterInfo> destination)
    {
        if (!uniformExpressionSet.TryGetProperty("UniformNumericParameters", out JsonElement numericParameters) || numericParameters.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement parameter in numericParameters.EnumerateArray())
        {
            FMaterialParameterInfo? parameterInfo = ParseMaterialParameterInfo(parameter);
            if (parameterInfo != null)
            {
                destination.Add(parameterInfo);
            }
        }
    }

    private static void ReadFallbackNumericParameters(JsonElement asset, List<FMaterialParameterInfo> destination)
    {
        if (!asset.TryGetProperty("Properties", out JsonElement properties) || properties.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        AppendMaterialParameterInfos(properties, "ScalarParameterValues", destination);
        AppendMaterialParameterInfos(properties, "VectorParameterValues", destination);
        AppendMaterialParameterInfos(properties, "DoubleVectorParameterValues", destination);
    }

    private static void AppendMaterialParameterInfos(JsonElement properties, string propertyName, List<FMaterialParameterInfo> destination)
    {
        if (!properties.TryGetProperty(propertyName, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement entry in array.EnumerateArray())
        {
            FMaterialParameterInfo? parameterInfo = ParseMaterialParameterInfo(entry);
            if (parameterInfo != null)
            {
                destination.Add(parameterInfo);
            }
        }
    }

    private static bool IsSingleParameterWrite(byte[] opcodeData, uint opcodeOffset, uint opcodeSize, uint numFields)
    {
        return numFields == 1 && opcodeSize == 3 && opcodeOffset + opcodeSize <= opcodeData.Length && opcodeData[opcodeOffset] == 3;
    }

    private static bool TryMapFieldType(string? fieldType, out int rows, out int columns)
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

    private static FMaterialParameterInfo? ParseMaterialParameterInfo(JsonElement element)
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

        EMaterialParameterAssociation association = ReadString(parameterInfo, "Association") switch
        {
            "EMaterialParameterAssociation::LayerParameter" => EMaterialParameterAssociation.LayerParameter,
            "EMaterialParameterAssociation::BlendParameter" => EMaterialParameterAssociation.BlendParameter,
            _ => EMaterialParameterAssociation.GlobalParameter
        };

        int index = parameterInfo.TryGetProperty("Index", out JsonElement indexElement) && indexElement.ValueKind == JsonValueKind.Number
            ? indexElement.GetInt32()
            : -1;
        return new FMaterialParameterInfo(name, association, index);
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
