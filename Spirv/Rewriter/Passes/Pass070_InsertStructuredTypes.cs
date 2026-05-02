using Ruri.ShaderTools.Spirv.Rewriter.Helpers;
using Ruri.ShaderTools.Spirv.Rewriter.Models;

namespace Ruri.ShaderTools.Spirv.Rewriter.Passes;

// Pass 070: actually emit the new SPIR-V types (cbuffer block + uniform pointer to it),
// the matching decorations, and the debug names that drive spirv-cross HLSL emission.
//
// Decoration order matters: OpDecorate / OpMemberDecorate must precede the types they
// describe. Hence two insertion sites — `ResolveDecorationInsertIndex` for decorations,
// `FindTypeSectionEndIndex` for the OpTypeStruct / OpTypePointer themselves.
//
// Naming uses DXC's `type.<BufferName>` convention for the struct type so spirv-cross
// emits `cbuffer type_<BufferName>` (the dot gets sanitised to `_`). The variable itself
// gets a temporary `__ruri_<BufferName>_var` placeholder; the symbol patcher overwrites
// it with the bare `<BufferName>` later. Two distinct strings are deliberate — sharing
// would trigger spirv-cross's name-uniquify pass and produce `BufferName_1` suffixes that
// bleed into emitted member names.
internal static class Pass070_InsertStructuredTypes
{
    public static void DoPass(RewriterPipelineState state)
    {
        foreach (BufferRewritePlan plan in state.Plans)
        {
            InsertStructuredType(state.Module, plan);
            InsertStructuredNames(state.Module, plan);
        }
    }

    private static void InsertStructuredType(SpirvModule module, BufferRewritePlan rewrite)
    {
        int decorationInsertIndex = TypeFactory.ResolveDecorationInsertIndex(module);

        var decorations = new List<SpirvInstruction>
        {
            new()
            {
                OpCode = SpvOpCode.OpDecorate,
                Words =
                [
                    SpvOpCode.MakeInstructionWord(SpvOpCode.OpDecorate, 3),
                    rewrite.NewStructTypeId,
                    SpvOpCode.DecorationBlock,
                ],
            },
        };

        for (int memberIndex = 0; memberIndex < rewrite.Layout.Members.Count; memberIndex++)
        {
            StructuredMemberLayout member = rewrite.Layout.Members[memberIndex];
            decorations.Add(new SpirvInstruction
            {
                OpCode = SpvOpCode.OpMemberDecorate,
                Words =
                [
                    SpvOpCode.MakeInstructionWord(SpvOpCode.OpMemberDecorate, 5),
                    rewrite.NewStructTypeId,
                    (uint)memberIndex,
                    SpvOpCode.DecorationOffset,
                    (uint)member.ByteOffset,
                ],
            });

            if (member.LogicalType.Kind == LogicalTypeKind.Matrix)
            {
                decorations.Add(new SpirvInstruction
                {
                    OpCode = SpvOpCode.OpMemberDecorate,
                    Words =
                    [
                        SpvOpCode.MakeInstructionWord(SpvOpCode.OpMemberDecorate, 4),
                        rewrite.NewStructTypeId,
                        (uint)memberIndex,
                        SpvOpCode.DecorationRowMajor,
                    ],
                });
                decorations.Add(new SpirvInstruction
                {
                    OpCode = SpvOpCode.OpMemberDecorate,
                    Words =
                    [
                        SpvOpCode.MakeInstructionWord(SpvOpCode.OpMemberDecorate, 5),
                        rewrite.NewStructTypeId,
                        (uint)memberIndex,
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
                SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypeStruct, (ushort)(2 + rewrite.MemberTypeIds.Count)),
                rewrite.NewStructTypeId,
            }.Concat(rewrite.MemberTypeIds).ToArray(),
        });

        module.Instructions.Insert(module.FindTypeSectionEndIndex(), new SpirvInstruction
        {
            OpCode = SpvOpCode.OpTypePointer,
            Words =
            [
                SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypePointer, 4),
                rewrite.NewPointerTypeId,
                SpvOpCode.StorageClassUniform,
                rewrite.NewStructTypeId,
            ],
        });
    }

    private static void InsertStructuredNames(SpirvModule module, BufferRewritePlan rewrite)
    {
        // Struct type and variable need DIFFERENT alias strings — when spirv-cross HLSL
        // backend flattens a uniform block, both names go through one shared name cache,
        // and any collision triggers a `_1` suffix that bleeds into the member-name prefix
        // (e.g. `UnityPerMaterial_1_MainTex_ST`). DXC's `type.<BufferName>` form gets
        // sanitised to `type_<BufferName>` in HLSL; the variable keeps the unadorned
        // `<BufferName>` (set later by SpirvPatcher) and members come out as
        // `<BufferName>_<member>`.
        module.InsertDebugName(rewrite.NewStructTypeId, "type." + rewrite.Info.Metadata.Name);
        module.InsertDebugName(rewrite.Info.VariableId, $"__ruri_{rewrite.Info.Metadata.Name}_var");
        for (int memberIndex = 0; memberIndex < rewrite.Layout.Members.Count; memberIndex++)
        {
            string name = rewrite.Layout.Members[memberIndex].Name;
            if (!string.IsNullOrWhiteSpace(name))
            {
                module.InsertDebugMemberName(rewrite.NewStructTypeId, (uint)memberIndex, name);
            }
        }
    }
}
