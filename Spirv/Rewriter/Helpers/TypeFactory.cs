using Ruri.ShaderTools.Spirv.Rewriter.Models;

namespace Ruri.ShaderTools.Spirv.Rewriter.Helpers;

// On-demand SPIR-V type creation. Every Ensure* method first checks the cache (TypeInfo /
// scanning module.Instructions) and returns the existing id if present; otherwise it
// inserts the matching OpType* instruction at the type section and records the new id.
//
// All inserts go to `module.FindTypeSectionEndIndex()` — keeps types in a single ordered
// block ahead of any function bodies, which is what spirv-cross expects.
internal static class TypeFactory
{
    public static uint EnsureScalarType(SpirvModule module, TypeInfo types, ScalarKind scalarKind) => scalarKind switch
    {
        ScalarKind.Float => EnsureFloatType(module, types),
        ScalarKind.Int => EnsureIntType(module, types),
        ScalarKind.UInt => EnsureUIntType(module, types),
        _ => 0,
    };

    public static uint EnsureFloatType(SpirvModule module, TypeInfo types)
    {
        if (types.FloatTypeId != 0)
        {
            return types.FloatTypeId;
        }

        uint resultId = module.AllocateId();
        module.Instructions.Insert(module.FindTypeSectionEndIndex(), new SpirvInstruction
        {
            OpCode = SpvOpCode.OpTypeFloat,
            Words = [SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypeFloat, 3), resultId, 32],
        });
        types.FloatTypeId = resultId;
        return resultId;
    }

    public static uint EnsureIntType(SpirvModule module, TypeInfo types)
    {
        if (types.IntTypeId != 0)
        {
            return types.IntTypeId;
        }

        uint resultId = module.AllocateId();
        module.Instructions.Insert(module.FindTypeSectionEndIndex(), new SpirvInstruction
        {
            OpCode = SpvOpCode.OpTypeInt,
            Words = [SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypeInt, 4), resultId, 32, 1],
        });
        types.IntTypeId = resultId;
        return resultId;
    }

    public static uint EnsureUIntType(SpirvModule module, TypeInfo types)
    {
        if (types.UIntTypeId != 0)
        {
            return types.UIntTypeId;
        }

        uint resultId = module.AllocateId();
        module.Instructions.Insert(module.FindTypeSectionEndIndex(), new SpirvInstruction
        {
            OpCode = SpvOpCode.OpTypeInt,
            Words = [SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypeInt, 4), resultId, 32, 0],
        });
        types.UIntTypeId = resultId;
        return resultId;
    }

    public static uint EnsureVectorType(SpirvModule module, TypeInfo types, ScalarKind scalarKind, int componentCount)
    {
        if (componentCount == 1)
        {
            return EnsureScalarType(module, types, scalarKind);
        }

        Dictionary<int, uint> map = scalarKind switch
        {
            ScalarKind.Float => types.FloatVectorTypeIds,
            ScalarKind.Int => types.IntVectorTypeIds,
            ScalarKind.UInt => types.UIntVectorTypeIds,
            _ => throw new InvalidOperationException("Unsupported vector scalar kind."),
        };

        if (map.TryGetValue(componentCount, out uint existingTypeId) && existingTypeId != 0)
        {
            return existingTypeId;
        }

        uint componentTypeId = EnsureScalarType(module, types, scalarKind);
        uint resultId = module.AllocateId();
        module.Instructions.Insert(module.FindTypeSectionEndIndex(), new SpirvInstruction
        {
            OpCode = SpvOpCode.OpTypeVector,
            Words = [SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypeVector, 4), resultId, componentTypeId, (uint)componentCount],
        });
        map[componentCount] = resultId;
        return resultId;
    }

    public static uint EnsureMatrixType(SpirvModule module, TypeInfo types, int rowCount, int columnCount)
    {
        if (types.MatrixTypeIds.TryGetValue((rowCount, columnCount), out uint existingTypeId) && existingTypeId != 0)
        {
            return existingTypeId;
        }

        uint vectorTypeId = EnsureVectorType(module, types, ScalarKind.Float, rowCount);
        uint resultId = module.AllocateId();
        module.Instructions.Insert(module.FindTypeSectionEndIndex(), new SpirvInstruction
        {
            OpCode = SpvOpCode.OpTypeMatrix,
            Words = [SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypeMatrix, 4), resultId, vectorTypeId, (uint)columnCount],
        });
        types.MatrixTypeIds[(rowCount, columnCount)] = resultId;
        return resultId;
    }

    public static uint EnsureArrayType(SpirvModule module, TypeInfo types, uint elementTypeId, int arrayLength, int arrayStride)
    {
        uint lengthConstantId = FindOrCreateUIntConstant(module, types, checked((uint)arrayLength));
        foreach (SpirvInstruction instruction in module.Instructions)
        {
            if (instruction.OpCode == SpvOpCode.OpTypeArray && instruction.Words.Length >= 4
                && instruction[2] == elementTypeId && instruction[3] == lengthConstantId)
            {
                return instruction[1];
            }
        }

        uint resultId = module.AllocateId();
        int decorationInsertIndex = ResolveDecorationInsertIndex(module);

        module.Instructions.Insert(decorationInsertIndex, new SpirvInstruction
        {
            OpCode = SpvOpCode.OpDecorate,
            Words =
            [
                SpvOpCode.MakeInstructionWord(SpvOpCode.OpDecorate, 4),
                resultId,
                SpvOpCode.DecorationArrayStride,
                (uint)arrayStride,
            ],
        });

        module.Instructions.Insert(module.FindTypeSectionEndIndex(), new SpirvInstruction
        {
            OpCode = SpvOpCode.OpTypeArray,
            Words = [SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypeArray, 4), resultId, elementTypeId, lengthConstantId],
        });

        return resultId;
    }

    public static uint FindOrCreateUIntConstant(SpirvModule module, TypeInfo types, uint value)
    {
        uint uintTypeId = EnsureUIntType(module, types);
        foreach (SpirvInstruction instruction in module.Instructions)
        {
            if (instruction.OpCode == SpvOpCode.OpConstant && instruction.Words.Length >= 4
                && instruction[1] == uintTypeId && instruction[3] == value)
            {
                return instruction[2];
            }
        }

        uint resultId = module.AllocateId();
        module.Instructions.Insert(module.FindTypeSectionEndIndex(), new SpirvInstruction
        {
            OpCode = SpvOpCode.OpConstant,
            Words = [SpvOpCode.MakeInstructionWord(SpvOpCode.OpConstant, 4), uintTypeId, resultId, value],
        });

        return resultId;
    }

    public static uint FindOrCreateUniformPointerType(SpirvModule module, uint memberTypeId)
    {
        foreach (SpirvInstruction instruction in module.Instructions)
        {
            if (instruction.OpCode == SpvOpCode.OpTypePointer && instruction.Words.Length >= 4
                && instruction[2] == SpvOpCode.StorageClassUniform && instruction[3] == memberTypeId)
            {
                return instruction[1];
            }
        }

        uint resultId = module.AllocateId();
        module.Instructions.Insert(module.FindTypeSectionEndIndex(), new SpirvInstruction
        {
            OpCode = SpvOpCode.OpTypePointer,
            Words =
            [
                SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypePointer, 4),
                resultId,
                SpvOpCode.StorageClassUniform,
                memberTypeId,
            ],
        });
        return resultId;
    }

    // Decorations land BEFORE the type block to satisfy SPIR-V layout rules. Walks back
    // past any leading OpDecorate / OpMemberDecorate so newly inserted decorations sit at
    // the right boundary instead of growing into the middle of an existing decoration run.
    public static int ResolveDecorationInsertIndex(SpirvModule module)
    {
        int index = module.FindFirstTypeInstructionIndex();
        while (index > 0 &&
               (module.Instructions[index - 1].OpCode == SpvOpCode.OpDecorate ||
                module.Instructions[index - 1].OpCode == SpvOpCode.OpMemberDecorate))
        {
            index--;
        }
        return index;
    }
}
