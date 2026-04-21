using System;
using System.Runtime.InteropServices;

namespace Ruri.ShaderDecompiler.Testing.Compilation;

internal static class D3DCompiler
{
    private const uint D3DCompileEnableStrictness = 1u << 11;
    private const uint D3DCompileOptimizationLevel3 = 1u << 15;

    public static byte[] Compile(string source, string entryPoint, string profile)
    {
        return Compile(source, entryPoint, profile, Array.Empty<(string Name, string Value)>());
    }

    public static byte[] Compile(string source, string entryPoint, string profile, params (string Name, string Value)[] defines)
    {
        IntPtr definesPtr = IntPtr.Zero;
        IntPtr[] allocatedStrings = Array.Empty<IntPtr>();

        if (defines.Length > 0)
        {
            allocatedStrings = new IntPtr[defines.Length * 2];
            int macroSize = Marshal.SizeOf<D3DShaderMacro>();
            definesPtr = Marshal.AllocHGlobal(macroSize * (defines.Length + 1));

            for (int i = 0; i < defines.Length; i++)
            {
                IntPtr namePtr = Marshal.StringToHGlobalAnsi(defines[i].Name);
                IntPtr valuePtr = Marshal.StringToHGlobalAnsi(defines[i].Value);
                allocatedStrings[i * 2] = namePtr;
                allocatedStrings[i * 2 + 1] = valuePtr;

                Marshal.StructureToPtr(new D3DShaderMacro
                {
                    Name = namePtr,
                    Definition = valuePtr,
                }, IntPtr.Add(definesPtr, i * macroSize), false);
            }

            Marshal.StructureToPtr(new D3DShaderMacro(), IntPtr.Add(definesPtr, defines.Length * macroSize), false);
        }

        int hr = D3DCompile(
            source,
            new UIntPtr((uint)source.Length),
            null,
            definesPtr,
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
            foreach (IntPtr allocated in allocatedStrings)
            {
                if (allocated != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(allocated);
                }
            }

            if (definesPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(definesPtr);
            }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DShaderMacro
    {
        public IntPtr Name;
        public IntPtr Definition;
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
