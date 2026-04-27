using System.Collections.Generic;
using Ruri.ShaderTools.Engine;

namespace Ruri.ShaderTools.Unreal;

internal enum UeSrtRegisterType
{
    Texture,
    ShaderResourceView,
    Sampler,
    UnorderedAccessView,
}

internal sealed record UeSrtRecord(
    int UniformBufferIndex,
    string? UniformBufferName,
    int ResourceIndex,
    int BindIndex,
    UeSrtRegisterType RegisterType);

// Decodes FShaderResourceTable's packed uint32 entries into named slot
// records. Layout per Engine/Source/Runtime/RHI/Public/RHIDefinitions.h
// FRHIResourceTableEntry:
//   bits  0..7  -> BindIndex          (shader register)
//   bits  8..23 -> ResourceIndex      (index into UB's resource list)
//   bits 24..31 -> UniformBufferIndex (slot of the UB)
// Header layout per Engine/Source/Runtime/D3D12RHI/Private/D3D12Commands.cpp:
//   ResourceMap[bufferIndex] is the array offset where that UB's token
//   stream begins; tokens are read until UniformBufferIndex changes.
internal static class UeShaderResourceTableDecoder
{
    public static List<UeSrtRecord> Decode(FShaderResourceTable srt, IReadOnlyList<string>? uniformBufferNames)
    {
        List<UeSrtRecord> result = new();
        if (srt.ResourceTableBits == 0)
        {
            return result;
        }

        DecodeMap(srt.ShaderResourceViewMap, srt.ResourceTableBits, UeSrtRegisterType.ShaderResourceView, uniformBufferNames, result);
        DecodeMap(srt.SamplerMap, srt.ResourceTableBits, UeSrtRegisterType.Sampler, uniformBufferNames, result);
        DecodeMap(srt.UnorderedAccessViewMap, srt.ResourceTableBits, UeSrtRegisterType.UnorderedAccessView, uniformBufferNames, result);
        return result;
    }

    public static (int BindIndex, int ResourceIndex, int UniformBufferIndex) Unpack(uint token)
    {
        int bindIndex = (int)(token & 0xFFu);
        int resourceIndex = (int)((token >> 8) & 0xFFFFu);
        int uniformBufferIndex = (int)((token >> 24) & 0xFFu);
        return (bindIndex, resourceIndex, uniformBufferIndex);
    }

    private static void DecodeMap(
        IReadOnlyList<uint>? map,
        uint resourceTableBits,
        UeSrtRegisterType registerType,
        IReadOnlyList<string>? uniformBufferNames,
        List<UeSrtRecord> result)
    {
        if (map == null || map.Count == 0)
        {
            return;
        }

        for (int bufferIndex = 0; bufferIndex < 32; bufferIndex++)
        {
            if ((resourceTableBits & (1u << bufferIndex)) == 0)
            {
                continue;
            }

            if (bufferIndex >= map.Count)
            {
                break;
            }

            uint headerOffset = map[bufferIndex];
            if (headerOffset == 0)
            {
                continue;
            }

            int idx = (int)headerOffset;
            while (idx >= 0 && idx < map.Count)
            {
                uint token = map[idx];
                if (token == 0xFFFFFFFFu)
                {
                    break;
                }

                (int bindIndex, int resourceIndex, int unpackedBufferIndex) = Unpack(token);
                if (unpackedBufferIndex != bufferIndex)
                {
                    break;
                }

                string? bufferName = uniformBufferNames != null && bufferIndex < uniformBufferNames.Count
                    ? uniformBufferNames[bufferIndex]
                    : null;

                result.Add(new UeSrtRecord(
                    bufferIndex,
                    bufferName,
                    resourceIndex,
                    bindIndex,
                    registerType));
                idx++;
            }
        }
    }
}
