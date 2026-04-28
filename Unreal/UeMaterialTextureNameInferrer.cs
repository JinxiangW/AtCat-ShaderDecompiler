using System;
using System.Collections.Generic;
using Ruri.ShaderTools.Spirv;

namespace Ruri.ShaderTools.Unreal;

// Recovers Material UB texture names by leveraging UE's mechanically-paired
// texture/sampler emission.
//
// In Engine/Source/Runtime/Engine/Private/Materials/HLSLMaterialTranslator.cpp:6108-6131,
// FHLSLMaterialTranslator::TextureSample() emits the sampler argument as:
//
//   SSM_FromTextureAsset           -> "<TextureName>Sampler"
//   SSM_Wrap_WorldGroupSettings    -> "GetMaterialSharedSampler(<TextureName>Sampler, View.MaterialTextureBilinearWrapedSampler)"
//                                  or "GetMaterialSharedSampler(<TextureName>Sampler, Material.Wrap_WorldGroupSettings)"
//   SSM_Clamp_WorldGroupSettings   -> "GetMaterialSharedSampler(<TextureName>Sampler, View.MaterialTextureBilinearClampedSampler)"
//                                  or "GetMaterialSharedSampler(<TextureName>Sampler, Material.Clamp_WorldGroupSettings)"
//   SSM_TerrainWeightmapGroupSettings -> "GetMaterialSharedSampler(<TextureName>Sampler, View.LandscapeWeightmapSampler)"
//
// where <TextureName> is "Material.Texture2D_<i>" / "Material.TextureCube_<i>" /
// etc., the same name CreateBufferStruct() registered for the Material UB
// member. After RemoveUniformBuffersFromSource flattens "." -> "_", the
// shader compiler sees them as paired top-level globals.
//
// On the SSM_FromTextureAsset path the texture and its OWN sampler are the
// arguments to the shader's `Texture.Sample(Sampler, ...)` call, so once we
// know the sampler is `Material_Texture2D_<i>Sampler` (recovered via SRT +
// CreateBufferStruct replay), the texture in that SampledImage pair is by
// construction `Material_Texture2D_<i>` -- no inference, no heuristic, the
// pairing is literally hardcoded one line above in the same C++ function.
//
// This pass scans the SPIR-V module for OpSampledImage instructions, walks
// back the texture-load and sampler-load operands to their OpVariable
// declarations, reads the (DescriptorSet, Binding) decorations off both,
// and -- for any pair whose sampler resolves to a Material UB sampler name
// -- adds a TextureParameter for the texture binding with the canonical
// Material UB texture name.
//
// Limits:
// - Only the SSM_FromTextureAsset path is closed-form recoverable (the
//   other SSM_* paths route through GetMaterialSharedSampler, which makes
//   the sampler argument at the OpSampledImage call site a *shared*
//   View / Material sampler that doesn't match the texture name).
//   For those, this pass does nothing and the texture stays anonymous.
//   That's correct: we can't *prove* the pairing for shared-sampler
//   textures from SPIR-V alone.
internal static class UeMaterialTextureNameInferrer
{
    private const ushort OpLoad = 61;
    private const ushort OpSampledImage = 86;

