using System;
using System.IO;
using Newtonsoft.Json;

namespace Ruri.ShaderTools;

// Minimal CLI wrapper around `ShaderDecompiler`. Takes a single shader
// binary (DXBC / DXIL / SPIR-V) plus an optional metadata sidecar and
// writes the decompiled HLSL/GLSL next to it. The previous selftest /
// .ushaderlib batch / preshader / spirv-image / litpoly / unitybinary
// scaffolding has been retired — UE library decompilation now runs
// in-process from FModelHook via `UeShaderLibraryDecompiler`, and the
// Unity path runs from `ShaderRuriDecompileExporter`.
//
// Usage:
//   ShaderDecompiler.exe <input> [output] [--metadata <path>]
//                                         [--format dxbc|dxil|spv|auto]
//                                         [--shader-model 50]
//
// `<input>`   binary shader file. Format is auto-detected from the magic
//             bytes when --format is omitted or "auto".
// `[output]`  output file. Defaults to "<input>.hlsl" / ".glsl" depending
//             on what spirv-cross emits. Use "-" to write to stdout.
// `--metadata` optional JSON file matching `SerializedProgramData`. When the
//             flag is omitted, "<input>.metadata.json" is loaded if present.
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        // Batch mode: `--batch <dir>` scans <dir>/*/with-symbols.input.bin (the layout the
        // failure-dumper uses) and decompiles them all through the parallel pool in one
        // shot. Useful for re-running an entire failure set after a fix without scripting
        // a per-blob loop. Optional `--max-concurrency N` overrides the default cap.
        if (args[0] == "--batch")
        {
            return RunBatch(args);
        }

        string inputPath = Path.GetFullPath(args[0]);
        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"Error: input file not found: {inputPath}");
            return 1;
        }

        string? outputPath = null;
        string? metadataPath = null;
        string? debugDumpDir = null;
        ShaderArchitecture format = ShaderArchitecture.Unknown;
        uint shaderModel = 50;

        for (int i = 1; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                switch (arg)
                {
                    case "--metadata" when i + 1 < args.Length:
                        metadataPath = args[++i];
                        break;
                    case "--format" when i + 1 < args.Length:
                        format = ParseFormat(args[++i]);
                        break;
                    case "--shader-model" when i + 1 < args.Length && uint.TryParse(args[++i], out uint sm):
                        shaderModel = sm;
                        break;
                    case "--debug-dump" when i + 1 < args.Length:
                        debugDumpDir = args[++i];
                        break;
                    default:
                        Console.Error.WriteLine($"Error: unknown option: {arg}");
                        PrintUsage();
                        return 1;
                }
            }
            else if (outputPath == null)
            {
                outputPath = arg;
            }
            else
            {
                Console.Error.WriteLine($"Error: unexpected positional argument: {arg}");
                PrintUsage();
                return 1;
            }
        }

        SerializedProgramData? symbols = LoadSymbols(inputPath, metadataPath);

        try
        {
            byte[] binary = File.ReadAllBytes(inputPath);
            using var decompiler = new ShaderDecompiler();
            DecompileOptions options = new()
            {
                Format = format,
                Metadata = symbols,
                ShaderModel = shaderModel,
                DebugDumpDirectory = debugDumpDir,
                DebugDumpStem = debugDumpDir is null ? null : Path.GetFileNameWithoutExtension(inputPath),
            };
            DecompileResult result = decompiler.Decompile(binary, options);

            if (debugDumpDir is not null) DumpIntermediates(debugDumpDir, inputPath, result, symbols);

            if (!result.Success)
            {
                Console.Error.WriteLine($"Decompilation failed: {result.ErrorMessage}");
                return 1;
            }

            string source = result.SourceCode ?? string.Empty;
            string extension = string.IsNullOrWhiteSpace(result.SourceFileExtension) ? ".hlsl" : result.SourceFileExtension;

            if (outputPath == "-")
            {
                Console.Out.Write(source);
                return 0;
            }

            string finalOutput = outputPath != null
                ? Path.GetFullPath(outputPath)
                : Path.ChangeExtension(inputPath, extension);

            string? parent = Path.GetDirectoryName(finalOutput);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            File.WriteAllText(finalOutput, source);
            Console.WriteLine($"Wrote {finalOutput}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal error: {ex.Message}");
            return 1;
        }
    }

    private static int RunBatch(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: --batch <dir> [--max-concurrency N]");
            return 1;
        }

        string root = Path.GetFullPath(args[1]);
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"Error: directory not found: {root}");
            return 1;
        }

        int maxConcurrency = 0;
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--max-concurrency" && i + 1 < args.Length && int.TryParse(args[++i], out int mc))
            {
                maxConcurrency = mc;
            }
        }

        // Discover (binary, metadata) pairs. Two layouts are accepted:
        //   1. Failure-dump format: <root>/<subdir>/{with-symbols|no-symbols}.input.bin + matching .metadata.json
        //      (Pass 180 picks the stem based on whether the material symbol resolver
        //       returned a hit for the shader; both forms share the same on-disk shape.)
        //   2. Flat format: <root>/*.bin (+ optional <name>.bin.metadata.json or <name>.metadata.json)
        // Mode 1 is what `--debug-dump` / the FModelHook failure dumper produce; mode 2 is what
        // the user gets from a custom export. We also try the repaired-output extensions
        // (.hlsl / .glsl) to skip subdirs that have already been resolved on a previous run.
        var inputs = new List<(string Stem, string BinPath, string? MetaPath)>();
        string[] failureStems = { "with-symbols", "no-symbols" };
        foreach (string subdir in Directory.GetDirectories(root))
        {
            foreach (string stem in failureStems)
            {
                string bin = Path.Combine(subdir, stem + ".input.bin");
                if (!File.Exists(bin)) continue;
                // Skip if a successful repair output already sits next to the
                // input — re-running batch over an already-fixed _failures
                // tree should be a no-op, not a re-decompile.
                if (File.Exists(bin + ".hlsl") || File.Exists(bin + ".glsl")) break;
                string meta = Path.Combine(subdir, stem + ".metadata.json");
                inputs.Add((Path.GetFileName(subdir), bin, File.Exists(meta) ? meta : null));
                break; // one stem per subdir is enough
            }
        }
        if (inputs.Count == 0)
        {
            foreach (string bin in Directory.GetFiles(root, "*.bin"))
            {
                string? meta = bin + ".metadata.json";
                if (!File.Exists(meta)) meta = Path.ChangeExtension(bin, ".metadata.json");
                inputs.Add((Path.GetFileNameWithoutExtension(bin), bin, File.Exists(meta) ? meta : null));
            }
        }

        if (inputs.Count == 0)
        {
            Console.Error.WriteLine($"No inputs found under {root}.");
            return 1;
        }

        // Build the request list. Keep the loaded metadata alongside so worker callbacks
        // can still write per-job outputs without reloading.
        var requests = new (byte[] Binary, DecompileOptions Options)[inputs.Count];
        for (int i = 0; i < inputs.Count; i++)
        {
            var (stem, binPath, metaPath) = inputs[i];
            SerializedProgramData? symbols = metaPath is null ? null : LoadSymbols(binPath, metaPath);
            requests[i] = (File.ReadAllBytes(binPath), new DecompileOptions
            {
                Format = ShaderArchitecture.Unknown,
                Metadata = symbols,
                ShaderModel = 51,
            });
        }

        Console.WriteLine($"Batch: {inputs.Count} jobs (max concurrency = {(maxConcurrency > 0 ? maxConcurrency : "auto")})");

        int ok = 0, fail = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using ShaderDecompiler decompiler = new();
        DecompileResult[] results = decompiler.Decompile(requests, (idx, r) =>
        {
            if (r.Success) System.Threading.Interlocked.Increment(ref ok);
            else System.Threading.Interlocked.Increment(ref fail);
        }, maxConcurrency: maxConcurrency);
        sw.Stop();

        // Write outputs next to the input bin files. The HLSL/GLSL extension comes back in
        // the result; default to .hlsl when the result didn't carry one (failure case).
        for (int i = 0; i < requests.Length; i++)
        {
            DecompileResult r = results[i];
            if (!r.Success || string.IsNullOrEmpty(r.SourceCode)) continue;
            string ext = string.IsNullOrEmpty(r.SourceFileExtension) ? ".hlsl" : r.SourceFileExtension;
            File.WriteAllText(inputs[i].BinPath + ext, r.SourceCode);
        }

        Console.WriteLine($"Batch done: {ok} ok / {fail} fail in {sw.Elapsed.TotalSeconds:F1}s");
        return fail == 0 ? 0 : 2;
    }

    private static void DumpIntermediates(string dir, string inputPath, DecompileResult result, SerializedProgramData? symbols)
    {
        Directory.CreateDirectory(dir);
        string stem = Path.GetFileNameWithoutExtension(inputPath);
        string Path1(string suffix) => Path.Combine(dir, stem + suffix);
        if (result.PreRewriteSpirv  is { Length: > 0 } a) File.WriteAllBytes(Path1(".01.pre-rewrite.spv"),  a);
        if (result.PostRewriteSpirv is { Length: > 0 } b) File.WriteAllBytes(Path1(".02.post-rewrite.spv"), b);
        if (result.PostPatchSpirv   is { Length: > 0 } c) File.WriteAllBytes(Path1(".03.post-patch.spv"),   c);
        if (!string.IsNullOrWhiteSpace(result.StructuredRewriteSummary))
            File.WriteAllText(Path1(".rewrite-summary.txt"), result.StructuredRewriteSummary, new System.Text.UTF8Encoding(false));
        if (symbols is not null)
            File.WriteAllText(Path1(".metadata.input.json"), JsonConvert.SerializeObject(symbols, Formatting.Indented), new System.Text.UTF8Encoding(false));
        Console.WriteLine($"Debug-dump → {dir}");
    }

    private static SerializedProgramData? LoadSymbols(string inputPath, string? explicitMetadataPath)
    {
        string? path = explicitMetadataPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            string sidecar = inputPath + ".metadata.json";
            if (File.Exists(sidecar)) path = sidecar;
        }

        if (string.IsNullOrWhiteSpace(path)) return null;

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Metadata file not found: {fullPath}");

        string json = File.ReadAllText(fullPath);
        SerializedProgramData? symbols = JsonConvert.DeserializeObject<SerializedProgramData>(json);
        if (symbols == null)
            throw new InvalidOperationException($"Failed to deserialize metadata: {fullPath}");
        return symbols;
    }

    private static ShaderArchitecture ParseFormat(string mode)
        => mode.ToLowerInvariant() switch
        {
            "dxbc" => ShaderArchitecture.Dxbc,
            "dxil" => ShaderArchitecture.Dxil,
            "spv" or "spirv" => ShaderArchitecture.SpirV,
            "auto" or "unknown" => ShaderArchitecture.Unknown,
            _ => throw new ArgumentException($"Unknown format: {mode}. Use dxbc / dxil / spv / auto."),
        };

    private static bool IsHelp(string arg)
        => arg is "-h" or "--help" or "/?" or "/help";

    private static void PrintUsage()
    {
        Console.WriteLine("Ruri.ShaderDecompiler — single-binary CLI");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  ShaderDecompiler.exe <input> [output] [--metadata <path>] [--format dxbc|dxil|spv|auto] [--shader-model 50] [--debug-dump <dir>]");
        Console.WriteLine();
        Console.WriteLine("  <input>          shader binary (DXBC, DXIL, or SPIR-V).");
        Console.WriteLine("  [output]         output path. Defaults to <input>.hlsl/.glsl. Use '-' for stdout.");
        Console.WriteLine("  --metadata       optional SerializedProgramData JSON. Auto-loaded from '<input>.metadata.json' if present.");
        Console.WriteLine("  --format         override format detection (default: auto-detect from magic bytes).");
        Console.WriteLine("  --shader-model   spirv-cross HLSL shader model (default: 50).");
        Console.WriteLine("  --debug-dump     directory to dump pre-rewrite / post-rewrite / post-patch SPIR-V plus metadata.");
        Console.WriteLine();
        Console.WriteLine("UE .ushaderlib batch decompile is now done in-process by Ruri.FModelHook.");
        Console.WriteLine("Unity shader decompile is done in-process by Ruri.RipperHook (AssetRipper).");
    }
}
