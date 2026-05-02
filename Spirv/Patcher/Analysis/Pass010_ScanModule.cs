using Ruri.ShaderTools.Spirv.Patcher.Helpers;

namespace Ruri.ShaderTools.Spirv.Patcher.Analysis;

// Pass 010 (analysis): read SPIR-V bytes, walk every instruction once, fill the maps the
// next pass needs.
//
// Single-pass walk — never iterates the module twice. Records:
//   * (Set, Binding) decorations per id
//   * Struct member byte offsets (OpMemberDecorate Offset)
//   * Type relationships (OpTypePointer storage class + pointee, OpTypeStruct membership)
//   * Image / Sampler / SampledImage type ids (the descriptor-type classifier inputs)
//   * Existing OpName entries (used as fallback "current name" for unmatched bindings)
//   * OpVariable → pointer type lookup
//
// The instruction walk uses raw word indices (rather than SpirvModule.Parse) — the patcher
// runs frequently in tight loops and avoiding the parse object's allocations is a measured
// win. The trade-off: this pass duplicates a tiny bit of decoder logic that
// SpvOpCode.GetOpCode / GetWordCount centralise.
internal static class Pass010_ScanModule
{
    public static void DoPass(BindingAnalysisState state)
    {
        state.Words = SpirvWordIo.BytesToWords(state.InputSpirv);
        uint[] words = state.Words;

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

            switch (opCode)
            {
                case SpvOpCode.OpDecorate when wordCount >= 4:
                    HandleDecorate(state, words, offset);
                    break;
                case SpvOpCode.OpMemberDecorate when wordCount >= 5:
                    HandleMemberDecorate(state, words, offset);
                    break;
                case SpvOpCode.OpTypePointer when wordCount >= 4:
                    state.TypePointerMap[words[offset + 1]] = (words[offset + 2], words[offset + 3]);
                    break;
                case SpvOpCode.OpTypeStruct:
                    {
                        uint structId = words[offset + 1];
                        state.StructTypeIds.Add(structId);
                        state.StructMemberCounts[structId] = wordCount >= 2 ? wordCount - 2 : 0;
                        break;
                    }
                case SpvOpCode.OpTypeImage:
                    state.ImageTypeIds.Add(words[offset + 1]);
                    break;
                case SpvOpCode.OpTypeSampler:
                    state.SamplerTypeIds.Add(words[offset + 1]);
                    break;
                case SpvOpCode.OpTypeSampledImage:
                    state.SampledImageTypeIds.Add(words[offset + 1]);
                    break;
                case SpvOpCode.OpName when wordCount >= 3:
                    {
                        uint targetId = words[offset + 1];
                        string? name = SpirvWordIo.ReadLiteralString(words, offset + 2, wordCount - 2);
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            state.IdToName[targetId] = name;
                        }
                        break;
                    }
                case SpvOpCode.OpVariable when wordCount >= 4:
                    state.VariableTypeMap[words[offset + 2]] = words[offset + 1];
                    break;
            }

            offset += wordCount;
        }
    }

    private static void HandleDecorate(BindingAnalysisState state, uint[] words, int offset)
    {
        uint targetId = words[offset + 1];
        uint decoration = words[offset + 2];

        if (decoration == SpvOpCode.DecorationDescriptorSet)
        {
            int set = (int)words[offset + 3];
            (int? Set, int? Binding) existing = state.IdToSetBinding.TryGetValue(targetId, out var v) ? v : (null, null);
            state.IdToSetBinding[targetId] = (set, existing.Binding);
        }
        else if (decoration == SpvOpCode.DecorationBinding)
        {
            int binding = (int)words[offset + 3];
            (int? Set, int? Binding) existing = state.IdToSetBinding.TryGetValue(targetId, out var v) ? v : (null, null);
            state.IdToSetBinding[targetId] = (existing.Set, binding);
        }
    }

    private static void HandleMemberDecorate(BindingAnalysisState state, uint[] words, int offset)
    {
        uint structId = words[offset + 1];
        int memberIndex = (int)words[offset + 2];
        uint decoration = words[offset + 3];

        if (decoration != SpvOpCode.DecorationOffset)
        {
            return;
        }

        if (!state.StructMemberOffsets.TryGetValue(structId, out Dictionary<int, uint>? memberOffsets))
        {
            memberOffsets = new Dictionary<int, uint>();
            state.StructMemberOffsets[structId] = memberOffsets;
        }
        memberOffsets[memberIndex] = words[offset + 4];
    }
}
