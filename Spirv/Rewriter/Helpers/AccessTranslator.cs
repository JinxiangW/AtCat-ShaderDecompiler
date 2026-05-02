using Ruri.ShaderTools.Spirv.Rewriter.Models;

namespace Ruri.ShaderTools.Spirv.Rewriter.Helpers;

// Pure translation logic: given a flat-array access path and a structured layout, produce
// the structured access (member index + sub-indices + result type id) that addresses the
// same byte offset. Stateless — takes everything it needs as parameters.
//
// Three entry points reflect the three SPIR-V access flavours the rewriter handles:
//
//   1. TryParseFlatAccessChain — read indices off an OpAccessChain instruction, decompose
//      the slot via LinearIndexDecomposer.
//   2. TranslateFlatAccess — find the structured member at a static byte offset; falls
//      through to TranslateDynamicFlatAccess when the slot is dynamic.
//   3. TranslateDynamicFlatAccess — same idea for dynamic indices into array members.
//
// All three return null on no-match, never throw — failures bubble up to either the
// composite-extract fallback (Pass090) or the validation pass (Pass050) which rejects the
// whole CB rewrite if no path succeeds.
internal static class AccessTranslator
{
    public static bool TryParseFlatAccessChain(SpirvInstruction instruction, ConstantMaps constants, out FlatAccessPath accessPath)
    {
        accessPath = null!;
        if (instruction.Words.Length < 4)
        {
            return false;
        }

        // First operand after `base` is sometimes a dummy zero (the wrapper member index of
        // the SPIR-V flat struct `CB { _arr_v4float _m0 }`). Detect a leading slot of all-
        // zeros and skip past it so we read the real register slot.
        int slotOperandIndex = 4;
        if (instruction.Words.Length >= 6
            && LinearIndexDecomposer.TryParseSlotExpression(instruction[4], constants, out SlotExpression leadingIndex)
            && leadingIndex.DynamicIndexId == 0
            && leadingIndex.DynamicIndexStride == 0
            && leadingIndex.ConstantRegisterOffset == 0)
        {
            slotOperandIndex = 5;
        }

        if (!LinearIndexDecomposer.TryParseSlotExpression(instruction[slotOperandIndex], constants, out SlotExpression slotExpression))
        {
            return false;
        }

        var extraIndices = new List<int>();
        for (int operandIndex = slotOperandIndex + 1; operandIndex < instruction.Words.Length; operandIndex++)
        {
            if (!constants.IdToValue.TryGetValue(instruction[operandIndex], out uint value))
            {
                return false;
            }

            extraIndices.Add(checked((int)value));
        }

        accessPath = new FlatAccessPath
        {
            Slot = slotExpression,
            ExtraIndices = extraIndices,
        };
        return true;
    }

    public static StructuredAccessTranslation? TranslateFlatAccess(StructuredBufferLayout layout, FlatAccessPath accessPath, ConstantMaps constants)
    {
        int componentIndex = accessPath.ExtraIndices.Count > 0 ? accessPath.ExtraIndices[0] : 0;
        if (accessPath.Slot.DynamicIndexId != 0)
        {
            return TranslateDynamicFlatAccess(layout, accessPath, constants, componentIndex);
        }

        int absoluteRegister = accessPath.Slot.ConstantRegisterOffset;
        int absoluteByteOffset = (absoluteRegister * 16) + (componentIndex * 4);
        for (int memberIndex = 0; memberIndex < layout.Members.Count; memberIndex++)
        {
            StructuredMemberLayout member = layout.Members[memberIndex];
            if (absoluteRegister < member.RegisterOffset || absoluteRegister >= member.RegisterOffset + member.RegisterCount)
            {
                continue;
            }

            if (!IsMemberByteMatch(member, absoluteByteOffset, accessPath.ExtraIndices))
            {
                continue;
            }

            StructuredAccessTranslation? logical = TranslateMemberAccess(member, absoluteRegister, componentIndex, accessPath.ExtraIndices, constants);
            if (logical == null || !TryGetConstantId(constants, (uint)memberIndex, out uint memberIndexConstantId))
            {
                continue;
            }

            var indices = new List<uint> { memberIndexConstantId };
            indices.AddRange(logical.Indices);

            return new StructuredAccessTranslation
            {
                Indices = indices,
                MemberTypeId = logical.MemberTypeId,
            };
        }

        return null;
    }

