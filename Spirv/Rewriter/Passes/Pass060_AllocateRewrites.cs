namespace Ruri.ShaderTools.Spirv.Rewriter.Passes;

// Pass 060: allocate the new OpTypeStruct + OpTypePointer ids each surviving plan needs.
//
// Tiny pass on purpose — the id allocation has to happen between "we've decided to rewrite
// this CB" (Pass050) and "we're physically inserting the type into the module" (Pass070),
// because Pass070 inserts decorations referring to ids by value. Splitting it out also
// records the (set, binding) → name mapping for downstream patcher consumers
// (`StructuredCBufferRewriter.GetResolvedBufferName`); the symbol patcher uses that to
// map builtin-bound bindings (`b0` → `ShaderVariablesGlobal`, etc.) back to friendly
// names regardless of whether the rewrite ultimately succeeded.
internal static class Pass060_AllocateRewrites
{
    public static void DoPass(RewriterPipelineState state)
    {
        foreach (var plan in state.Plans)
        {
            plan.NewStructTypeId = state.Module.AllocateId();
            plan.NewPointerTypeId = state.Module.AllocateId();
            state.ResolvedBufferNames[(plan.Info.Metadata.Set, plan.Info.Metadata.Binding)] = plan.Info.Metadata.Name;
            state.Summary.Add($"[{plan.Info.Metadata.Name}] rewrite planned with {plan.Layout.Members.Count} members");
        }
    }
}
