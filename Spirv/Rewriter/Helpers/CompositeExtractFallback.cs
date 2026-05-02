using Ruri.ShaderTools.Spirv.Rewriter.Models;

namespace Ruri.ShaderTools.Spirv.Rewriter.Helpers;

// "Can we rewrite this access chain by leaving it as-is and rewriting its downstream
// CompositeExtracts directly into member loads instead?" — used by Pass050 (validation)
// and Pass080 (planning).
//
// The triggering shape: an access chain reads a whole vec4 register, but the metadata only
// names sub-components of that register (e.g. `_UseDitherClip` at byte 68 = register 4.y;
// no whole-vec4 member at register 4). The vector-level access fails to translate, but
// each `OpCompositeExtract` on the load picks out a specific component — and that component
// often DOES correspond to a named member.
//
// Recognised input chains:
//   * OpLoad → OpCompositeExtract                (HLSL cb._m0[N].y)
//   * OpLoad → OpBitcast → OpCompositeExtract    (HLSL asuint(cb._m0[N]).y, common for bool
//                                                 members stored as uint)
//
// The Bitcast hop is significant: spirv-dis reports it as a v4float→v4uint reinterpret,
// preserves vector width, and only changes the scalar element type. Without following it,
// the load looks like it has "no CompositeExtract users" and the whole CB is rejected for
// rewrite (the user-visible symptom: `UnityPerMaterial_m0[5]` in HLSL instead of named
// fields).
internal static class CompositeExtractFallback
{
    public static bool CanRewrite(
        SpirvModule module,
        uint accessChainResultId,
        StructuredBufferLayout layout,
        FlatAccessPath accessPath,
        ConstantMaps constants)
    {
        bool foundLoad = false;
        foreach (SpirvInstruction instruction in module.Instructions)
        {
            if (instruction.OpCode != SpvOpCode.OpLoad || instruction.Words.Length < 4 || instruction[3] != accessChainResultId)
            {
                continue;
            }

            foundLoad = true;
            uint loadResultId = instruction[2];

            // Build the set of "valid composite-extract sources" that resolve back to this
            // Load: the Load result itself plus every OpBitcast whose source is the Load.
            HashSet<uint> compositeExtractSources = CollectCompositeExtractSources(module, loadResultId);

            bool hasCompositeExtractUsers = false;
            foreach (SpirvInstruction user in module.Instructions)
            {
                if (user.OpCode != SpvOpCode.OpCompositeExtract || user.Words.Length < 5
                    || !compositeExtractSources.Contains(user[3]))
                {
                    continue;
                }

                hasCompositeExtractUsers = true;
                FlatAccessPath directAccessPath = accessPath.Clone();
                directAccessPath.ExtraIndices.AddRange(user.Words.Skip(4).Select(static value => checked((int)value)));
                if (AccessTranslator.TranslateFlatAccess(layout, directAccessPath, constants) == null)
                {
                    return false;
                }
            }

            if (!hasCompositeExtractUsers)
            {
                return false;
            }
        }

        return foundLoad;
    }

    public static HashSet<uint> CollectCompositeExtractSources(SpirvModule module, uint loadResultId)
    {
        var sources = new HashSet<uint> { loadResultId };
        foreach (SpirvInstruction maybeBitcast in module.Instructions)
        {
            if (maybeBitcast.OpCode == SpvOpCode.OpBitcast
                && maybeBitcast.Words.Length >= 4
                && maybeBitcast[3] == loadResultId)
            {
                sources.Add(maybeBitcast[2]);
            }
        }
        return sources;
    }
}
