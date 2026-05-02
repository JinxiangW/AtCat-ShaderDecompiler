using Ruri.ShaderTools.Spirv.Rewriter.Models;

namespace Ruri.ShaderTools.Spirv.Rewriter.Helpers;

// Decomposes an OpAccessChain index expression into the canonical form
//   `dynamicIndexId * dynamicIndexStride + constantRegisterOffset`.
//
// DXC routinely emits indices like `(instanceId << 4) | constantOffset`, `(i * 256) + 5`,
// or chains of those. Recognising the strided-array shape independently of the SPIR-V form
// is what lets the rewriter map dynamic accesses (e.g. AdditionalLights[i]) onto member-
// level access chains. Left/right associativity is handled, and OpBitwiseOr is treated as
// OpIAdd when the right-hand constant fits below the implied alignment (DXC emits OR for
// bit-disjoint adds).
//
// Falls back to "treat the whole expression as opaque dynamic id, stride=1, offset=0" when
// the structure isn't one we recognise — this is required so dynamic+dynamic / dynamic*
// dynamic patterns don't hard-fail validation; downstream layout matching just won't
// find a member for them and they'll go through the composite-extract fallback path.
internal static class LinearIndexDecomposer
{
    private const ushort OpIAdd = 128;
    private const ushort OpISub = 130;
    private const ushort OpIMul = 132;
    private const ushort OpShiftLeftLogical = 196;
    private const ushort OpBitwiseOr = 197;

    public static bool TryParseSlotExpression(uint operandId, ConstantMaps constants, out SlotExpression expression)
    {
        expression = null!;
        if (constants.IdToValue.TryGetValue(operandId, out uint constantValue))
        {
            // Constants whose magnitude doesn't fit in int (>= 2^31) cannot be a literal
            // register offset for any practical SPIR-V CB shape — a non-array uniform's
            // register count is at most a few thousand. Treat the slot as opaque-dynamic
            // so the rest of the pipeline can fall through to its composite-extract path
            // instead of throwing OverflowException out of the rewriter and losing the
            // entire shader.
            if (constantValue > int.MaxValue)
            {
                expression = new SlotExpression { DynamicIndexId = operandId, DynamicIndexStride = 1 };
                return true;
            }

            expression = new SlotExpression { ConstantRegisterOffset = (int)constantValue };
            return true;
        }

        // The decompose helper does its own checked arithmetic in a few places; on overflow
        // we again treat the whole expression as opaque-dynamic instead of bubbling out.
        // The concrete shape is recoverable later via composite-extract fallback even when
        // the register algebra doesn't fit our canonical `idx*stride+offset` form.
        try
        {
            if (TryDecompose(constants.Definitions, constants.IdToValue, operandId,
                    out uint dynamicIndexId, out int dynamicStride, out int constantOffset))
            {
                expression = new SlotExpression
                {
                    DynamicIndexId = dynamicIndexId,
                    DynamicIndexStride = dynamicStride,
                    ConstantRegisterOffset = constantOffset,
                };
                return true;
            }
        }
        catch (OverflowException)
        {
            expression = new SlotExpression { DynamicIndexId = operandId, DynamicIndexStride = 1 };
            return true;
        }

        return false;
    }

    private static bool TryDecompose(
        Dictionary<uint, SpirvInstruction> definitions,
        Dictionary<uint, uint> constants,
        uint valueId,
        out uint dynamicIndexId,
        out int dynamicStride,
        out int constantOffset)
    {
        dynamicIndexId = 0;
        dynamicStride = 0;
        constantOffset = 0;

        if (!definitions.TryGetValue(valueId, out SpirvInstruction? definition))
        {
            return false;
        }

        if ((definition.OpCode == OpIAdd || definition.OpCode == OpISub || definition.OpCode == OpBitwiseOr)
            && definition.Words.Length >= 5)
        {
            uint left = definition[3];
            uint right = definition[4];

            // OpBitwiseOr behaves identically to OpIAdd when the right-hand constant is
            // smaller than the alignment of the left-hand expression — DXC frequently emits
            // `(i << k) | c` (where c < 2^k) for `i * stride + c` because the bits don't
            // overlap. Required to recognise Unity instancing access patterns of the form
            // `cb[(instanceId << 4) | n]`.
            if (constants.TryGetValue(right, out uint rightConst)
                && TryDecompose(definitions, constants, left, out dynamicIndexId, out dynamicStride, out constantOffset))
            {
                constantOffset += definition.OpCode == OpISub ? -checked((int)rightConst) : checked((int)rightConst);
                return true;
            }

            if ((definition.OpCode == OpIAdd || definition.OpCode == OpBitwiseOr)
                && constants.TryGetValue(left, out uint leftConst)
                && TryDecompose(definitions, constants, right, out dynamicIndexId, out dynamicStride, out constantOffset))
            {
                constantOffset += checked((int)leftConst);
                return true;
            }

            // dynamic+dynamic / dynamic-dynamic: fall through to the opaque-id default. The
            // older code returned false here, but downstream the access chain then hit the
            // generic fallback (`stride=1, offset=0, dynamicId=valueId`) anyway — making
            // this path outright fail meant the WHOLE CB rewrite was rejected over a single
            // unrecognised access pattern.
        }

        if ((definition.OpCode == OpIMul || definition.OpCode == OpShiftLeftLogical) && definition.Words.Length >= 5)
        {
            uint left = definition[3];
            uint right = definition[4];

            if (constants.TryGetValue(right, out uint rightConst))
            {
                dynamicIndexId = left;
                dynamicStride = definition.OpCode == OpShiftLeftLogical
                    ? 1 << checked((int)rightConst)
                    : checked((int)rightConst);
                constantOffset = 0;
                return true;
            }

            if (definition.OpCode == OpIMul && constants.TryGetValue(left, out uint leftConst))
            {
                dynamicIndexId = right;
                dynamicStride = checked((int)leftConst);
                constantOffset = 0;
                return true;
            }

            // dynamic*dynamic: opaque-id default, same rationale.
        }

        // Default: the whole valueId is an opaque dynamic index, stride 1 element.
        dynamicIndexId = valueId;
        dynamicStride = 1;
        constantOffset = 0;
        return true;
    }
}
