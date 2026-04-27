using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Ruri.ShaderTools;
using Ruri.ShaderTools.Engine;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Ruri.ShaderTools.Spirv;
using Ruri.ShaderTools.Testing.Compilation;
using Ruri.ShaderTools.Unreal;

namespace Ruri.ShaderTools
{
    class Program
    {
        static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                return RunUnityBinaryDirectoryAuto(ResolveUnityBinaryAutoAssetRoot());
            }

            if (args.Length >= 1 && string.Equals(args[0], "--unitybinary-litpoly-selftest", StringComparison.OrdinalIgnoreCase))
            {
                string? outputDir = args.Length > 1 ? args[1] : null;
                return RunUnityBinaryLitPolySelfTest(outputDir);
            }

            if (args.Length >= 1 && string.Equals(args[0], "--unitybinary-session", StringComparison.OrdinalIgnoreCase))
            {
                return RunUnityBinarySession(args);
            }

            if (args.Length >= 1 && string.Equals(args[0], "--selftest", StringComparison.OrdinalIgnoreCase))
            {
                string outputRoot = args.Length > 1
                    ? Path.GetFullPath(args[1])
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SelfTestOutput");
                return RunSelfTest(outputRoot);
            }

            if (args.Length >= 1 && string.Equals(args[0], "--analyze-preshader", StringComparison.OrdinalIgnoreCase))
            {
                return RunAnalyzePreshader(args);
            }

            if (args.Length >= 1 && string.Equals(args[0], "--analyze-spirv-images", StringComparison.OrdinalIgnoreCase))
            {
                return RunAnalyzeSpirvImages(args);
            }

            if (args.Length >= 1 && string.Equals(args[0], "--auto-decompile", StringComparison.OrdinalIgnoreCase))
            {
                return RunAutoDecompile(args);
            }

            string inputPath = Path.GetFullPath(args[0]);

            if (TryRunUnityBinaryAuto(inputPath, args, out int unityBinaryExitCode))
            {
                return unityBinaryExitCode;
            }

            // For .ushaderlib inputs there is no legacy mode positional;
            // args[1] is the output directory directly. For other inputs
            // args[1] is the mode (dxbc/dxil/spv/-unknown) and args[2] is
            // the output path.
            bool isLibraryInput = inputPath.EndsWith(".ushaderlib", StringComparison.OrdinalIgnoreCase);
            string mode = !isLibraryInput && args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : "";
            string? outputPath = isLibraryInput
                ? (args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null)
                : (args.Length > 2 && !args[2].StartsWith("--", StringComparison.Ordinal) ? args[2] : null);
            bool keepTemps = false;
            string? mappingPath = null;
            string? symbolsPath = null;
            string? materialFilter = null;

