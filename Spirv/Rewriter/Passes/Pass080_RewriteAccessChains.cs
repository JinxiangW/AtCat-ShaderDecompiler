using Ruri.ShaderTools.Spirv.Rewriter.Helpers;
using Ruri.ShaderTools.Spirv.Rewriter.Models;

namespace Ruri.ShaderTools.Spirv.Rewriter.Passes;

// Pass 080: walk the module once and:
//
//   * Rewrite each rewritten CB's `OpVariable` to point at the new struct's pointer type.
//   * Translate every `OpAccessChain` / `OpInBoundsAccessChain` targeting one of those
//     variables into the matching structured access. Two outcomes per chain:
//       - direct translation (member at this byte offset): rewrite the chain's words and
//         record a `RewrittenAccessChainInfo` with `Translation` non-null.
//       - composite-extract fallback (whole-vector load, named members for sub-components
//         only): leave the chain's words alone, but record it in `state.RewrittenChains`
//         so Pass090 can fork the path per OpCompositeExtract.
//
// Plans contribute uniform pointer types via `FindOrCreateUniformPointerType`; the cache
// (`pointerTypeByMemberTypeId`) is rebuilt per-call rather than carried in state because
// it's only read by the next pass and a fresh map is cheap.
internal static class Pass080_RewriteAccessChains
{
    public static void DoPass(RewriterPipelineState state)
    {
        if (state.Plans.Count == 0)
        {
            return;
        }

        var rewriteByVariableId = state.Plans.ToDictionary(static p => p.Info.VariableId);
        var pointerTypeByMemberTypeId = new Dictionary<uint, uint>();
        var rewrittenChains = new Dictionary<uint, RewrittenAccessChainInfo>();

        foreach (BufferRewritePlan plan in state.Plans)
        {
            foreach (uint memberTypeId in plan.MemberTypeIds)
            {
                if (!pointerTypeByMemberTypeId.ContainsKey(memberTypeId))
                {
                    pointerTypeByMemberTypeId[memberTypeId] = TypeFactory.FindOrCreateUniformPointerType(state.Module, memberTypeId);
                }
            }
        }

        foreach (SpirvInstruction instruction in state.Module.Instructions)
        {
            if (instruction.OpCode == SpvOpCode.OpVariable && instruction.Words.Length >= 4)
            {
                if (rewriteByVariableId.TryGetValue(instruction[2], out BufferRewritePlan? rewrite))
                {
                    instruction[1] = rewrite.NewPointerTypeId;
                }

                continue;
            }

            if ((instruction.OpCode != SpvOpCode.OpAccessChain && instruction.OpCode != SpvOpCode.OpInBoundsAccessChain)
                || instruction.Words.Length < 4)
            {
                continue;
            }

            if (!rewriteByVariableId.TryGetValue(instruction[3], out BufferRewritePlan? plan)
                || !AccessTranslator.TryParseFlatAccessChain(instruction, state.Constants, out FlatAccessPath accessPath))
            {
                continue;
            }

            StructuredAccessTranslation? translated = AccessTranslator.TranslateFlatAccess(plan.Layout, accessPath, state.Constants);
            if (translated == null)
            {
                if (CompositeExtractFallback.CanRewrite(state.Module, instruction[2], plan.Layout, accessPath, state.Constants))
                {
                    rewrittenChains[instruction[2]] = new RewrittenAccessChainInfo
                    {
                        AccessChainResultId = instruction[2],
                        BaseVariableId = instruction[3],
                        InstructionOpCode = instruction.OpCode,
                        Plan = plan,
                        OriginalAccessPath = accessPath.Clone(),
                    };
                }

                continue;
            }

            if (!pointerTypeByMemberTypeId.TryGetValue(translated.MemberTypeId, out uint pointerTypeId))
            {
                continue;
            }

            instruction.Words = new[]
            {
                SpvOpCode.MakeInstructionWord(instruction.OpCode, (ushort)(4 + translated.Indices.Count)),
                pointerTypeId,
                instruction[2],
                instruction[3],
            }.Concat(translated.Indices).ToArray();

            rewrittenChains[instruction[2]] = new RewrittenAccessChainInfo
            {
                AccessChainResultId = instruction[2],
                BaseVariableId = instruction[3],
                InstructionOpCode = instruction.OpCode,
                Plan = plan,
                OriginalAccessPath = accessPath.Clone(),
                Translation = translated,
            };
        }

        state.RewrittenChains = rewrittenChains;
        state.UniformPointerTypes = pointerTypeByMemberTypeId;
    }
}