    public static StructuredAccessTranslation? CreateTranslation(ConstantMaps constants, uint memberTypeId, params int[] indices)
    {
        var translatedIndices = new List<uint>(indices.Length);
        foreach (int index in indices)
        {
            if (!TryGetConstantId(constants, (uint)index, out uint constantId))
            {
                return null;
            }

            translatedIndices.Add(constantId);
        }

        return new StructuredAccessTranslation
        {
            Indices = translatedIndices,
            MemberTypeId = memberTypeId,
        };
    }

    public static bool TryGetConstantId(ConstantMaps constants, uint value, out uint constantId)
    {
        return constants.ValueToId.TryGetValue(value, out constantId);
    }

    private static bool IsMemberByteMatch(StructuredMemberLayout member, int absoluteByteOffset, List<int> extraIndices)
    {
        int memberStart = member.ByteOffset;
        int memberEnd = memberStart + Math.Max(member.LogicalType.DeclaredByteSize, 4);
        if (member.LogicalType.Kind == LogicalTypeKind.Struct)
        {
            memberEnd = memberStart + (member.LogicalType.StructByteSize * Math.Max(member.LogicalType.ArrayLength, 1));
            return absoluteByteOffset >= memberStart && absoluteByteOffset < memberEnd;
        }

        if (member.LogicalType.Kind == LogicalTypeKind.Matrix)
        {
            return absoluteByteOffset >= memberStart && absoluteByteOffset < memberEnd;
        }

        if (member.LogicalType.ArrayLength > 1)
        {
            // Flat DXBC constant buffers commonly address 16-byte aligned arrays by register
            // slot without emitting an extra component index. Accept any byte inside the
            // declared span and let TranslateMemberAccess map the register delta back to the
            // structured array index.
            return absoluteByteOffset >= memberStart && absoluteByteOffset < memberEnd;
        }

        return extraIndices.Count == 0
            ? absoluteByteOffset == memberStart
            : absoluteByteOffset >= memberStart && absoluteByteOffset < memberEnd;
    }

    private static StructuredAccessTranslation? TranslateMemberAccess(
        StructuredMemberLayout member,
        int absoluteRegister,
        int componentIndex,
        List<int> extraIndices,
        ConstantMaps constants)
    {
        int localRegister = absoluteRegister - member.RegisterOffset;
        int memberComponentOffset = (member.ByteOffset % 16) / 4;
        List<int> trailingIndices = extraIndices.Count > 1 ? extraIndices.Skip(1).ToList() : [];

        if (member.LogicalType.Kind == LogicalTypeKind.Struct)
        {
            return TranslateStructMemberAccess(member, absoluteRegister, componentIndex, extraIndices, constants);
        }

        if (member.LogicalType.Kind == LogicalTypeKind.Matrix)
        {
            if (localRegister < 0 || localRegister >= member.LogicalType.Columns)
            {
                return null;
            }

            if (extraIndices.Count == 0)
            {
                // Column access: matrix[col] yields a vec(rowCount), not a matrix.
                return member.ColumnVectorTypeId != 0
                    ? CreateTranslation(constants, member.ColumnVectorTypeId, localRegister)
                    : null;
            }

            if (componentIndex < 0 || componentIndex >= member.LogicalType.Rows
                || trailingIndices.Count > 0 || member.ScalarTypeId == 0)
            {
                return null;
            }

            // Component access: matrix[col][component] yields a scalar.
            return CreateTranslation(constants, member.ScalarTypeId, localRegister, componentIndex);
        }

        if (member.RegisterCount == 1)
        {
            if (member.LogicalType.Kind == LogicalTypeKind.Scalar)
            {
                return componentIndex == memberComponentOffset && trailingIndices.Count == 0
                    ? CreateTranslation(constants, member.ResolvedTypeId)
                    : null;
            }

            if (member.LogicalType.Kind == LogicalTypeKind.Vector)
            {
                if (trailingIndices.Count > 0)
                {
                    return null;
                }

                // Bare access (no per-component extra index): return the whole vector ptr.
                // Adding a component index here was wrong — it would dive into the vector
                // type but keep the parent vec4 result type, producing an invalid
                // `ptr-vec4 [..., component]` access chain that spirv-cross rejects with
                // "Cannot subdivide a scalar value".
                if (extraIndices.Count == 0)
                {
                    return componentIndex == memberComponentOffset
                        ? CreateTranslation(constants, member.ResolvedTypeId)
                        : null;
                }

                int relativeComponentIndex = componentIndex - memberComponentOffset;
                if (relativeComponentIndex < 0 || relativeComponentIndex >= member.LogicalType.Rows
                    || member.ScalarTypeId == 0)
                {
                    return null;
                }

                // Per-component access: dive one level deeper, ptr-scalar result.
                return CreateTranslation(constants, member.ScalarTypeId, relativeComponentIndex);
            }
        }

        if (localRegister < 0 || localRegister >= member.RegisterCount || trailingIndices.Count > 0)
        {
            return null;
        }

        // Multi-register fall-through: arrays of scalar / vector / matrix. The result type
        // depends on how deep the chain walks, NOT on the member's full type id (which
        // includes the array wrapper). Using `ResolvedTypeId` here produces a chain like
        // `ptr-arrayOfV4 → arrIdx → component` whose result type still claims the whole
        // array — spirv-cross then tries to subdivide a scalar and dies.
        if (member.LogicalType.ArrayLength > 1 && member.ArrayElementTypeId != 0)
        {
            // 1 array index → element type (scalar / vec / matrix), no further indices.
            if (extraIndices.Count == 0)
            {
                return CreateTranslation(constants, member.ArrayElementTypeId, localRegister);
            }

            // Per-component access into the element. Only meaningful when the element is a
            // vector — array of scalars cannot be subdivided further, array of matrices
            // would need an additional column index and is not on the hot path.
            if (member.LogicalType.Kind == LogicalTypeKind.Vector && member.ScalarTypeId != 0)
            {
                int relativeComponentIndex = componentIndex - memberComponentOffset;
                if (relativeComponentIndex < 0 || relativeComponentIndex >= member.LogicalType.Rows)
                {
                    return null;
                }

                return CreateTranslation(constants, member.ScalarTypeId, localRegister, relativeComponentIndex);
            }

            // Scalar array with trailing component (or matrix array): can't translate here.
            return null;
        }

        return extraIndices.Count > 0
            ? CreateTranslation(constants, member.ResolvedTypeId, localRegister, componentIndex)
            : CreateTranslation(constants, member.ResolvedTypeId, localRegister);
    }

