using Ruri.ShaderTools.Spirv.Patcher.Helpers;

namespace Ruri.ShaderTools.Spirv.Patcher.Patch;

// Pass 030 (patch): splice the new OpName / OpMemberName word arrays into the filtered
// SPIR-V at the debug-section boundary, then serialise back to bytes.
//
// "Debug-section boundary" is the last index where SPIR-V allows OpName / OpMemberName /
// OpString / OpSource* / OpLine* — everything before the type section. The scanner finds
// it by walking from the header until it hits the first OpDecorate or first type op.
// Inserting names there matches what spirv-cross expects and avoids splitting the type
// block.
//
// Short-circuit: if there are no new instructions to inject, return the original bytes
// (NOT the filtered ones — when nothing's being replaced and nothing's being inserted, we
// shouldn't churn the byte buffer at all).
internal static class Pass030_InsertAndSerialize
{
    public static void DoPass(PatchPipelineState state)
    {
        if (state.NewInstructions.Count == 0)
        {
            state.OutputSpirv = state.InputSpirv;
            return;
        }

        uint[] words = state.FilteredWords ?? throw new InvalidOperationException("Pass010 must run before Pass030.");
        int insertOffset = FindDebugInsertionPoint(words);
        int additionalWords = state.NewInstructions.Sum(arr => arr.Length);
        uint[] newWords = new uint[words.Length + additionalWords];

        Array.Copy(words, 0, newWords, 0, insertOffset);

        int writeOffset = insertOffset;
        foreach (uint[] instr in state.NewInstructions)
        {
            Array.Copy(instr, 0, newWords, writeOffset, instr.Length);
            writeOffset += instr.Length;
        }

        Array.Copy(words, insertOffset, newWords, writeOffset, words.Length - insertOffset);

        state.OutputSpirv = SpirvWordIo.WordsToBytes(newWords);
    }

    private static int FindDebugInsertionPoint(uint[] words)
    {
        int offset = SpvOpCode.HeaderWordCount;
        int lastDebugEnd = SpvOpCode.HeaderWordCount;

        while (offset < words.Length)
        {
            uint instrWord = words[offset];
            ushort opCode = SpvOpCode.GetOpCode(instrWord);
            ushort wordCount = SpvOpCode.GetWordCount(instrWord);
            if (wordCount == 0)
            {
                break;
            }

            // OpCodes 3..10 cover OpSource / OpSourceContinued / OpSourceExtension /
            // OpName / OpMemberName / OpString / OpExtInstImport — the debug + name
            // section. Bumping the boundary past the last of these keeps the new
            // OpName / OpMemberName grouped at the end of that section.
            if (opCode >= 3 && opCode <= 10)
            {
                lastDebugEnd = offset + wordCount;
            }
            else if (opCode == SpvOpCode.OpDecorate || opCode >= SpvOpCode.OpTypeVoid)
            {
                break;
            }

            offset += wordCount;
        }

        return lastDebugEnd;
    }
}
