using Ruri.ShaderTools.Spirv.Rewriter.Helpers;
using Ruri.ShaderTools.Spirv.Rewriter.Models;

namespace Ruri.ShaderTools.Spirv.Rewriter.Passes;

// Pass 050: gate each rewrite plan on "can we successfully rewrite *every* OpAccessChain
// targeting this CB's variable?". An ALL-OR-NOTHING check by design — if even one chain
// can't be translated and can't fall back to per-CompositeExtract rewriting, abandon the
// CB. A partial rewrite would leave the variable's pointer type pointing at the new
// struct while some OpAccessChain still uses indices for the old flat array, which
// trips spirv-cross with cryptic "cannot subdivide a scalar value" / "no decorated id"
// errors deep in HLSL emission.
//
// The two acceptable outcomes per chain:
//   1. `AccessTranslator.TranslateFlatAccess` returns a member translation directly
//      (the typical case — register N maps cleanly to a named member).
//   2. The chain reads a whole vec4 register that has no exact-match member, but every
//      downstream OpCompositeExtract (optionally one OpBitcast hop) DOES translate. The
//      access chain itself stays as-is; Pass090 will replace the extracts with direct
//      member loads.
//
// Failure summary lines are intentionally rich — they include the unparseable instruction
// shape so a human can spirv-dis the dump and see exactly which pattern needs handling.
internal static class Pass050_ValidateAccessChains
{
    public static void DoPass(RewriterPipelineState state)
    {
        var survived = new List<BufferRewritePlan>(state.Plans.Count);
        foreach (BufferRewritePlan plan in state.Plans)
        {
            if (!CanRewriteAllAccessChains(state.Module, plan, state.Constants, out string? failure))
            {
                state.Summary.Add($"[{plan.Info.Metadata.Name}] rewrite validation failed: {failure}");
                continue;
            }

            survived.Add(plan);
        }
        state.Plans = survived;
    }

    private static bool CanRewriteAllAccessChains(SpirvModule module, BufferRewritePlan plan, ConstantMaps constants, out string? failure)
    {
        failure = null;
        int accessChainCount = 0;

        foreach (SpirvInstruction instruction in module.Instructions)
        {
            if ((instruction.OpCode != SpvOpCode.OpAccessChain && instruction.OpCode != SpvOpCode.OpInBoundsAccessChain)
                || instruction.Words.Length < 4)
            {
                continue;
            }

            if (instruction[3] != plan.Info.VariableId)
            {
                continue;
            }

            accessChainCount++;
            if (!AccessTranslator.TryParseFlatAccessChain(instruction, constants, out FlatAccessPath accessPath))
            {
                failure = $"unsupported access chain parse for resultId={instruction[2]} op={instruction.OpCode} words=[{string.Join(",", instruction.Words)}]";
                return false;
            }

            bool directOk = AccessTranslator.TranslateFlatAccess(plan.Layout, accessPath, constants) != null;
            bool compositeFallbackOk = !directOk && CompositeExtractFallback.CanRewrite(module, instruction[2], plan.Layout, accessPath, constants);
            if (!directOk && !compositeFallbackOk)
            {
                failure = $"unsupported access translation for resultId={instruction[2]} slotConst={accessPath.Slot.ConstantRegisterOffset} slotDynamic={accessPath.Slot.DynamicIndexId} stride={accessPath.Slot.DynamicIndexStride} extra=[{string.Join(",", accessPath.ExtraIndices)}] op={instruction.OpCode} words=[{string.Join(",", instruction.Words)}]";
                return false;
            }
        }

        if (accessChainCount == 0)
        {
            failure = "no access chains found for variable";
            return false;
        }

        return true;
    }
}
