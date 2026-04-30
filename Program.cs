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
// `--metadata` optional JSON file matching `ShaderSymbolData`. When the
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

        string inputPath = Path.GetFullPath(args[0]);
        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"Error: input file not found: {inputPath}");
            return 1;
        }

        string? outputPath = null;
        string? metadataPath = null;
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

        ShaderSymbolData? symbols = LoadSymbols(inputPath, metadataPath);

        try
        {
            byte[] binary = File.ReadAllBytes(inputPath);
            using var decompiler = new ShaderDecompiler();
            DecompileResult result = decompiler.Decompile(binary, format, symbols, shaderModel);

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

    private static ShaderSymbolData? LoadSymbols(string inputPath, string? explicitMetadataPath)
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
        ShaderSymbolData? symbols = JsonConvert.DeserializeObject<ShaderSymbolData>(json);
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
        Console.WriteLine("  ShaderDecompiler.exe <input> [output] [--metadata <path>] [--format dxbc|dxil|spv|auto] [--shader-model 50]");
        Console.WriteLine();
        Console.WriteLine("  <input>          shader binary (DXBC, DXIL, or SPIR-V).");
        Console.WriteLine("  [output]         output path. Defaults to <input>.hlsl/.glsl. Use '-' for stdout.");
        Console.WriteLine("  --metadata       optional ShaderSymbolData JSON. Auto-loaded from '<input>.metadata.json' if present.");
        Console.WriteLine("  --format         override format detection (default: auto-detect from magic bytes).");
        Console.WriteLine("  --shader-model   spirv-cross HLSL shader model (default: 50).");
        Console.WriteLine();
        Console.WriteLine("UE .ushaderlib batch decompile is now done in-process by Ruri.FModelHook.");
        Console.WriteLine("Unity shader decompile is done in-process by Ruri.RipperHook (AssetRipper).");
    }
}
