using System;
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
        _typedSlotByAuthorName = BuildAuthorIndex(counts);
    }

    private readonly Dictionary<string, string> _typedSlotByAuthorName;

    // Author-facing parameter name -> typed slot name (e.g. "Bamboo_base_maps"
    // -> "Texture2D_1"). Used by the texture-from-sampler-pair inferrer to
    // resolve sampler names like "Material_Bamboo_base_mapsSampler" back to
    // their typed slot when picking the texture binding name.
    public bool TryResolveAuthorName(string authorName, out string typedSlot)
        => _typedSlotByAuthorName.TryGetValue(authorName, out typedSlot!);

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
        AppendTextureSamplerPairs(result, "Texture2D", counts.Standard2D, counts.Standard2DAuthorNames);
        AppendTextureSamplerPairs(result, "TextureCube", counts.Cube, counts.CubeAuthorNames);
        AppendTextureSamplerPairs(result, "Texture2DArray", counts.Array2D, counts.Array2DAuthorNames);
        AppendTextureSamplerPairs(result, "TextureCubeArray", counts.ArrayCube, counts.ArrayCubeAuthorNames);
        AppendTextureSamplerPairs(result, "VolumeTexture", counts.Volume, counts.VolumeAuthorNames);
        AppendTextureSamplerPairs(result, "ExternalTexture", counts.External, counts.ExternalAuthorNames);

        // VirtualTextureStack page tables are inserted between External textures
        // and Virtual physical textures. Each stack emits:
        //   PageTable0_<i>           (TEXTURE)
        //   [PageTable1_<i>          (TEXTURE) — only when Stack.NumLayers > 4]
        //   PageTableIndirection_<i> (TEXTURE)
        //
        // Per-stack `NumLayers` is the source of truth, but it is NOT carried
        // by `UnifiedShaderMetadata.json` (FModel's hook flattens UES without
        // the VTStacks array). When `VirtualTextureStackLayerCounts` is null,
        // we INFER the stack count from the `Resources[]` length: there's a
        // known number of texture entries between the External block and the
        // Virtual physical block, and any TEXTURE entry there must be a VT
        // page-table member. We assume `NumLayers <= 4` for every stack
        // (the dominant case in shipped projects); a `>4`-layer stack would
        // require the actual VTStacks array to disambiguate.
        if (counts.VirtualTextureStackLayerCounts != null)
        {
            AppendVirtualTextureStacks(result, counts.VirtualTextureStackLayerCounts);
        }
        else if (counts.TotalResourceCount is int total)
        {
            int textureSamplerPairsConsumed = 2 * (counts.Standard2D + counts.Cube + counts.Array2D + counts.ArrayCube + counts.Volume + counts.External);
            int virtualPhysicalConsumed = 2 * counts.Virtual;
            int fixedTrailingSamplers = 2; // Wrap + Clamp
            int vtStackTextureCount = total - textureSamplerPairsConsumed - virtualPhysicalConsumed - fixedTrailingSamplers;
            if (vtStackTextureCount > 0 && vtStackTextureCount % 2 == 0)
            {
                int inferredStackCount = vtStackTextureCount / 2;
                List<int> assumedLayers = new(inferredStackCount);
                for (int i = 0; i < inferredStackCount; i++)
                {
                    assumedLayers.Add(2); // <= 4 -> emit PageTable0 + Indirection only
                }
                AppendVirtualTextureStacks(result, assumedLayers);
            }
            // If vtStackTextureCount % 2 != 0, at least one stack must have
            // NumLayers > 4 (3 entries) and we cannot uniquely solve the mix
            // without the actual VTStacks array. Skip the page-table block;
            // downstream layout will be off after this block, so do not name
            // anything past External when this happens. Caller can detect
            // this by comparing ResourceMemberNames.Count vs Resources.Num().
        }

        AppendTextureSamplerPairs(result, "VirtualTexturePhysical", counts.Virtual, counts.VirtualAuthorNames);
        // Fixed members emitted unconditionally by CreateBufferStruct at the end.
        result.Add("Wrap_WorldGroupSettings");
        result.Add("Clamp_WorldGroupSettings");
        return result;
    }

    private static void AppendTextureSamplerPairs(List<string> result, string baseName, int count, IReadOnlyList<string?>? authorNames = null)
    {
        for (int i = 0; i < count; i++)
        {
            string? author = (authorNames != null && i < authorNames.Count) ? authorNames[i] : null;
            // Prefer the author-facing parameter name when present (sanitized
            // for HLSL identifiers); fall back to the typed slot name UE
            // generated via CreateBufferStruct's printf. Either form is
            // source-truth: typed comes from UE's `Texture2D_<i>` template,
            // author-name comes from the `.uasset` ParameterInfo.Name.
            string sanitized = SanitizeHlslIdent(author);
            string textureName = string.IsNullOrEmpty(sanitized) ? $"{baseName}_{i}" : sanitized;
            result.Add(textureName);
            result.Add($"{textureName}Sampler");
        }
    }

    private static string SanitizeHlslIdent(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "None", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[raw.Length];
        int written = 0;
        foreach (char c in raw)
        {
            char ch = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ? c : '_';
            buffer[written++] = ch;
        }
        if (written == 0)
        {
            return string.Empty;
        }
        // HLSL identifier cannot start with a digit; prepend underscore.
        if (buffer[0] >= '0' && buffer[0] <= '9')
        {
            return "_" + new string(buffer[..written]);
        }
        return new string(buffer[..written]);
    }

    private static Dictionary<string, string> BuildAuthorIndex(MaterialResourceCounts counts)
    {
        Dictionary<string, string> index = new(StringComparer.Ordinal);
        Add(index, "Texture2D", counts.Standard2D, counts.Standard2DAuthorNames);
        Add(index, "TextureCube", counts.Cube, counts.CubeAuthorNames);
        Add(index, "Texture2DArray", counts.Array2D, counts.Array2DAuthorNames);
        Add(index, "TextureCubeArray", counts.ArrayCube, counts.ArrayCubeAuthorNames);
        Add(index, "VolumeTexture", counts.Volume, counts.VolumeAuthorNames);
        Add(index, "ExternalTexture", counts.External, counts.ExternalAuthorNames);
        Add(index, "VirtualTexturePhysical", counts.Virtual, counts.VirtualAuthorNames);
        return index;

        static void Add(Dictionary<string, string> idx, string baseName, int count, IReadOnlyList<string?>? authorNames)
        {
            if (authorNames == null) return;
            for (int i = 0; i < count && i < authorNames.Count; i++)
            {
                string sanitized = SanitizeHlslIdent(authorNames[i]);
                if (sanitized.Length > 0)
                {
                    // Map author-name -> typed slot for both texture and sampler
                    idx[sanitized] = $"{baseName}_{i}";
                    idx[sanitized + "Sampler"] = $"{baseName}_{i}Sampler";
                }
            }
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
        IReadOnlyList<int>? VirtualTextureStackLayerCounts,
        // Optional: total number of entries in
        // FRHIUniformBufferLayoutInitializer.Resources[]. When the unified
        // metadata path strips VTStacks, this lets us infer the VT stack
        // count by subtraction so the layout still resolves correctly.
        int? TotalResourceCount = null,
        // Per-typed-block author names from
        // UniformTextureParameters[Type][i].ParameterInfo.Name (or
        // ParameterName in the flattened unified-metadata shape). Each list
        // is parallel to the corresponding count: index `i` is the user-
        // facing name of the `i`-th texture in that typed block, or null /
        // "None" when the slot is anonymous (compiler-internal). When set,
        // the layout uses these to override the typed slot names like
        // `Texture2D_<i>` with the user-recognisable identifier so the
        // HLSL output reads as `Material_BambooBaseMaps` rather than
        // `Material_Texture2D_1`.
        IReadOnlyList<string?>? Standard2DAuthorNames = null,
        IReadOnlyList<string?>? CubeAuthorNames = null,
        IReadOnlyList<string?>? Array2DAuthorNames = null,
        IReadOnlyList<string?>? ArrayCubeAuthorNames = null,
        IReadOnlyList<string?>? VolumeAuthorNames = null,
        IReadOnlyList<string?>? ExternalAuthorNames = null,
        IReadOnlyList<string?>? VirtualAuthorNames = null);
}
