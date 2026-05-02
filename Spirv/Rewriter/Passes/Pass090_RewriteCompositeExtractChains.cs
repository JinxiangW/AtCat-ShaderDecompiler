using Ruri.ShaderTools.Spirv.Rewriter.Helpers;
using Ruri.ShaderTools.Spirv.Rewriter.Models;

namespace Ruri.ShaderTools.Spirv.Rewriter.Passes;

// Pass 090: handle the composite-extract fallback chains that Pass080 left intact.
//
// For every tracked OpLoad (whose pointer is a rewritten access chain), look at downstream
// `OpCompositeExtract` users — possibly via one `OpBitcast` hop. Each extract reads a
// specific component of the loaded vector, which together with the access chain's slot
// gives a precise byte offset; the access translator usually maps that offset back to a
// named member, and we replace the extract with a fresh `AccessChain + Load` reading the
// member directly.
//
// The Bitcast hop (`Load v4float → Bitcast v4uint → CompositeExtract uint`) is the
// HLSL `asuint(cb._m0[N]).y` pattern — common for bool members stored as uint. We follow
// it transparently; the dead-code cascade in Pass100 cleans up the now-unused Bitcast.
//
// Loads with no extracts left to rewrite still get re-typed in place when the access
// chain itself translated to a vector-shaped member (Pass080 set `Translation` non-null).
// That keeps the load valid against the new struct's pointer type without a downstream
// extract.
internal static class Pass090_RewriteCompositeExtractChains
{
    public static void DoPass(RewriterPipelineState state)
    {
        if (state.RewrittenChains.Count == 0)
        {
            return;
        }

        var loadInfos = new Dictionary<uint, RewrittenLoadInfo>();
        // OpBitcast result id → underlying tracked Load. Bitcasts from v4float to v4uint
        // (or similar) preserve vector width and only change scalar element type.
        var bitcastToLoad = new Dictionary<uint, RewrittenLoadInfo>();
        // Bitcast id → instruction ref. Used by Pass100 to NOP the Bitcast once its
        // CompositeExtract consumers are gone.
        state.ProcessedBitcasts = new Dictionary<uint, SpirvInstruction>();
        state.TrackedLoads = loadInfos;

        for (int index = 0; index < state.Module.Instructions.Count; index++)
        {
            SpirvInstruction instruction = state.Module.Instructions[index];

            if (instruction.OpCode == SpvOpCode.OpLoad && instruction.Words.Length >= 4
                && state.RewrittenChains.TryGetValue(instruction[3], out RewrittenAccessChainInfo? accessInfo))
            {
                loadInfos[instruction[2]] = new RewrittenLoadInfo
                {
                    Instruction = instruction,
                    ResultId = instruction[2],
                    OriginalResultTypeId = instruction[1],
                    HasCompositeExtractUsers = false,
                    AccessChain = accessInfo,
                };
                continue;
            }

            // Map Bitcasts that source from a tracked Load. SSA order guarantees the
            // Bitcast follows its source Load in module order, so this single-pass build
            // is sound.
            if (instruction.OpCode == SpvOpCode.OpBitcast && instruction.Words.Length >= 4
                && loadInfos.TryGetValue(instruction[3], out RewrittenLoadInfo? bitcastSource))
            {
                bitcastToLoad[instruction[2]] = bitcastSource;
                continue;
            }

            if (instruction.OpCode != SpvOpCode.OpCompositeExtract || instruction.Words.Length < 5)
            {
                continue;
            }

            RewrittenLoadInfo? loadInfo = ResolveLoadFromCompositeOperand(state, loadInfos, bitcastToLoad, instruction[3]);
            if (loadInfo == null)
            {
                continue;
            }

            loadInfo.HasCompositeExtractUsers = true;

            FlatAccessPath directAccessPath = loadInfo.AccessChain.OriginalAccessPath.Clone();
            directAccessPath.ExtraIndices.AddRange(instruction.Words.Skip(4).Select(static value => checked((int)value)));

            StructuredAccessTranslation? translated = AccessTranslator.TranslateFlatAccess(loadInfo.AccessChain.Plan.Layout, directAccessPath, state.Constants);
            if (translated == null || !state.UniformPointerTypes.TryGetValue(translated.MemberTypeId, out uint pointerTypeId))
            {
                continue;
            }

            uint pointerResultId = state.Module.AllocateId();
            state.Module.Instructions.Insert(index, new SpirvInstruction
            {
                OpCode = loadInfo.AccessChain.InstructionOpCode,
                Words = new[]
                {
                    SpvOpCode.MakeInstructionWord(loadInfo.AccessChain.InstructionOpCode, (ushort)(4 + translated.Indices.Count)),
                    pointerTypeId,
                    pointerResultId,
                    loadInfo.AccessChain.BaseVariableId,
                }.Concat(translated.Indices).ToArray(),
            });
            state.Module.Instructions.Insert(index + 1, new SpirvInstruction
            {
                OpCode = SpvOpCode.OpLoad,
                Words =
                [
                    SpvOpCode.MakeInstructionWord(SpvOpCode.OpLoad, 4),
                    translated.MemberTypeId,
                    instruction[2],
                    pointerResultId,
                ],
            });

            // Fresh AC + Load take over the extract's result id, so any further consumer
            // of the original composite extract automatically reads the new member load.
            index += 2;
            instruction.OpCode = SpvOpCode.OpNop;
            instruction.Words = [SpvOpCode.MakeInstructionWord(SpvOpCode.OpNop, 1)];
        }

        // Loads whose only "consumer" was a CompositeExtract that read the whole vector
        // (no sub-component index — the access path is byte-exact for the vector-shaped
        // member) skip the rewrite branch above; re-type the load in place.
        foreach (RewrittenLoadInfo loadInfo in loadInfos.Values)
        {
            SpirvInstruction loadInstruction = loadInfo.Instruction;
            if (loadInstruction.OpCode != SpvOpCode.OpLoad || loadInstruction.Words.Length < 4)
            {
                continue;
            }

            if (!loadInfo.HasCompositeExtractUsers && loadInfo.AccessChain.Translation != null)
            {
                loadInstruction[1] = loadInfo.AccessChain.Translation.MemberTypeId;
            }
        }
    }

    private static RewrittenLoadInfo? ResolveLoadFromCompositeOperand(
        RewriterPipelineState state,
        Dictionary<uint, RewrittenLoadInfo> loadInfos,
        Dictionary<uint, RewrittenLoadInfo> bitcastToLoad,
        uint compositeOperandId)
    {
        if (loadInfos.TryGetValue(compositeOperandId, out RewrittenLoadInfo? directLoad))
        {
            return directLoad;
        }

        if (!bitcastToLoad.TryGetValue(compositeOperandId, out RewrittenLoadInfo? viaBitcast))
        {
            return null;
        }

        // Track the Bitcast for the dead-code cleanup. Re-find by id; cheap because at
        // most one Bitcast per result id.
        foreach (SpirvInstruction maybeBitcast in state.Module.Instructions)
        {
            if (maybeBitcast.OpCode == SpvOpCode.OpBitcast && maybeBitcast.Words.Length >= 3 && maybeBitcast[2] == compositeOperandId)
            {
                state.ProcessedBitcasts[compositeOperandId] = maybeBitcast;
                break;
            }
        }
        return viaBitcast;
    }
}
