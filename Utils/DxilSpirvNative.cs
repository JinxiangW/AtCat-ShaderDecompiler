using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ruri.ShaderTools.Native;

/// <summary>
/// In-process DXIL/DXBC-container → SPIR-V conversion via the dxil-spirv C API
/// (<c>dxil_spv_*</c>, Hans-Kristian Arntzen's dxil-spirv, the same engine vkd3d-proton uses).
/// Drop-in replacement for shelling out to <c>dxil-spirv.exe</c>: no temp <c>.dxil</c>/<c>.spv</c>
/// files, no process spawn. There is no NuGet distribution of dxil-spirv, so the shared library
/// (<c>dxil-spirv-c-shared.dll</c>) ships under <c>Tools/</c> and is loaded by <see cref="NativeToolsLoader"/>.
///
/// Behaviour is byte-for-byte equivalent to the previous CLI invocation
/// <c>dxil-spirv --ssbo-uav --ssbo-srv [--raw-llvm]</c>: the two SSBO flags are realised by the
/// SRV/UAV resource remappers below, which reproduce the CLI's <c>remap_srv</c>/<c>remap_uav</c>
/// exactly for this configuration (no <c>--bindless</c>, no root descriptors).
///
/// Performance: the input is pinned in place (no copy); the remapper callbacks are static
/// <c>[UnmanagedCallersOnly]</c> function pointers (no delegate allocation, no marshalling) and
/// touch only stack memory. The per-call thread-allocator scope both satisfies the API contract
/// and bounds native memory (each conversion's scratch is freed at scope end). The single managed
/// allocation is the returned SPIR-V byte[] — the payload. Independent contexts make it fully
/// parallel across the batch worker pool.
/// </summary>
internal static unsafe class DxilSpirvNative
{
    private const string Lib = "dxil-spirv-c-shared";

    // dxil_spv_resource_kind
    private const int KIND_TYPED_BUFFER = 10;
    private const int KIND_RAW_BUFFER = 11;
    private const int KIND_STRUCTURED_BUFFER = 12;
    // dxil_spv_vulkan_descriptor_type
    private const int DESC_IDENTITY = 0;
    private const int DESC_SSBO = 1;

