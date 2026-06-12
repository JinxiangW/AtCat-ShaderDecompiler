using System.Reflection;
using System.Runtime.InteropServices;

namespace Ruri.ShaderTools.Native;

/// <summary>
/// Resolves the in-process native libraries the decompiler P/Invokes into. There are no
/// child processes and no disk round-trips any more — every stage runs in-process:
/// <list type="bullet">
///   <item><c>spirv-cross.dll</c> — supplied by the <c>Silk.NET.SPIRV.Cross.Native</c>
///   NuGet package (kept current via the package), restored under
///   <c>runtimes/win-x64/native</c>.</item>
///   <item><c>dxil-spirv-c-shared.dll</c> — DXIL→SPIR-V (Hans-Kristian Arntzen's dxil-spirv).
///   No NuGet distribution exists upstream, so the shared library ships under <c>Tools/</c>
///   and is loaded in-process here.</item>
///   <item><c>dxilconv.dll</c> — Microsoft's DXBC→DXIL converter, ships under <c>Tools/</c>.</item>
/// </list>
/// A single <see cref="NativeLibrary.SetDllImportResolver"/> hook probes the package's
/// runtimes folder and the Tools folder, loading each library by full path. On Windows a
/// rooted-path load uses <c>LOAD_WITH_ALTERED_SEARCH_PATH</c>, so a library's own transitive
/// dependencies (e.g. <c>dxilconv.dll</c> → <c>dxil.dll</c>) resolve from the same directory.
/// </summary>
internal static class NativeToolsLoader
{
    private static int _initialized;
    private static string[] _searchDirs = Array.Empty<string>();

    /// <summary>Registers the resolver once per process. Safe to call repeatedly / concurrently.</summary>
    public static void EnsureInitialized(string? toolsDir)
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;

        string baseDir = AppContext.BaseDirectory;
        var dirs = new List<string>();
        void Add(string? d)
        {
            if (!string.IsNullOrWhiteSpace(d) && Directory.Exists(d) &&
                !dirs.Contains(d, StringComparer.OrdinalIgnoreCase))
                dirs.Add(d!);
        }

        Add(toolsDir);
        Add(Path.Combine(baseDir, "Tools"));
        Add(Path.Combine(baseDir, "runtimes", "win-x64", "native"));
        Add(baseDir);
        _searchDirs = dirs.ToArray();

        NativeLibrary.SetDllImportResolver(typeof(NativeToolsLoader).Assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        string fileName = libraryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? libraryName
            : libraryName + ".dll";

        foreach (string dir in _searchDirs)
        {
            string full = Path.Combine(dir, fileName);
            if (File.Exists(full) && NativeLibrary.TryLoad(full, out IntPtr handle))
                return handle;
        }

        // Not one of ours (or not found here) — let the default resolver try.
        return IntPtr.Zero;
    }
}
