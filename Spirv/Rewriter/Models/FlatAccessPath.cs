namespace Ruri.ShaderTools.Spirv.Rewriter.Models;

// A parsed flat-array access chain (`cb._m0[register].component...`) decomposed into the
// register slot expression plus any trailing literal indices. Independent of the rewriter
// state — `Clone()` is used to fork the path when extending it with composite-extract
// indices in the post-load fallback.
internal sealed class FlatAccessPath
{
    public SlotExpression Slot { get; set; } = new();
    public List<int> ExtraIndices { get; set; } = new();

    public FlatAccessPath Clone()
    {
        return new FlatAccessPath
        {
            Slot = new SlotExpression
            {
                ConstantRegisterOffset = Slot.ConstantRegisterOffset,
                DynamicIndexId = Slot.DynamicIndexId,
                DynamicIndexStride = Slot.DynamicIndexStride,
            },
            ExtraIndices = ExtraIndices.ToList(),
        };
    }
}
