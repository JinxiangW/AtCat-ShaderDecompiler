using System.Collections.Generic;
using System.Linq;

namespace Ruri.ShaderTools.Unreal;

// Bridges SRT decode + the shader's optional FShaderCodeUniformBuffers
// list to a ShaderSymbolData populated with named bindings:
//   - one BufferBinding per uniform buffer (`b<i>` named `<UBName>`)
//   - one TextureParameter / SamplerParameter / BufferBinding /
//     UAVParameter per SRT entry, named `<UBName>_<ResourceLabel>`.
//
// The ResourceLabel here is a *shape-correct placeholder* — for the
// `Material` UB it is later overwritten with proper
// `Texture2D_<i>` / `Texture2D_<i>Sampler` etc. by the Material UB
// layout helper. For engine UBs (`View`, `OpaqueBasePass`, ...) it is
// looked up against EngineUniformBuffers, with a placeholder fallback.
//
// Anonymous placeholders carry the UB context so that even when we
// don't yet know the canonical member name, the decompiled HLSL gets
// a far more readable identifier than spirv-cross's
// `_RegisterSpace0[N]`.
internal static class UeShaderResourceTableSymbolizer
{
    public static void EnrichSymbolData(
        ShaderSymbolData target,
        UnrealShaderParser.UnrealMetadata? unrealMetadata,
        UeMaterialUniformBufferLayout? materialLayout = null)
    {
        if (unrealMetadata == null)
        {
            return;
        }

        IReadOnlyList<string>? uniformBufferNames = unrealMetadata.UniformBufferNames;
        AppendUniformBufferBindings(target, uniformBufferNames);

        FShaderResourceTable srt = unrealMetadata.SRT;
        if (System.Environment.GetEnvironmentVariable("RURI_SRT_DEBUG") == "1")
        {
            DumpSrt(srt, uniformBufferNames);
        }
        List<UeSrtRecord> records = UeShaderResourceTableDecoder.Decode(srt, uniformBufferNames);
        foreach (UeSrtRecord record in records)
        {
            string resolvedName = ResolveResourceName(record, materialLayout);
            switch (record.RegisterType)
            {
                case UeSrtRegisterType.Texture:
                case UeSrtRegisterType.ShaderResourceView:
                    AppendTextureParameter(target, record, resolvedName);
                    break;
                case UeSrtRegisterType.Sampler:
                    AppendSamplerParameter(target, record, resolvedName);
                    break;
                case UeSrtRegisterType.UnorderedAccessView:
                    AppendUavParameter(target, record, resolvedName);
                    break;
            }
        }
    }

    private static void DumpSrt(FShaderResourceTable srt, IReadOnlyList<string>? uniformBufferNames)
    {
        System.Console.Error.WriteLine($"[SRT] ResourceTableBits=0x{srt.ResourceTableBits:X8} ({System.Convert.ToString(srt.ResourceTableBits, 2).PadLeft(32, '0')})");
        if (uniformBufferNames != null)
        {
            for (int i = 0; i < uniformBufferNames.Count; i++)
            {
                bool used = (srt.ResourceTableBits & (1u << i)) != 0;
                System.Console.Error.WriteLine($"[SRT] UB[{i}] = {uniformBufferNames[i]} (used={used})");
            }
        }
        DumpMap("SRV/Texture", srt.ShaderResourceViewMap);
        DumpMap("Sampler", srt.SamplerMap);
        DumpMap("UAV", srt.UnorderedAccessViewMap);
        DumpMap("LayoutHashes", srt.ResourceTableLayoutHashes);
    }

    private static void DumpMap(string label, IReadOnlyList<uint>? map)
    {
        if (map == null)
        {
            System.Console.Error.WriteLine($"[SRT] {label}: <null>");
            return;
        }
        System.Console.Error.WriteLine($"[SRT] {label} ({map.Count} entries):");
        for (int i = 0; i < map.Count; i++)
        {
            uint token = map[i];
            (int bindIndex, int resourceIndex, int unpackedBufferIndex) = UeShaderResourceTableDecoder.Unpack(token);
            System.Console.Error.WriteLine($"[SRT]   [{i:D3}] = 0x{token:X8} -> bind={bindIndex} resource={resourceIndex} ub={unpackedBufferIndex}");
        }
    }

