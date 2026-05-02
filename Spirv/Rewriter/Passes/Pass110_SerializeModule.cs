namespace Ruri.ShaderTools.Spirv.Rewriter.Passes;

// Pass 110: serialise the mutated SpirvModule back to bytes. Tiny pass kept on its own so
// the orchestrator's pass list stays uniform and you can spot "we got here, but didn't
// produce output" failures in the log.
internal static class Pass110_SerializeModule
{
    public static void DoPass(RewriterPipelineState state)
    {
        state.RewriteApplied = state.Plans.Count > 0;
        state.OutputSpirv = state.Module.ToBytes();
    }
}
