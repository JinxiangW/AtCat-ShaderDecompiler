using Ruri.ShaderTools.Spirv.Rewriter.Models;

namespace Ruri.ShaderTools.Spirv.Rewriter.Helpers;

// Translates StructuredMemberLayout (logical) → SPIR-V type id (concrete). Recurses through
// nested struct members, deferring to TypeFactory for primitive scalar/vector/matrix/array
// type creation. Mutates the module in place — emits OpTypeStruct + OpMemberDecorate for
// every nested struct that isn't already in the module.
//
// `ScalarTypeId` and `ColumnVectorTypeId` are pre-resolved for vec/matrix members so the
// access translator's per-component branch can produce a correct ptr-scalar / ptr-vec
// result type. Without this pre-resolution, sub-component accesses inherit the parent
// vec/matrix type and spirv-cross trips on "Cannot subdivide a scalar value".
internal static class MemberTypeResolver
{
    public static uint ResolveMemberTypeId(SpirvModule module, TypeInfo types, StructuredMemberLayout member)
    {
        MemberLogicalType logicalType = member.LogicalType;
        uint baseTypeId = logicalType.Kind switch
        {
            LogicalTypeKind.Scalar => TypeFactory.EnsureScalarType(module, types, logicalType.ScalarKind),
            LogicalTypeKind.Vector => TypeFactory.EnsureVectorType(module, types, logicalType.ScalarKind, logicalType.Rows),
            LogicalTypeKind.Matrix => TypeFactory.EnsureMatrixType(module, types, logicalType.Rows, logicalType.Columns),
            LogicalTypeKind.Struct => EnsureStructType(module, types, member),
            _ => 0,
        };

        if (baseTypeId == 0)
        {
            return 0;
        }

        if (logicalType.ArrayLength > 1)
        {
            // HLSL cbuffer rule: every array element starts on a 16-byte boundary regardless
            // of element size. So `float arr[8]` lands as 8 vec4 slots (128 bytes), with
            // arr[i] occupying only `.x` of each slot. Matrices use a column-vec4 stride;
            // structs use their own (caller-rounded) byte size.
            //
            // We can only emit this rewrite for cbuffer-bound members, so a smaller "tight"
            // stride (4 / 8 / 12 bytes) was always wrong — spirv-cross HLSL backend would
            // reject it with "cbuffer ... cannot be expressed with either HLSL packing
            // layout or packoffset".
            int stride = logicalType.Kind switch
            {
                LogicalTypeKind.Struct => logicalType.StructByteSize,
                LogicalTypeKind.Matrix => logicalType.Columns * 16,
                _ => 16, // scalar / vec2 / vec3 / vec4 arrays — all 16-byte stride in cbuffer.
            };
            return TypeFactory.EnsureArrayType(module, types, baseTypeId, logicalType.ArrayLength, Math.Max(stride, 16));
        }

        return baseTypeId;
    }

