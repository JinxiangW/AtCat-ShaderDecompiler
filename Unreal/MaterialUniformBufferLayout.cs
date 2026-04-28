using System.Collections.Generic;

namespace Ruri.ShaderTools.Unreal;

// Replays FUniformExpressionSet::CreateBufferStruct() to enumerate
// the resource members of the `Material` uniform buffer in the exact
// order UE writes them, so we can map an SRT ResourceIndex back to a
// canonical name.
//
// Source: Engine/Source/Runtime/Engine/Private/Materials/MaterialUniformExpressions.cpp:341-503.
//
// Only resource-typed members (UBMT_TEXTURE / UBMT_SRV / UBMT_SAMPLER)
// land in FRHIUniformBufferLayout.Resources[] -- the SRT ResourceIndex
// indexes into that list. Numeric-typed leading members
// (`VTPackedPageTableUniform`, `VTPackedUniform`, `PreshaderBuffer`)
// occupy bytes at the start of the constant buffer but do not consume
// resource slots, so we skip them here.
//
// Order replayed by CreateBufferStruct (every block is conditional on
// its count > 0; absent counts emit zero entries):
//
//   for each Standard2D[i]    : Texture2D_<i>            (UBMT_TEXTURE)
//                                Texture2D_<i>Sampler     (UBMT_SAMPLER)
//   for each Cube[i]          : TextureCube_<i>          (TEXTURE)
//                                TextureCube_<i>Sampler   (SAMPLER)
//   for each Array2D[i]       : Texture2DArray_<i>       (TEXTURE)
//                                Texture2DArray_<i>Sampler(SAMPLER)
//   for each ArrayCube[i]     : TextureCubeArray_<i>     (TEXTURE)
//                                TextureCubeArray_<i>Sampler(SAMPLER)
//   for each Volume[i]        : VolumeTexture_<i>        (TEXTURE)
//                                VolumeTexture_<i>Sampler (SAMPLER)
//   for each External[i]      : ExternalTexture_<i>      (TEXTURE)
//                                ExternalTexture_<i>Sampler (SAMPLER)  // UE source uses MediaTextureSamplerNames which prints "ExternalTexture_<i>Sampler"
//   for each VTStack[i]       : VirtualTexturePageTable0_<i>           (TEXTURE)
//                                VirtualTexturePageTable1_<i>          (TEXTURE)  // only when Stack.NumLayers > 4
//                                VirtualTexturePageTableIndirection_<i>(TEXTURE)
//   for each Virtual[i]       : VirtualTexturePhysical_<i>             (UBMT_SRV, not TEXTURE -- supports sRGB/non-sRGB aliasing)
//                                VirtualTexturePhysical_<i>Sampler     (SAMPLER)
//   Wrap_WorldGroupSettings                                            (SAMPLER, unconditional)
//   Clamp_WorldGroupSettings                                           (SAMPLER, unconditional)
internal sealed class UeMaterialUniformBufferLayout
{
    private readonly List<string> _resourceMemberNames;

    public UeMaterialUniformBufferLayout(MaterialResourceCounts counts)
    {
        _resourceMemberNames = BuildResourceMemberNames(counts);
    }

    public string? ResolveResourceName(UeSrtRecord record)
    {
        int idx = record.ResourceIndex;
        if (idx < 0 || idx >= _resourceMemberNames.Count)
        {
            return null;
        }

        return $"Material_{_resourceMemberNames[idx]}";
    }

    public IReadOnlyList<string> ResourceMemberNames => _resourceMemberNames;

    private static List<string> BuildResourceMemberNames(MaterialResourceCounts counts)
    {
        List<string> result = new();
        AppendTextureSamplerPairs(result, "Texture2D", counts.Standard2D);
        AppendTextureSamplerPairs(result, "TextureCube", counts.Cube);
        AppendTextureSamplerPairs(result, "Texture2DArray", counts.Array2D);
        AppendTextureSamplerPairs(result, "TextureCubeArray", counts.ArrayCube);
        AppendTextureSamplerPairs(result, "VolumeTexture", counts.Volume);
        AppendTextureSamplerPairs(result, "ExternalTexture", counts.External);
        AppendVirtualTextureStacks(result, counts.VirtualTextureStackLayerCounts);
        AppendTextureSamplerPairs(result, "VirtualTexturePhysical", counts.Virtual);
        // Fixed members emitted unconditionally by CreateBufferStruct at the end.
        result.Add("Wrap_WorldGroupSettings");
        result.Add("Clamp_WorldGroupSettings");
        return result;
    }

    private static void AppendTextureSamplerPairs(List<string> result, string baseName, int count)
    {
        for (int i = 0; i < count; i++)
        {
            result.Add($"{baseName}_{i}");
            result.Add($"{baseName}_{i}Sampler");
        }
    }

    private static void AppendVirtualTextureStacks(List<string> result, IReadOnlyList<int>? layerCountsPerStack)
    {
        if (layerCountsPerStack == null)
        {
            return;
        }

        for (int i = 0; i < layerCountsPerStack.Count; i++)
        {
            result.Add($"VirtualTexturePageTable0_{i}");
            if (layerCountsPerStack[i] > 4)
            {
                result.Add($"VirtualTexturePageTable1_{i}");
            }
            result.Add($"VirtualTexturePageTableIndirection_{i}");
        }
    }

    public sealed record MaterialResourceCounts(
        int Standard2D,
        int Cube,
        int Array2D,
        int ArrayCube,
        int Volume,
        int External,
        int Virtual,
        IReadOnlyList<int>? VirtualTextureStackLayerCounts);
}