    /// <summary>
    /// Convert a DXIL container (DXBC archive holding a DXIL chunk) or raw LLVM bitcode to SPIR-V.
    /// </summary>
    /// <param name="dxil">DXIL/DXBC bytes (when <paramref name="rawLlvm"/> is false) or raw LLVM bitcode.</param>
    /// <param name="rawLlvm">true → <c>dxil_spv_parse_dxil</c> (raw <c>BC\xC0\xDE</c> bitcode);
    /// false → <c>dxil_spv_parse_dxil_blob</c> (a DXBC/DXContainer-wrapped DXIL chunk).</param>
    public static byte[]? Convert(ReadOnlySpan<byte> dxil, bool rawLlvm, out string? error)
    {
        error = null;
        if (dxil.Length < 4) { error = "DXIL input too small."; return null; }

        // Required by the API: every dxil_spv_* call on this thread must run inside an allocator
        // context. Per-call begin/end keeps native scratch memory bounded across a long batch.
        dxil_spv_begin_thread_allocator_context();
        IntPtr blob = IntPtr.Zero, converter = IntPtr.Zero;
        try
        {
            int r;
            fixed (byte* p = dxil)
            {
                r = rawLlvm
                    ? dxil_spv_parse_dxil(p, (nuint)dxil.Length, out blob)
                    : dxil_spv_parse_dxil_blob(p, (nuint)dxil.Length, out blob);
            }
            if (r != 0 || blob == IntPtr.Zero) { error = $"dxil_spv_parse_dxil{(rawLlvm ? "" : "_blob")} failed ({r})."; return null; }

            if (dxil_spv_create_converter(blob, out converter) != 0 || converter == IntPtr.Zero)
            { error = "dxil_spv_create_converter failed."; return null; }

            // --ssbo-uav --ssbo-srv: route structured/raw SRV & UAV buffers through SSBO storage
            // (fixes "raw 64-bit load-store must be SSBO/UBO/BDA"). Textures keep IDENTITY.
            dxil_spv_converter_set_srv_remapper(converter, &RemapSrv, null);
            dxil_spv_converter_set_uav_remapper(converter, &RemapUav, null);

            if (dxil_spv_converter_run(converter) != 0)
            { error = "dxil_spv_converter_run failed."; return null; }

            if (dxil_spv_converter_get_compiled_spirv(converter, out CompiledSpirv compiled) != 0 ||
                compiled.Data == IntPtr.Zero || compiled.Size == 0)
            { error = "dxil_spv_converter_get_compiled_spirv returned no data."; return null; }

            var result = new byte[(int)compiled.Size];
            Marshal.Copy(compiled.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (converter != IntPtr.Zero) dxil_spv_converter_free(converter);
            if (blob != IntPtr.Zero) dxil_spv_parsed_blob_free(blob);
            dxil_spv_end_thread_allocator_context();
        }
    }

    // === Resource remappers — exact reproduction of dxil-spirv's CLI remap_srv/remap_uav for
    // the (bindless=false, root_descriptors=empty, ssbo_uav=ssbo_srv=true) configuration. ===

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int RemapSrv(void* userdata, D3DBinding* binding, SrvVulkanBinding* vk)
    {
        *vk = default;
        if (IsGlobalHeap(binding))
        {
            vk->Buffer.UseHeap = 1;
            vk->Buffer.Set = 0;
            vk->Buffer.Binding = 0;
        }
        else
        {
            vk->Buffer.UseHeap = 0;
            vk->Buffer.Set = binding->RegisterSpace;
            vk->Buffer.Binding = binding->RegisterIndex;
        }

        if (binding->Kind == KIND_STRUCTURED_BUFFER || binding->Kind == KIND_RAW_BUFFER)
            vk->Buffer.DescriptorType = DESC_SSBO;

        vk->Offset.Set = 15;
        vk->Offset.Binding = 0;
        return 1; // DXIL_SPV_TRUE
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int RemapUav(void* userdata, UavD3DBinding* binding, UavVulkanBinding* vk)
    {
        *vk = default;
        D3DBinding* d3d = &binding->D3D;
        if (IsGlobalHeap(d3d))
        {
            vk->Buffer.UseHeap = 1;
            vk->Buffer.Set = 0;
            vk->Buffer.Binding = 0;
        }
        else
        {
            vk->Buffer.UseHeap = 0;
            vk->Buffer.Set = d3d->RegisterSpace;
            vk->Buffer.Binding = d3d->RegisterIndex;
        }

        if (d3d->Kind == KIND_STRUCTURED_BUFFER || d3d->Kind == KIND_RAW_BUFFER)
            vk->Buffer.DescriptorType = DESC_SSBO;

        vk->Offset.Set = 15;
        vk->Offset.Binding = 0;

        if ((binding->HasCounter & 0xFF) != 0)
        {
            vk->Counter.UseHeap = 0;
            vk->Counter.Set = 7;
            vk->Counter.Binding = d3d->ResourceIndex;
        }
        return 1; // DXIL_SPV_TRUE
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsGlobalHeap(D3DBinding* b)
        => b->RegisterIndex == uint.MaxValue && b->RegisterSpace == uint.MaxValue && b->RangeSize == uint.MaxValue;

    // === Blittable struct mirrors of dxil_spirv_c.h (LayoutKind.Sequential matches the C ABI;
    // dxil_spv_bool modelled as int — its 4-byte slot/padding makes every later field offset
    // identical whether the C typedef is 1 or 4 bytes). ===

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DBinding   // dxil_spv_d3d_binding (28 bytes)
    {
        public int Stage;
        public int Kind;
        public uint ResourceIndex;
        public uint RegisterSpace;
        public uint RegisterIndex;
        public uint RangeSize;
        public uint Alignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VulkanBinding   // dxil_spv_vulkan_binding (24 bytes)
    {
        public uint Set;
        public uint Binding;
        public uint RootOrInputAttachmentIndex;   // union { root_constant_index; input_attachment_index; }
        public uint HeapRootOffset;                // bindless.heap_root_offset
        public int UseHeap;                        // bindless.use_heap (dxil_spv_bool)
        public int DescriptorType;                 // dxil_spv_vulkan_descriptor_type
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SrvVulkanBinding   // dxil_spv_srv_vulkan_binding (48 bytes)
    {
        public VulkanBinding Buffer;
        public VulkanBinding Offset;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UavD3DBinding   // dxil_spv_uav_d3d_binding (32 bytes)
    {
        public D3DBinding D3D;
        public int HasCounter;     // dxil_spv_bool
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UavVulkanBinding   // dxil_spv_uav_vulkan_binding (72 bytes)
    {
        public VulkanBinding Buffer;
        public VulkanBinding Counter;
        public VulkanBinding Offset;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompiledSpirv   // dxil_spv_compiled_spirv
    {
        public IntPtr Data;
        public nuint Size;
    }

    // === P/Invoke (dxil_spirv_c.h). ===
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void dxil_spv_begin_thread_allocator_context();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void dxil_spv_end_thread_allocator_context();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int dxil_spv_parse_dxil_blob(byte* data, nuint size, out IntPtr blob);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int dxil_spv_parse_dxil(byte* data, nuint size, out IntPtr blob);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int dxil_spv_create_converter(IntPtr blob, out IntPtr converter);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void dxil_spv_converter_set_srv_remapper(
        IntPtr converter, delegate* unmanaged[Cdecl]<void*, D3DBinding*, SrvVulkanBinding*, int> remapper, void* userdata);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void dxil_spv_converter_set_uav_remapper(
        IntPtr converter, delegate* unmanaged[Cdecl]<void*, UavD3DBinding*, UavVulkanBinding*, int> remapper, void* userdata);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int dxil_spv_converter_run(IntPtr converter);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int dxil_spv_converter_get_compiled_spirv(IntPtr converter, out CompiledSpirv compiled);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void dxil_spv_converter_free(IntPtr converter);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void dxil_spv_parsed_blob_free(IntPtr blob);
}
