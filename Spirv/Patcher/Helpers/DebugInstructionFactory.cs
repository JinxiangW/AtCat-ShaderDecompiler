using System.Text;

namespace Ruri.ShaderTools.Spirv.Patcher.Helpers;

// Synthesises freshly-encoded OpName / OpMemberName instruction words. The string payload
// is null-terminated and zero-padded to a 4-byte word boundary per the SPIR-V spec; the
// returned array is the ready-to-splice instruction including its opcode/wordcount header
// at index 0.
internal static class DebugInstructionFactory
{
    public static uint[] CreateOpName(uint id, string name)
    {
        byte[] paddedName = EncodePaddedName(name);
        int wordCount = 2 + paddedName.Length / 4;
        uint[] instr = new uint[wordCount];
        instr[0] = SpvOpCode.MakeInstructionWord(SpvOpCode.OpName, (ushort)wordCount);
        instr[1] = id;
        for (int i = 0; i < paddedName.Length / 4; i++)
        {
            instr[2 + i] = BitConverter.ToUInt32(paddedName, i * 4);
        }
        return instr;
    }

    public static uint[] CreateOpMemberName(uint typeId, uint memberIndex, string name)
    {
        byte[] paddedName = EncodePaddedName(name);
        int wordCount = 3 + paddedName.Length / 4;
        uint[] instr = new uint[wordCount];
        instr[0] = SpvOpCode.MakeInstructionWord(SpvOpCode.OpMemberName, (ushort)wordCount);
        instr[1] = typeId;
        instr[2] = memberIndex;
        for (int i = 0; i < paddedName.Length / 4; i++)
        {
            instr[3 + i] = BitConverter.ToUInt32(paddedName, i * 4);
        }
        return instr;
    }

    private static byte[] EncodePaddedName(string name)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        int paddedLength = (nameBytes.Length + 1 + 3) / 4 * 4;
        byte[] padded = new byte[paddedLength];
        Array.Copy(nameBytes, padded, nameBytes.Length);
        return padded;
    }
}
