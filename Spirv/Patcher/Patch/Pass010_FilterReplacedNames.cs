using Ruri.ShaderTools.Spirv.Patcher.Helpers;

namespace Ruri.ShaderTools.Spirv.Patcher.Patch;

// Pass 010 (patch): walk the input SPIR-V and copy every word into a filtered output, but
// SKIP any OpName / OpMemberName whose target is being replaced by an injected name.
//
// This is critical for HLSL output quality: dxil-spirv routinely emits machine-generated
// debug names (`_8`, `_14`, `type_0`) ahead of any patching. If we just appended new
// OpName entries on top, spirv-cross's name-uniquify pass would happily pick the original
// machine names anyway, and the metadata-recovered names would silently lose the priority
// race. The filtered word stream guarantees the new injection is the only OpName for the
// target id when the patcher is done.
internal static class Pass010_FilterReplacedNames
{
    public static void DoPass(PatchPipelineState state)
    {
        if (state.InputSpirv.Length < SpvOpCode.HeaderWordCount * 4)
        {
            throw new ArgumentException("Invalid SPIR-V binary");
        }

        uint[] words = SpirvWordIo.BytesToWords(state.InputSpirv);
        if (words[0] != SpvOpCode.MagicNumber)
        {
            throw new ArgumentException("Invalid SPIR-V magic");
        }

        state.ReplacedIds = state.Names.Select(static x => x.Id).ToHashSet();
        state.ReplacedMembers = state.MemberNames.Select(static x => (x.TypeId, x.MemberIndex)).ToHashSet();

        var filteredWords = new List<uint>(words.Length);
        for (int i = 0; i < SpvOpCode.HeaderWordCount; i++)
        {
            filteredWords.Add(words[i]);
        }

        int offset = SpvOpCode.HeaderWordCount;
        while (offset < words.Length)
        {
            uint instrWord = words[offset];
            ushort opCode = SpvOpCode.GetOpCode(instrWord);
            ushort wordCount = SpvOpCode.GetWordCount(instrWord);
            if (wordCount == 0)
            {
                break;
            }

            if (!ShouldSkip(opCode, wordCount, words, offset, state))
            {
                for (int i = 0; i < wordCount; i++)
                {
                    filteredWords.Add(words[offset + i]);
                }
            }

            offset += wordCount;
        }

        state.FilteredWords = filteredWords.ToArray();
    }

    private static bool ShouldSkip(ushort opCode, ushort wordCount, uint[] words, int offset, PatchPipelineState state)
    {
        if (opCode == SpvOpCode.OpName && wordCount >= 2)
        {
            return state.ReplacedIds.Contains(words[offset + 1]);
        }
        if (opCode == SpvOpCode.OpMemberName && wordCount >= 3)
        {
            return state.ReplacedMembers.Contains((words[offset + 1], words[offset + 2]));
        }
        return false;
    }
}
