using Ruri.ShaderTools.Spirv.Rewriter.Models;

namespace Ruri.ShaderTools.Spirv.Rewriter.Helpers;

// Pure metadata → StructuredBufferLayout translator. No SPIR-V awareness — that comes later
// in TypeFactory. Reads NumericShaderParameter / StructParameter (set by the symbol pipeline)
// and produces a member list with byte offsets, register coverage, and logical types.
//
// Members beyond the SPIR-V flat array length are silently dropped — the metadata can list
// fields the shader never reads, and forcing them to fit would either require synthetic
// padding (wrong) or kill the rewrite (we'd lose the named recovery for everything).
internal static class LayoutBuilder
{
    public static StructuredBufferLayout? Build(FlatUniformBufferInfo flatBuffer)
    {
        ConstantBufferParameter constantBuffer = flatBuffer.ConstantBuffer;
        bool hasNumeric = constantBuffer.VectorParameters.Length > 0 || constantBuffer.MatrixParameters.Length > 0;
        if (!hasNumeric && constantBuffer.StructParameters.Length == 0)
        {
            return null;
        }

        var layout = new StructuredBufferLayout();
        var members = new List<StructuredMemberLayout>();
        int maxAvailableByteOffset = flatBuffer.ArrayLength * 16;

        foreach (NumericShaderParameter parameter in constantBuffer.AllNumericParameters.OrderBy(static p => p.Index))
        {
            StructuredMemberLayout? member = TryCreateScalarOrVectorMember(parameter, maxAvailableByteOffset);
            if (member != null)
            {
                members.Add(member);
            }
        }

        foreach (StructParameter structParameter in flatBuffer.ConstantBuffer.StructParameters.OrderBy(static p => p.Index))
        {
            StructuredMemberLayout? member = TryCreateStructMember(structParameter, maxAvailableByteOffset);
            if (member != null)
            {
                members.Add(member);
            }
        }

        members.Sort(static (left, right) => left.ByteOffset.CompareTo(right.ByteOffset));
        if (members.Count == 0)
        {
            return null;
        }

        int maxUsedByteOffset = 0;
        int maxReferencedByteOffset = 0;
        foreach (StructuredMemberLayout member in members)
        {
            layout.Members.Add(member);
            maxUsedByteOffset = Math.Max(maxUsedByteOffset, member.ByteOffset + GetMemberSpanBytes(member));
            maxReferencedByteOffset = Math.Max(maxReferencedByteOffset, GetReferencedByteEnd(member));
        }

        layout.RequiredRegisterCount = Math.Max(1, (maxUsedByteOffset + 15) / 16);
        layout.MaxUsedRegisterCount = Math.Max(1, (maxReferencedByteOffset + 15) / 16);

        // Tail-padding fill: SPIR-V's flat `_m0[N]` may extend past the last named member
        // (compiler tail-pad, dead-code reads of rounded-up registers, etc.). Without a
        // member covering those slots, any OpAccessChain into them fails Pass050 and we
        // lose the named recovery for the WHOLE CB. Emit a `_pad_tail` float4[k] spanning
        // the gap so every chain in the SPIR-V flat array has a structured target.
        if (layout.MaxUsedRegisterCount < flatBuffer.ArrayLength)
        {
            int tailStart = layout.MaxUsedRegisterCount;
            int tailRegisters = flatBuffer.ArrayLength - tailStart;
            var tailType = new MemberLogicalType
            {
                Kind = LogicalTypeKind.Vector,
                ScalarKind = ScalarKind.Float,
                Rows = 4,
                Columns = 1,
                ArrayLength = tailRegisters,
                DeclaredByteSize = tailRegisters * 16,
                IsMatrix = false,
            };
            layout.Members.Add(new StructuredMemberLayout
            {
                Name = "_pad_tail",
                ByteOffset = tailStart * 16,
                LogicalType = tailType,
                RegisterOffset = tailStart,
                RegisterCount = tailRegisters,
            });
            layout.RequiredRegisterCount = flatBuffer.ArrayLength;
        }

        return layout;
    }

    public static int GetMemberSpanBytes(StructuredMemberLayout member)
    {
        return member.LogicalType.Kind == LogicalTypeKind.Struct
            ? member.LogicalType.StructByteSize * Math.Max(member.LogicalType.ArrayLength, 1)
            : member.LogicalType.Kind == LogicalTypeKind.Matrix
            ? member.LogicalType.Columns * 16 * Math.Max(member.LogicalType.ArrayLength, 1)
            : member.LogicalType.DeclaredByteSize;
    }

    public static int GetRequiredRegisterCount(int byteOffset, MemberLogicalType type)
    {
        if (type.Kind == LogicalTypeKind.Matrix)
        {
            return Math.Max(1, type.Columns * Math.Max(type.ArrayLength, 1));
        }

        int startRegister = byteOffset / 16;
        int endByteOffset = byteOffset + Math.Max(type.DeclaredByteSize, 4);
        int endRegister = Math.Max(startRegister + 1, (endByteOffset + 15) / 16);
        return endRegister - startRegister;
    }

    private static int GetReferencedByteEnd(StructuredMemberLayout member)
    {
        if (IsPaddingMember(member))
        {
            return 0;
        }

        if (member.LogicalType.Kind != LogicalTypeKind.Struct
            || member.LogicalType.StructMembers == null
            || member.LogicalType.StructMembers.Count == 0)
        {
            return member.ByteOffset + GetMemberSpanBytes(member);
        }

        int childEnd = 0;
        foreach (StructuredMemberLayout child in member.LogicalType.StructMembers)
        {
            childEnd = Math.Max(childEnd, GetReferencedByteEnd(child));
        }

        return member.ByteOffset + childEnd;
    }

