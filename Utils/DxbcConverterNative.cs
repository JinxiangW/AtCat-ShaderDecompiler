using System.Runtime.InteropServices;

namespace Ruri.ShaderTools.Native;

/// <summary>
/// In-process Shader-Model-5.x DXBC → DXIL conversion via Microsoft's <c>dxilconv.dll</c>
/// (<c>IDxbcConverter</c>, the library behind the <c>dxbc2dxil.exe</c> tool). Drop-in replacement
/// for shelling out to <c>dxbc2dxil.exe</c>: no temp files, no process spawn. The produced DXIL
/// container is then parsed by <see cref="DxilSpirvNative"/> via <c>dxil_spv_parse_dxil_blob</c>.
///
/// The converter is reached through the plain <c>DxcCreateInstance</c> export (no COM registration
/// / <c>CoInitialize</c> needed — DXC objects are free-threaded), and <c>IDxbcConverter::Convert</c>
/// is invoked directly through the vtable (slot 3, after the three <c>IUnknown</c> methods) with an
/// unmanaged function pointer, so there is no RCW / runtime COM-marshalling overhead.
///
/// Performance: the DXBC input is pinned in place (no copy); the single managed allocation is the
/// returned DXIL byte[] — the payload.
/// </summary>
internal static unsafe class DxbcConverterNative
{
    private const string Lib = "dxilconv";

    // DxbcConverter.h (DirectXShaderCompiler/projects/dxilconv).
    private static readonly Guid CLSID_DxbcConverter = new("4900391E-B752-4EDD-A885-6FB76E25ADDB");
    private static readonly Guid IID_IDxbcConverter = new("5F956ED5-78D1-4B15-8247-F7187614A041");

    /// <summary>
    /// Convert SM5.x DXBC bytecode to a DXIL container. Returns null on failure (message in
    /// <paramref name="error"/>).
    /// </summary>
    public static byte[]? Convert(ReadOnlySpan<byte> dxbc, out string? error)
    {
        error = null;

        IntPtr converter;
        int hr = DxcCreateInstance(in CLSID_DxbcConverter, in IID_IDxbcConverter, out converter);
        if (hr < 0 || converter == IntPtr.Zero)
        {
            error = $"DxcCreateInstance(DxbcConverter) failed (hr=0x{hr:X8}).";
            return null;
        }

        try
        {
            // IDxbcConverter::Convert — vtable slot 3 (IUnknown occupies 0/1/2):
            //   HRESULT Convert(LPCVOID pDxbc, UINT32 DxbcSize, LPCWSTR pExtraOptions,
            //                   LPVOID *ppDxil, UINT32 *pDxilSize, LPWSTR *ppDiag)
            IntPtr vtable = *(IntPtr*)converter;
            var convert = (delegate* unmanaged[Stdcall]<IntPtr, void*, uint, char*, IntPtr*, uint*, IntPtr*, int>)
                ((IntPtr*)vtable)[3];

            IntPtr dxil = IntPtr.Zero, diag = IntPtr.Zero;
            uint dxilSize = 0;
            fixed (byte* input = dxbc)
            {
                hr = convert(converter, input, (uint)dxbc.Length, null, &dxil, &dxilSize, &diag);
            }

            try
            {
                if (hr < 0 || dxil == IntPtr.Zero || dxilSize == 0)
                {
                    string? message = diag != IntPtr.Zero ? Marshal.PtrToStringUni(diag) : null;
                    error = $"IDxbcConverter::Convert failed (hr=0x{hr:X8}).{(string.IsNullOrWhiteSpace(message) ? "" : " " + message)}";
                    return null;
                }

                var result = new byte[dxilSize];
                Marshal.Copy(dxil, result, 0, (int)dxilSize);
                return result;
            }
            finally
            {
                // Out-params are CoTaskMem-allocated by the converter (CComHeapPtr).
                if (dxil != IntPtr.Zero) Marshal.FreeCoTaskMem(dxil);
                if (diag != IntPtr.Zero) Marshal.FreeCoTaskMem(diag);
            }
        }
        finally
        {
            Marshal.Release(converter);
        }
    }

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    private static extern int DxcCreateInstance(in Guid rclsid, in Guid riid, out IntPtr ppv);
}
