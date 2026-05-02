using Ruri.ShaderTools.Spirv.Rewriter.Helpers;
using Ruri.ShaderTools.Spirv.Rewriter.Models;

namespace Ruri.ShaderTools.Spirv.Rewriter.Passes;

// Pass 100: structural dead-code cleanup. Three cascades — Bitcasts, Loads, AccessChains —
// each NOP-d only when the module no longer references the result id from any non-NOP
// id-bearing slot.
//
// Why structural and not use-counts: SPIR-V ops carry literal values in operand slots
// (OpConstant value words, OpExtInst's instruction enum, OpCompositeExtract's component
// indices, …). When those literals coincide numerically with a real SSA id, a generic
// counter inflates the use count and dead instructions stay alive. `LiveIdScanner` scans
// the module the same way but knows which ops to skip (constant-defs, decorations, debug
// names, ...) so the answer it gives is the structurally correct one.
//
// Cascade order matters: Bitcasts first (their consumers, the OpCompositeExtract instances,
// have already been NOP-d in Pass090); then Loads (their consumers were the Bitcasts /
// extracts we just removed); finally AccessChains (their consumers were the Loads). Each
// cascade re-runs the live-id scan against the current module, so the dependencies clear
// out top-down.
//
// Why we MUST NOP the access chains: Pass080 rewrote each variable's pointer type to the
// new struct's pointer type. An old-style flat-array OpAccessChain (`%uint_0 %register`)
// against the new struct is invalid — spirv-cross trips on "Cannot subdivide a scalar
// value". Removing them once nothing live consumes them is the only safe option.
internal static class Pass100_CleanupDeadCode
{
    public static void DoPass(RewriterPipelineState state)
    {
        if (state.RewrittenChains.Count == 0)
        {
            return;
        }

        NopUnusedBitcasts(state);
        NopUnusedLoads(state);
        NopUnusedAccessChains(state);
    }

    private static void NopUnusedBitcasts(RewriterPipelineState state)
    {
        foreach (KeyValuePair<uint, SpirvInstruction> kvp in state.ProcessedBitcasts)
        {
            SpirvInstruction bitcastInstr = kvp.Value;
            if (bitcastInstr.OpCode != SpvOpCode.OpBitcast)
            {
                continue;
            }

            if (!LiveIdScanner.HasLiveIdConsumer(state.Module, kvp.Key))
            {
                NopInstruction(bitcastInstr);
            }
        }
    }

    private static void NopUnusedLoads(RewriterPipelineState state)
    {
        foreach (RewrittenLoadInfo loadInfo in state.TrackedLoads.Values)
        {
            SpirvInstruction loadInstruction = loadInfo.Instruction;
            if (loadInstruction.OpCode != SpvOpCode.OpLoad || loadInstruction.Words.Length < 4)
            {
                continue;
            }

            if (!LiveIdScanner.HasLiveIdConsumer(state.Module, loadInfo.ResultId))
            {
                NopInstruction(loadInstruction);
            }
        }
    }

    private static void NopUnusedAccessChains(RewriterPipelineState state)
    {
        foreach (uint accessChainId in state.RewrittenChains.Keys)
        {
            if (LiveIdScanner.HasLiveIdConsumer(state.Module, accessChainId))
            {
                continue;
            }

            foreach (SpirvInstruction inst in state.Module.Instructions)
            {
                if ((inst.OpCode != SpvOpCode.OpAccessChain && inst.OpCode != SpvOpCode.OpInBoundsAccessChain)
                    || inst.Words.Length < 3
                    || inst[2] != accessChainId)
                {
                    continue;
                }

                NopInstruction(inst);
                break;
            }
        }
    }

    private static void NopInstruction(SpirvInstruction instruction)
    {
        instruction.OpCode = SpvOpCode.OpNop;
        instruction.Words = [SpvOpCode.MakeInstructionWord(SpvOpCode.OpNop, 1)];
    }
}
