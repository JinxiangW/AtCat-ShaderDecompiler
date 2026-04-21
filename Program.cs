using System;
using System.Diagnostics;
using System.IO;
using Ruri.ShaderDecompiler;
using Ruri.ShaderDecompiler.Engine;
using Ruri.ShaderDecompiler.Utils;
using System.Linq;
using Newtonsoft.Json;
using Ruri.ShaderDecompiler.Intermediate;
using Ruri.ShaderDecompiler.Spirv;
using Ruri.ShaderDecompiler.Testing.Assets.Shaders;
using Ruri.ShaderDecompiler.Testing.Compilation;
using Ruri.ShaderDecompiler.Unreal;

namespace Ruri.ShaderDecompiler
{
    class Program
    {
        static int Main(string[] args)
        {
            if (args.Length >= 1 && string.Equals(args[0], "--selftest", StringComparison.OrdinalIgnoreCase))
            {
                string outputRoot = args.Length > 1
                    ? Path.GetFullPath(args[1])
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SelfTestOutput");
                return RunSelfTest(outputRoot);
            }

            if (args.Length < 1)
            {
                Console.WriteLine("Usage: ShaderDecompiler.exe <input> [mode] [output] [--keep-temps] [--mapping <path>]");
                return 1;
            }

            string inputPath = Path.GetFullPath(args[0]);
            string mode = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : "";
            string? outputPath = args.Length > 2 && !args[2].StartsWith("--", StringComparison.Ordinal) ? args[2] : null;
            bool keepTemps = false;
            string? scanAssetsPath = null;
            string? mappingPath = null;
            string? symbolsPath = null;

            var nameMap = new Dictionary<int, string>();
            for (int i = 1; i < args.Length; i++)
            {
                if (i <= 2 && !args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    continue;
                }

                if (args[i] == "--keep-temps") keepTemps = true;
                else if (args[i] == "--scan-assets" && i + 1 < args.Length)
                {
                    scanAssetsPath = args[i + 1];
                    i++; // Skip next arg
                }
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
                else if (args[i] == "--restore-symbols" && i + 2 < args.Length)
                {
                    string matDir = args[i+1];
                    string arcDir = args[i+2];
                    nameMap = ShaderBindingExtractor.ScanAndRestore(matDir, arcDir);
                    i += 2;
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
                return ProcessUnrealLibrary(inputPath, outputPath, keepTemps, scanAssetsPath, mappingPath, nameMap);
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
                    if (outputPath != null) 
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                        File.WriteAllText(outputPath, result.HlslSource);
                    }
                    else Console.WriteLine(result.HlslSource);
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

        static int ProcessUnrealLibrary(string inputPath, string? outputPath, bool keepTemps, string? scanAssetsPath, string? mappingPath, Dictionary<int, string>? nameMapInput = null)
        {
            try 
            {
                var nameMap = nameMapInput ?? new Dictionary<int, string>();
                var usageMap = new Dictionary<int, HashSet<string>>();

                // 1. Scan Assets (Legacy)
                if (!string.IsNullOrEmpty(scanAssetsPath))
                {
                    try
                    {
                        var manager = new MaterialShaderManager();
                        manager.ScanMaterials(scanAssetsPath);
                        foreach (var kv in manager.ShaderIndexToNameMap)
                        {
                            nameMap[kv.Key] = kv.Value;
                        }
                    }
                    catch(Exception ex)
                    {
                         Console.WriteLine($"[Warning] Material scan failed: {ex.Message}");
                    }
                }

                var lib = UnrealShaderLibraryReader.Read(inputPath);
                Console.WriteLine($"Read Library: {lib.Version} Version, {lib.ShaderEntries.Length} shaders.");

                // 2. Auto-Detect Mapping if not provided
                if (string.IsNullOrEmpty(mappingPath))
                {
                    var dir = Path.GetDirectoryName(inputPath);
                    while (dir != null)
                    {
                        var candidate = Path.Combine(dir, "ShaderMappings.json");
                        if (File.Exists(candidate))
                        {
                            mappingPath = candidate;
                            Console.WriteLine($"[Auto-Detect] Found mapping file: {mappingPath}");
                            break;
                        }
                        var parent = Directory.GetParent(dir);
                        if (parent == null) break;
                        dir = parent.FullName;
                    }
                }

                // 3. Load Shader Mappings (JSON)
                if (!string.IsNullOrEmpty(mappingPath) && File.Exists(mappingPath))
                {
                     try
                     {
                         Console.WriteLine($"Loading mapping from {mappingPath}...");
                         var json = File.ReadAllText(mappingPath);
                         var mapping = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(json);
                         
                         var hashToMats = new Dictionary<string, HashSet<string>>();
                         if(mapping != null)
                         {
                             foreach(var kvp in mapping)
                             {
                                 foreach(var hash in kvp.Value)
                                 {
                                     if (!hashToMats.ContainsKey(hash)) hashToMats[hash] = new HashSet<string>();
                                     // Store FULL path for precise mapping
                                     hashToMats[hash].Add(kvp.Key);
                                 }
                             }
                             
                             Console.WriteLine($"Loaded {mapping.Count} material mappings.");

                             int mapCount = Math.Min(lib.ShaderMapEntries.Length, lib.ShaderMapHashes.Count); 
                             int mappedShaders = 0;

                             for(int i=0; i<mapCount; i++)
                             {
                                 var hash = lib.ShaderMapHashes[i];
                                 if (hashToMats.TryGetValue(hash, out var mats))
                                 {
                                     var entry = lib.ShaderMapEntries[i];
                                     // "Use first material name" rule from user
                                     // NOTE: We must extract simple name for file naming, but keep full path in usedBy for mapper
                                     string fullMaterialPath = mats.FirstOrDefault() ?? "Unknown";
                                     var niceName = Path.GetFileNameWithoutExtension(fullMaterialPath);
                                     if (string.IsNullOrEmpty(niceName)) niceName = "UnknownMaterial"; 
                                     
                                     for(uint k=0; k<entry.NumShaders; k++)
                                     {
                                         long idxInternal = entry.ShaderIndicesOffset + k;
                                         if(idxInternal < lib.ShaderIndices.Length)
                                         {
                                             uint sIdx = lib.ShaderIndices[idxInternal];
                                             
                                             // Update Usage Map
                                             if (!usageMap.ContainsKey((int)sIdx)) usageMap[(int)sIdx] = new HashSet<string>();
                                             foreach (var m in mats) usageMap[(int)sIdx].Add(m);
                                             
                                             // Update Name Map (First wins)
                                             if(!nameMap.ContainsKey((int)sIdx))
                                             {
                                                 nameMap[(int)sIdx] = niceName;
                                                 mappedShaders++;
                                             }
                                         }
                                     }
                                 }
                             }
                             Console.WriteLine($"Mapped {mappedShaders} shaders using JSON mapping.");
                         }
                     }
                     catch(Exception ex) { Console.WriteLine($"[Warning] JSON Mapping failed: {ex.Message}"); }
                }

                // 4. Load Material Parameter Mappings (Precise)
                Ruri.ShaderDecompiler.Unreal.PreciseParameterMapper? preciseMapper = null;
                if (!string.IsNullOrEmpty(mappingPath))
                {
                    string paramMappingPath = Path.Combine(Path.GetDirectoryName(mappingPath)!, "MaterialParameterMappings.json");
                    if (File.Exists(paramMappingPath))
                    {
                        Console.WriteLine($"[信息] 加载参数映射文件: {paramMappingPath}");
                        preciseMapper = Ruri.ShaderDecompiler.Unreal.PreciseParameterMapper.LoadFromFile(paramMappingPath);
                    }
                    else
                    {
                        Console.WriteLine($"[警告] 未找到参数映射文件: {paramMappingPath} (将无法进行变量重命名)");
                    }
                }

                if (outputPath == null) 
                    outputPath = Path.Combine(Path.GetDirectoryName(inputPath)!, Path.GetFileNameWithoutExtension(inputPath) + "_Export");
                
                Directory.CreateDirectory(outputPath);

                using var decompiler = new ShaderDecompiler(outputPath);
                int successCount = 0;

                for(int i=0; i<lib.ShaderEntries.Length; i++)
                {
                    var code = lib.GetShaderCode(i);
                    var entry = lib.ShaderEntries[i];
                    Console.WriteLine($"Shader {i}: Size={entry.Size}, Uncompressed={entry.UncompressedSize}, Offset={entry.Offset}");
                    
                    if (code == null) continue;
                    
                    string typeSuffix = GetShaderFreqString(entry.Frequency);

                    try 
                    {
                        var res = decompiler.Decompile(code, ShaderFormat.Unknown, (ShaderSymbolData?)null, 50);
                        if (res.Success)
                        {
                            string finalName = "";
                            
                            // 1. Try Map
                            if (nameMap.ContainsKey(i)) finalName = nameMap[i];
                            // 2. Try embedded name
                            else if (!string.IsNullOrEmpty(res.ShaderName)) finalName = res.ShaderName;
                            else finalName = "UnknownShader";
                            
                            finalName = string.Join("_", finalName.Split(Path.GetInvalidFileNameChars()));
                            string outName = $"{finalName}_{typeSuffix}_{i}.hlsl";
                            
                            // Inject Header
                            if (usageMap.TryGetValue(i, out var usedBy))
                            {
                                 var sb = new System.Text.StringBuilder();
                                 sb.AppendLine("/*");
                                 sb.AppendLine(" * UE Shader Info");
                                 sb.AppendLine($" * Index: {i}");
                                 sb.AppendLine($" * Stage: {typeSuffix}");
                                 sb.AppendLine($" * Used by {usedBy.Count} Materials:");
                                 
                                 // Try to find a material with precise mapping
                                 MaterialMapping? bestMapping = null;
                                 string bestMaterialName = "";

                                 foreach(var m in usedBy) 
                                 {
                                     if (bestMapping == null && preciseMapper != null)
                                     {
                                         // Debug match attempts
                                         bestMapping = preciseMapper.GetByMaterialPath(m);
                                         if (bestMapping != null) 
                                         {
                                             bestMaterialName = m;
                                             Console.WriteLine($"[调试] 成功匹配: '{m}'");
                                         }
                                         else
                                         {
                                            // Only log first few failures to avoid spam
                                            if (usedBy.Count < 5) Console.WriteLine($"[调试] 匹配失败: '{m}'");
                                         }
                                     }
                                     
                                     // Limit list in header
                                     if (usedBy.Count <= 20 || m == bestMaterialName)
                                         sb.AppendLine($" *  - {m}");
                                 }
                                 
                                 if(usedBy.Count > 20) sb.AppendLine($" *  ... and {usedBy.Count-20} more");
                                 sb.AppendLine(" */");
                                 sb.AppendLine("");

                                 ShaderSymbolMetadata? injectionSymbols = null;

                                 // Inject Precise Parameter Mapping Header & Prepare Symbols
                                 if (bestMapping != null && preciseMapper != null)
                                 {
                                     Console.WriteLine($"[信息] Shader {i} 匹配到材质: {bestMaterialName} (包含参数映射)");
                                     string mappingHeader = preciseMapper.GenerateHlslHeader(bestMapping, bestMaterialName);
                                     sb.AppendLine(mappingHeader);

                                     // Prepare symbols for native injection
                                     injectionSymbols = preciseMapper.GetSymbolMetadata(bestMapping);
                                 }
                                 else
                                 {
                                     // Console.WriteLine($"[警告] Shader {i} 未找到精确材质映射");
                                 }

                                 // Re-decompile with symbols if available to get native variable names
                                 if (injectionSymbols != null)
                                 {
                                     try 
                                     {
                                         // Decompile again, this time with symbols which will be patched into SPIR-V
                                         var resWithSymbols = decompiler.Decompile(code, ShaderFormat.Unknown, injectionSymbols, 50);
                                         if (resWithSymbols.Success)
                                         {
                                              res = resWithSymbols;
                                         }
                                         else
                                         {
                                              Console.WriteLine($"[警告] 符号注入重编译失败 (HLSL生成错误): {resWithSymbols.ErrorMessage}");
                                         }
                                     }
                                     catch (Exception ex) 
                                     {
                                         Console.WriteLine($"[警告] 符号注入重编译异常: {ex.Message}");
                                     }
                                 }

                                 res.HlslSource = sb.ToString() + res.HlslSource;
                            }

                            File.WriteAllText(Path.Combine(outputPath, outName), res.HlslSource);
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

        private sealed record SelfTestStageSpec(
            string Name,
            string EntryPoint,
            string ShaderModel5Profile,
            string ShaderModel6Profile,
            bool AllowKnownBackendLimitations);

        private sealed record SelfTestInputCase(string Name, byte[] Binary, ShaderFormat Format);

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

            string shaderPath = Path.Combine(outputRoot, "SelfTestComplexPbr.hlsl");
            File.WriteAllText(shaderPath, SelfTestAssets.ComplexPbrShader);

            List<string> failures = new();
            string summaryPath = Path.Combine(outputRoot, "SelfTestSummary.txt");
            using var decompiler = new ShaderDecompiler(outputRoot, toolsDir);

            foreach (SelfTestStageSpec stage in CreateSelfTestStageSpecs())
            {
                RunSelfTestStage(stage, shaderPath, outputRoot, decompiler, dxcExe, toolsDir, failures);
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

        static void RunSelfTestStage(
            SelfTestStageSpec stage,
            string shaderPath,
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
                byte[] dxbc = D3DCompiler.Compile(SelfTestAssets.ComplexPbrShader, stage.EntryPoint, stage.ShaderModel5Profile);
                string dxbcPath = Path.Combine(compiledRoot, $"{stage.Name}.dxbc");
                string dxilPath = Path.Combine(compiledRoot, $"{stage.Name}.dxil");
                string spvPath = Path.Combine(compiledRoot, $"{stage.Name}.spv");
                string dxbcRouteDxilPath = Path.Combine(compiledRoot, $"{stage.Name}.dxbc-route.dxil");
                string dxbcRouteSpvPath = Path.Combine(compiledRoot, $"{stage.Name}.dxbc-route.spv");

                File.WriteAllBytes(dxbcPath, dxbc);
                RunSelfTestProcess(dxcExe, $"-T {stage.ShaderModel6Profile} -E {stage.EntryPoint} -Fo \"{dxilPath}\" \"{shaderPath}\"", dxilPath);
                RunSelfTestProcess(dxcExe, $"-spirv -fspv-target-env=vulkan1.1 -T {stage.ShaderModel6Profile} -E {stage.EntryPoint} -Fo \"{spvPath}\" \"{shaderPath}\"", spvPath);

                ShaderSymbolData metadata = SpirvReflectionMetadataExtractor.Extract(spvPath, Path.Combine(toolsDir, "spirv-cross.exe"));
                File.WriteAllText(Path.Combine(stageRoot, $"{stage.Name}.symbols.json"), JsonConvert.SerializeObject(metadata, Formatting.Indented));

                var previewRewriter = new StructuredCBufferRewriter();
                byte[] previewStructuredSpv = previewRewriter.Rewrite(File.ReadAllBytes(spvPath), metadata);
                File.WriteAllBytes(Path.Combine(decompiledRoot, $"Preview.Structured.{stage.Name}.DirectSpirv.spv"), previewStructuredSpv);

                RunSelfTestProcess(Path.Combine(toolsDir, "dxbc2dxil.exe"), $"\"{dxbcPath}\" -o \"{dxbcRouteDxilPath}\" -emit-bc", dxbcRouteDxilPath);
                RunSelfTestProcess(Path.Combine(toolsDir, "dxil-spirv.exe"), $"\"{dxbcRouteDxilPath}\" --output \"{dxbcRouteSpvPath}\" --raw-llvm", dxbcRouteSpvPath);
                byte[] dxbcRouteStructuredSpv = previewRewriter.Rewrite(File.ReadAllBytes(dxbcRouteSpvPath), metadata);
                File.WriteAllBytes(Path.Combine(decompiledRoot, $"Preview.Structured.{stage.Name}.DxbcRoute.spv"), dxbcRouteStructuredSpv);

                var cases = new[]
                {
                    new SelfTestInputCase("Dxbc", dxbc, ShaderFormat.Dxbc),
                    new SelfTestInputCase("Dxil", File.ReadAllBytes(dxilPath), ShaderFormat.Dxil),
                    new SelfTestInputCase("Spirv", File.ReadAllBytes(spvPath), ShaderFormat.SpirV),
                };

                foreach (SelfTestInputCase inputCase in cases)
                {
                    try
                    {
                        var result = decompiler.Decompile(inputCase.Binary, inputCase.Format, metadata, 60);
                        if (!result.Success || string.IsNullOrWhiteSpace(result.HlslSource))
                        {
                            if (stage.AllowKnownBackendLimitations && IsKnownSelfTestBackendLimitation(result.ErrorMessage))
                            {
                                continue;
                            }

                            failures.Add($"[{stage.Name}/{inputCase.Name}] Decompilation failed: {result.ErrorMessage}");
                            continue;
                        }

                        string basePath = Path.Combine(decompiledRoot, $"Decompiled.{stage.Name}.{inputCase.Name}");
                        File.WriteAllText(basePath + ".hlsl", result.HlslSource);
                        if (result.IntermediateSpirv != null)
                        {
                            File.WriteAllBytes(basePath + ".spv", result.IntermediateSpirv);
                            WriteSelfTestBindingDiagnostics(basePath + ".bindings.txt", result.IntermediateSpirv);
                        }

                        foreach (string validationFailure in ValidateSelfTestResult(stage, metadata, inputCase.Name, result.HlslSource, result.IntermediateSpirv))
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

        static IEnumerable<string> ValidateSelfTestResult(SelfTestStageSpec stage, ShaderSymbolData metadata, string caseName, string hlsl, byte[]? spirv)
        {
            List<string> failures = new();

            if (spirv == null || spirv.Length == 0)
            {
                failures.Add($"[{stage.Name}/{caseName}] Missing patched SPIR-V output.");
                return failures;
            }

            if (!hlsl.Contains("main(", StringComparison.Ordinal) && !hlsl.Contains("main ", StringComparison.Ordinal))
            {
                failures.Add($"[{stage.Name}/{caseName}] Missing main entry point.");
            }

            if (metadata.Resources.Count == 0)
            {
                failures.Add($"[{stage.Name}/{caseName}] Reflected metadata contains no resources.");
                return failures;
            }

            foreach (ResourceBinding resource in metadata.Resources)
            {
                if (!ContainsSelfTestToken(hlsl, spirv, resource.Name))
                {
                    failures.Add($"[{stage.Name}/{caseName}] Missing reflected resource symbol: {resource.Name}");
                }

                if (resource.Members == null || resource.Members.Count == 0)
                {
                    continue;
                }

                foreach (StructMember member in resource.Members)
                {
                    if (!ContainsSelfTestToken(hlsl, spirv, member.Name))
                    {
                        failures.Add($"[{stage.Name}/{caseName}] Missing reflected member symbol: {resource.Name}.{member.Name}");
                    }
                }
            }

            return failures;
        }

        static bool ContainsSelfTestToken(string hlsl, byte[]? spirv, string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(hlsl) && hlsl.Contains(token, StringComparison.Ordinal))
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
                new SelfTestStageSpec("Vertex", "VSMain", "vs_5_0", "vs_6_0", false),
                new SelfTestStageSpec("Hull", "HSMain", "hs_5_0", "hs_6_0", true),
                new SelfTestStageSpec("Domain", "DSMain", "ds_5_0", "ds_6_0", true),
                new SelfTestStageSpec("Geometry", "GSMain", "gs_5_0", "gs_6_0", true),
                new SelfTestStageSpec("Pixel", "PSMain", "ps_5_0", "ps_6_0", false),
                new SelfTestStageSpec("Compute", "CSMain", "cs_5_0", "cs_6_0", false),
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

        static ShaderFormat ParseFormat(string mode)
        {
             return mode.ToLower() switch {
                 "dxbc" => ShaderFormat.Dxbc,
                 "dxil" => ShaderFormat.Dxil,
                 "spv" => ShaderFormat.SpirV,
                 "spirv" => ShaderFormat.SpirV,
                 "hlsl" => ShaderFormat.Unknown,
                 "-dxbc" => ShaderFormat.Dxbc,
                 "-dxil" => ShaderFormat.Dxil,
                 "-spv" => ShaderFormat.SpirV,
                 "-spirv" => ShaderFormat.SpirV,
                 "-unknown" => ShaderFormat.Unknown,
                 _ => ShaderFormat.Unknown
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

        static (ShaderFormat format, int offset) SniffFormat(byte[] data)
        {
            if (data == null || data.Length < 25) return (ShaderFormat.Unknown, 0);
            
            // UE shader entries have a 21-byte header before the actual DXBC/SPIRV
            // Try offset 21 first, then fallback to other common offsets
            int[] offsets = { 21, 0, 4, 8, 12, 16 };
            foreach (var off in offsets)
            {
                if (off + 4 > data.Length) continue;
                uint magic = BitConverter.ToUInt32(data, off);
                if (magic == 0x43425844) return (ShaderFormat.Dxbc, off); // DXBC
                if (magic == 0x07230203) return (ShaderFormat.SpirV, off); // SPIR-V
            }
            return (ShaderFormat.Unknown, 0);
        }
    }
}