    private static StructuredAccessTranslation? TranslateStructMemberAccess(
        StructuredMemberLayout member,
        int absoluteRegister,
        int componentIndex,
        List<int> extraIndices,
        ConstantMaps constants)
    {
        int localByteOffset = (absoluteRegister * 16) + (componentIndex * 4) - member.ByteOffset;
        int structArrayLength = Math.Max(member.LogicalType.ArrayLength, 1);
        int structByteSize = Math.Max(member.LogicalType.StructByteSize, 1);
        int structElementIndex = localByteOffset / structByteSize;
        int elementLocalByteOffset = localByteOffset % structByteSize;
        if (structElementIndex < 0 || structElementIndex >= structArrayLength)
        {
            return null;
        }

        List<StructuredMemberLayout>? children = member.LogicalType.StructMembers;
        if (children == null)
        {
            return null;
        }

        for (int childIndex = 0; childIndex < children.Count; childIndex++)
        {
            StructuredMemberLayout child = children[childIndex];
            if (elementLocalByteOffset < child.ByteOffset
                || elementLocalByteOffset >= child.ByteOffset + LayoutBuilder.GetMemberSpanBytes(child))
            {
                continue;
            }

            if (!IsMemberByteMatch(child, elementLocalByteOffset, extraIndices))
            {
                continue;
            }

            StructuredAccessTranslation? childTranslation = TranslateMemberAccess(
                child,
                member.RegisterOffset + ((absoluteRegister - member.RegisterOffset) - (structElementIndex * (structByteSize / 16))),
                componentIndex,
                extraIndices,
                constants);

            if (childTranslation == null || !TryGetConstantId(constants, (uint)childIndex, out uint childIndexConstantId))
            {
                continue;
            }

            var translatedIndices = new List<uint>();
            if (structArrayLength > 1)
            {
                if (!TryGetConstantId(constants, (uint)structElementIndex, out uint structElementConstantId))
                {
                    continue;
                }

                translatedIndices.Add(structElementConstantId);
            }

            translatedIndices.Add(childIndexConstantId);
            translatedIndices.AddRange(childTranslation.Indices);

            return new StructuredAccessTranslation
            {
                Indices = translatedIndices,
                MemberTypeId = childTranslation.MemberTypeId,
            };
        }

        return null;
    }

