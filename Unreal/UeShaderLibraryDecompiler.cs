using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Ruri.ShaderTools.Engine;

namespace Ruri.ShaderTools.Unreal;

// Public, in-process API for decompiling a UE serialized shader library
// (.ushaderlib) to a directory of HLSL (or GLSL) source files plus
// per-shader metadata sidecars. Mirrors what Program.cs's
// `ProcessUnrealLibrary` did when invoked from the CLI, so FModelHook can
// call it directly after exporting a .ushaderlib instead of shelling out
// to Ruri.ShaderDecompiler.exe.
public static class UeShaderLibraryDecompiler
{
    public sealed class Options
    {
        public string LibraryPath { get; init; } = string.Empty;
        public string OutputDirectory { get; init; } = string.Empty;
        public string? UnifiedMetadataPath { get; init; }
        public string? MaterialFilter { get; init; }
        public IReadOnlyCollection<int>? ShaderIndexFilter { get; init; }
        public uint ShaderModel { get; init; } = 51;
        // false → keep an existing output dir (incremental runs from FModelHook
        // dispatching one library at a time); true → wipe & recreate (CLI batch).
        public bool RecreateOutputDirectory { get; init; } = true;
        // Default-on: every per-shader Decompile failure dumps its inputs/
        // intermediates/error under `<OutputDirectory>/_failures/<stem>/`,
        // letting users diff pre-rewrite vs post-rewrite vs post-patch
        // SPIR-V offline.
        public bool DumpFailures { get; init; } = true;
        public Action<string>? Log { get; init; }
        public Action<string>? LogError { get; init; }
    }

    public sealed record Summary(int TotalShaders, int Decompiled, int Skipped, int Failed);

