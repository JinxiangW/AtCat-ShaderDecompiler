using System.Diagnostics;
using Newtonsoft.Json;
using Ruri.ShaderDecompiler.Testing.Assets.Shaders;
using Ruri.ShaderDecompiler.Testing.Compilation;
using Ruri.ShaderDecompiler.Spirv;

namespace Ruri.ShaderDecompiler.Testing.SelfTest;

internal static class SelfTestRunner
{
    public static ShaderSymbolData CreateSyntheticMetadata()
    {
        return CreateSyntheticMetadataCore();
    }

    public static int Run(string outputRoot)
    {
        Directory.CreateDirectory(outputRoot);
        CleanupOutputRoot(outputRoot);
        string decompiledRoot = Path.Combine(outputRoot, "Decompiled");
        Directory.CreateDirectory(decompiledRoot);

        string toolsDir = ResolveToolsDirectory();
        string? dxcExe = ResolveDxcExecutable(toolsDir);

        if (dxcExe == null)
        {
            Console.Error.WriteLine("Self-test failed: dxc.exe not found under Tools or known SDK locations.");
            return 1;
        }

        string shaderPath = Path.Combine(outputRoot, "SelfTestPbr.hlsl");
        string dxilPath = Path.Combine(outputRoot, "SelfTestPbr.dxil");
        string spvPath = Path.Combine(outputRoot, "SelfTestPbr.spv");
        string dxbcRouteDxilPath = Path.Combine(outputRoot, "SelfTestPbr.dxbc-route.dxil");
        string dxbcRouteSpvPath = Path.Combine(outputRoot, "SelfTestPbr.dxbc-route.spv");

        try
        {
            File.WriteAllText(shaderPath, SelfTestAssets.CommonShader);

            byte[] dxbc = D3DCompiler.Compile(SelfTestAssets.CommonShader, "PSMain", "ps_5_0");
            File.WriteAllBytes(Path.Combine(outputRoot, "SelfTestPbr.dxbc"), dxbc);

            RunProcess(dxcExe, $"-T ps_6_0 -E PSMain -Fo \"{dxilPath}\" \"{shaderPath}\"", dxilPath);
            RunProcess(dxcExe, $"-spirv -fspv-target-env=vulkan1.1 -T ps_6_0 -E PSMain -Fo \"{spvPath}\" \"{shaderPath}\"", spvPath);

            var metadata = CreateSyntheticMetadataCore();
            File.WriteAllText(Path.Combine(outputRoot, "SelfTestPbr.symbols.json"), JsonConvert.SerializeObject(metadata, Formatting.Indented));
            var previewRewriter = new StructuredCBufferRewriter();
            byte[] previewStructuredSpv = previewRewriter.Rewrite(File.ReadAllBytes(spvPath), metadata);
            File.WriteAllBytes(Path.Combine(decompiledRoot, "Preview.Structured.DirectSpirv.spv"), previewStructuredSpv);

            RunProcess(Path.Combine(toolsDir, "dxbc2dxil.exe"), $"\"{Path.Combine(outputRoot, "SelfTestPbr.dxbc")}\" -o \"{dxbcRouteDxilPath}\" -emit-bc", dxbcRouteDxilPath);
            RunProcess(Path.Combine(toolsDir, "dxil-spirv.exe"), $"\"{dxbcRouteDxilPath}\" --output \"{dxbcRouteSpvPath}\" --raw-llvm", dxbcRouteSpvPath);
            byte[] dxbcRouteStructuredSpv = previewRewriter.Rewrite(File.ReadAllBytes(dxbcRouteSpvPath), metadata);
            File.WriteAllBytes(Path.Combine(decompiledRoot, "Preview.Structured.DxbcRoute.spv"), dxbcRouteStructuredSpv);

            using var decompiler = new ShaderDecompiler(outputRoot, toolsDir);

            var cases = new[]
            {
                (Name: "Dxil", Binary: File.ReadAllBytes(dxilPath), Format: ShaderFormat.Dxil),
                (Name: "Dxbc", Binary: dxbc, Format: ShaderFormat.Dxbc),
                (Name: "Spirv", Binary: File.ReadAllBytes(spvPath), Format: ShaderFormat.SpirV),
            };

            foreach (var testCase in cases)
            {
                var result = decompiler.Decompile(testCase.Binary, testCase.Format, metadata, 51);
                if (!result.Success || string.IsNullOrWhiteSpace(result.HlslSource))
                {
                    Console.Error.WriteLine($"Self-test {testCase.Name} failed: {result.ErrorMessage}");
                    return 2;
                }

                File.WriteAllText(Path.Combine(decompiledRoot, $"Decompiled.{testCase.Name}.hlsl"), result.HlslSource);
                if (result.IntermediateSpirv != null)
                {
                    File.WriteAllBytes(Path.Combine(decompiledRoot, $"Decompiled.{testCase.Name}.spv"), result.IntermediateSpirv);
                }

                if (string.Equals(testCase.Name, "Dxil", StringComparison.Ordinal) && result.IntermediateSpirv != null)
                {
                    WriteBindingDiagnostics(Path.Combine(decompiledRoot, "Diagnostics.DxilBindings.txt"), result.IntermediateSpirv);
                }

                ValidateResult(testCase.Name, result.HlslSource, result.IntermediateSpirv);
            }

            Console.WriteLine($"Self-test passed. Output: {outputRoot}");
            return 0;
        }
        finally
        {
            DeleteIfExists(shaderPath);
            DeleteIfExists(dxbcRouteDxilPath);
            DeleteIfExists(dxbcRouteSpvPath);
        }
    }