    public static int InferAndAppend(byte[] spirv, ShaderSymbolData symbols)
    {
        if (spirv == null || spirv.Length < SpvOpCode.HeaderWordCount * 4)
        {
            return 0;
        }
        if (symbols.Samplers.Count == 0)
        {
            return 0;
        }

        uint[] words = BytesToWords(spirv);
        if (words.Length < SpvOpCode.HeaderWordCount || words[0] != SpvOpCode.MagicNumber)
        {
            return 0;
        }

        // Build maps in a single pass:
        //   loadResult -> sourceVarId          (from OpLoad pointer if pointer is a variable)
        //   varId -> (DescriptorSet, Binding)  (from OpDecorate)
        Dictionary<uint, uint> loadToVar = new();
        Dictionary<uint, int?> varToSet = new();
        Dictionary<uint, int?> varToBinding = new();
        List<(uint ImageLoadId, uint SamplerLoadId)> sampledImagePairs = new();

        int offset = SpvOpCode.HeaderWordCount;
        while (offset < words.Length)
        {
            uint header = words[offset];
            ushort opCode = SpvOpCode.GetOpCode(header);
            ushort wordCount = SpvOpCode.GetWordCount(header);
            if (wordCount == 0)
            {
                break;
            }

            switch (opCode)
            {
                case SpvOpCode.OpDecorate when wordCount >= 4:
                    {
                        uint targetId = words[offset + 1];
                        uint decoration = words[offset + 2];
                        if (decoration == SpvOpCode.DecorationDescriptorSet)
                        {
                            varToSet[targetId] = (int)words[offset + 3];
                        }
                        else if (decoration == SpvOpCode.DecorationBinding)
                        {
                            varToBinding[targetId] = (int)words[offset + 3];
                        }
                        break;
                    }
                case OpLoad when wordCount >= 4:
                    {
                        // OpLoad result_type result_id pointer [memory_access]
                        uint resultId = words[offset + 2];
                        uint pointerId = words[offset + 3];
                        // We optimistically treat the pointer as the variable
                        // itself; in HLSL-style SPIR-V from dxil-spirv the
                        // texture/sampler loads consume OpVariables directly
                        // without intervening OpAccessChain. If a later OpLoad
                        // overwrites the same result id, we just keep the most
                        // recent one (single-static-assignment in valid SPIR-V
                        // means this only happens across non-overlapping basic
                        // blocks anyway, so any of them points at the same
                        // texture/sampler binding for this pass's purposes).
                        loadToVar[resultId] = pointerId;
                        break;
                    }
                case OpSampledImage when wordCount >= 5:
                    {
                        // OpSampledImage result_type result_id image sampler
                        uint imageOperand = words[offset + 3];
                        uint samplerOperand = words[offset + 4];
                        sampledImagePairs.Add((imageOperand, samplerOperand));
                        break;
                    }
            }

            offset += wordCount;
        }

        if (sampledImagePairs.Count == 0)
        {
            return 0;
        }

        // Build a lookup: bindIndex -> sampler name (only Material samplers).
        Dictionary<int, string> samplerNameByBinding = new();
        foreach (SamplerParameter sampler in symbols.Samplers)
        {
            if (sampler.Set != 0 || string.IsNullOrWhiteSpace(sampler.Name))
            {
                continue;
            }
            samplerNameByBinding[sampler.Index] = sampler.Name!;
        }
        if (samplerNameByBinding.Count == 0)
        {
            return 0;
        }

        // Build a lookup: bindIndex -> existing TextureParameter (so we
        // don't overwrite a name that already came from a more authoritative
        // source like SRT-bound Material textures).
        HashSet<int> existingTextureBindings = new();
        foreach (TextureParameter texture in symbols.TextureParameters)
        {
            if (texture.Set == 0)
            {
                existingTextureBindings.Add(texture.Index);
            }
        }

        int appended = 0;
        HashSet<int> alreadyInferred = new();
        foreach ((uint imageLoadId, uint samplerLoadId) in sampledImagePairs)
        {
            if (!loadToVar.TryGetValue(imageLoadId, out uint imageVarId)
                || !loadToVar.TryGetValue(samplerLoadId, out uint samplerVarId))
            {
                continue;
            }

            int? imageSet = varToSet.GetValueOrDefault(imageVarId);
            int? imageBinding = varToBinding.GetValueOrDefault(imageVarId);
            int? samplerSet = varToSet.GetValueOrDefault(samplerVarId);
            int? samplerBinding = varToBinding.GetValueOrDefault(samplerVarId);
            if (imageSet != 0 || imageBinding == null || samplerSet != 0 || samplerBinding == null)
            {
                continue;
            }

            if (existingTextureBindings.Contains(imageBinding.Value)
                || alreadyInferred.Contains(imageBinding.Value))
            {
                continue;
            }

            if (!samplerNameByBinding.TryGetValue(samplerBinding.Value, out string? samplerName))
            {
                continue;
            }

            string? textureName = DeriveTextureNameFromSamplerName(samplerName);
            if (textureName == null)
            {
                continue;
            }

            symbols.TextureParameters.Add(new TextureParameter
            {
                Name = textureName,
                NameIndex = -1,
                Index = imageBinding.Value,
                Set = 0,
                SamplerIndex = samplerBinding.Value,
                MultiSampled = false,
                Dim = 2,
            });
            alreadyInferred.Add(imageBinding.Value);
            appended++;
        }

        return appended;
    }

    // SSM_FromTextureAsset (the only SSM whose sampler argument *is* the
    // texture's own paired sampler) emits sampler name "<TexName>Sampler".
    // The TexName can be either CreateBufferStruct's typed name (Texture2D_<i>
    // etc.) or the author-facing parameter name (`BambooBaseMaps`) when our
    // layout substituted it. Either way, stripping the trailing "Sampler"
    // suffix gives the texture's name. The Wrap/Clamp_WorldGroupSettings
    // unconditional samplers don't have a paired texture, so reject them.
    private static string? DeriveTextureNameFromSamplerName(string samplerName)
    {
        const string SamplerSuffix = "Sampler";
        if (!samplerName.EndsWith(SamplerSuffix, StringComparison.Ordinal))
        {
            return null;
        }
        if (!samplerName.StartsWith("Material_", StringComparison.Ordinal))
        {
            return null;
        }

        string textureName = samplerName.Substring(0, samplerName.Length - SamplerSuffix.Length);

        // Reject the two unconditional fixed members. They have no paired
        // texture (UE emits them as standalone shared samplers).
        if (textureName.EndsWith("_Wrap_WorldGroupSettings", StringComparison.Ordinal)
            || textureName.EndsWith("_Clamp_WorldGroupSettings", StringComparison.Ordinal))
        {
            return null;
        }

        return textureName;
    }

    private static uint[] BytesToWords(byte[] bytes)
    {
        uint[] words = new uint[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, words, 0, words.Length * 4);
        return words;
    }
}
