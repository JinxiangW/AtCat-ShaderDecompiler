using System;
using System.Runtime.InteropServices;

namespace Ruri.ShaderDecompiler.Testing.Compilation;

internal static class D3DCompiler
{
    private const uint D3DCompileEnableStrictness = 1u << 11;
    private const uint D3DCompileOptimizationLevel3 = 1u << 15;

    public static byte[] Compile(string source, string entryPoint, string profile)
    {
        int hr = D3DCompile(
            source,
            new UIntPtr((uint)source.Length),
            null,
            IntPtr.Zero,
            IntPtr.Zero,
            entryPoint,
            profile,
            D3DCompileEnableStrictness | D3DCompileOptimizationLevel3,
            0,
            out IntPtr codeBlob,
            out IntPtr errorBlob);

        try
        {
            if (hr < 0 || codeBlob == IntPtr.Zero)
            {
                string error = errorBlob != IntPtr.Zero ? ReadBlobAsString(errorBlob) : $"D3DCompile failed with HRESULT 0x{hr:X8}.";
                throw new InvalidOperationException(error);
            }

            return ReadBlobAsBytes(codeBlob);
        }
        finally
        {
            if (codeBlob != IntPtr.Zero)
            {
                Marshal.Release(codeBlob);
            }

            if (errorBlob != IntPtr.Zero)
            {
                Marshal.Release(errorBlob);
            }
        }
    }

    private static unsafe byte[] ReadBlobAsBytes(IntPtr blob)
    {
        if (blob == IntPtr.Zero)
        {
            return Array.Empty<byte>();
        }

        void** vtbl = *(void***)blob;
        var getBufferPointer = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr>)vtbl[3];
        var getBufferSize = (delegate* unmanaged[Stdcall]<IntPtr, nuint>)vtbl[4];

        IntPtr ptr = getBufferPointer(blob);
        int size = checked((int)getBufferSize(blob));
        if (ptr == IntPtr.Zero || size <= 0)
        {
            return Array.Empty<byte>();
        }

        byte[] bytes = new byte[size];
        Marshal.Copy(ptr, bytes, 0, size);
        return bytes;
    }

    private static string ReadBlobAsString(IntPtr blob)
    {
        return System.Text.Encoding.UTF8.GetString(ReadBlobAsBytes(blob)).TrimEnd('\0', '\r', '\n');
    }

    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int D3DCompile(
        [MarshalAs(UnmanagedType.LPStr)] string srcData,
        UIntPtr srcDataSize,
        [MarshalAs(UnmanagedType.LPStr)] string? sourceName,
        IntPtr defines,
        IntPtr include,
        [MarshalAs(UnmanagedType.LPStr)] string entryPoint,
        [MarshalAs(UnmanagedType.LPStr)] string target,
        uint flags1,
        uint flags2,
        out IntPtr code,
        out IntPtr errorMsgs);

}