    private static uint EnsureStructType(SpirvModule module, TypeInfo types, StructuredMemberLayout member)
    {
        if (member.ResolvedTypeId != 0)
        {
            return member.ResolvedTypeId;
        }

        List<StructuredMemberLayout>? children = member.LogicalType.StructMembers;
        if (children == null || children.Count == 0)
        {
            return 0;
        }

        var childTypeIds = new List<uint>(children.Count);
        foreach (StructuredMemberLayout child in children)
        {
            uint childTypeId = ResolveMemberTypeId(module, types, child);
            if (childTypeId == 0)
            {
                return 0;
            }

            child.ResolvedTypeId = childTypeId;

            // Mirror the top-level pre-resolution of scalar/column-vector/array-element ids
            // for vec/matrix/array children. Without this, a matrix nested inside a struct
            // array (e.g. UnityPerDrawArray.unity_ObjectToWorld) hits the matrix branch with
            // ColumnVectorTypeId == 0 and bails out — which is what was causing
            // struct-of-struct-array cbuffers like UnityInstancing_SRP_UnityPerDraw to fail
            // rewrite. Same idea for ArrayElementTypeId on nested array fields.
            uint childScalarId = child.LogicalType.Kind == LogicalTypeKind.Scalar
                || child.LogicalType.Kind == LogicalTypeKind.Vector
                || child.LogicalType.Kind == LogicalTypeKind.Matrix
                    ? TypeFactory.EnsureScalarType(module, types, child.LogicalType.ScalarKind)
                    : 0;
            child.ScalarTypeId = childScalarId;
            if (child.LogicalType.Kind == LogicalTypeKind.Matrix)
            {
                child.ColumnVectorTypeId = TypeFactory.EnsureVectorType(module, types, child.LogicalType.ScalarKind, child.LogicalType.Rows);
            }
            if (child.LogicalType.ArrayLength > 1)
            {
                child.ArrayElementTypeId = child.LogicalType.Kind switch
                {
                    LogicalTypeKind.Scalar => childScalarId,
                    LogicalTypeKind.Vector => TypeFactory.EnsureVectorType(module, types, child.LogicalType.ScalarKind, child.LogicalType.Rows),
                    LogicalTypeKind.Matrix => TypeFactory.EnsureMatrixType(module, types, child.LogicalType.Rows, child.LogicalType.Columns),
                    _ => 0,
                };
            }
            childTypeIds.Add(childTypeId);
        }

        uint structTypeId = module.AllocateId();
        int decorationInsertIndex = TypeFactory.ResolveDecorationInsertIndex(module);

        var decorations = new List<SpirvInstruction>();
        for (int childIndex = 0; childIndex < children.Count; childIndex++)
        {
            StructuredMemberLayout child = children[childIndex];
            decorations.Add(new SpirvInstruction
            {
                OpCode = SpvOpCode.OpMemberDecorate,
                Words =
                [
                    SpvOpCode.MakeInstructionWord(SpvOpCode.OpMemberDecorate, 5),
                    structTypeId,
                    (uint)childIndex,
                    SpvOpCode.DecorationOffset,
                    (uint)child.ByteOffset,
                ],
            });

            if (child.LogicalType.Kind == LogicalTypeKind.Matrix)
            {
                decorations.Add(new SpirvInstruction
                {
                    OpCode = SpvOpCode.OpMemberDecorate,
                    Words =
                    [
                        SpvOpCode.MakeInstructionWord(SpvOpCode.OpMemberDecorate, 4),
                        structTypeId,
                        (uint)childIndex,
                        SpvOpCode.DecorationRowMajor,
                    ],
                });
                decorations.Add(new SpirvInstruction
                {
                    OpCode = SpvOpCode.OpMemberDecorate,
                    Words =
                    [
                        SpvOpCode.MakeInstructionWord(SpvOpCode.OpMemberDecorate, 5),
                        structTypeId,
                        (uint)childIndex,
                        SpvOpCode.DecorationMatrixStride,
                        16,
                    ],
                });
            }
        }

        module.Instructions.InsertRange(decorationInsertIndex, decorations);
        module.Instructions.Insert(module.FindTypeSectionEndIndex(), new SpirvInstruction
        {
            OpCode = SpvOpCode.OpTypeStruct,
            Words = new[]
            {
                SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypeStruct, (ushort)(2 + childTypeIds.Count)),
                structTypeId,
            }.Concat(childTypeIds).ToArray(),
        });

        if (!string.IsNullOrWhiteSpace(member.Name))
        {
            module.InsertDebugName(structTypeId, member.Name);
        }

        for (int childIndex = 0; childIndex < children.Count; childIndex++)
        {
            string name = children[childIndex].Name;
            if (!string.IsNullOrWhiteSpace(name))
            {
                module.InsertDebugMemberName(structTypeId, (uint)childIndex, name);
            }
        }

        member.ResolvedTypeId = structTypeId;
        return structTypeId;
    }
}
