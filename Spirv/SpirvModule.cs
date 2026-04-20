namespace Ruri.ShaderDecompiler.Spirv;

using System.Text;

internal sealed class SpirvInstruction
{
    public ushort OpCode { get; set; }
    public ushort WordCount { get; set; }
    public uint[] Words { get; set; } = Array.Empty<uint>();
    public int Offset { get; set; }

    public uint this[int index]
    {
        get => Words[index];
        set => Words[index] = value;
    }
}

internal sealed class SpirvModule
{
    public uint[] Header { get; private set; } = Array.Empty<uint>();
    public List<SpirvInstruction> Instructions { get; } = new();

    public static SpirvModule Parse(byte[] bytes)
    {
        uint[] words = BytesToWords(bytes);
        if (words.Length < SpvOpCode.HeaderWordCount || words[0] != SpvOpCode.MagicNumber)
        {
            throw new ArgumentException("Invalid SPIR-V binary.");
        }

        var module = new SpirvModule
        {
            Header = words.Take(SpvOpCode.HeaderWordCount).ToArray()
        };

        int offset = SpvOpCode.HeaderWordCount;
        while (offset < words.Length)
        {
            uint word0 = words[offset];
            ushort opCode = SpvOpCode.GetOpCode(word0);
            ushort wordCount = SpvOpCode.GetWordCount(word0);
            if (wordCount == 0)
            {
                break;
            }

            var instruction = new SpirvInstruction
            {
                OpCode = opCode,
                WordCount = wordCount,
                Offset = offset,
                Words = words.Skip(offset).Take(wordCount).ToArray()
            };
            module.Instructions.Add(instruction);
            offset += wordCount;
        }

        return module;
    }