    private static void CleanupOutputRoot(string outputRoot)
    {
        DeleteIfExists(Path.Combine(outputRoot, "SelfTestPbr.hlsl"));
        DeleteIfExists(Path.Combine(outputRoot, "SelfTestPbr.dxbc-route.dxil"));
        DeleteIfExists(Path.Combine(outputRoot, "SelfTestPbr.dxbc-route.spv"));
        DeleteIfExists(Path.Combine(outputRoot, "Decompiled", "Diagnostics.DxilBindings.txt"));
    }

    private static void WriteBindingDiagnostics(string outputPath, byte[] spirv)
    {
        var patcher = new SpirvPatcher();
        var bindings = patcher.AnalyzeBindingsDetailed(spirv);
        using var writer = new StreamWriter(outputPath, false);
        foreach (var binding in bindings)
        {
            writer.WriteLine($"Id={binding.Id} Set={binding.Set} Binding={binding.Binding} Type={binding.DescriptorType} CurrentName={binding.CurrentName} StructTypeId={binding.StructTypeId} StructMemberCount={binding.StructMemberCount}");
        }
    }

    private static ShaderSymbolData CreateSyntheticMetadataCore()
    {
        return new ShaderSymbolData
        {
            Stage = ShaderStage.Pixel,
            DebugName = "SyntheticPbrPixel",
            Resources =
            {
                new ResourceBinding
                {
                    Name = "ViewData",
                    Set = 0,
                    Binding = 0,
                    Type = ShaderResourceType.ConstantBuffer,
                    RegisterType = 'b',
                    Members = new List<StructMember>
                    {
                        new StructMember { Name = "ViewProjection", Index = 0, ByteOffset = 0, ByteSize = 64, TypeName = "float4x4" },
                        new StructMember { Name = "CameraPosition_Exposure", Index = 1, ByteOffset = 64, ByteSize = 16, TypeName = "float4" },
                        new StructMember { Name = "LightDirection_Intensity", Index = 2, ByteOffset = 80, ByteSize = 16, TypeName = "float4" },
                        new StructMember { Name = "LightColor_RoughnessBias", Index = 3, ByteOffset = 96, ByteSize = 16, TypeName = "float4" },
                    }
                },
                new ResourceBinding
                {
                    Name = "MaterialParams",
                    Set = 0,
                    Binding = 1,
                    Type = ShaderResourceType.ConstantBuffer,
                    RegisterType = 'b',
                    Members = new List<StructMember>
                    {
                        new StructMember { Name = "BaseColorFactor", Index = 0, ByteOffset = 0, ByteSize = 16, TypeName = "float4" },
                        new StructMember { Name = "SurfaceParams", Index = 1, ByteOffset = 16, ByteSize = 16, TypeName = "float4" },
                        new StructMember { Name = "EmissiveColor_AlphaCutoff", Index = 2, ByteOffset = 32, ByteSize = 16, TypeName = "float4" },
                    }
                },
                new ResourceBinding { Name = "AlbedoTexture", Set = 0, Binding = 0, Type = ShaderResourceType.Texture, RegisterType = 't' },
                new ResourceBinding { Name = "NormalTexture", Set = 0, Binding = 1, Type = ShaderResourceType.Texture, RegisterType = 't' },
                new ResourceBinding { Name = "MaterialTexture", Set = 0, Binding = 2, Type = ShaderResourceType.Texture, RegisterType = 't' },
                new ResourceBinding { Name = "EmissiveTexture", Set = 0, Binding = 3, Type = ShaderResourceType.Texture, RegisterType = 't' },
                new ResourceBinding { Name = "ReflectionProbe", Set = 0, Binding = 4, Type = ShaderResourceType.Texture, RegisterType = 't' },
                new ResourceBinding { Name = "LinearWrapSampler", Set = 0, Binding = 0, Type = ShaderResourceType.Sampler, RegisterType = 's' },
            }
        };
    }