    private static StructuredAccessTranslation? TranslateDynamicFlatAccess(
        StructuredBufferLayout layout,
        FlatAccessPath accessPath,
        ConstantMaps constants,
        int componentIndex)
    {
        if (accessPath.Slot.DynamicIndexId == 0 || accessPath.Slot.DynamicIndexStride <= 0)
        {
            return null;
        }

        for (int memberIndex = 0; memberIndex < layout.Members.Count; memberIndex++)
        {
            StructuredMemberLayout member = layout.Members[memberIndex];
            if (member.LogicalType.Kind == LogicalTypeKind.Struct || Math.Max(member.LogicalType.ArrayLength, 1) <= 1)
            {
                continue;
            }

            int elementRegisterStride = GetDynamicElementRegisterStride(member);
            if (elementRegisterStride != accessPath.Slot.DynamicIndexStride)
            {
                continue;
            }

            int localRegisterOffset = accessPath.Slot.ConstantRegisterOffset - member.RegisterOffset;
            if (localRegisterOffset < 0 || localRegisterOffset >= elementRegisterStride)
            {
                continue;
            }

            if (!TryGetConstantId(constants, (uint)memberIndex, out uint memberIndexConstantId))
            {
                continue;
            }

            StructuredAccessTranslation? memberTranslation = TranslateDynamicArrayMemberAccess(
                member, localRegisterOffset, componentIndex, accessPath.ExtraIndices, accessPath.Slot.DynamicIndexId, constants);
            if (memberTranslation == null)
            {
                continue;
            }

            var indices = new List<uint> { memberIndexConstantId };
            indices.AddRange(memberTranslation.Indices);
            return new StructuredAccessTranslation
            {
                Indices = indices,
                MemberTypeId = memberTranslation.MemberTypeId,
            };
        }

        // Second sweep: dynamic indexing into a struct-array member. UnityInstancing_SRP_
        // UnityPerDraw is the canonical case (UnityPerDrawArray[instanceId].fieldX).
        for (int memberIndex = 0; memberIndex < layout.Members.Count; memberIndex++)
        {
            StructuredMemberLayout member = layout.Members[memberIndex];
            if (member.LogicalType.Kind != LogicalTypeKind.Struct
                || Math.Max(member.LogicalType.ArrayLength, 1) <= 1)
            {
                continue;
            }

            int elementRegisterStride = Math.Max(1, (member.LogicalType.StructByteSize + 15) / 16);
            int localRegisterOffset = accessPath.Slot.ConstantRegisterOffset - member.RegisterOffset;
            if (localRegisterOffset < 0 || localRegisterOffset >= elementRegisterStride)
            {
                continue;
            }

            if (elementRegisterStride != accessPath.Slot.DynamicIndexStride)
            {
                continue;
            }

            if (accessPath.ExtraIndices.Count > 1)
            {
                return null;
            }

            List<StructuredMemberLayout>? children = member.LogicalType.StructMembers;
            if (children == null)
            {
                return null;
            }

            int localByteOffset = (localRegisterOffset * 16) + (componentIndex * 4);
            for (int childIndex = 0; childIndex < children.Count; childIndex++)
            {
                StructuredMemberLayout child = children[childIndex];
                if (localByteOffset < child.ByteOffset
                    || localByteOffset >= child.ByteOffset + LayoutBuilder.GetMemberSpanBytes(child))
                {
                    continue;
                }

                if (!TryGetConstantId(constants, (uint)childIndex, out uint childIndexConstantId))
                {
                    continue;
                }

                StructuredAccessTranslation? childTranslation = TranslateMemberAccess(
                    child, localRegisterOffset, componentIndex, accessPath.ExtraIndices, constants);
                if (childTranslation == null)
                {
                    continue;
                }

                if (!TryGetConstantId(constants, (uint)memberIndex, out uint memberIndexConstantId))
                {
                    continue;
                }

                var indices = new List<uint> { memberIndexConstantId, accessPath.Slot.DynamicIndexId, childIndexConstantId };
                indices.AddRange(childTranslation.Indices);
                return new StructuredAccessTranslation
                {
                    Indices = indices,
                    MemberTypeId = childTranslation.MemberTypeId,
                };
            }
        }

        return null;
    }

