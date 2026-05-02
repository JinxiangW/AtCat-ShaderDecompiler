using Ruri.ShaderTools.Spirv.Rewriter.Helpers;
using Ruri.ShaderTools.Spirv.Rewriter.Models;

namespace Ruri.ShaderTools.Spirv.Rewriter.Passes;

// Pass 040: resolve each member's SPIR-V type id (creating new types as needed), and
// synthesise the OpConstant integers the access translator will eventually plug into
// rewritten OpAccessChain index slots.
//
// For every plan we:
//   1. Walk its layout members through `MemberTypeResolver.ResolveMemberTypeId`. That
//      ensures the matching scalar / vector / matrix / array / nested-struct types exist
//      in the module, and emits any missing OpType* + OpDecorate instructions. Each member
//      stores back its `ResolvedTypeId` plus, for vec/matrix, a `ScalarTypeId` /
//      `ColumnVectorTypeId` so the per-component access path can produce a correct
//      ptr-scalar / ptr-vec result type.
//   2. Call `EnsureTranslationConstants` to make sure every integer the access translator
//      could request as an index (member index, register, component, array element) has a
//      uint OpConstant in the module — the translator only looks them up by value, so
//      missing constants would fail validation later for no real reason.
//
// Plans whose member-type resolution fails are dropped with a summary line and stay out of
// the rest of the pipeline.
internal static class Pass040_PrepareNewTypes
{
    public static void DoPass(RewriterPipelineState state)
    {
        var survived = new List<BufferRewritePlan>(state.Plans.Count);
        foreach (BufferRewritePlan plan in state.Plans)
        {
            if (!ResolveMemberTypes(state, plan))
            {
                state.Summary.Add($"[{plan.Info.Metadata.Name}] member type resolution failed");
                continue;
            }

            EnsureTranslationConstants(state.Module, state.Types, state.Constants, plan.Layout);
            survived.Add(plan);
        }
        state.Plans = survived;
    }

    private static bool ResolveMemberTypes(RewriterPipelineState state, BufferRewritePlan plan)
    {
        var memberTypeIds = new List<uint>(plan.Layout.Members.Count);
        foreach (StructuredMemberLayout member in plan.Layout.Members)
        {
            uint memberTypeId = MemberTypeResolver.ResolveMemberTypeId(state.Module, state.Types, member);
            if (memberTypeId == 0)
            {
                return false;
            }

            member.ResolvedTypeId = memberTypeId;
            // Pre-resolve scalar / column-vector / array-element types so the access
            // translator can produce ptr-scalar / ptr-vec / ptr-element results without
            // re-creating types.
            //
            // ScalarTypeId is the deepest scalar (component-level): identical to the member
            // type when the member itself is scalar, or the vec/matrix component scalar
            // otherwise. Note the array case: the member type may be `arr_float`, but the
            // component scalar is `float`, NOT the array — using `memberTypeId` for a scalar
            // *array* would inherit the array type and break per-element access translation
            // downstream.
            uint scalarKindId = member.LogicalType.Kind == LogicalTypeKind.Scalar
                || member.LogicalType.Kind == LogicalTypeKind.Vector
                || member.LogicalType.Kind == LogicalTypeKind.Matrix
                    ? TypeFactory.EnsureScalarType(state.Module, state.Types, member.LogicalType.ScalarKind)
                    : 0;
            member.ScalarTypeId = scalarKindId;
            if (member.LogicalType.Kind == LogicalTypeKind.Matrix)
            {
                member.ColumnVectorTypeId = TypeFactory.EnsureVectorType(state.Module, state.Types, member.LogicalType.ScalarKind, member.LogicalType.Rows);
            }

            // ArrayElementTypeId — the type after walking one array index step. Set only for
            // array members; for non-array members the existing single-step access path uses
            // ResolvedTypeId / ScalarTypeId / ColumnVectorTypeId as before.
            if (member.LogicalType.ArrayLength > 1)
            {
                member.ArrayElementTypeId = member.LogicalType.Kind switch
                {
                    LogicalTypeKind.Scalar => scalarKindId,
                    LogicalTypeKind.Vector => TypeFactory.EnsureVectorType(state.Module, state.Types, member.LogicalType.ScalarKind, member.LogicalType.Rows),
                    LogicalTypeKind.Matrix => TypeFactory.EnsureMatrixType(state.Module, state.Types, member.LogicalType.Rows, member.LogicalType.Columns),
                    _ => 0,
                };
            }

            memberTypeIds.Add(memberTypeId);
        }

        plan.MemberTypeIds = memberTypeIds;
        return true;
    }

    private static void EnsureTranslationConstants(SpirvModule module, TypeInfo types, ConstantMaps constants, StructuredBufferLayout layout)
    {
        int maxConstantValue = layout.Members.Count + 8;
        foreach (StructuredMemberLayout member in layout.Members)
        {
            AccumulateTranslationConstantRange(member, ref maxConstantValue);
        }

        for (uint value = 0; value <= maxConstantValue; value++)
        {
            uint constantId = TypeFactory.FindOrCreateUIntConstant(module, types, value);
            constants.IdToValue[constantId] = value;
            constants.ValueToId[value] = constantId;
        }
    }

    private static void AccumulateTranslationConstantRange(StructuredMemberLayout member, ref int maxConstantValue)
    {
        maxConstantValue = Math.Max(maxConstantValue, member.RegisterCount);
        maxConstantValue = Math.Max(maxConstantValue, member.LogicalType.Rows);
        maxConstantValue = Math.Max(maxConstantValue, member.LogicalType.Columns);
        maxConstantValue = Math.Max(maxConstantValue, member.LogicalType.ArrayLength);
        maxConstantValue = Math.Max(maxConstantValue, member.LogicalType.SecondaryArrayLength);

        if (member.LogicalType.StructMembers != null)
        {
            maxConstantValue = Math.Max(maxConstantValue, member.LogicalType.StructMembers.Count);
            foreach (StructuredMemberLayout child in member.LogicalType.StructMembers)
            {
                AccumulateTranslationConstantRange(child, ref maxConstantValue);
            }
        }
    }
}
