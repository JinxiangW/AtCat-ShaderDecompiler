using Ruri.ShaderTools.Spirv.Rewriter.Helpers;
using Ruri.ShaderTools.Spirv.Rewriter.Models;

namespace Ruri.ShaderTools.Spirv.Rewriter.Passes;

// Pass 030: turn each matched flat buffer into a `BufferRewritePlan` carrying its
// metadata-derived `StructuredBufferLayout`. The layout itself is built by `LayoutBuilder`
// (pure metadata → byte-offset member list); this pass just creates the plans and drops
// any flat buffer whose layout is empty / unbuildable / doesn't fit the SPIR-V flat array.
//
// The "fit" check is intentionally tight (`ArrayStride == 16` + at least one member inside
// the flat array length): a smaller stride means the SPIR-V buffer wasn't compiled as a
// cbuffer at all, and a member sitting past the flat array would be an out-of-bounds read
// in the rewritten module.
internal static class Pass030_BuildLayouts
{
    public static void DoPass(RewriterPipelineState state)
    {
        foreach (FlatUniformBufferInfo flatBuffer in state.FlatBuffers)
        {
            StructuredBufferLayout? layout = LayoutBuilder.Build(flatBuffer);
            if (layout == null)
            {
                state.Summary.Add($"[{flatBuffer.Metadata.Name}] layout build failed");
                continue;
            }

            if (!IsValidFlatUniformBuffer(flatBuffer, layout))
            {
                state.Summary.Add($"[{flatBuffer.Metadata.Name}] layout does not fit flat buffer: stride={flatBuffer.ArrayStride}, arrayLength={flatBuffer.ArrayLength}, usedRegisters={layout.MaxUsedRegisterCount}");
                continue;
            }

            state.Plans.Add(new BufferRewritePlan
            {
                Info = flatBuffer,
                Layout = layout,
            });
        }
    }

    private static bool IsValidFlatUniformBuffer(FlatUniformBufferInfo flatBuffer, StructuredBufferLayout layout)
    {
        if (flatBuffer.ArrayStride != 16)
        {
            return false;
        }

        return layout.Members.Count > 0
            && layout.Members.Any(member => member.RegisterOffset < flatBuffer.ArrayLength);
    }
}
