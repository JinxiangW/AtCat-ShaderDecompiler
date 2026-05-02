using System.Text;

namespace Ruri.ShaderTools.Spirv.Patcher.Helpers;

// Low-level SPIR-V word ↔ byte conversion + literal-string read used by both analysis and
// patch pipelines. No SPIR-V semantic knowledge — just byte fiddling.
internal static class SpirvWordIo
{
    public static uint[] BytesToWords(byte[] bytes)
    {
        uint[] words = new uint[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, words, 0, bytes.Length);
        return words;
    }

    public static byte[] WordsToBytes(uint[] words)
    {
        byte[] bytes = new byte[words.Length * 4];
        Buffer.BlockCopy(words, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static string? ReadLiteralString(uint[] words, int start, int wordCount)
    {
        byte[] bytes = new byte[wordCount * 4];
        Buffer.BlockCopy(words, start * 4, bytes, 0, bytes.Length);
        int nullIndex = Array.IndexOf(bytes, (byte)0);
        if (nullIndex < 0)
        {
            nullIndex = bytes.Length;
        }
        return Encoding.UTF8.GetString(bytes, 0, nullIndex);
    }
}
