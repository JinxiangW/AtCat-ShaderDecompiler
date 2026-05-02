namespace Ruri.ShaderTools.Spirv.Rewriter.Helpers;

// Structural "is this id still consumed?" check used by the dead-code cascade in Pass100.
//
// Why this exists instead of a use-count map: several SPIR-V ops carry literal values in
// operand slots — OpConstant value words, OpExtInst's instruction enum, OpCompositeExtract's
// component indices, OpDecorate's decoration enum + literal arguments, etc. When those
// literals happen to coincide numerically with a real SSA id, a generic counter inflates
// the use count and a dead access chain stays alive (which then trips spirv-cross with
// "Cannot subdivide a scalar value" because the chain walks a struct shape that no longer
// exists). Skipping these ops at the instruction level — and skipping the result-id /
// result-type slots of every other op — gives us the only correct structural answer.
internal static class LiveIdScanner
{
    public static bool HasLiveIdConsumer(SpirvModule module, uint targetId)
    {
        foreach (SpirvInstruction instruction in module.Instructions)
        {
            if (instruction.OpCode == SpvOpCode.OpNop)
            {
                continue;
            }

            if (IsLiteralBearingMetadataOp(instruction.OpCode) || IsLiteralValueConstantOp(instruction.OpCode))
            {
                continue;
            }

            int? resultIdIndex = SpvInstructionTraits.GetResultIdIndex(instruction);
            int? resultTypeIndex = SpvInstructionTraits.GetResultTypeIdIndex(instruction);
            for (int operandIndex = 1; operandIndex < instruction.Words.Length; operandIndex++)
            {
                if ((resultIdIndex.HasValue && operandIndex == resultIdIndex.Value)
                    || (resultTypeIndex.HasValue && operandIndex == resultTypeIndex.Value))
                {
                    continue;
                }

                if (IsLiteralOperandSlot(instruction.OpCode, operandIndex))
                {
                    continue;
                }

                if (instruction[operandIndex] == targetId)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Names, decorations, source/string blocks. None of these participate in data flow.
    // Opcode numbers are stable per the SPIR-V spec.
    private static bool IsLiteralBearingMetadataOp(ushort opCode)
    {
        return opCode == SpvOpCode.OpName              // 5
            || opCode == SpvOpCode.OpMemberName        // 6
            || opCode == SpvOpCode.OpDecorate          // 71
            || opCode == SpvOpCode.OpMemberDecorate    // 72
            || opCode == 73                            // OpDecorationGroup
            || opCode == 74                            // OpGroupDecorate
            || opCode == 75                            // OpGroupMemberDecorate
            || opCode == 7                             // OpString
            || opCode == 3                             // OpSource
            || opCode == 2                             // OpSourceContinued
            || opCode == 4                             // OpSourceExtension
            || opCode == 330                           // OpModuleProcessed
            || opCode == 8                             // OpLine
            || opCode == 317                           // OpNoLine
            || opCode == SpvOpCode.OpExecutionMode     // 16
            || opCode == 331                           // OpExecutionModeId
            || opCode == SpvOpCode.OpEntryPoint        // 15
            || opCode == SpvOpCode.OpCapability        // 17
            || opCode == 10                            // OpExtension
            || opCode == SpvOpCode.OpExtInstImport     // 11
            || opCode == SpvOpCode.OpMemoryModel;      // 14
    }

    // Constant-definition ops where every word past the result id is a literal (or there
    // are no further words at all). Skipping the whole instruction is safe: the only id
    // reference is the result type at index 1, which the caller already excludes.
    // OpConstantComposite / OpSpecConstantComposite are NOT skipped — their post-result
    // words are id references to constituent constants, real data-flow edges.
    private static bool IsLiteralValueConstantOp(ushort opCode)
    {
        return opCode == 41   // OpConstantTrue
            || opCode == 42   // OpConstantFalse
            || opCode == 43   // OpConstant — value literal words (1 for 32-bit, 2 for 64-bit)
            || opCode == 45   // OpConstantSampler — three mode literals
            || opCode == 46   // OpConstantNull
            || opCode == 48   // OpSpecConstantTrue
            || opCode == 49   // OpSpecConstantFalse
            || opCode == 50;  // OpSpecConstant — like OpConstant
    }

    // Operand slots that hold literal values rather than SSA ids inside ops that otherwise
    // mix the two. Without skipping these, a numeric collision between a real SSA id and
    // (for example) an OpExtInst instruction-enum (`81 == NClamp` in GLSL.std.450) keeps
    // a dead OpAccessChain alive — exactly the Bug D archetype the playbook warns about.
    //
    // Whole-instruction skip lists (OpDecorate, OpName, OpExecutionMode, ...) handle ops
    // that are pure metadata; this helper is for the data-flow ops where some, but not
    // all, post-result slots are literal.
    //
    // Layouts cross-checked against the SPIR-V 1.x core spec.
    private static bool IsLiteralOperandSlot(ushort opCode, int operandIndex)
    {
        return opCode switch
        {
            // OpExtInst:        [h, type, result, set-id, instruction-enum-lit, operand-ids...]
            12 => operandIndex == 4,

            // OpVectorShuffle:  [h, type, result, v1, v2, comp0-lit, comp1-lit, ...]
            79 => operandIndex >= 5,

            // OpCompositeExtract: [h, type, result, composite, idx0-lit, idx1-lit, ...]
            SpvOpCode.OpCompositeExtract => operandIndex >= 4,

            // OpCompositeInsert: [h, type, result, value, composite, idx0-lit, idx1-lit, ...]
            82 => operandIndex >= 5,

            // OpSwitch:         [h, selector, default-lbl, (lit, target-lbl)...] — literal
            // and label words alternate after slot 2; we cannot disambiguate without parsing
            // the selector width, so skip everything past the default label. The cleanup
            // pass only ever asks about pointer-typed result ids, which cannot legally
            // appear as either a switch case literal or a branch label.
            250 => operandIndex >= 3,

            _ => false,
        };
    }
}
