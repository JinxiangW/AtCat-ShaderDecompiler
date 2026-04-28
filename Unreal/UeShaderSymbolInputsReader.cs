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
            ReadUniformExpressionSet(inputs, uniformExpressionSet.Value);
        }

        ReadFallbackNumericParameters(asset, inputs.NumericParameterInfos);

        return inputs.NumericParameterInfos.Count == 0 && inputs.MaterialConstantBuffer == null
            ? null
            : inputs;
    }

    // Direct entry: caller already has the FUniformExpressionSet element
    // (e.g. from UnifiedShaderMetadata.json's
    // `MaterialInterfaces[<path>].LoadedShaderMaps[*].MaterialShaderMapContent.UniformExpressionSet`),
    // so we skip the per-material-asset wrapping and read the bridge
    // straight off it. `UsedLoadedMaterialResources` is forced true so
    // the source's score reflects that we picked a properly cooked
    // shader map.
    public static UeShaderSymbolInputs? ReadFromUniformExpressionSet(string materialPath, string? shaderPlatform, JsonElement uniformExpressionSet)
    {
        UeShaderSymbolInputs inputs = new()
        {
            MaterialPath = materialPath,
            ShaderPlatform = shaderPlatform,
            UsedLoadedMaterialResources = true,
        };

        ReadUniformExpressionSet(inputs, uniformExpressionSet);

        return inputs.NumericParameterInfos.Count == 0
               && inputs.MaterialConstantBuffer == null
               && inputs.MaterialResourceCounts == null
            ? null
            : inputs;
    }

    private static void ReadUniformExpressionSet(UeShaderSymbolInputs inputs, JsonElement uniformExpressionSet)
    {
        inputs.MaterialConstantBuffer = ReadMaterialConstantBuffer(uniformExpressionSet);
        ReadUniformNumericParameters(uniformExpressionSet, inputs.NumericParameterInfos);
        inputs.MaterialResourceCounts = ReadMaterialResourceCounts(uniformExpressionSet);
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

        // Walk every preshader. Each is a `Material` CB slot writer:
        //   field[FieldIndex] = evaluate(opcode stream)
        // The field record carries the authoritative (BufferOffset, Type) of
        // the slot; we honour that even when the opcode stream is too complex
        // to fully simulate. Naming is best-effort from the opcode stream:
        //   `Parameter(N)`  -> parameters[N].Name
        //   `Parameter(N) + ComponentSwizzle(...)` -> ParamName_<swizzle>
        //   `Parameter(N) + Saturate/Rcp/...` -> ParamName_<op>
        //   anything else -> ParamName_expr_<byteOffset> or `f_<byteOffset>`
        // Rationale: rewriter only needs (byteOffset, type) per slot to
        // expand cbuffer struct correctly; missing slots collapse the whole
        // CB to a single anonymous float4 array (the M_Bamboo_tree bug).
        HashSet<int> seenOffsets = new();
        HashSet<string> seenNames = new(StringComparer.Ordinal);
        List<VectorParameter> vectorParams = new();
        foreach (JsonElement preshader in uniformPreshaders.EnumerateArray())
        {
            uint opcodeOffset = ReadUInt32(preshader, "OpcodeOffset");
            uint opcodeSize = ReadUInt32(preshader, "OpcodeSize");
            uint fieldIndex = ReadUInt32(preshader, "FieldIndex");
            uint numFields = ReadUInt32(preshader, "NumFields");

            // Only single-field-output preshaders for now. Multi-field writes
            // emerge for struct outputs and are uncommon for Material CBs;
            // adding them later requires walking each field's ComponentIndex
            // and Type independently.
            if (numFields != 1)
            {
                continue;
            }
            if (fieldIndex >= uniformPreshaderFields.GetArrayLength())
            {
                continue;
            }

            JsonElement field = uniformPreshaderFields[checked((int)fieldIndex)];
            if (!TryMapFieldType(ReadString(field, "Type"), out int rows, out _))
            {
                continue;
            }

            int byteOffset = checked((int)ReadUInt32(field, "BufferOffset") * 4);
            if (!seenOffsets.Add(byteOffset))
            {
                continue;
            }

            string name = DerivePreshaderName(opcodeData, opcodeOffset, opcodeSize, uniformNumericParameters, byteOffset);
            if (!seenNames.Add(name))
            {
                name = $"{name}_at_{byteOffset}";
                seenNames.Add(name);
            }

            vectorParams.Add(new VectorParameter
            {
                Name = name,
                NameIndex = -1,
                Type = ShaderParamType.Float,
                ByteOffset = byteOffset,
                ArraySize = 1,
                IsMatrix = false,
                RowCount = (byte)rows,
                ColumnCount = 1,
            });
        }

        if (vectorParams.Count == 0)
        {
            return null;
        }

        materialBuffer.VectorParams = vectorParams
            .OrderBy(static p => p.ByteOffset)
            .ToArray();
        return materialBuffer;
    }

    private static string SwizzleSuffix(byte numE, byte r, byte g, byte b, byte a)
    {
        if (numE == 0 || numE > 4)
        {
            return string.Empty;
        }

        Span<byte> indices = stackalloc byte[4] { r, g, b, a };
        Span<char> chars = stackalloc char[4];
        for (int i = 0; i < numE; i++)
        {
            byte v = indices[i];
            char c = v switch
            {
                0 => 'x',
                1 => 'y',
                2 => 'z',
                3 => 'w',
                _ => '\0',
            };
            if (c == '\0')
            {
                return string.Empty;
            }
            chars[i] = c;
        }
        return new string(chars[..numE]);
    }

    // Name the cbuffer slot from the preshader opcode stream.
    //
    // Honesty rule: name the slot only if we can decode *every* byte of the
    // opcode stream into a closed-form description of what the runtime VM
    // writes into the slot. If any byte is unaccounted for (unrecognized
    // opcode, partial read, multi-Parameter expression we don't model), fall
    // back to anonymous `f_<byteOffset>`. This guarantees that any printed
    // name describes a value whose runtime byte content we can reproduce
    // exactly from public UE 5.1 source semantics — no guessing.
    //
    // Decoded forms:
    //   Parameter(N)                          -> parameters[N].Name
    //   Parameter(N) + ComponentSwizzle(..)   -> ParamName_<xyzw...>
    //   Parameter(N) + UnaryOp                -> ParamName_<op>
    //
    // EPreshaderOpcode reference: Engine/Source/Runtime/Engine/Public/Shader/Preshader.h:19-75
    // (Parameter=3, Rcp=22, Saturate=25, Abs=26, Floor=27, Ceil=28, Round=29,
    //  Trunc=30, Sign=31, Frac=32, Fractional=33, ComponentSwizzle=36, Neg=45).
    // ComponentSwizzle payload: Engine/Source/Runtime/Engine/Private/Shader/Preshader.cpp:649-655
    // (uint8 NumElements, IndexR, IndexG, IndexB, IndexA).
    private static string DerivePreshaderName(
        byte[] data,
        uint offset,
        uint size,
        JsonElement parameters,
        int byteOffset)
    {
        string anonymous = $"f_{byteOffset}";

        // Must start with Parameter(N): exactly 1 + 2 = 3 bytes.
        if (size < 3 || offset >= (uint)data.Length || offset + 3 > (uint)data.Length)
        {
            return anonymous;
        }
        if (data[offset] != 3)
        {
            return anonymous;
        }

        ushort paramIdx = BitConverter.ToUInt16(data, checked((int)offset + 1));
        if (paramIdx >= parameters.GetArrayLength())
        {
            return anonymous;
        }

        FMaterialParameterInfo? info = ParseMaterialParameterInfo(parameters[paramIdx]);
        if (info == null)
        {
            return anonymous;
        }
        string baseName = info.Name;

        // Pure Parameter(N) — slot is byte-equal to the parameter.
        if (size == 3)
        {
            return baseName;
        }

        // Parameter(N) + one trailing op that fully consumes the rest of the
        // opcode stream.
        int rest = checked((int)offset + 3);
        int restSize = checked((int)size) - 3;
        if (rest >= data.Length || restSize <= 0)
        {
            return anonymous;
        }
        byte tailOp = data[rest];

        // ComponentSwizzle: 1 op byte + 5 payload bytes (NumE, R, G, B, A).
        if (tailOp == 36 && restSize == 6 && rest + 6 <= data.Length)
        {
            byte numE = data[rest + 1];
            byte r = data[rest + 2];
            byte g = data[rest + 3];
            byte b = data[rest + 4];
            byte a = data[rest + 5];
            string swizzle = SwizzleSuffix(numE, r, g, b, a);
            if (!string.IsNullOrEmpty(swizzle))
            {
                return $"{baseName}_{swizzle}";
            }
            return anonymous;
        }

        // Unary in-place ops: 1 op byte and nothing else.
        if (restSize == 1)
        {
            string? unary = tailOp switch
            {
                22 => "rcp",
                25 => "sat",
                26 => "abs",
                27 => "floor",
                28 => "ceil",
                29 => "round",
                30 => "trunc",
                31 => "sign",
                32 => "frac",
                33 => "fractional",
                45 => "neg",
                _ => null,
            };
            if (unary != null)
            {
                return $"{baseName}_{unary}";
            }
        }

        // Anything else is a multi-step expression (Constants, Clamp, Append,
        // arithmetic, second Parameter pulls). We can't describe the slot's
        // runtime value in closed form -> anonymous.
        return anonymous;
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
        int Virtual = ReadTypedArrayLength(textureParams, 5);

        int External = 0;
        if (uniformExpressionSet.TryGetProperty("UniformExternalTextureParameters", out JsonElement externalParams) && externalParams.ValueKind == JsonValueKind.Array)
        {
            External = externalParams.GetArrayLength();
        }

        // VTStack page tables are independent of UniformTextureParameters[Virtual].
        // Each FMaterialVirtualTextureStack carries its own NumLayers, which gates
        // whether a 5th-8th layer page table (VirtualTexturePageTable1_<i>) is
        // emitted in addition to PageTable0/Indirection. We need the per-stack
        // layer count, not just the stack count.
        List<int>? vtStackLayers = null;
        if (uniformExpressionSet.TryGetProperty("VTStacks", out JsonElement vtStacks) && vtStacks.ValueKind == JsonValueKind.Array)
        {
            vtStackLayers = new List<int>(vtStacks.GetArrayLength());
            foreach (JsonElement stack in vtStacks.EnumerateArray())
            {
                vtStackLayers.Add(ReadVirtualTextureStackNumLayers(stack));
            }
        }

        return new UeMaterialUniformBufferLayout.MaterialResourceCounts(
            Standard2D: Standard2D,
            Cube: Cube,
            Array2D: Array2D,
            ArrayCube: ArrayCube,
            Volume: Volume,
            External: External,
            Virtual: Virtual,
            VirtualTextureStackLayerCounts: vtStackLayers);
    }

    // FMaterialVirtualTextureStack stores LayerUniformExpressionIndices as an
    // 8-element fixed array; "NumLayers" is the count of indices that are not
    // INDEX_NONE. The shape in FModel/CUE4Parse JSON varies, so probe a few
    // common forms; if none match we conservatively assume <=4 layers (no
    // PageTable1_<i> entry).
    private static int ReadVirtualTextureStackNumLayers(JsonElement stack)
    {
        if (stack.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        if (stack.TryGetProperty("NumLayers", out JsonElement numLayers) && numLayers.ValueKind == JsonValueKind.Number)
        {
            return numLayers.GetInt32();
        }

        if (stack.TryGetProperty("LayerUniformExpressionIndices", out JsonElement layers) && layers.ValueKind == JsonValueKind.Array)
        {
            int count = 0;
            foreach (JsonElement element in layers.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }
                int value = element.GetInt32();
                if (value >= 0)
                {
                    count++;
                }
            }
            return count;
        }

        return 0;
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
        // Accept both shapes:
        //   * per-material `.uasset.json` (FModel "Save Properties"):
        //     `{ "ParameterInfo": { "Name": "...", "Association": "...", "Index": ... }, ... }`
        //   * UnifiedShaderMetadata.json (Ruri.FModelHook hook output):
        //     flattened `{ "ParameterName": "...", "Association": "...", "Index": ..., ... }`
        JsonElement parameterInfo;
        bool nested;
        if (element.TryGetProperty("ParameterInfo", out parameterInfo) && parameterInfo.ValueKind == JsonValueKind.Object)
        {
            nested = true;
        }
        else
        {
            parameterInfo = element;
            nested = false;
        }

        string? name = nested
            ? ReadString(parameterInfo, "Name")
            : (ReadString(parameterInfo, "ParameterName") ?? ReadString(parameterInfo, "Name"));
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name, "None", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? associationRaw = ReadString(parameterInfo, "Association");
        EMaterialParameterAssociation association = associationRaw switch
        {
            "EMaterialParameterAssociation::LayerParameter" => EMaterialParameterAssociation.LayerParameter,
            "EMaterialParameterAssociation::BlendParameter" => EMaterialParameterAssociation.BlendParameter,
            "LayerParameter" => EMaterialParameterAssociation.LayerParameter,
            "BlendParameter" => EMaterialParameterAssociation.BlendParameter,
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