    public byte[] ToBytes()
    {
        var allWords = new List<uint>(Header.Length + Instructions.Sum(i => i.Words.Length));
        allWords.AddRange(Header);
        foreach (var instruction in Instructions)
        {
            instruction.Words[0] = SpvOpCode.MakeInstructionWord(instruction.OpCode, (ushort)instruction.Words.Length);
            allWords.AddRange(instruction.Words);
        }

        byte[] bytes = new byte[allWords.Count * 4];
        Buffer.BlockCopy(allWords.ToArray(), 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public uint AllocateId()
    {
        uint nextId = Math.Max(Header[3], FindMaxResultId() + 1);
        Header[3] = nextId + 1;
        return nextId;
    }

    private uint FindMaxResultId()
    {
        uint maxId = 0;
        foreach (var instruction in Instructions)
        {
            int? resultIdIndex = GetResultIdIndex(instruction.OpCode, instruction.Words.Length);
            if (resultIdIndex.HasValue)
            {
                uint resultId = instruction.Words[resultIdIndex.Value];
                if (resultId > maxId)
                {
                    maxId = resultId;
                }
            }
        }

        return maxId;
    }

    private static int? GetResultIdIndex(ushort opCode, int wordCount)
    {
        return opCode switch
        {
            SpvOpCode.OpTypeVoid => wordCount >= 2 ? 1 : null,
            SpvOpCode.OpTypeBool => wordCount >= 2 ? 1 : null,
            SpvOpCode.OpTypeInt => wordCount >= 2 ? 1 : null,
            SpvOpCode.OpTypeFloat => wordCount >= 2 ? 1 : null,
            SpvOpCode.OpTypeVector => wordCount >= 2 ? 1 : null,
            SpvOpCode.OpTypeMatrix => wordCount >= 2 ? 1 : null,
            SpvOpCode.OpTypeImage => wordCount >= 2 ? 1 : null,
            SpvOpCode.OpTypeSampler => wordCount >= 2 ? 1 : null,
            SpvOpCode.OpTypeSampledImage => wordCount >= 2 ? 1 : null,
            SpvOpCode.OpTypeArray => wordCount >= 2 ? 1 : null,
            SpvOpCode.OpTypeRuntimeArray => wordCount >= 2 ? 1 : null,
            SpvOpCode.OpTypeStruct => wordCount >= 2 ? 1 : null,
            SpvOpCode.OpTypeOpaque => wordCount >= 2 ? 1 : null,
            SpvOpCode.OpTypePointer => wordCount >= 2 ? 1 : null,
            SpvOpCode.OpConstant => wordCount >= 3 ? 2 : null,
            SpvOpCode.OpConstantComposite => wordCount >= 3 ? 2 : null,
            SpvOpCode.OpVariable => wordCount >= 3 ? 2 : null,
            SpvOpCode.OpLoad => wordCount >= 3 ? 2 : null,
            SpvOpCode.OpAccessChain => wordCount >= 3 ? 2 : null,
            SpvOpCode.OpInBoundsAccessChain => wordCount >= 3 ? 2 : null,
            SpvOpCode.OpCompositeExtract => wordCount >= 3 ? 2 : null,
            _ => null
        };
    }

    public int FindFirstTypeInstructionIndex()
    {
        for (int i = 0; i < Instructions.Count; i++)
        {
            ushort op = Instructions[i].OpCode;
            if (op >= SpvOpCode.OpTypeVoid && op <= SpvOpCode.OpTypePointer)
            {
                return i;
            }
        }

        return Instructions.Count;
    }

    public int FindDebugInsertionIndex()
    {
        int insertIndex = 0;
        for (int i = 0; i < Instructions.Count; i++)
        {
            ushort op = Instructions[i].OpCode;
            if (op == SpvOpCode.OpName || op == SpvOpCode.OpMemberName || op == SpvOpCode.OpExtInstImport)
            {
                insertIndex = i + 1;
                continue;
            }

            if (op == SpvOpCode.OpDecorate || op == SpvOpCode.OpMemberDecorate || op >= SpvOpCode.OpTypeVoid)
            {
                break;
            }
        }

        return insertIndex;
    }

    public void InsertDebugName(uint id, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        Instructions.Insert(FindDebugInsertionIndex(), CreateDebugStringInstruction(SpvOpCode.OpName, id, null, name));
    }

    public void InsertDebugMemberName(uint typeId, uint memberIndex, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        Instructions.Insert(FindDebugInsertionIndex(), CreateDebugStringInstruction(SpvOpCode.OpMemberName, typeId, memberIndex, name));
    }

    public int FindTypeSectionEndIndex()
    {
        int lastTypeIndex = -1;
        for (int i = 0; i < Instructions.Count; i++)
        {
            ushort op = Instructions[i].OpCode;
            bool isType = op >= SpvOpCode.OpTypeVoid && op <= SpvOpCode.OpTypePointer;
            if (isType)
            {
                lastTypeIndex = i;
            }
        }

        return lastTypeIndex >= 0 ? lastTypeIndex + 1 : Instructions.Count;
    }

    public int FindFirstFunctionInstructionIndex()
    {
        for (int i = 0; i < Instructions.Count; i++)
        {
            if (Instructions[i].OpCode == 54)
            {
                return i;
            }
        }

        return Instructions.Count;
    }

    private static uint[] BytesToWords(byte[] bytes)
    {
        uint[] words = new uint[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, words, 0, bytes.Length);
        return words;
    }

    private static SpirvInstruction CreateDebugStringInstruction(ushort opCode, uint targetId, uint? memberIndex, string name)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        int paddedLength = (nameBytes.Length + 1 + 3) / 4 * 4;
        byte[] paddedName = new byte[paddedLength];
        Array.Copy(nameBytes, paddedName, nameBytes.Length);

        int extraOperands = memberIndex.HasValue ? 2 : 1;
        int wordCount = 1 + extraOperands + (paddedLength / 4);
        uint[] words = new uint[wordCount];
        words[0] = SpvOpCode.MakeInstructionWord(opCode, (ushort)wordCount);
        words[1] = targetId;

        int stringStartIndex = 2;
        if (memberIndex.HasValue)
        {
            words[2] = memberIndex.Value;
            stringStartIndex = 3;
        }

        for (int i = 0; i < paddedLength / 4; i++)
        {
            words[stringStartIndex + i] = BitConverter.ToUInt32(paddedName, i * 4);
        }

        return new SpirvInstruction
        {
            OpCode = opCode,
            Words = words
        };
    }
}
