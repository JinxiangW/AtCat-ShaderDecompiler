namespace Ruri.ShaderTools.Spirv;

using Ruri.ShaderTools.Spirv.Rewriter.Helpers;

/// <summary>
/// Removes dxbc-spirv's redundant u32-to-u64 promotion before an unsigned
/// integer-to-float conversion. SM5 DXBC has no 64-bit integer operation here,
/// and the promotion prevents SPIRV-Cross from emitting SM5 HLSL.
/// </summary>
internal static class DxbcUtofNormalizer
{
    private const uint Int64Capability = 11;

    public static byte[] Normalize(byte[] spirv)
    {
        SpirvModule module = SpirvModule.Parse(spirv);
        Dictionary<uint, SpirvInstruction> definitions = BuildDefinitions(module);
        HashSet<uint> uint32Types = FindIntegerTypes(module, width: 32, signedness: 0);
        HashSet<uint> uint64Types = FindIntegerTypes(module, width: 64, signedness: 0);
        HashSet<uint> float32Types = FindFloatTypes(module, width: 32);
        var matchedConversions = new HashSet<SpirvInstruction>();
        var candidateUint64Types = new HashSet<uint>();
        int rewriteCount = 0;

        foreach (SpirvInstruction convert in module.Instructions)
        {
            if (convert.OpCode != SpvOpCode.OpConvertUToF || convert.Words.Length != 4
                || !float32Types.Contains(convert[1])
                || !definitions.TryGetValue(convert[3], out SpirvInstruction? promote)
                || promote.OpCode != SpvOpCode.OpUConvert || promote.Words.Length != 4
                || !uint64Types.Contains(promote[1])
                || !definitions.TryGetValue(promote[3], out SpirvInstruction? source))
            {
                continue;
            }

            int? sourceTypeIndex = SpvInstructionTraits.GetResultTypeIdIndex(source);
            if (!sourceTypeIndex.HasValue || !uint32Types.Contains(source[sourceTypeIndex.Value]))
            {
                continue;
            }

            convert[3] = promote[3];
            matchedConversions.Add(promote);
            candidateUint64Types.Add(promote[1]);
            rewriteCount++;
        }

        if (rewriteCount == 0)
        {
            return spirv;
        }

        foreach (SpirvInstruction promote in matchedConversions)
        {
            uint resultId = promote[2];
            if (!LiveIdScanner.HasLiveIdConsumer(module, resultId))
            {
                RemoveDefinitionAndMetadata(module, promote, resultId);
            }
        }

        foreach (uint typeId in candidateUint64Types)
        {
            if (!HasTypeUse(module, typeId) && definitions.TryGetValue(typeId, out SpirvInstruction? type))
            {
                RemoveDefinitionAndMetadata(module, type, typeId);
            }
        }

        bool hasInt64Type = module.Instructions.Any(static instruction =>
            instruction.OpCode == SpvOpCode.OpTypeInt
            && instruction.Words.Length == 4
            && instruction[2] == 64);
        if (!hasInt64Type)
        {
            module.Instructions.RemoveAll(static instruction =>
                instruction.OpCode == SpvOpCode.OpCapability
                && instruction.Words.Length == 2
                && instruction[1] == Int64Capability);
        }

        return module.ToBytes();
    }

    private static Dictionary<uint, SpirvInstruction> BuildDefinitions(SpirvModule module)
    {
        var definitions = new Dictionary<uint, SpirvInstruction>();
        foreach (SpirvInstruction instruction in module.Instructions)
        {
            int? resultIdIndex = SpvInstructionTraits.GetResultIdIndex(instruction);
            if (resultIdIndex.HasValue)
            {
                definitions[instruction[resultIdIndex.Value]] = instruction;
            }
        }
        return definitions;
    }

    private static HashSet<uint> FindIntegerTypes(SpirvModule module, uint width, uint signedness)
        => module.Instructions
            .Where(instruction => instruction.OpCode == SpvOpCode.OpTypeInt
                && instruction.Words.Length == 4
                && instruction[2] == width
                && instruction[3] == signedness)
            .Select(static instruction => instruction[1])
            .ToHashSet();

    private static HashSet<uint> FindFloatTypes(SpirvModule module, uint width)
        => module.Instructions
            .Where(instruction => instruction.OpCode == SpvOpCode.OpTypeFloat
                && instruction.Words.Length == 3
                && instruction[2] == width)
            .Select(static instruction => instruction[1])
            .ToHashSet();

    private static bool HasTypeUse(SpirvModule module, uint typeId)
    {
        foreach (SpirvInstruction instruction in module.Instructions)
        {
            int? resultTypeIndex = SpvInstructionTraits.GetResultTypeIdIndex(instruction);
            if (resultTypeIndex.HasValue && instruction[resultTypeIndex.Value] == typeId)
            {
                return true;
            }
        }

        return LiveIdScanner.HasLiveIdConsumer(module, typeId);
    }

    private static void RemoveDefinitionAndMetadata(
        SpirvModule module,
        SpirvInstruction definition,
        uint resultId)
    {
        module.Instructions.Remove(definition);
        module.Instructions.RemoveAll(instruction =>
            (instruction.OpCode == SpvOpCode.OpName || instruction.OpCode == SpvOpCode.OpDecorate)
            && instruction.Words.Length >= 2
            && instruction[1] == resultId);
    }
}