    private static void ValidateResult(string name, string hlsl, byte[]? spirv)
    {
        string[] anyTextureTokens =
        {
            "AlbedoTexture",
            "Texture2D<float4>",
            "TextureCube<float4>"
        };

        if (!anyTextureTokens.Any(token => hlsl.Contains(token, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Self-test {name} missing texture declarations.");
        }

        string[] requiredTokens =
        {
            "Sample(",
            "main("
        };

        foreach (string token in requiredTokens)
        {
            if (!hlsl.Contains(token, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Self-test {name} missing expected token: {token}");
            }
        }

        if (string.Equals(name, "Spirv", StringComparison.OrdinalIgnoreCase))
        {
            string[] structuredTokens =
            {
                "cbuffer ViewData",
                "cbuffer MaterialParams",
                "ViewProjection",
                "BaseColorFactor"
            };

            foreach (string token in structuredTokens)
            {
                if (!hlsl.Contains(token, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Self-test {name} missing expected token: {token}");
                }
            }
        }
        else
        {
            string[] structuredTokens =
            {
                "cbuffer ViewData",
                "cbuffer MaterialParams",
                "ViewProjection",
                "BaseColorFactor"
            };

            foreach (string token in structuredTokens)
            {
                if (!hlsl.Contains(token, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Self-test {name} missing expected token: {token}");
                }
            }
        }

        if (spirv == null || spirv.Length == 0)
        {
            throw new InvalidOperationException($"Self-test {name} missing patched SPIR-V output.");
        }
    }

    private static bool ContainsAsciiToken(byte[] bytes, string token)
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

    private static void RunProcess(string exePath, string arguments, string? expectedOutputPath = null)
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

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string? FindFile(string rootDir, string fileName)
    {
        if (!Directory.Exists(rootDir))
        {
            return null;
        }

        return Directory.EnumerateFiles(rootDir, fileName, SearchOption.AllDirectories).FirstOrDefault();
    }

    private static string? FindDxcExecutable(string rootDir)
    {
        if (!Directory.Exists(rootDir))
        {
            return null;
        }

        string[] preferred =
        {
            Path.Combine(rootDir, "bin", "x64", "dxc.exe"),
            Path.Combine(rootDir, "x64", "dxc.exe"),
        };

        foreach (string candidate in preferred)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Directory.EnumerateFiles(rootDir, "dxc.exe", SearchOption.AllDirectories)
            .OrderBy(path => path.Contains("x64", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault();
    }

    private static string? ResolveDxcExecutable(string toolsDir)
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
            string? exe = FindDxcExecutable(root);
            if (!string.IsNullOrWhiteSpace(exe))
            {
                return exe;
            }
        }

        return null;
    }

    private static string ResolveToolsDirectory()
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
}