            var nameMap = new Dictionary<int, string>();
            for (int i = 1; i < args.Length; i++)
            {
                if (i <= 2 && !args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    continue;
                }

                if (args[i] == "--keep-temps") keepTemps = true;
                else if (args[i] == "--mapping" && i + 1 < args.Length)
                {
                    mappingPath = args[i + 1];
                    i++; 
                }
                else if (args[i] == "--symbols" && i + 1 < args.Length)
                {
                    symbolsPath = args[i + 1];
                    i++;
                }
                else if (args[i] == "--material" && i + 1 < args.Length)
                {
                    materialFilter = args[i + 1];
                    i++;
                }
            }

            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine($"Error: Input file '{inputPath}' not found.");
                return 1;
            }

            // Handle .ushaderlib
            if (inputPath.EndsWith(".ushaderlib", StringComparison.OrdinalIgnoreCase))
            {
                return ProcessUnrealLibrary(inputPath, outputPath, keepTemps, mappingPath, nameMap, materialFilter);
            }

            // Legacy single file mode logic
            if(string.IsNullOrEmpty(mode))
            {
                 mode = "-unknown"; // Treat as unknown/auto
            }


            try
            {
                var format = ParseFormat(mode);
                var binary = File.ReadAllBytes(inputPath);
                var symbols = LoadShaderSymbols(inputPath, symbolsPath);
                
                using var decompiler = new ShaderDecompiler(); 
                
                var result = decompiler.Decompile(binary, format, symbols, 50);
                
                if (result.Success)
                {
                    string sourceText = result.SourceCode ?? result.HlslSource ?? string.Empty;
                    if (outputPath != null) 
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                        File.WriteAllText(outputPath, sourceText);
                    }
                    else Console.WriteLine(sourceText);
                    return 0;
                }
                else
                {
                     Console.Error.WriteLine($"Decompilation failed: {result.ErrorMessage}");
                     return 1;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Fatal Error: {ex.Message}");
                return 1;
            }
        }

        static bool TryRunUnityBinaryAuto(string inputPath, string[] args, out int exitCode)
        {
            exitCode = 0;

            if (Directory.Exists(inputPath))
            {
                exitCode = RunUnityBinaryDirectoryAuto(inputPath);
                return true;
            }

            if (!File.Exists(inputPath) || !IsUnityBinaryDxbcAssetPath(inputPath))
            {
                return false;
            }

            string metadataPath = GetUnityBinaryMetadataPath(inputPath);
            if (!File.Exists(metadataPath))
            {
                return false;
            }

            string outputDir = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)
                ? Path.GetFullPath(args[1])
                : GetUnityBinaryDefaultOutputDirectory(inputPath);

            exitCode = RunUnityBinarySession(new[] { "--unitybinary-session", inputPath, metadataPath, outputDir });
            return true;
        }

        static int RunUnityBinaryDirectoryAuto(string directoryPath)
        {
            string outputRoot = ResolveTestingShaderOutputRoot();
            string[] dxbcFiles = Directory.GetFiles(directoryPath, "*.dxbc.bin", SearchOption.AllDirectories)
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (dxbcFiles.Length == 0)
            {
                Console.Error.WriteLine($"Error: no UnityBinary DXBC assets found under {directoryPath}");
                return 1;
            }

            int failures = 0;
            foreach (string dxbcPath in dxbcFiles)
            {
                string metadataPath = GetUnityBinaryMetadataPath(dxbcPath);
                if (!File.Exists(metadataPath))
                {
                    continue;
                }

                string outputDir = GetUnityBinaryDefaultOutputDirectory(dxbcPath, directoryPath, outputRoot);
                int result = RunUnityBinarySession(new[] { "--unitybinary-session", dxbcPath, metadataPath, outputDir });
                if (result != 0)
                {
                    failures++;
                }
            }

            return failures == 0 ? 0 : 1;
        }

        static bool IsUnityBinaryDxbcAssetPath(string path)
        {
            return path.EndsWith(".dxbc.bin", StringComparison.OrdinalIgnoreCase)
                && path.IndexOf(".shader.sub", StringComparison.OrdinalIgnoreCase) >= 0
                && path.IndexOf(".pass", StringComparison.OrdinalIgnoreCase) >= 0
                && path.IndexOf(".blob", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static string GetUnityBinaryMetadataPath(string dxbcPath)
        {
            return dxbcPath[..^".dxbc.bin".Length] + ".metadata.json";
        }

        static string GetUnityBinaryDefaultOutputDirectory(string dxbcPath)
        {
            string parent = Path.GetDirectoryName(dxbcPath) ?? AppDomain.CurrentDomain.BaseDirectory;
            string baseName = Path.GetFileName(dxbcPath[..^".dxbc.bin".Length]);
            return Path.Combine(parent, baseName);
        }

        static string GetUnityBinaryDefaultOutputDirectory(string dxbcPath, string assetRoot, string outputRoot)
        {
            string baseName = Path.GetFileName(dxbcPath[..^".dxbc.bin".Length]);
            string parent = Path.GetDirectoryName(dxbcPath) ?? assetRoot;
            string relativeParent = Path.GetRelativePath(assetRoot, parent);

            if (string.IsNullOrEmpty(relativeParent) || string.Equals(relativeParent, ".", StringComparison.Ordinal))
            {
                return Path.Combine(outputRoot, baseName);
            }

            return Path.Combine(outputRoot, relativeParent, baseName);
        }

        static int ProcessUnrealLibrary(string inputPath, string? outputPath, bool keepTemps, string? mappingPath, Dictionary<int, string>? nameMapInput = null, string? materialFilter = null)
        {
            try 
            {
                var nameMap = nameMapInput ?? new Dictionary<int, string>();
                string? normalizedMaterialFilter = string.IsNullOrWhiteSpace(materialFilter)
                    ? null
                    : materialFilter.Replace('\\', '/');
                HashSet<string>? materialFilterVariants = BuildMaterialPathVariants(normalizedMaterialFilter);

                var lib = UnrealShaderLibraryReader.Read(inputPath);
                Console.WriteLine($"Read Library: {lib.Version} Version, {lib.ShaderEntries.Length} shaders.");

                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    Console.Error.WriteLine("Error: ushaderlib processing requires an explicit output directory argument.");
                    return 1;
                }

                if (string.IsNullOrWhiteSpace(mappingPath))
                {
                    Console.WriteLine("Note: --mapping not provided. Falling back to per-library .assetinfo.json/.stableinfo.json sidecars only.");
                }

                Console.WriteLine($"Mapping path: {(mappingPath ?? "<null>")}");
                if (!string.IsNullOrEmpty(mappingPath))
                {
                    Console.WriteLine($"Mapping exists: {File.Exists(mappingPath)}");
                }

                UeShaderLibraryAssetIndex assetIndex = UeShaderLibraryAssetIndexReader.Read(inputPath, lib, mappingPath ?? string.Empty, materialFilter);
                Dictionary<int, HashSet<string>> usageMap = assetIndex.UsageByShaderIndex;
                foreach ((int shaderIndex, string displayName) in assetIndex.DisplayNameByShaderIndex)
                {
                    if (!nameMap.ContainsKey(shaderIndex))
                    {
                        nameMap[shaderIndex] = displayName;
                    }
                }

                // 3. Load unified UE metadata.
                UnifiedShaderMetadataResolver? unifiedResolver = null;
                if (!string.IsNullOrEmpty(mappingPath) && File.Exists(mappingPath))
                {
                    Console.WriteLine($"Loading unified metadata from {mappingPath}...");
                    unifiedResolver = UnifiedShaderMetadataResolver.LoadFromFile(mappingPath);
                    if (unifiedResolver != null)
                    {
                        var hashToMats = unifiedResolver.BuildPackageShaderMapHashToMaterialsMap(normalizedMaterialFilter);
                        Console.WriteLine($"Loaded {hashToMats.Count} shader map hash entries.");
                    }
                }

                // 4. Load runtime UE material metadata resolver.
                UeShaderSymbolReader? materialSymbolExtractor = null;
                if (!string.IsNullOrEmpty(mappingPath))
                {
                    string exportRoot = Path.GetDirectoryName(mappingPath)!;
                    if (Directory.Exists(exportRoot))
                    {
                        materialSymbolExtractor = new UeShaderSymbolReader(exportRoot);
                    }
                }

                outputPath = Path.GetFullPath(outputPath);
                RecreateDirectory(outputPath);
                Console.WriteLine($"Session output: {outputPath}");

                using var decompiler = new ShaderDecompiler(outputPath);
                int successCount = 0;

                for(int i=0; i<lib.ShaderEntries.Length; i++)
                {
                    var code = lib.GetShaderCode(i);
                    var entry = lib.ShaderEntries[i];
                    Console.WriteLine($"Shader {i}: Size={entry.Size}, Uncompressed={entry.UncompressedSize}, Offset={entry.Offset}");
                    
                     if (code == null) continue;

                    if (materialFilterVariants is { Count: > 0 })
                    {
                        if (!usageMap.TryGetValue(i, out var filteredUsage) ||
                            !filteredUsage.Any(m => MaterialPathMatchesFilter(m, materialFilterVariants)))
                        {
                            continue;
                        }
                    }
                     
                    string typeSuffix = GetShaderFreqString(entry.Frequency);

                    try 
                    {
                        var res = decompiler.Decompile(code, ShaderArchitecture.Unknown, (ShaderSymbolData?)null, 50);
                        if (res.Success)
                        {
                            string finalName = "";
                            
                            // 1. Try Map
                            if (nameMap.ContainsKey(i)) finalName = nameMap[i];
                            // 2. Try embedded name
                            else if (!string.IsNullOrEmpty(res.ShaderName)) finalName = res.ShaderName;
                            else finalName = "UnknownShader";
                            
                            finalName = string.Join("_", finalName.Split(Path.GetInvalidFileNameChars()));
                            string sourceExtension = string.IsNullOrWhiteSpace(res.SourceFileExtension) ? ".hlsl" : res.SourceFileExtension;
                            string outName = $"{finalName}_{typeSuffix}_{i}{sourceExtension}";
                            ShaderSymbolData? injectionSymbols = null;
                            if (usageMap.TryGetValue(i, out var usedBy))
                            {
                                UeShaderSymbolSource? bestMaterialInfo = null;
                                string shaderPlatformForShader = entry.Frequency switch
                                {
                                    0 or 1 or 2 or 3 or 4 or 5 => "SP_PCD3D_SM5",
                                    _ => string.Empty
                                };

                                foreach (string material in usedBy)
                                {
                                    if (bestMaterialInfo == null && materialSymbolExtractor != null)
                                    {
                                        bestMaterialInfo = materialSymbolExtractor.GetSource(material, shaderPlatformForShader);
                                    }
                                }

                                if (bestMaterialInfo != null)
                                {
                                    injectionSymbols = bestMaterialInfo.Metadata;
                                }

                                if (injectionSymbols != null)
                                {
                                    if (bestMaterialInfo?.MaterialLayout != null)
                                    {
                                        try
                                        {
                                            UnrealShaderParser.Parse(code, out _, out UnrealShaderParser.UnrealMetadata? unrealMetadata);
                                            UeShaderResourceTableSymbolizer.EnrichSymbolData(injectionSymbols, unrealMetadata, bestMaterialInfo.MaterialLayout);
                                            injectionSymbols.RefreshCompatibilityViews();
                                        }
                                        catch (Exception ex)
                                        {
                                            Console.Error.WriteLine($"Material-aware SRT enrichment failed: {ex.Message}");
                                        }
                                    }

                                    try
                                    {
                                        DecompileResult resWithSymbols = decompiler.Decompile(code, ShaderArchitecture.Unknown, injectionSymbols, 50);
                                        if (resWithSymbols.Success)
                                        {
                                            res = resWithSymbols;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.Error.WriteLine($"Symbol-injected redecompile failed: {ex.Message}");
                                    }
                                }

                                if (res.FinalMetadata != null)
                                {
                                    res.FinalMetadata.UsedMaterials = usedBy
                                        .OrderBy(static material => material, StringComparer.OrdinalIgnoreCase)
                                        .ToList();
                                }
                            }

                            string outputFilePath = Path.Combine(outputPath, outName);
                            string basePath = Path.Combine(outputPath, Path.GetFileNameWithoutExtension(outName));
                            File.WriteAllText(outputFilePath, res.SourceCode ?? res.HlslSource ?? string.Empty);

                            if (res.FinalMetadata != null)
                            {
                                File.WriteAllText(basePath + ".metadata.json", JsonConvert.SerializeObject(res.FinalMetadata, Formatting.Indented));
                            }

                            if (res.IntermediateSpirv != null && res.IntermediateSpirv.Length > 0)
                            {
                                File.WriteAllBytes(basePath + ".spv", res.IntermediateSpirv);
                            }
                            successCount++;
                        }
                        else
                        {
                            Console.WriteLine($"Shader {i}: Decompilation failed: {res.ErrorMessage}");
                        }
                    } 
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Warning: Failed to decompile shader {i}: {ex.Message}");
                    }
                }
                
                Console.WriteLine($"Extracted {lib.ShaderEntries.Length} shaders. Decompiled {successCount}.");
                Console.WriteLine($"Output: {outputPath}");
                return 0;
            }
            catch(Exception ex)
            {
                Console.Error.WriteLine($"Library Error: {ex.Message}");
                return 1;
            }
        }

        static string GetShaderFreqString(byte frequency)
        {
            return frequency switch
            {
                0 => "VS",
                1 => "HS",
                2 => "DS",
                3 => "PS",
                4 => "GS",
                5 => "CS",
                6 => "RG", // RayGen
                7 => "RM", // RayMiss
                8 => "RH", // RayHit
                9 => "RC", // RayCallable
                10 => "MS", // Mesh
                11 => "AS", // Amplification
                _ => $"Freq{frequency}"
            };
        }

        static int RunAutoDecompile(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: ShaderDecompiler.exe --auto-decompile <exportRoot> [--material <filter>]");
                Console.Error.WriteLine("  Loops every *.ushaderlib under <exportRoot>/Content and decompiles each to <exportRoot>/Decompiled/<libname>/.");
                return 1;
            }

            string exportRoot = Path.GetFullPath(args[1]);
            if (!Directory.Exists(exportRoot))
            {
                Console.Error.WriteLine($"Error: export root not found: {exportRoot}");
                return 1;
            }

            string? materialFilter = null;
            for (int i = 2; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--material", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    materialFilter = args[i + 1];
                    i++;
                }
            }

            string contentDir = Path.Combine(exportRoot, "Content");
            string searchRoot = Directory.Exists(contentDir) ? contentDir : exportRoot;
            string[] libraries = Directory.GetFiles(searchRoot, "*.ushaderlib", SearchOption.AllDirectories)
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (libraries.Length == 0)
            {
                Console.Error.WriteLine($"Error: no *.ushaderlib found under {searchRoot}");
                return 1;
            }

            string mappingPath = Path.Combine(exportRoot, "UnifiedShaderMetadata.json");
            bool hasMapping = File.Exists(mappingPath);
            if (!hasMapping)
            {
                Console.Error.WriteLine($"Note: UnifiedShaderMetadata.json missing at {mappingPath}; falling back to sidecar-only resolution.");
            }

            string decompiledRoot = Path.Combine(exportRoot, "Decompiled");
            Directory.CreateDirectory(decompiledRoot);

            int failures = 0;
            int totalShaders = 0;
            foreach (string library in libraries)
            {
                string libName = Path.GetFileNameWithoutExtension(library);
                string outputDir = Path.Combine(decompiledRoot, libName);
                Console.WriteLine($"=== {libName} ===");
                Console.WriteLine($"  library: {library}");
                Console.WriteLine($"  output:  {outputDir}");

                int rc;
                try
                {
                    rc = ProcessUnrealLibrary(library, outputDir, false, hasMapping ? mappingPath : null, null, materialFilter);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  Library {libName} crashed: {ex.Message}");
                    failures++;
                    continue;
                }

                if (rc != 0)
                {
                    failures++;
                }

                if (Directory.Exists(outputDir))
                {
                    int hlslCount = Directory.GetFiles(outputDir, "*.hlsl", SearchOption.TopDirectoryOnly).Length;
                    int glslCount = Directory.GetFiles(outputDir, "*.glsl", SearchOption.TopDirectoryOnly).Length;
                    int produced = hlslCount + glslCount;
                    totalShaders += produced;
                    Console.WriteLine($"  produced: {produced} shader source files (.hlsl={hlslCount}, .glsl={glslCount})");
                }
            }

            Console.WriteLine();
            Console.WriteLine($"Auto-decompile finished. Libraries={libraries.Length} Failed={failures} ProducedShaders={totalShaders}");
            return failures == 0 ? 0 : 2;
        }

        static int RunAnalyzePreshader(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: ShaderDecompiler.exe --analyze-preshader <UnifiedShaderMetadata.json> --material <material path>");
                return 1;
            }

            string metadataPath = Path.GetFullPath(args[1]);
            string? materialPath = null;
            for (int i = 2; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--material", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    materialPath = args[i + 1];
                    i++;
                }
            }

            if (string.IsNullOrWhiteSpace(materialPath))
            {
                Console.Error.WriteLine("Missing required option: --material <material path>");
                return 1;
            }

            try
            {
                return ShaderTruthAnalyzer.Analyze(metadataPath, materialPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Analyze Error: {ex.Message}");
                return 1;
            }
        }

        private static bool MaterialPathMatchesFilter(string materialPath, HashSet<string> filterVariants)
        {
            HashSet<string> materialVariants = BuildMaterialPathVariants(materialPath);
            return materialVariants.Overlaps(filterVariants);
        }

        private static HashSet<string> BuildMaterialPathVariants(string? materialPath)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(materialPath))
            {
                return result;
            }

            string normalized = materialPath.Replace('\\', '/');
            AddMaterialPathVariant(result, normalized);

            if (normalized.StartsWith("/", StringComparison.Ordinal))
            {
                AddMaterialPathVariant(result, normalized[1..]);
            }
            else
            {
                AddMaterialPathVariant(result, "/" + normalized);
            }

            int dotIndex = normalized.LastIndexOf('.');
            int slashIndex = normalized.LastIndexOf('/');
            if (dotIndex > slashIndex)
            {
                AddMaterialPathVariant(result, normalized[..dotIndex]);
            }

            foreach (string current in result.ToArray())
            {
                int contentMarkerIndex = current.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
                if (contentMarkerIndex >= 0)
                {
                    string trimmed = current[(contentMarkerIndex + "/Content/".Length)..];
                    AddMaterialPathVariant(result, trimmed);
                    AddMaterialPathVariant(result, "/" + trimmed);
                }
                else if (current.StartsWith("Content/", StringComparison.OrdinalIgnoreCase))
                {
                    string trimmed = current["Content/".Length..];
                    AddMaterialPathVariant(result, trimmed);
                    AddMaterialPathVariant(result, "/" + trimmed);
                }
            }

            return result;
        }

        private static void AddMaterialPathVariant(HashSet<string> variants, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                variants.Add(value!);
            }
        }

        private sealed record SelfTestStageSpec(
            string Name,
            string EntryPoint,
            string DxbcProfile,
            string DxcProfile,
            bool AllowKnownBackendLimitations,
            string ShaderModelFolder,
            string ShaderFileName);

        private sealed record SelfTestInputCase(string Name, byte[] Binary, ShaderArchitecture Format);

        static int RunSelfTest(string outputRoot)
        {
            RecreateDirectory(outputRoot);

            string toolsDir = ResolveSelfTestToolsDirectory();
            string? dxcExe = ResolveSelfTestDxcExecutable(toolsDir);
            if (dxcExe == null)
            {
                Console.Error.WriteLine("Self-test failed: dxc.exe not found under Tools or known SDK locations.");
                return 1;
            }

            List<string> failures = new();
            string summaryPath = Path.Combine(outputRoot, "SelfTestSummary.txt");
            using var decompiler = new ShaderDecompiler(outputRoot, toolsDir);

            foreach (SelfTestStageSpec stage in CreateSelfTestStageSpecs())
            {
                RunSelfTestStage(stage, outputRoot, decompiler, dxcExe, toolsDir, failures);
            }

            File.WriteAllLines(summaryPath, failures.Count == 0
                ? new[] { "Self-test passed with no failures." }
                : new[] { "Self-test failures:" }.Concat(failures));

            if (failures.Count > 0)
            {
                Console.Error.WriteLine($"Self-test completed with {failures.Count} failure(s). Summary: {summaryPath}");
                return 2;
            }

            Console.WriteLine($"Self-test passed. Output: {outputRoot}");
            return 0;
        }

        static int RunAnalyzeSpirvImages(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: ShaderDecompiler.exe --analyze-spirv-images <shader.spv>");
                return 1;
            }

            string spirvPath = Path.GetFullPath(args[1]);
            if (!File.Exists(spirvPath))
            {
                Console.Error.WriteLine($"SPIR-V file not found: {spirvPath}");
                return 1;
            }

            SpirvModule module = SpirvModule.Parse(File.ReadAllBytes(spirvPath));
            var names = new Dictionary<uint, string>();
            var pointerTypes = new Dictionary<uint, (uint StorageClass, uint PointedTypeId)>();
            var variablePointerTypes = new Dictionary<uint, uint>();
            var imageTypes = new Dictionary<uint, SpirvImageTypeInfo>();
            var sampledImageTypes = new Dictionary<uint, uint>();
            var descriptorSets = new Dictionary<uint, uint>();
            var bindings = new Dictionary<uint, uint>();

            foreach (SpirvInstruction instruction in module.Instructions)
            {
                switch (instruction.OpCode)
                {
                    case SpvOpCode.OpName when instruction.Words.Length >= 3:
                        {
                            string? name = ReadSpirvString(instruction.Words, 2);
                            if (!string.IsNullOrWhiteSpace(name))
                            {
                                names[instruction[1]] = name;
                            }

                            break;
                        }
                    case SpvOpCode.OpDecorate when instruction.Words.Length >= 4:
                        if (instruction[2] == SpvOpCode.DecorationDescriptorSet)
                        {
                            descriptorSets[instruction[1]] = instruction[3];
                        }
                        else if (instruction[2] == SpvOpCode.DecorationBinding)
                        {
                            bindings[instruction[1]] = instruction[3];
                        }

                        break;
                    case SpvOpCode.OpTypePointer when instruction.Words.Length >= 4:
                        pointerTypes[instruction[1]] = (instruction[2], instruction[3]);
                        break;
                    case SpvOpCode.OpVariable when instruction.Words.Length >= 4:
                        variablePointerTypes[instruction[2]] = instruction[1];
                        break;
                    case SpvOpCode.OpTypeImage when instruction.Words.Length >= 9:
                        imageTypes[instruction[1]] = new SpirvImageTypeInfo(
                            SampledTypeId: instruction[2],
                            Dim: instruction[3],
                            Depth: instruction[4],
                            Arrayed: instruction[5],
                            Multisampled: instruction[6],
                            Sampled: instruction[7],
                            Format: instruction[8]);
                        break;
                    case SpvOpCode.OpTypeSampledImage when instruction.Words.Length >= 3:
                        sampledImageTypes[instruction[1]] = instruction[2];
                        break;
                }
            }

            foreach ((uint variableId, uint pointerTypeId) in variablePointerTypes.OrderBy(pair => descriptorSets.TryGetValue(pair.Key, out uint set) ? set : uint.MaxValue).ThenBy(pair => bindings.TryGetValue(pair.Key, out uint binding) ? binding : uint.MaxValue))
            {
                if (!descriptorSets.TryGetValue(variableId, out uint set) || !bindings.TryGetValue(variableId, out uint binding))
                {
                    continue;
                }

                if (!pointerTypes.TryGetValue(pointerTypeId, out (uint StorageClass, uint PointedTypeId) pointerInfo) || pointerInfo.StorageClass != SpvOpCode.StorageClassUniformConstant)
                {
                    continue;
                }

                uint pointedTypeId = pointerInfo.PointedTypeId;
                bool isSampledImageWrapper = false;
                if (sampledImageTypes.TryGetValue(pointedTypeId, out uint imageTypeId))
                {
                    pointedTypeId = imageTypeId;
                    isSampledImageWrapper = true;
                }

                if (!imageTypes.TryGetValue(pointedTypeId, out SpirvImageTypeInfo imageInfo))
                {
                    continue;
                }

                string name = names.TryGetValue(variableId, out string? variableName) ? variableName : $"id_{variableId}";
                Console.WriteLine($"Set={set} Binding={binding} VarId={variableId} Name={name} SampledImageWrapper={isSampledImageWrapper}");
                Console.WriteLine($"  ImageTypeId={pointedTypeId} SampledTypeId={imageInfo.SampledTypeId} Dim={FormatSpirvDim(imageInfo.Dim)} Arrayed={imageInfo.Arrayed} Depth={imageInfo.Depth} MS={imageInfo.Multisampled} Sampled={imageInfo.Sampled} Format={imageInfo.Format}");
            }

            return 0;
        }

        static string? ReadSpirvString(uint[] words, int startIndex)
        {
            if (startIndex >= words.Length)
            {
                return null;
            }

            byte[] bytes = new byte[(words.Length - startIndex) * 4];
            Buffer.BlockCopy(words, startIndex * 4, bytes, 0, bytes.Length);
            int terminatorIndex = Array.IndexOf(bytes, (byte)0);
            int length = terminatorIndex >= 0 ? terminatorIndex : bytes.Length;
            return length == 0 ? string.Empty : Encoding.UTF8.GetString(bytes, 0, length);
        }

        static string FormatSpirvDim(uint dim)
        {
            return dim switch
            {
                0 => "1D",
                1 => "2D",
                2 => "3D",
                3 => "Cube",
                4 => "Rect",
                5 => "Buffer",
                6 => "SubpassData",
                _ => $"Unknown({dim})"
            };
        }

        readonly record struct SpirvImageTypeInfo(
            uint SampledTypeId,
            uint Dim,
            uint Depth,
            uint Arrayed,
            uint Multisampled,
            uint Sampled,
            uint Format);

        static void RunSelfTestStage(
            SelfTestStageSpec stage,
            string outputRoot,
            ShaderDecompiler decompiler,
            string dxcExe,
            string toolsDir,
            List<string> failures)
        {
            string stageRoot = Path.Combine(outputRoot, stage.Name);
            string compiledRoot = Path.Combine(stageRoot, "Compiled");
            string decompiledRoot = Path.Combine(stageRoot, "Decompiled");
            Directory.CreateDirectory(compiledRoot);
            Directory.CreateDirectory(decompiledRoot);

            try
            {
                string shaderPath = ResolveSelfTestShaderPath(stage);
                string shaderSource = File.ReadAllText(shaderPath);
                byte[] dxbc = D3DCompiler.CompileFile(shaderPath, stage.EntryPoint, stage.DxbcProfile);
                string dxbcPath = Path.Combine(compiledRoot, $"{stage.Name}.dxbc");
                string dxilPath = Path.Combine(compiledRoot, $"{stage.Name}.dxil");
                string spvPath = Path.Combine(compiledRoot, $"{stage.Name}.spv");
                string dxbcRouteDxilPath = Path.Combine(compiledRoot, $"{stage.Name}.dxbc-route.dxil");
                string dxbcRouteSpvPath = Path.Combine(compiledRoot, $"{stage.Name}.dxbc-route.spv");

                File.WriteAllBytes(dxbcPath, dxbc);
                RunSelfTestProcess(dxcExe, $"-T {stage.DxcProfile} -E {stage.EntryPoint} -Fo \"{dxilPath}\" \"{shaderPath}\"", dxilPath);
                RunSelfTestProcess(dxcExe, $"-spirv -fspv-target-env=vulkan1.1 -T {stage.DxcProfile} -E {stage.EntryPoint} -Fo \"{spvPath}\" \"{shaderPath}\"", spvPath);

                ShaderSymbolData metadata = LoadSelfTestMetadata(stage, spvPath, toolsDir);
                File.WriteAllText(Path.Combine(stageRoot, $"{stage.Name}.symbols.json"), JsonConvert.SerializeObject(metadata, Formatting.Indented));

                var previewRewriter = new StructuredCBufferRewriter();
                byte[] previewStructuredSpv = previewRewriter.Rewrite(File.ReadAllBytes(spvPath), metadata);
                File.WriteAllBytes(Path.Combine(decompiledRoot, $"Preview.Structured.{stage.Name}.DirectSpirv.spv"), previewStructuredSpv);
                File.WriteAllText(Path.Combine(decompiledRoot, $"Preview.Structured.{stage.Name}.DirectSpirv.txt"), previewRewriter.LastRewriteSummary);

                RunSelfTestProcess(Path.Combine(toolsDir, "dxbc2dxil.exe"), $"\"{dxbcPath}\" -o \"{dxbcRouteDxilPath}\" -emit-bc", dxbcRouteDxilPath);
                RunSelfTestProcess(Path.Combine(toolsDir, "dxil-spirv.exe"), $"\"{dxbcRouteDxilPath}\" --output \"{dxbcRouteSpvPath}\" --raw-llvm", dxbcRouteSpvPath);
                byte[] dxbcRouteStructuredSpv = previewRewriter.Rewrite(File.ReadAllBytes(dxbcRouteSpvPath), metadata);
                File.WriteAllBytes(Path.Combine(decompiledRoot, $"Preview.Structured.{stage.Name}.DxbcRoute.spv"), dxbcRouteStructuredSpv);
                File.WriteAllText(Path.Combine(decompiledRoot, $"Preview.Structured.{stage.Name}.DxbcRoute.txt"), previewRewriter.LastRewriteSummary);

                var cases = new[]
                {
                    new SelfTestInputCase("Dxbc", dxbc, ShaderArchitecture.Dxbc),
                    new SelfTestInputCase("Dxil", File.ReadAllBytes(dxilPath), ShaderArchitecture.Dxil),
                    new SelfTestInputCase("Spirv", File.ReadAllBytes(spvPath), ShaderArchitecture.SpirV),
                };

                foreach (SelfTestInputCase inputCase in cases)
                {
                    try
                    {
                        var result = decompiler.Decompile(inputCase.Binary, inputCase.Format, metadata, 60);
                        string sourceText = result.SourceCode ?? result.HlslSource ?? string.Empty;
                        if (!result.Success || string.IsNullOrWhiteSpace(sourceText))
                        {
                            if (stage.AllowKnownBackendLimitations && IsKnownSelfTestBackendLimitation(result.ErrorMessage))
                            {
                                continue;
                            }

                            failures.Add($"[{stage.Name}/{inputCase.Name}] Decompilation failed: {result.ErrorMessage}");
                            continue;
                        }

                        string basePath = Path.Combine(decompiledRoot, $"Decompiled.{stage.Name}.{inputCase.Name}");
                        string sourceExtension = string.IsNullOrWhiteSpace(result.SourceFileExtension) ? ".hlsl" : result.SourceFileExtension;
                        File.WriteAllText(basePath + sourceExtension, sourceText);
                        if (!string.Equals(sourceExtension, ".hlsl", StringComparison.OrdinalIgnoreCase))
                        {
                            File.WriteAllText(basePath + ".hlsl", sourceText);
                        }
                        File.WriteAllText(basePath + ".language.txt", result.SourceLanguage ?? "hlsl");
                        if (result.IntermediateSpirv != null)
                        {
                            File.WriteAllBytes(basePath + ".spv", result.IntermediateSpirv);
                            WriteSelfTestBindingDiagnostics(basePath + ".bindings.txt", result.IntermediateSpirv);
                        }

                        foreach (string validationFailure in ValidateSelfTestResult(stage, metadata, inputCase.Name, sourceText, result.IntermediateSpirv))
                        {
                            failures.Add(validationFailure);
                        }
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"[{stage.Name}/{inputCase.Name}] Exception: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                failures.Add($"[{stage.Name}] Compilation pipeline failed: {ex.Message}");
            }
        }

        static IEnumerable<string> ValidateSelfTestResult(SelfTestStageSpec stage, ShaderSymbolData metadata, string caseName, string source, byte[]? spirv)
        {
            List<string> failures = new();

            if (spirv == null || spirv.Length == 0)
            {
                failures.Add($"[{stage.Name}/{caseName}] Missing patched SPIR-V output.");
                return failures;
            }

            if (!source.Contains("main(", StringComparison.Ordinal) && !source.Contains("main ", StringComparison.Ordinal) && !source.Contains("patch_main(", StringComparison.Ordinal))
            {
                failures.Add($"[{stage.Name}/{caseName}] Missing main entry point.");
            }

             if (metadata.GetResourceBindingCount() == 0 && metadata.ConstantBuffers.Count == 0)
             {
                 failures.Add($"[{stage.Name}/{caseName}] Reflected metadata contains no resources.");
                 return failures;
             }

             foreach (ConstantBuffer constantBuffer in metadata.ConstantBuffers)
             {
                  if (!ContainsSelfTestToken(source, spirv, constantBuffer.Name))
                  {
                      failures.Add($"[{stage.Name}/{caseName}] Missing reflected resource symbol: {constantBuffer.Name}");
                  }

                 List<ConstantBufferParameter> allParameters = GetAllConstantBufferParameters(constantBuffer);
                 if (allParameters.Count == 0)
                 {
                     continue;
                 }

                 if (ShouldAllowCompressedMatrixMembers(constantBuffer))
                 {
                      bool hasAnyMember = allParameters.Any(parameter => ContainsSelfTestToken(source, spirv, parameter.ParamName));
                     if (!hasAnyMember)
                     {
                         failures.Add($"[{stage.Name}/{caseName}] Missing reflected member symbols for compressed matrix buffer: {constantBuffer.Name}");
                     }

                     continue;
                 }

                 foreach (ConstantBufferParameter parameter in allParameters)
                 {
                      if (!ContainsSelfTestToken(source, spirv, parameter.ParamName))
                      {
                          failures.Add($"[{stage.Name}/{caseName}] Missing reflected member symbol: {constantBuffer.Name}.{parameter.ParamName}");
                      }
                 }
             }

             foreach (var resource in metadata.EnumerateResourceBindings().Where(r => r.RegisterType != 'b'))
             {
                  if (!ContainsSelfTestToken(source, spirv, resource.Name))
                  {
                      failures.Add($"[{stage.Name}/{caseName}] Missing reflected resource symbol: {resource.Name}");
                  }
             }

            return failures;
        }

        static bool ShouldAllowCompressedMatrixMembers(ConstantBuffer constantBuffer)
        {
            List<ConstantBufferParameter> allParameters = GetAllConstantBufferParameters(constantBuffer);
            if (allParameters.Count == 0)
            {
                return false;
            }

            return allParameters.All(parameter => parameter.IsMatrix && parameter.Rows == 4 && parameter.Columns == 4);
        }

        static bool ContainsSelfTestToken(string source, byte[]? spirv, string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(source) && source.Contains(token, StringComparison.Ordinal))
            {
                return true;
            }

            return spirv != null && spirv.Length > 0 && ContainsSelfTestAsciiToken(spirv, token);
        }

        static bool ContainsSelfTestAsciiToken(byte[] bytes, string token)
        {
            byte[] needle = System.Text.Encoding.ASCII.GetBytes(token);
            if (needle.Length == 0 || bytes.Length < needle.Length)
            {
                return false;
            }

            for (int i = 0; i <= bytes.Length - needle.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (bytes[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    return true;
                }
            }

            return false;
        }

        static void WriteSelfTestBindingDiagnostics(string outputPath, byte[] spirv)
        {
            var patcher = new SpirvPatcher();
            var bindings = patcher.AnalyzeBindingsDetailed(spirv);
            using var writer = new StreamWriter(outputPath, false);
            foreach (var binding in bindings)
            {
                writer.WriteLine($"Id={binding.Id} Set={binding.Set} Binding={binding.Binding} Type={binding.DescriptorType} CurrentName={binding.CurrentName} StructTypeId={binding.StructTypeId} StructMemberCount={binding.StructMemberCount}");
            }
        }

        static SelfTestStageSpec[] CreateSelfTestStageSpecs()
        {
            return new[]
            {
                new SelfTestStageSpec("Vertex", "VSMain", "vs_4_0", "vs_6_0", false, "SM4", "SelfTestVertex.hlsl"),
                new SelfTestStageSpec("Hull", "HSMain", "hs_5_0", "hs_6_0", true, "SM5", "SelfTestHull.hlsl"),
                new SelfTestStageSpec("Domain", "DSMain", "ds_5_0", "ds_6_0", true, "SM5", "SelfTestDomain.hlsl"),
                new SelfTestStageSpec("Geometry", "GSMain", "gs_4_0", "gs_6_0", true, "SM4", "SelfTestGeometry.hlsl"),
                new SelfTestStageSpec("Pixel", "PSMain", "ps_4_0", "ps_6_0", false, "SM4", "SelfTestPixel.hlsl"),
                new SelfTestStageSpec("Compute", "CSMain", "cs_5_0", "cs_6_0", false, "SM5", "SelfTestCompute.hlsl"),
            };
        }

        static bool IsKnownSelfTestBackendLimitation(string? errorMessage)
        {
            if (string.IsNullOrWhiteSpace(errorMessage))
            {
                return false;
            }

            return errorMessage.Contains("Unsupported builtin in HLSL", StringComparison.Ordinal)
                || errorMessage.Contains("SPIRV-Cross threw an exception", StringComparison.Ordinal);
        }

        static void RecreateDirectory(string path)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }

            Directory.CreateDirectory(path);
        }

        static int RunUnityBinarySession(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("Usage: ShaderDecompiler.exe --unitybinary-session <dxbc.bin> <metadata.json> [outputDir]");
                return 1;
            }

            string dxbcPath = Path.GetFullPath(args[1]);
            string metadataPath = Path.GetFullPath(args[2]);
            string outputDir = args.Length > 3
                ? Path.GetFullPath(args[3])
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Testing", "Session");

            if (!File.Exists(dxbcPath))
            {
                Console.Error.WriteLine($"Error: DXBC file not found: {dxbcPath}");
                return 1;
            }

            if (!File.Exists(metadataPath))
            {
                Console.Error.WriteLine($"Error: metadata file not found: {metadataPath}");
                return 1;
            }

            ShaderSymbolData metadata;
            try
            {
                string metadataJson = File.ReadAllText(metadataPath);
                metadata = JsonConvert.DeserializeObject<ShaderSymbolData>(metadataJson)
                    ?? throw new InvalidOperationException($"Failed to deserialize metadata: {metadataPath}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: failed to load metadata: {ex.Message}");
                return 1;
            }

            RecreateDirectory(outputDir);

            try
            {
                byte[] dxbc = File.ReadAllBytes(dxbcPath);
                string tempRoot = Path.Combine(Path.GetTempPath(), "RuriShaderDecompiler", "UnityBinarySession");
                Directory.CreateDirectory(tempRoot);
                using var decompiler = new ShaderDecompiler(tempRoot);
                DecompileResult result = decompiler.Decompile(dxbc, ShaderArchitecture.Dxbc, metadata, 50);
                string sourceText = result.SourceCode ?? result.HlslSource ?? string.Empty;
                if (!result.Success || string.IsNullOrWhiteSpace(sourceText) || result.IntermediateSpirv == null)
                {
                    string error = result.ErrorMessage ?? "Unknown decompilation failure.";
                    File.WriteAllText(Path.Combine(outputDir, "unitybinary.error.txt"), error);
                    Console.Error.WriteLine(error);
                    return 1;
                }

                // Keep this session output deterministic so Unity partial-cbuffer fixes can be
                // iterated offline against the exact same artifacts every run.
                string errorPath = Path.Combine(outputDir, "unitybinary.error.txt");
                if (File.Exists(errorPath))
                {
                    File.Delete(errorPath);
                }

                string sourceExtension = string.IsNullOrWhiteSpace(result.SourceFileExtension) ? ".hlsl" : result.SourceFileExtension;
                string legacyHlslPath = Path.Combine(outputDir, "unitybinary.hlsl");
                string sourcePath = Path.Combine(outputDir, "unitybinary" + sourceExtension);
                File.WriteAllText(sourcePath, sourceText);
                if (!string.Equals(sourcePath, legacyHlslPath, StringComparison.OrdinalIgnoreCase))
                {
                    File.WriteAllText(legacyHlslPath, sourceText);
                }
                File.WriteAllBytes(Path.Combine(outputDir, "unitybinary.spv"), result.IntermediateSpirv);
                File.WriteAllText(Path.Combine(outputDir, "unitybinary.metadata.json"), JsonConvert.SerializeObject(result.FinalMetadata ?? metadata, Formatting.Indented));
                File.WriteAllText(Path.Combine(outputDir, "unitybinary.rewrite.txt"), result.StructuredRewriteSummary ?? string.Empty);
                File.WriteAllText(Path.Combine(outputDir, "unitybinary.language.txt"), result.SourceLanguage ?? "hlsl");
                WriteSelfTestBindingDiagnostics(Path.Combine(outputDir, "unitybinary.bindings.txt"), result.IntermediateSpirv);
                Console.WriteLine($"UnityBinary session output: {outputDir}");
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(outputDir, "unitybinary.error.txt"), ex.ToString());
                Console.Error.WriteLine($"UnityBinary session failed: {ex.Message}");
                return 1;
            }
        }

        static int RunUnityBinaryLitPolySelfTest(string? outputDir)
        {
            string assetRoot = ResolveUnityBinaryLitPolyAssetRoot();
            string sessionRoot = string.IsNullOrWhiteSpace(outputDir)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Testing", "Session", "UnityBinary", "litpoly")
                : Path.GetFullPath(outputDir);

            RecreateDirectory(sessionRoot);

            string[] metadataFiles = Directory.GetFiles(assetRoot, "*.metadata.json", SearchOption.TopDirectoryOnly)
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (metadataFiles.Length == 0)
            {
                Console.Error.WriteLine($"Error: no litpoly metadata files found under {assetRoot}");
                return 1;
            }

            int failures = 0;
            foreach (string metadataPath in metadataFiles)
            {
                string fileName = Path.GetFileName(metadataPath);
                string dxbcPath = Path.Combine(assetRoot, fileName.Replace(".metadata.json", ".dxbc.bin", StringComparison.OrdinalIgnoreCase));
                if (!File.Exists(dxbcPath))
                {
                    Console.Error.WriteLine($"Error: matching DXBC file not found for {fileName}");
                    failures++;
                    continue;
                }

                string caseName = fileName.Replace(".HGBuffer.metadata.json", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace(".metadata.json", string.Empty, StringComparison.OrdinalIgnoreCase);
                string caseOutputDir = Path.Combine(sessionRoot, caseName);
                int exitCode = RunUnityBinarySession(new[] { "--unitybinary-session", dxbcPath, metadataPath, caseOutputDir });
                if (exitCode != 0)
                {
                    failures++;
                }
            }

            Console.WriteLine($"UnityBinary litpoly self-test output: {sessionRoot}");
            return failures == 0 ? 0 : 1;
        }

        static void RunSelfTestProcess(string exePath, string arguments, string? expectedOutputPath = null)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exePath)!
            };

            using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {exePath}");
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Process failed: {Path.GetFileName(exePath)} {arguments}\n{stdout}\n{stderr}");
            }

            if (!string.IsNullOrWhiteSpace(expectedOutputPath) && !File.Exists(expectedOutputPath))
            {
                throw new InvalidOperationException($"Process produced no output file: {Path.GetFileName(exePath)} {arguments}\n{stdout}\n{stderr}");
            }
        }

        static string? FindSelfTestFile(string rootDir, string fileName)
        {
            if (!Directory.Exists(rootDir))
            {
                return null;
            }

            return Directory.EnumerateFiles(rootDir, fileName, SearchOption.AllDirectories).FirstOrDefault();
        }

        static string ResolveUnityBinaryLitPolyAssetRoot()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string relativePath = Path.Combine("Testing", "Assets", "Shaders", "UnityBinary", "litpoly");
            string[] roots =
            {
                Path.GetFullPath(Path.Combine(baseDir, "..", "..")),
                baseDir,
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..")),
            };

            foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string candidate = Path.Combine(root, relativePath);
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new DirectoryNotFoundException($"UnityBinary litpoly asset root not found: {relativePath}");
        }

        static string ResolveUnityBinaryAutoAssetRoot()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string relativePath = Path.Combine("Testing", "Assets", "Shaders", "UnityBinary");
            string[] roots =
            {
                Path.GetFullPath(Path.Combine(baseDir, "..", "..")),
                baseDir,
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..")),
            };

            foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string candidate = Path.Combine(root, relativePath);
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new DirectoryNotFoundException($"UnityBinary asset root not found: {relativePath}");
        }

        static string ResolveTestingShaderOutputRoot()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string relativePath = Path.Combine("Testing", "Assets", "Shaders", "Output");
            string[] roots =
            {
                Path.GetFullPath(Path.Combine(baseDir, "..", "..")),
                baseDir,
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..")),
            };

            foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string candidate = Path.Combine(root, relativePath);
                string parent = Path.GetDirectoryName(candidate);
                if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
                {
                    return candidate;
                }
            }

            return Path.Combine(baseDir, relativePath);
        }

        static string? ResolveSelfTestDxcExecutable(string toolsDir)
        {
            string[] candidateRoots =
            {
                Path.Combine(toolsDir, "dxc"),
                toolsDir,
                Path.Combine(toolsDir, "bin"),
                @"D:\Tools\Program64\Microsoft Visual Studio\18\Insiders\VC\Tools\Llvm\x64\bin",
                @"C:\Program Files\Microsoft DirectX Shader Compiler",
                @"C:\Program Files (x86)\Microsoft DirectX Shader Compiler"
            };

            foreach (string root in candidateRoots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string[] preferred =
                {
                    Path.Combine(root, "bin", "x64", "dxc.exe"),
                    Path.Combine(root, "x64", "dxc.exe"),
                };

                foreach (string candidate in preferred)
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                string? exe = FindSelfTestFile(root, "dxc.exe");
                if (!string.IsNullOrWhiteSpace(exe))
                {
                    return exe;
                }
            }

            return null;
        }

        static string ResolveSelfTestToolsDirectory()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(baseDir, "Tools"),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "Tools")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "Tools")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "Tools")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "Tools")),
            };

            foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (Directory.Exists(candidate) && ShaderDecompiler.HasDirectTools(candidate))
                {
                    return candidate;
                }
            }

            return Path.Combine(baseDir, "Tools");
        }

        static string ResolveSelfTestShaderPath(SelfTestStageSpec stage)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string relativeStagePath = Path.Combine("Testing", "Assets", "Shaders", stage.ShaderModelFolder, stage.Name, stage.ShaderFileName);
            string relativeFlatPath = Path.Combine("Testing", "Assets", "Shaders", stage.ShaderModelFolder, stage.ShaderFileName);
            string[] roots =
            {
                Path.GetFullPath(Path.Combine(baseDir, "..", "..")),
                baseDir,
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..")),
            };

            string[] candidates = roots
                .SelectMany(root => new[]
                {
                    Path.Combine(root, relativeStagePath),
                    Path.Combine(root, relativeFlatPath),
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new FileNotFoundException($"Self-test shader file not found for {stage.Name}.", candidates.Last());
        }

        static ShaderSymbolData LoadSelfTestMetadata(SelfTestStageSpec stage, string spirvPath, string toolsDir)
        {
            string? metadataPath = ResolveSelfTestMetadataPath(stage);
            if (!string.IsNullOrWhiteSpace(metadataPath) && File.Exists(metadataPath))
            {
                string json = File.ReadAllText(metadataPath);
                ShaderSymbolData? parsed = JsonConvert.DeserializeObject<ShaderSymbolData>(json);
                if (parsed != null)
                {
                    UpgradeLegacySelfTestMetadata(json, parsed);
                    NormalizeMetadataToUscLayout(parsed);
                    return parsed;
                }
            }

            ShaderSymbolData extracted = SpirvReflectionMetadataExtractor.Extract(spirvPath, Path.Combine(toolsDir, "spirv-cross.exe"));
            NormalizeMetadataToUscLayout(extracted);
            return extracted;
        }

        static void NormalizeMetadataToUscLayout(ShaderSymbolData metadata)
        {
            foreach (ConstantBuffer constantBuffer in metadata.ConstantBuffers)
            {
                foreach (ConstantBufferParameter parameter in GetAllConstantBufferParameters(constantBuffer))
                {
                    ValidateUscLayout(parameter);
                }
            }
        }

        static void UpgradeLegacySelfTestMetadata(string json, ShaderSymbolData metadata)
        {
            if (metadata.GetResourceBindingCount() > 0)
            {
                return;
            }

            try
            {
                JObject root = JObject.Parse(json);
                if (root["Resources"] is not JArray resources)
                {
                    return;
                }

                foreach (JToken resource in resources)
                {
                    string name = resource.Value<string>("Name") ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    int binding = resource.Value<int?>("Binding") ?? 0;
                    int set = resource.Value<int?>("Set") ?? 0;
                    char registerType = (resource.Value<string>("RegisterType") ?? string.Empty).FirstOrDefault();

                    switch (registerType)
                    {
                        case 'b':
                            if (!metadata.ConstantBufferBindings.Any(existing => existing.Set == set && existing.Index == binding && string.Equals(existing.Name, name, StringComparison.Ordinal)))
                            {
                                metadata.ConstantBufferBindings.Add(new BufferBinding
                                {
                                    Name = name,
                                    Set = set,
                                    Index = binding,
                                    ArraySize = 0,
                                });
                            }
                            break;
                        case 't':
                            if (!metadata.TextureParameters.Any(existing => existing.Set == set && existing.Index == binding && string.Equals(existing.Name, name, StringComparison.Ordinal)))
                            {
                                metadata.TextureParameters.Add(new TextureParameter
                                {
                                    Name = name,
                                    Set = set,
                                    Index = binding,
                                    SamplerIndex = -1,
                                    MultiSampled = false,
                                    Dim = 2,
                                });
                            }
                            break;
                        case 's':
                            if (!metadata.Samplers.Any(existing => existing.Set == set && existing.Index == binding))
                            {
                                metadata.Samplers.Add(new SamplerParameter
                                {
                                    Set = set,
                                    Index = binding,
                                    Sampler = (uint)binding,
                                });
                            }
                            break;
                        case 'u':
                            if (!metadata.UAVs.Any(existing => existing.Set == set && existing.Index == binding && string.Equals(existing.Name, name, StringComparison.Ordinal)))
                            {
                                metadata.UAVs.Add(new UAVParameter
                                {
                                    Name = name,
                                    Set = set,
                                    Index = binding,
                                    OriginalIndex = binding,
                                });
                            }
                            break;
                    }
                }
            }
            catch
            {
            }
        }

        static List<ConstantBufferParameter> GetAllConstantBufferParameters(ConstantBuffer constantBuffer)
        {
            var result = new List<ConstantBufferParameter>(constantBuffer.CBParams);
            foreach (StructParameter structParameter in constantBuffer.StructParams)
            {
                result.AddRange(structParameter.CBParams);
            }

            return result;
        }

        static void ValidateUscLayout(ConstantBufferParameter parameter)
        {
            if (parameter == null)
            {
                return;
            }

            if (parameter.Rows <= 0 || parameter.Columns <= 0)
            {
                throw new InvalidOperationException($"Missing USC metadata dimensions for parameter '{parameter.ParamName}'.");
            }

            if (parameter.ByteOffset < 0)
            {
                throw new InvalidOperationException($"Missing USC metadata byte index for parameter '{parameter.ParamName}'.");
            }

            if (parameter.ArraySize <= 0)
            {
                parameter.ArraySize = 1;
            }
        }

        static string? ResolveSelfTestMetadataPath(SelfTestStageSpec stage)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string relativePath = Path.Combine("Testing", "Assets", "Metadata", stage.ShaderModelFolder, stage.Name, stage.ShaderFileName.Replace(".hlsl", ".metadata.json", StringComparison.OrdinalIgnoreCase));
            string[] roots =
            {
                Path.GetFullPath(Path.Combine(baseDir, "..", "..")),
                baseDir,
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..")),
            };

            foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string candidate = Path.Combine(root, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        static ShaderArchitecture ParseFormat(string mode)
        {
             return mode.ToLower() switch {
                 "dxbc" => ShaderArchitecture.Dxbc,
                 "dxil" => ShaderArchitecture.Dxil,
                 "spv" => ShaderArchitecture.SpirV,
                 "spirv" => ShaderArchitecture.SpirV,
                 "hlsl" => ShaderArchitecture.Unknown,
                 "-dxbc" => ShaderArchitecture.Dxbc,
                 "-dxil" => ShaderArchitecture.Dxil,
                 "-spv" => ShaderArchitecture.SpirV,
                 "-spirv" => ShaderArchitecture.SpirV,
                 "-unknown" => ShaderArchitecture.Unknown,
                 _ => ShaderArchitecture.Unknown
             };
        }

        static bool IsLikelyMode(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string lower = value.ToLowerInvariant();
            return lower is "hlsl" or "dxbc" or "dxil" or "spv" or "spirv"
                or "-dxbc" or "-dxil" or "-spv" or "-spirv" or "-unknown";
        }

        static ShaderSymbolData? LoadShaderSymbols(string inputPath, string? explicitSymbolsPath)
        {
            string? symbolsPath = explicitSymbolsPath;
            if (string.IsNullOrWhiteSpace(symbolsPath))
            {
                string sidecar = Path.ChangeExtension(inputPath, ".symbols.json");
                if (File.Exists(sidecar))
                {
                    symbolsPath = sidecar;
                }
            }

            if (string.IsNullOrWhiteSpace(symbolsPath))
            {
                return null;
            }

            string fullPath = Path.GetFullPath(symbolsPath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"Symbols file not found: {fullPath}");
            }

            var json = File.ReadAllText(fullPath);
            var symbols = JsonConvert.DeserializeObject<ShaderSymbolData>(json);
            if (symbols == null)
            {
                throw new InvalidOperationException($"Failed to deserialize shader symbols: {fullPath}");
            }

            return symbols;
        }

        static (ShaderArchitecture format, int offset) SniffFormat(byte[] data)
        {
            if (data == null || data.Length < 25) return (ShaderArchitecture.Unknown, 0);
            
            // UE shader entries have a 21-byte header before the actual DXBC/SPIRV
            // Try offset 21 first, then fallback to other common offsets
            int[] offsets = { 21, 0, 4, 8, 12, 16 };
            foreach (var off in offsets)
            {
                if (off + 4 > data.Length) continue;
                uint magic = BitConverter.ToUInt32(data, off);
                if (magic == 0x43425844) return (ShaderArchitecture.Dxbc, off); // DXBC
                if (magic == 0x07230203) return (ShaderArchitecture.SpirV, off); // SPIR-V
            }
            return (ShaderArchitecture.Unknown, 0);
        }
    }
}