    private static void AppendUniformBufferBindings(ShaderSymbolData target, IReadOnlyList<string>? uniformBufferNames)
    {
        if (uniformBufferNames == null)
        {
            return;
        }

        for (int i = 0; i < uniformBufferNames.Count; i++)
        {
            string name = uniformBufferNames[i];
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (target.ConstantBufferBindings.Any(existing => existing.Set == 0 && existing.Index == i))
            {
                continue;
            }

            target.ConstantBufferBindings.Add(new BufferBinding
            {
                Name = name,
                NameIndex = -1,
                Index = i,
                Set = 0,
                ArraySize = 0,
            });
        }
    }

    private static void AppendTextureParameter(ShaderSymbolData target, UeSrtRecord record, string resolvedName)
    {
        if (target.TextureParameters.Any(existing => existing.Set == 0 && existing.Index == record.BindIndex))
        {
            return;
        }

        target.TextureParameters.Add(new TextureParameter
        {
            Name = resolvedName,
            NameIndex = -1,
            Index = record.BindIndex,
            Set = 0,
            SamplerIndex = -1,
            MultiSampled = false,
            Dim = 2,
        });
    }

    private static void AppendSamplerParameter(ShaderSymbolData target, UeSrtRecord record, string resolvedName)
    {
        if (target.Samplers.Any(existing => existing.Set == 0 && existing.Index == record.BindIndex))
        {
            return;
        }

        target.Samplers.Add(new SamplerParameter
        {
            Sampler = (uint)record.BindIndex,
            Index = record.BindIndex,
            Set = 0,
        });
    }

    private static void AppendUavParameter(ShaderSymbolData target, UeSrtRecord record, string resolvedName)
    {
        if (target.UAVs.Any(existing => existing.Set == 0 && existing.Index == record.BindIndex))
        {
            return;
        }

        target.UAVs.Add(new UAVParameter
        {
            Name = resolvedName,
            NameIndex = -1,
            Index = record.BindIndex,
            Set = 0,
            OriginalIndex = record.BindIndex,
        });
    }

    private static string ResolveResourceName(UeSrtRecord record, UeMaterialUniformBufferLayout? materialLayout)
    {
        string ubName = string.IsNullOrWhiteSpace(record.UniformBufferName)
            ? $"UB{record.UniformBufferIndex}"
            : record.UniformBufferName!;

        if (string.Equals(ubName, "Material", System.StringComparison.Ordinal) && materialLayout != null)
        {
            string? typed = materialLayout.ResolveResourceName(record);
            if (!string.IsNullOrWhiteSpace(typed))
            {
                return typed!;
            }
        }

        // Engine UBs (View, OpaqueBasePass, SceneTextures, LumenCardScene,
        // VirtualShadowMap, ...) — their per-member names live only in
        // engine C++ source and are NOT serialized into cooked data.
        // Recovery from a shipping cook alone is impossible by design
        // (see UE_SHIPPING_NAME_TRUTH.md). We deliberately do not
        // hard-code those layouts: they would silently rot across UE
        // versions and outright fabricate names for any custom-engine
        // fork. So everything outside the Material UB falls through to
        // the placeholder below — UB context is preserved (`View_SRV45`
        // tells the reader which UB the slot belongs to and at which
        // resource index) without inventing a member name we cannot
        // prove from the game files.
        string suffix = record.RegisterType switch
        {
            UeSrtRegisterType.Sampler => $"Sampler{record.ResourceIndex}",
            UeSrtRegisterType.UnorderedAccessView => $"UAV{record.ResourceIndex}",
            UeSrtRegisterType.ShaderResourceView => $"SRV{record.ResourceIndex}",
            _ => $"Resource{record.ResourceIndex}",
        };
        return $"{ubName}_{suffix}";
    }
}
