using Ruri.ShaderTools.Spirv.Rewriter.Models;

namespace Ruri.ShaderTools.Spirv.Rewriter.Passes;

// Pass 010: parse the input SPIR-V and walk it once to fill three caches:
//
//   * ModuleAnalysis  — set/binding decorations, pointer types, struct types, vector / array
//                       shapes, OpConstant id→value, array-stride decorations, OpVariable
//                       pointer types. Everything later passes need to recognise the
//                       module's existing shape without re-scanning.
//   * ConstantMaps    — id↔value lookup for OpConstant + OpConstantNull. Lets the access
//                       translator see "%uint_4" as the integer 4.
//   * TypeInfo        — primitive scalar / vector / matrix type ids cached by shape so the
//                       type factory can reuse rather than re-emit.
//
// All three live on `state` for the rest of the pipeline. Mutating-pass discipline: nothing
// after Pass010 should re-walk the module to look up these facts; if you need a new fact,
// teach this pass to record it.
internal static class Pass010_AnalyzeModule
{
    public static void DoPass(RewriterPipelineState state)
    {
        state.Module = SpirvModule.Parse(state.InputSpirv);
        state.Analysis = AnalyseModule(state.Module);
        state.Constants = BuildConstantMaps(state.Module);
        state.Types = AnalyseScalarTypes(state.Module, state.Analysis);

        state.Summary.Add($"Metadata resources={state.Metadata.GetResourceBindingCount()}, constantBuffers={state.Metadata.ConstantBufferParameters.Count}");
        state.Summary.Add($"Analyzed decoratedIds={state.Analysis.SetBindingById.Count}, variables={state.Analysis.VariablePointerTypes.Count}, pointers={state.Analysis.PointerTypes.Count}, structs={state.Analysis.StructMembers.Count}, arrays={state.Analysis.ArrayTypes.Count}");
    }

    private static ModuleAnalysis AnalyseModule(SpirvModule module)
    {
        var analysis = new ModuleAnalysis();
        foreach (SpirvInstruction instruction in module.Instructions)
        {
            switch (instruction.OpCode)
            {
                case SpvOpCode.OpDecorate when instruction.Words.Length >= 4:
                    {
                        uint targetId = instruction[1];
                        uint decoration = instruction[2];
                        if (decoration == SpvOpCode.DecorationDescriptorSet)
                        {
                            int set = (int)instruction[3];
                            (int? Set, int? Binding) existing = analysis.SetBindingById.TryGetValue(targetId, out var value) ? value : (null, null);
                            analysis.SetBindingById[targetId] = (set, existing.Binding);
                        }
                        else if (decoration == SpvOpCode.DecorationBinding)
                        {
                            int binding = (int)instruction[3];
                            (int? Set, int? Binding) existing = analysis.SetBindingById.TryGetValue(targetId, out var value) ? value : (null, null);
                            analysis.SetBindingById[targetId] = (existing.Set, binding);
                        }
                        else if (decoration == SpvOpCode.DecorationArrayStride)
                        {
                            analysis.ArrayStrides[targetId] = instruction[3];
                        }
                        break;
                    }
                case SpvOpCode.OpTypePointer when instruction.Words.Length >= 4:
                    analysis.PointerTypes[instruction[1]] = (instruction[2], instruction[3]);
                    break;
                case SpvOpCode.OpVariable when instruction.Words.Length >= 4:
                    analysis.VariablePointerTypes[instruction[2]] = instruction[1];
                    break;
                case SpvOpCode.OpTypeStruct when instruction.Words.Length >= 3:
                    analysis.StructMembers[instruction[1]] = instruction.Words.Skip(2).ToArray();
                    break;
                case SpvOpCode.OpTypeVector when instruction.Words.Length >= 4:
                    analysis.VectorShapes[instruction[1]] = (instruction[2], instruction[3]);
                    break;
                case SpvOpCode.OpTypeArray when instruction.Words.Length >= 4:
                    analysis.ArrayTypes[instruction[1]] = (instruction[2], instruction[3]);
                    break;
                case SpvOpCode.OpConstant when instruction.Words.Length >= 4:
                    analysis.Constants[instruction[2]] = instruction[3];
                    break;
            }
        }
        return analysis;
    }

    private static ConstantMaps BuildConstantMaps(SpirvModule module)
    {
        const ushort OpConstantNull = 46;
        var result = new ConstantMaps();
        foreach (SpirvInstruction instruction in module.Instructions)
        {
            int? resultIdIndex = SpvInstructionTraits.GetResultIdIndex(instruction);
            if (resultIdIndex.HasValue)
            {
                result.Definitions[instruction[resultIdIndex.Value]] = instruction;
            }

            if (instruction.OpCode == SpvOpCode.OpConstant && instruction.Words.Length >= 4)
            {
                result.IdToValue[instruction[2]] = instruction[3];
                result.ValueToId[instruction[3]] = instruction[2];
            }

            if (instruction.OpCode == OpConstantNull && instruction.Words.Length >= 3)
            {
                result.IdToValue[instruction[2]] = 0;
                if (!result.ValueToId.ContainsKey(0))
                {
                    result.ValueToId[0] = instruction[2];
                }
            }
        }
        return result;
    }

    private static TypeInfo AnalyseScalarTypes(SpirvModule module, ModuleAnalysis analysis)
    {
        var info = new TypeInfo();
        foreach (SpirvInstruction instruction in module.Instructions)
        {
            switch (instruction.OpCode)
            {
                case SpvOpCode.OpTypeFloat when instruction.Words.Length >= 3 && instruction[2] == 32:
                    info.FloatTypeId = instruction[1];
                    break;
                case SpvOpCode.OpTypeInt when instruction.Words.Length >= 4 && instruction[2] == 32 && instruction[3] == 1:
                    info.IntTypeId = instruction[1];
                    break;
                case SpvOpCode.OpTypeInt when instruction.Words.Length >= 4 && instruction[2] == 32 && instruction[3] == 0:
                    info.UIntTypeId = instruction[1];
                    break;
                case SpvOpCode.OpTypeVector when instruction.Words.Length >= 4:
                    RecordVectorType(info, instruction[1], instruction[2], instruction[3]);
                    break;
                case SpvOpCode.OpTypeMatrix when instruction.Words.Length >= 4:
                    RecordMatrixType(info, analysis, instruction[1], instruction[2], instruction[3]);
                    break;
            }
        }
        return info;
    }

    private static void RecordVectorType(TypeInfo info, uint typeId, uint componentTypeId, uint componentCount)
    {
        if (componentTypeId == info.FloatTypeId)
        {
            info.FloatVectorTypeIds[(int)componentCount] = typeId;
        }
        else if (componentTypeId == info.IntTypeId)
        {
            info.IntVectorTypeIds[(int)componentCount] = typeId;
        }
        else if (componentTypeId == info.UIntTypeId)
        {
            info.UIntVectorTypeIds[(int)componentCount] = typeId;
        }
    }

    private static void RecordMatrixType(TypeInfo info, ModuleAnalysis analysis, uint typeId, uint vectorTypeId, uint columnCount)
    {
        if (!analysis.TryGetVectorShape(vectorTypeId, out uint componentTypeId, out uint rowCount) || componentTypeId != info.FloatTypeId)
        {
            return;
        }

        info.MatrixTypeIds[(checked((int)rowCount), checked((int)columnCount))] = typeId;
    }
}