    private static bool IsPaddingMember(StructuredMemberLayout member)
    {
        return !string.IsNullOrWhiteSpace(member.Name)
            && member.Name.StartsWith("_pad", StringComparison.Ordinal);
    }

    private static StructuredMemberLayout? TryCreateScalarOrVectorMember(NumericShaderParameter parameter, int maxAvailableByteOffset)
    {
        if (parameter.Index < 0 || parameter.Index >= maxAvailableByteOffset)
        {
            return null;
        }

        MemberLogicalType? logicalType = TryCreateLogicalTypeFromMetadata(parameter);
        if (logicalType == null)
        {
            return null;
        }

        return new StructuredMemberLayout
        {
            Name = parameter.Name ?? string.Empty,
            ByteOffset = parameter.Index,
            Metadata = parameter,
            LogicalType = logicalType,
            RegisterOffset = parameter.Index / 16,
            RegisterCount = GetRequiredRegisterCount(parameter.Index, logicalType),
        };
    }

    private static StructuredMemberLayout? TryCreateStructMember(StructParameter structParameter, int maxAvailableByteOffset)
    {
        bool hasMembers = structParameter.VectorMembers.Length > 0 || structParameter.MatrixMembers.Length > 0;
        if (structParameter.Index < 0 || structParameter.Index >= maxAvailableByteOffset || !hasMembers)
        {
            return null;
        }

        var childMembers = new List<StructuredMemberLayout>();
        int structEnd = Math.Min(maxAvailableByteOffset, structParameter.Index + Math.Max(structParameter.StructSize, 0));
        foreach (NumericShaderParameter child in structParameter.AllNumericMembers.OrderBy(static p => p.Index))
        {
            if (child.Index < structParameter.Index || child.Index >= structEnd)
            {
                continue;
            }

            MemberLogicalType? childType = TryCreateLogicalTypeFromMetadata(child);
            if (childType == null)
            {
                return null;
            }

            childMembers.Add(new StructuredMemberLayout
            {
                Name = child.Name ?? string.Empty,
                ByteOffset = child.Index - structParameter.Index,
                Metadata = child,
                LogicalType = childType,
                RegisterOffset = (child.Index - structParameter.Index) / 16,
                RegisterCount = GetRequiredRegisterCount(child.Index - structParameter.Index, childType),
            });
        }

        if (childMembers.Count == 0)
        {
            return null;
        }

        childMembers.Sort(static (left, right) => left.ByteOffset.CompareTo(right.ByteOffset));

        var logicalType = new MemberLogicalType
        {
            Kind = LogicalTypeKind.Struct,
            StructName = structParameter.Name,
            StructByteSize = childMembers.Max(static c => c.ByteOffset + GetMemberSpanBytes(c)),
            StructMembers = childMembers,
            ArrayLength = Math.Max(structParameter.ArraySize, 1),
            DeclaredByteSize = Math.Max(structParameter.ArraySize, 1) * childMembers.Max(static c => c.ByteOffset + GetMemberSpanBytes(c)),
        };

        return new StructuredMemberLayout
        {
            Name = structParameter.Name,
            ByteOffset = structParameter.Index,
            LogicalType = logicalType,
            RegisterOffset = structParameter.Index / 16,
            RegisterCount = Math.Max(1, ((logicalType.StructByteSize * Math.Max(logicalType.ArrayLength, 1)) + 15) / 16),
        };
    }

    private static MemberLogicalType? TryCreateLogicalTypeFromMetadata(NumericShaderParameter parameter)
    {
        if (parameter.RowCount <= 0 || parameter.ColumnCount <= 0)
        {
            return null;
        }

        ScalarKind? scalarKind = TryResolveScalarKind(parameter.Type);
        if (scalarKind == null)
        {
            return null;
        }

        return new MemberLogicalType
        {
            Kind = parameter.IsMatrix
                ? LogicalTypeKind.Matrix
                : parameter.RowCount == 1 ? LogicalTypeKind.Scalar : LogicalTypeKind.Vector,
            ScalarKind = scalarKind.Value,
            Rows = parameter.RowCount,
            Columns = parameter.ColumnCount,
            ArrayLength = Math.Max(parameter.ArraySize, 1),
            DeclaredByteSize = GetDeclaredByteSize(parameter),
            UscIndex = parameter.Index,
            IsMatrix = parameter.IsMatrix,
        };
    }

    private static ScalarKind? TryResolveScalarKind(ShaderParamType paramType) => paramType switch
    {
        ShaderParamType.Float => ScalarKind.Float,
        ShaderParamType.Int => ScalarKind.Int,
        ShaderParamType.Bool => ScalarKind.UInt,
        ShaderParamType.UInt => ScalarKind.UInt,
        _ => null,
    };

    private static int GetDeclaredByteSize(NumericShaderParameter parameter)
    {
        int arrayLength = Math.Max(parameter.ArraySize, 1);
        if (parameter.IsMatrix)
        {
            return parameter.ColumnCount * 16 * arrayLength;
        }

        return parameter.RowCount * parameter.ColumnCount * arrayLength * 4;
    }
}