    private static int GetDynamicElementRegisterStride(StructuredMemberLayout member)
    {
        if (member.LogicalType.Kind == LogicalTypeKind.Matrix)
        {
            return Math.Max(1, member.LogicalType.Columns);
        }

        int elementByteSize = member.LogicalType.ArrayLength > 1
            ? Math.Max(4, member.LogicalType.DeclaredByteSize / Math.Max(member.LogicalType.ArrayLength, 1))
            : Math.Max(member.LogicalType.DeclaredByteSize, 4);
        return Math.Max(1, (elementByteSize + 15) / 16);
    }

    private static StructuredAccessTranslation? TranslateDynamicArrayMemberAccess(
        StructuredMemberLayout member,
        int localRegisterOffset,
        int componentIndex,
        List<int> extraIndices,
        uint dynamicIndexId,
        ConstantMaps constants)
    {
        if (extraIndices.Count > 1)
        {
            return null;
        }

        if (member.LogicalType.Kind == LogicalTypeKind.Matrix)
        {
            if (localRegisterOffset < 0 || localRegisterOffset >= member.LogicalType.Columns)
            {
                return null;
            }

            if (!TryGetConstantId(constants, (uint)localRegisterOffset, out uint registerConstantId))
            {
                return null;
            }

            if (extraIndices.Count == 0)
            {
                // Column access (no per-component extra): ptr-vec(rowCount) result.
                if (member.ColumnVectorTypeId == 0)
                {
                    return null;
                }

                return new StructuredAccessTranslation
                {
                    Indices = [dynamicIndexId, registerConstantId],
                    MemberTypeId = member.ColumnVectorTypeId,
                };
            }

            if (componentIndex < 0 || componentIndex >= member.LogicalType.Rows
                || member.ScalarTypeId == 0
                || !TryGetConstantId(constants, (uint)componentIndex, out uint componentConstantId))
            {
                return null;
            }

            // Component access: matrix[col][component] yields a scalar.
            return new StructuredAccessTranslation
            {
                Indices = [dynamicIndexId, registerConstantId, componentConstantId],
                MemberTypeId = member.ScalarTypeId,
            };
        }

        if (localRegisterOffset != 0)
        {
            return null;
        }

        // For dynamic-array members the result type after walking one array index is the
        // array's *element* type (vec4 / scalar), NOT the array's resolved type. The latter
        // is the wrapped array, and a `ptr-arrayOfX → arrIdx → component` chain inherits
        // the array claim and crashes spirv-cross with "Cannot subdivide a scalar value".
        if (member.LogicalType.Kind == LogicalTypeKind.Scalar)
        {
            int memberComponentOffset = (member.ByteOffset % 16) / 4;
            uint elementType = member.ArrayElementTypeId != 0 ? member.ArrayElementTypeId : member.ResolvedTypeId;
            return componentIndex == memberComponentOffset
                ? new StructuredAccessTranslation
                {
                    Indices = [dynamicIndexId],
                    MemberTypeId = elementType,
                }
                : null;
        }

        if (member.LogicalType.Kind == LogicalTypeKind.Vector)
        {
            int memberComponentOffset = (member.ByteOffset % 16) / 4;

            if (extraIndices.Count == 0)
            {
                // Bare access into a dynamic-array vec4 member: ptr-vec4 result, only the
                // array index. Adding a component index here would invalidate the access
                // chain (see static-vec4 fix in TranslateMemberAccess).
                uint elementType = member.ArrayElementTypeId != 0 ? member.ArrayElementTypeId : member.ResolvedTypeId;
                return componentIndex == memberComponentOffset
                    ? new StructuredAccessTranslation
                    {
                        Indices = [dynamicIndexId],
                        MemberTypeId = elementType,
                    }
                    : null;
            }

            int relativeComponentIndex = componentIndex - memberComponentOffset;
            if (relativeComponentIndex < 0 || relativeComponentIndex >= member.LogicalType.Rows
                || member.ScalarTypeId == 0
                || !TryGetConstantId(constants, (uint)relativeComponentIndex, out uint componentConstantId))
            {
                return null;
            }

            return new StructuredAccessTranslation
            {
                Indices = [dynamicIndexId, componentConstantId],
                MemberTypeId = member.ScalarTypeId,
            };
        }

        return null;
    }
}