    public static Summary Decompile(Options options)
    {
        if (string.IsNullOrWhiteSpace(options.LibraryPath))
            throw new ArgumentException("LibraryPath is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.OutputDirectory))
            throw new ArgumentException("OutputDirectory is required.", nameof(options));
        if (!File.Exists(options.LibraryPath))
            throw new FileNotFoundException("UE shader library not found.", options.LibraryPath);

        Action<string> log = options.Log ?? (_ => { });
        Action<string> logError = options.LogError ?? (_ => { });

        string? normalizedMaterialFilter = string.IsNullOrWhiteSpace(options.MaterialFilter)
            ? null
            : options.MaterialFilter!.Replace('\\', '/');
        HashSet<string>? materialFilterVariants = BuildMaterialPathVariants(normalizedMaterialFilter);

        var lib = UnrealShaderLibraryReader.Read(options.LibraryPath);
        log($"Read library: v{lib.Version}, {lib.ShaderEntries.Length} shaders ({Path.GetFileName(options.LibraryPath)}).");

        string mappingPath = options.UnifiedMetadataPath ?? string.Empty;
        UeShaderLibraryAssetIndex assetIndex = UeShaderLibraryAssetIndexReader.Read(
            options.LibraryPath, lib, mappingPath, options.MaterialFilter);
        Dictionary<int, HashSet<string>> usageMap = assetIndex.UsageByShaderIndex;

        var nameMap = new Dictionary<int, string>();
        foreach ((int shaderIndex, string displayName) in assetIndex.DisplayNameByShaderIndex)
        {
            nameMap[shaderIndex] = displayName;
        }

        UeShaderSymbolReader? materialSymbolExtractor = null;
        UeUnifiedMaterialReader? unifiedMaterialReader = null;
        if (!string.IsNullOrEmpty(mappingPath))
        {
            string exportRoot = Path.GetDirectoryName(mappingPath) ?? string.Empty;
            if (Directory.Exists(exportRoot))
            {
                materialSymbolExtractor = new UeShaderSymbolReader(exportRoot);
            }
            if (File.Exists(mappingPath))
            {
                unifiedMaterialReader = UeUnifiedMaterialReader.LoadFromFile(mappingPath);
            }
        }

        string outputDir = Path.GetFullPath(options.OutputDirectory);
        bool filteredRun = (options.ShaderIndexFilter is { Count: > 0 }) || (materialFilterVariants is { Count: > 0 });
        if (options.RecreateOutputDirectory && !filteredRun)
        {
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
        }
        Directory.CreateDirectory(outputDir);

        using var decompiler = new ShaderDecompiler(outputDir);
        int decompiled = 0;
        int skipped = 0;
        int failed = 0;
        int unknownNameCounter = 0;
        string failuresRoot = Path.Combine(outputDir, "_failures");

        for (int i = 0; i < lib.ShaderEntries.Length; i++)
        {
            if (options.ShaderIndexFilter is { Count: > 0 } && !options.ShaderIndexFilter.Contains(i))
            {
                skipped++;
                continue;
            }

            byte[]? code = lib.GetShaderCode(i);
            if (code == null)
            {
                skipped++;
                continue;
            }

            if (materialFilterVariants is { Count: > 0 })
            {
                if (!usageMap.TryGetValue(i, out HashSet<string>? filteredUsage) ||
                    !filteredUsage.Any(m => MaterialPathMatchesFilter(m, materialFilterVariants)))
                {
                    skipped++;
                    continue;
                }
            }

            UnrealShaderLibraryReader.FShaderCodeEntry entry = lib.ShaderEntries[i];
            string typeSuffix = GetShaderFreqString(entry.Frequency);

            // Provisional dump stem — used only for the failure folder so we
            // don't burn a duplicate `UnrealShaderParser.Parse` call up front
            // just to look up the embedded name. The real output filename is
            // computed AFTER the first decompile from `res.ShaderName`,
            // matching the original (single-parse) flow.
            string provisionalStem = nameMap.TryGetValue(i, out string? preMapped) && !string.IsNullOrWhiteSpace(preMapped)
                ? string.Join("_", preMapped.Split(Path.GetInvalidFileNameChars())) + $"_{typeSuffix}_{i}"
                : $"shader_{typeSuffix}_{i:D6}";
            string failureDumpDir = Path.Combine(failuresRoot, provisionalStem);

            DecompileOptions BuildOptions(ShaderSymbolData? meta, string stage)
                => new()
                {
                    Format = ShaderArchitecture.Unknown,
                    Metadata = meta,
                    ShaderModel = options.ShaderModel,
                    DebugDumpDirectory = options.DumpFailures ? failureDumpDir : null,
                    DebugDumpStem = options.DumpFailures ? stage : null,
                };

            try
            {
                DecompileResult res = decompiler.Decompile(code, BuildOptions(null, "01-no-symbols"));
                if (!res.Success)
                {
                    failed++;
                    LogFailure(logError, i, provisionalStem, res, "no-symbols");
                    continue;
                }

                // Resolve the on-disk name. UE strips per-shader names, so
                // the only useful name source is "which material(s) use this
                // shader" — pulled from the unified metadata or the per-
                // library sidecar:
                //   1. nameMap[i] — display name pre-derived from unified
                //      metadata (first-material's filename component).
                //   2. First entry in usageMap[i] — material path resolved
                //      via sidecar resolution; pick the first one.
                //   3. res.ShaderName — almost always missing for cooked UE
                //      shaders, but cheap to check and occasionally set.
                //   4. UnknownShader{counter} — last resort, per-run counter
                //      to keep dump folders / listings visually distinct.
                string finalName;
                if (nameMap.TryGetValue(i, out string? mapped) && !string.IsNullOrWhiteSpace(mapped))
                {
                    finalName = mapped;
                }
                else if (usageMap.TryGetValue(i, out HashSet<string>? materials) && materials.Count > 0)
                {
                    string firstMaterial = materials.OrderBy(static m => m, StringComparer.OrdinalIgnoreCase).First();
                    string materialName = Path.GetFileNameWithoutExtension(firstMaterial);
                    finalName = !string.IsNullOrWhiteSpace(materialName) ? materialName : $"UnknownShader{unknownNameCounter++:D6}";
                }
                else if (!string.IsNullOrWhiteSpace(res.ShaderName))
                {
                    finalName = res.ShaderName!;
                }
                else
                {
                    finalName = $"UnknownShader{unknownNameCounter++:D6}";
                }
                finalName = string.Join("_", finalName.Split(Path.GetInvalidFileNameChars()));
                string outNameStemNoExt = $"{finalName}_{typeSuffix}_{i}";

                string sourceExtension = string.IsNullOrWhiteSpace(res.SourceFileExtension) ? ".hlsl" : res.SourceFileExtension;
                string outName = outNameStemNoExt + sourceExtension;

                if (usageMap.TryGetValue(i, out HashSet<string>? usedBy))
                {
                    string shaderPlatformForShader = entry.Frequency switch
                    {
                        0 or 1 or 2 or 3 or 4 or 5 => "SP_PCD3D_SM5",
                        _ => string.Empty,
                    };

                    UeShaderSymbolSource? bestMaterialInfo = null;
                    foreach (string material in usedBy)
                    {
                        if (bestMaterialInfo == null && unifiedMaterialReader != null)
                        {
                            bestMaterialInfo = unifiedMaterialReader.GetSource(material, shaderPlatformForShader);
                        }
                        if (bestMaterialInfo == null && materialSymbolExtractor != null)
                        {
                            bestMaterialInfo = materialSymbolExtractor.GetSource(material, shaderPlatformForShader);
                        }
                    }

                    if (bestMaterialInfo != null)
                    {
                        // Defensive copy: the cached Metadata is shared across every shader
                        // that uses this material. EnrichSymbolData and the texture-sampler-pair
                        // inferrer mutate it in place; without a clone, additions accumulate
                        // and spirv-cross dedupes them with `_1`/`_2` suffixes on subsequent
                        // shaders.
                        ShaderSymbolData injectionSymbols = CloneShaderSymbolData(bestMaterialInfo.Metadata);

                        if (bestMaterialInfo.MaterialLayout != null)
                        {
                            try
                            {
                                UnrealShaderParser.Parse(code, out _, out UnrealShaderParser.UnrealMetadata? unrealMetadata);
                                UeShaderResourceTableSymbolizer.EnrichSymbolData(injectionSymbols, unrealMetadata, bestMaterialInfo.MaterialLayout);
                            }
                            catch (Exception ex)
                            {
                                logError($"Shader {i} ({outNameStemNoExt}): SRT enrichment failed: {ex.Message}");
                            }
                        }

                        try
                        {
                            DecompileResult resWithSymbols = decompiler.Decompile(code, BuildOptions(injectionSymbols, "02-with-symbols"));
                            if (resWithSymbols.Success)
                            {
                                res = resWithSymbols;
                            }
                            else
                            {
                                LogFailure(logError, i, outNameStemNoExt, resWithSymbols, "with-symbols");
                            }
                        }
                        catch (Exception ex)
                        {
                            logError($"Shader {i} ({outNameStemNoExt}): symbol-injected re-decompile failed: {ex.Message}");
                        }
                    }

                    if (res.FinalMetadata != null)
                    {
                        res.FinalMetadata.UsedMaterials = usedBy
                            .OrderBy(static material => material, StringComparer.OrdinalIgnoreCase)
                            .ToList();
                    }
                }

                string outputFilePath = Path.Combine(outputDir, outName);
                string basePath = Path.Combine(outputDir, outNameStemNoExt);
                File.WriteAllText(outputFilePath, res.SourceCode ?? string.Empty);

                if (res.FinalMetadata != null)
                {
                    File.WriteAllText(basePath + ".metadata.json", JsonConvert.SerializeObject(res.FinalMetadata, Formatting.Indented));
                }

                decompiled++;
            }
            catch (Exception ex)
            {
                failed++;
                logError($"Shader {i} ({provisionalStem}): exception: {ex.Message}");
            }
        }

        log($"Library {Path.GetFileName(options.LibraryPath)}: total={lib.ShaderEntries.Length} decompiled={decompiled} skipped={skipped} failed={failed}.");
        return new Summary(lib.ShaderEntries.Length, decompiled, skipped, failed);
    }

    private static void LogFailure(Action<string> logError, int shaderIndex, string stem, DecompileResult res, string label)
    {
        string firstLine = res.ErrorMessage?.Split('\n', 2, StringSplitOptions.None)[0]?.Trim() ?? "<no message>";
        string dumpHint = string.IsNullOrEmpty(res.DebugDumpDirectory)
            ? string.Empty
            : $" (dumped: {res.DebugDumpDirectory})";
        logError($"Shader {shaderIndex} ({stem}) [{label}, stage={res.FailedStage ?? "unknown"}]: {firstLine}{dumpHint}");
    }

    private static ShaderSymbolData CloneShaderSymbolData(ShaderSymbolData source)
    {
        return new ShaderSymbolData
        {
            ConstantBuffers = new List<ConstantBuffer>(source.ConstantBuffers),
            ConstantBufferBindings = new List<BufferBinding>(source.ConstantBufferBindings),
            TextureParameters = new List<TextureParameter>(source.TextureParameters),
            Samplers = new List<SamplerParameter>(source.Samplers),
            UAVs = new List<UAVParameter>(source.UAVs),
            EntryPoint = source.EntryPoint,
            DebugName = source.DebugName,
            UsedMaterials = new List<string>(source.UsedMaterials),
        };
    }

    private static string GetShaderFreqString(byte frequency)
        => frequency switch
        {
            0 => "VS",
            1 => "HS",
            2 => "DS",
            3 => "PS",
            4 => "GS",
            5 => "CS",
            6 => "RG",
            7 => "RM",
            8 => "RH",
            9 => "RC",
            10 => "MS",
            11 => "AS",
            _ => $"Freq{frequency}",
        };

    private static bool MaterialPathMatchesFilter(string materialPath, HashSet<string> filterVariants)
    {
        HashSet<string> materialVariants = BuildMaterialPathVariants(materialPath)!;
        return materialVariants.Overlaps(filterVariants);
    }

    private static HashSet<string>? BuildMaterialPathVariants(string? materialPath)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(materialPath)) return result;

        string normalized = materialPath.Replace('\\', '/');
        result.Add(normalized);
        if (normalized.StartsWith("/", StringComparison.Ordinal)) result.Add(normalized[1..]);
        else result.Add("/" + normalized);

        int dotIndex = normalized.LastIndexOf('.');
        int slashIndex = normalized.LastIndexOf('/');
        if (dotIndex > slashIndex) result.Add(normalized[..dotIndex]);

        foreach (string current in result.ToArray())
        {
            int contentMarkerIndex = current.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
            if (contentMarkerIndex >= 0)
            {
                string trimmed = current[(contentMarkerIndex + "/Content/".Length)..];
                result.Add(trimmed);
                result.Add("/" + trimmed);
            }
            else if (current.StartsWith("Content/", StringComparison.OrdinalIgnoreCase))
            {
                string trimmed = current["Content/".Length..];
                result.Add(trimmed);
                result.Add("/" + trimmed);
            }
        }

        return result;
    }
}
