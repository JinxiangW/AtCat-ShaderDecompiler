using System.Collections.Generic;

namespace Ruri.ShaderTools.Unreal;

// Mirrors the resource-member ordering produced by
// FUniformExpressionSet::CreateBufferStruct() for the `Material`
// uniform buffer, so we can map an SRT ResourceIndex back to the
// canonical member name (`Texture2D_<i>`, `Texture2D_<i>Sampler`,
// `TextureCube_<i>`, `VolumeTexture_<i>`, `ExternalTexture_<i>`,
// `VirtualTexturePhysical_<i>`, ...).
//
// Source ordering is fixed in
// Engine/Source/Runtime/Engine/Private/Materials/MaterialUniformExpressions.cpp
// CreateBufferStruct(): texture/sampler pairs are emitted typed-array
// by typed-array, in the order Standard2D -> Cube -> Array2D ->
// ArrayCube -> Volume -> External -> Virtual. After all texture
// resources, virtual-texture page tables are emitted as SRVs.
//
// What we *don't* know without inspecting the cooked metadata is
// how many entries each typed array has. Counts come from the
// material's UniformExpressionSet:
// - UniformTextureParameters[Standard2D|Array2D|Cube|ArrayCube|Volume|Virtual]
// - UniformExternalTextureParameters
// We accept those counts at construction and do the replay locally.
internal sealed class UeMaterialUniformBufferLayout
{
    private readonly List<string> _resourceMemberNames;
    private readonly Dictionary<string, int> _samplerCountByMember = new();

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
        AppendTextureSamplerPairs(result, "VirtualTexturePhysical", counts.VirtualPhysical);
        AppendSrvOnly(result, "VirtualTexturePageTable0", counts.VirtualPageTable);
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

    private static void AppendSrvOnly(List<string> result, string baseName, int count)
    {
        for (int i = 0; i < count; i++)
        {
            result.Add($"{baseName}_{i}");
        }
    }

    public sealed record MaterialResourceCounts(
        int Standard2D,
        int Cube,
        int Array2D,
        int ArrayCube,
        int Volume,
        int External,
        int VirtualPhysical,
        int VirtualPageTable);
}
