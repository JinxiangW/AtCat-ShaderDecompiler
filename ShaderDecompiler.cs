using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Ruri.ShaderDecompiler.Intermediate;
using Ruri.ShaderDecompiler.Spirv;

namespace Ruri.ShaderDecompiler;

/// <summary>
/// The input shader binary format.
/// </summary>
public enum ShaderFormat
{
    Unknown = 0,
    Dxbc,   // Shader Model 5.x and below
    Dxil,   // Shader Model 6.x
    SpirV,  // Vulkan SPIR-V
}

/// <summary>
/// Result of a decompilation operation.
/// </summary>
public class DecompileResult
{
    public bool Success { get; set; }
    public string? HlslSource { get; set; }
    public string? ErrorMessage { get; set; }
    public byte[]? IntermediateSpirv { get; set; }
    public string? ShaderName { get; set; }
}

/// <summary>
/// Main shader decompiler class.
/// </summary>
public sealed class ShaderDecompiler : IDisposable
{
    private const int DefaultPerStepTimeoutMs = 30000;
    private readonly SpirvPatcher _patcher = new();
    private readonly StructuredCBufferRewriter _structuredCBufferRewriter = new();
    private readonly string _baseDir;
    private readonly string? _toolsDir;
    private bool _disposed;

    public string TempDir { get; set; }

    public ShaderDecompiler(string? tempDir = null, string? toolsDir = null)
    {
        _baseDir = AppDomain.CurrentDomain.BaseDirectory;
        TempDir = tempDir ?? _baseDir;
        _toolsDir = FindToolsDirectory(toolsDir);
    }

    public DecompileResult Decompile(
        byte[] binary,
        ShaderFormat format = ShaderFormat.Unknown,
        ShaderSymbolData? metadata = null,
        uint shaderModel = 51)
    {
        if (binary == null || binary.Length == 0)
        {
            return new DecompileResult
            {
                Success = false,
                ErrorMessage = "Shader binary is empty."
            };
        }

        if (_toolsDir == null)
        {
            return new DecompileResult
            {
                Success = false,
                ErrorMessage = "Decompiler tools not found. Expected dxbc2dxil.exe, dxil-spirv.exe, and spirv-cross.exe."
            };
        }

        string tempPrefix = $"temp_{Guid.NewGuid():N}";
        string tempDxbc = Path.Combine(TempDir, $"{tempPrefix}.dxbc");
        string tempDxil = Path.Combine(TempDir, $"{tempPrefix}.dxil");
        string tempSpv = Path.Combine(TempDir, $"{tempPrefix}.spv");
        string tempHlsl = Path.Combine(TempDir, $"{tempPrefix}.hlsl");

        try
        {
            var bundle = Unreal.UnrealShaderParser.Parse(binary);
            byte[] processingBinary = bundle.NativeCode;
            string? recoveredShaderName = (bundle.EngineMetadata as Unreal.UnrealShaderParser.UnrealMetadata)?.ShaderName;

            if (format == ShaderFormat.Unknown)
            {
                format = bundle.Architecture switch
                {
                    ShaderArchitecture.Dxbc when LooksLikeDxilContainer(processingBinary) => ShaderFormat.Dxil,
                    ShaderArchitecture.Dxbc => ShaderFormat.Dxbc,
                    ShaderArchitecture.Dxil => ShaderFormat.Dxil,
                    ShaderArchitecture.SpirV => ShaderFormat.SpirV,
                    _ => SniffShaderFormat(processingBinary)
                };
            }

            ShaderSymbolData finalMetadata = MergeMetadata(bundle.Symbols, metadata);

            byte[] spirv = format switch
            {
                ShaderFormat.Dxbc => ConvertDxbcToSpirv(processingBinary, tempDxbc, tempDxil, tempSpv),
                ShaderFormat.Dxil => ConvertDxilToSpirv(processingBinary, tempDxil, tempSpv),
                ShaderFormat.SpirV => processingBinary,
                _ => throw new ArgumentException($"Unsupported shader format: {format}")
            };

            // IMPORTANT:
            // DXIL -> dxil-spirv output is not equivalent to direct dxc -spirv output.
            // In practice the DXIL route often keeps resource variables and rewritten UBO types
            // in a different shape, so the SPIR-V symbol patch pass MUST still run after the
            // structured cbuffer rewrite. Do not short-circuit to "rewrite already happened" here,
            // otherwise DXIL resource names regress back to machine names like _8 / _29 and AI
            // assistants will be tempted to reintroduce HLSL text post-processing.
            spirv = _structuredCBufferRewriter.Rewrite(spirv, finalMetadata);
            byte[] patchedSpirv = PatchSpirvSymbols(spirv, finalMetadata);
            bool hasStructuredCbuffers = HasStructuredConstantBuffers(patchedSpirv, finalMetadata);
            bool useStructuredPath = _structuredCBufferRewriter.LastRewriteApplied || hasStructuredCbuffers;
            string hlsl = CompileSpirVToHlsl(patchedSpirv, shaderModel, tempSpv, tempHlsl, !useStructuredPath);

            return new DecompileResult
            {
                Success = true,
                HlslSource = hlsl,
                IntermediateSpirv = patchedSpirv,
                ShaderName = recoveredShaderName ?? finalMetadata.DebugName
            };
        }
        catch (Exception ex)
        {
            return new DecompileResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
        finally
        {
            DeleteIfExists(tempDxbc);
            DeleteIfExists(tempDxil);
            DeleteIfExists(tempSpv);
            DeleteIfExists(tempHlsl);
        }
    }

    public DecompileResult Decompile(
        byte[] binary,
        ShaderFormat format,
        ShaderSymbolMetadata? symbols,
        uint shaderModel = 50)
    {
        return Decompile(binary, format, ConvertMetadata(symbols), shaderModel);
    }

    private ShaderSymbolData MergeMetadata(ShaderSymbolMetadata? bundleSymbols, ShaderSymbolData? explicitMetadata)
    {
        var merged = new ShaderSymbolData();

        if (bundleSymbols != null)
        {
            merged = ConvertMetadata(bundleSymbols);
        }

        if (explicitMetadata == null)
        {
            return merged;
        }

        if (!string.IsNullOrWhiteSpace(explicitMetadata.EntryPoint))
        {
            merged.EntryPoint = explicitMetadata.EntryPoint;
        }

        if (explicitMetadata.Stage != ShaderStage.Unknown)
        {
            merged.Stage = explicitMetadata.Stage;
        }

        if (!string.IsNullOrWhiteSpace(explicitMetadata.DebugName))
        {
            merged.DebugName = explicitMetadata.DebugName;
        }

        foreach (var resource in explicitMetadata.Resources)
        {
            merged.Resources.RemoveAll(r =>
                r.Set == resource.Set &&
                r.Binding == resource.Binding &&
                r.RegisterType == resource.RegisterType);
            merged.Resources.Add(CloneResource(resource));
        }

        foreach (var constantBuffer in explicitMetadata.ConstantBuffers)
        {
            merged.ConstantBuffers.RemoveAll(cb => string.Equals(cb.Name, constantBuffer.Name, StringComparison.Ordinal));
            merged.ConstantBuffers.Add(CloneConstantBuffer(constantBuffer));
        }

        return merged;
    }

    private static ShaderSymbolData ConvertMetadata(ShaderSymbolMetadata? symbols)
    {
        var data = new ShaderSymbolData();
        if (symbols == null)
        {
            return data;
        }

        if (!string.IsNullOrWhiteSpace(symbols.EntryPoint))
        {
            data.EntryPoint = symbols.EntryPoint;
        }

        foreach (var resource in symbols.Resources)
        {
            data.Resources.Add(new ResourceBinding
            {
                Name = resource.Name ?? string.Empty,
                Set = resource.Set,
                Binding = resource.Binding,
                Type = ConvertResourceType(resource.Type),
                RegisterType = GuessRegisterType(ConvertResourceType(resource.Type)),
                Tag = resource.Slot ?? 0
            });
        }

        return data;
    }

    private byte[] ConvertDxbcToSpirv(byte[] rawDxbc, string tempDxbc, string tempDxil, string tempSpv)
    {
        byte[] actualDxbc = ExtractDxbc(rawDxbc);
        if (!LooksLikeDxbc(actualDxbc))
        {
            var packed = TryExtractPackedDxbc(rawDxbc);
            if (packed?.psDxbc != null && LooksLikeDxbc(packed.Value.psDxbc))
            {
                actualDxbc = packed.Value.psDxbc;
            }
        }

        if (!LooksLikeDxbc(actualDxbc))
        {
            throw new InvalidOperationException("Input does not contain a valid DXBC payload.");
        }

        File.WriteAllBytes(tempDxbc, actualDxbc);

        try
        {
            RunTool(
                new[]
                {
                    Path.Combine(_toolsDir!, "dxbc2dxil.exe"),
                    tempDxbc,
                    "-o",
                    tempDxil,
                    "-emit-bc"
                },
                DefaultPerStepTimeoutMs,
                "dxbc2dxil");

            if (!File.Exists(tempDxil))
            {
                throw new InvalidOperationException("dxbc2dxil did not produce a DXIL file.");
            }

            return ConvertDxilToSpirv(File.ReadAllBytes(tempDxil), tempDxil, tempSpv, true);
        }
        catch (Exception ex) when (IsDxbc2DxilUnavailable(ex))
        {
            // Some hosts expose dxilconv.dll without a usable converter registration.
            // Fall back to dxil-spirv's built-in DXBC path so DXBC inputs still decompile.
            RunTool(
                new[]
                {
                    Path.Combine(_toolsDir!, "dxil-spirv.exe"),
                    tempDxbc,
                    "--output",
                    tempSpv
                },
                DefaultPerStepTimeoutMs,
                "dxil-spirv (DXBC fallback)");

            if (!File.Exists(tempSpv))
            {
                throw new InvalidOperationException("dxil-spirv fallback did not produce a SPIR-V file.");
            }

            return File.ReadAllBytes(tempSpv);
        }
    }

    private byte[] ConvertDxilToSpirv(byte[] dxil, string tempDxil, string tempSpv, bool rawLlvm = false)
    {
        File.WriteAllBytes(tempDxil, dxil);

        var args = new List<string>
        {
            Path.Combine(_toolsDir!, "dxil-spirv.exe"),
            tempDxil,
            "--output",
            tempSpv
        };

        if (rawLlvm)
        {
            args.Add("--raw-llvm");
        }

        RunTool(args.ToArray(), DefaultPerStepTimeoutMs, "dxil-spirv");

        if (!File.Exists(tempSpv))
        {
            throw new InvalidOperationException("dxil-spirv did not produce a SPIR-V file.");
        }

        return File.ReadAllBytes(tempSpv);
    }

    private string CompileSpirVToHlsl(byte[] spirv, uint shaderModel, string tempSpv, string tempHlsl, bool flattenUbo)
    {
        File.WriteAllBytes(tempSpv, spirv);

        var args = new List<string>
        {
            Path.Combine(_toolsDir!, "spirv-cross.exe"),
            tempSpv,
            "--output",
            tempHlsl,
            "--hlsl"
        };

        if (flattenUbo)
        {
            args.Add("--flatten-ubo");
        }

        args.Add("--shader-model");
        args.Add(shaderModel.ToString());
        args.Add("--force-zero-initialized-variables");

        RunTool(args.ToArray(), DefaultPerStepTimeoutMs, "spirv-cross");

        if (!File.Exists(tempHlsl))
        {
            throw new InvalidOperationException("spirv-cross did not produce an HLSL file.");
        }

        return File.ReadAllText(tempHlsl, Encoding.UTF8);
    }

    private byte[] PatchSpirvSymbols(byte[] spirv, ShaderSymbolData metadata)
    {
        // IMPORTANT:
        // This pass is now the single source of truth for symbol restoration.
        // Do not add HLSL-side renaming back here. If a DXIL case still comes out with
        // names like _8 / _29 / ViewData_1_ViewProjection, the bug must be fixed in the
        // SPIR-V analysis or patch logic below, not by resurrecting PostProcessHlsl().
        if (metadata.Resources.Count == 0)
        {
            return spirv;
        }

        var detailedBindings = _patcher.AnalyzeBindingsDetailed(spirv);
        var patches = new List<(uint Id, string Name)>();
        var memberPatches = new List<(uint TypeId, uint MemberIndex, string Name)>();

        foreach (var resource in metadata.Resources)
        {
            if (string.IsNullOrWhiteSpace(resource.Name))
            {
                continue;
            }

            var matches = detailedBindings.Where(b => b.Set == resource.Set && b.Binding == resource.Binding).ToList();
            foreach (var match in matches)
            {
                if (!IsMetadataResourceMatch(resource, match.DescriptorType))
                {
                    continue;
                }

                if (match.DescriptorType == "UniformBuffer" && match.StructTypeId.HasValue && match.StructTypeId.Value != 0)
                {
                    patches.Add((match.StructTypeId.Value, resource.Name));
                    patches.Add((match.Id, resource.Name));

                    ConstantBuffer? constantBuffer = metadata.ConstantBuffers.FirstOrDefault(cb => string.Equals(cb.Name, resource.Name, StringComparison.Ordinal));
                    if (constantBuffer != null)
                    {
                        List<ConstantBufferParameter> allParameters = GetAllConstantBufferParameters(constantBuffer);
                        bool isCompressedMatrixBuffer =
                            match.StructMemberCount == 1 &&
                            allParameters.Count > 0 &&
                            allParameters.All(p => p.IsMatrix && p.Rows == 4 && p.Columns == 4);

                        if (isCompressedMatrixBuffer)
                        {
                            string combinedName = string.Join("_", allParameters.Select(p => p.ParamName));
                            memberPatches.Add((match.StructTypeId.Value, 0, combinedName));
                            continue;
                        }

                        bool patchedAnyMember = false;
                        foreach (var parameter in allParameters.Where(p => !string.IsNullOrWhiteSpace(p.ParamName)))
                        {
                            int? targetIndex = null;

                            if (parameter.Index >= 0 && match.MemberOffsets.Count > 0)
                            {
                                foreach (var offsetKvp in match.MemberOffsets)
                                {
                                    if (offsetKvp.Value == (uint)parameter.Index)
                                    {
                                        targetIndex = offsetKvp.Key;
                                        break;
                                    }
                                }
                            }

                            if (!targetIndex.HasValue && parameter.Index >= 0 && parameter.Index < match.StructMemberCount)
                            {
                                targetIndex = parameter.Index;
                            }

                            if (targetIndex.HasValue)
                            {
                                memberPatches.Add((match.StructTypeId.Value, (uint)targetIndex.Value, parameter.ParamName));
                                patchedAnyMember = true;
                            }
                        }
                    }
                }

                patches.Add((match.Id, resource.Name));
            }
        }

        if (patches.Count == 0 && memberPatches.Count == 0)
        {
            return spirv;
        }

        return _patcher.PatchByIds(spirv, patches, memberPatches);
    }

    private bool HasStructuredConstantBuffers(byte[] spirv, ShaderSymbolData metadata)
    {
        if (metadata.Resources.Count == 0)
        {
            return false;
        }

        var detailedBindings = _patcher.AnalyzeBindingsDetailed(spirv);
        foreach (var resource in metadata.Resources.Where(IsMetadataConstantBuffer))
        {
            bool matchedStructuredBuffer = detailedBindings.Any(binding =>
                binding.Set == resource.Set
                && binding.Binding == resource.Binding
                && string.Equals(binding.DescriptorType, "UniformBuffer", StringComparison.Ordinal)
                && binding.StructMemberCount > 1);

            if (matchedStructuredBuffer)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsMetadataConstantBuffer(ResourceBinding resource)
    {
        return resource.RegisterType == 'b';
    }

    private static bool IsRegisterTypeMatch(char registerType, string? descriptorType)
    {
        return descriptorType switch
        {
            "UniformBuffer" => registerType == 'b',
            "Sampler" => registerType == 's',
            "SampledImage" => registerType is 't' or 'u',
            "StorageBuffer" => registerType is 't' or 'u',
            "StorageImage" => registerType == 'u',
            _ => true
        };
    }

    private static bool IsMetadataResourceMatch(ResourceBinding resource, string? descriptorType)
    {
        if (!IsRegisterTypeMatch(resource.RegisterType, descriptorType))
        {
            return false;
        }

        return descriptorType switch
        {
            "UniformBuffer" => resource.RegisterType == 'b',
            "Sampler" => resource.RegisterType == 's',
            "StorageImage" => resource.RegisterType == 'u',
            "StorageBuffer" => resource.RegisterType == 'u',
            "SampledImage" => resource.RegisterType is 't' or 'u',
            _ => IsDescriptorTypeMatch(resource.Type, descriptorType)
        };
    }


    public static string? FindToolsDirectory(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && Directory.Exists(overridePath) && HasDirectTools(overridePath))
        {
            return overridePath;
        }

        string[] candidates =
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools"),
            AppDomain.CurrentDomain.BaseDirectory,
            Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Tools")),
            Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Tools")),
            Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "Tools")),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "x64", "ShaderTools"),
            Path.Combine(Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty) ?? string.Empty, "x64", "ShaderTools")
        };

        foreach (string candidate in candidates.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(candidate) && HasDirectTools(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static bool HasDirectTools(string dir)
    {
        return File.Exists(Path.Combine(dir, "dxbc2dxil.exe"))
            && File.Exists(Path.Combine(dir, "dxil-spirv.exe"))
            && File.Exists(Path.Combine(dir, "spirv-cross.exe"));
    }

    public static byte[] ExtractDxbc(byte[] data)
    {
        for (int i = 0; i <= data.Length - 4; i++)
        {
            if (data[i] == (byte)'D' && data[i + 1] == (byte)'X' && data[i + 2] == (byte)'B' && data[i + 3] == (byte)'C')
            {
                if (i == 0)
                {
                    return data;
                }

                var result = new byte[data.Length - i];
                Buffer.BlockCopy(data, i, result, 0, result.Length);
                return result;
            }
        }

        return data;
    }

    public static (byte[] psDxbc, byte[] vsDxbc)? TryExtractPackedDxbc(byte[] programData)
    {
        if (programData == null || programData.Length < 180)
        {
            return null;
        }

        int block1Len = BitConverter.ToInt32(programData, 4);
        int block2Len = BitConverter.ToInt32(programData, 8);
        int dxbcOffset = BitConverter.ToInt32(programData, 12);

        if (block1Len + block2Len != programData.Length || dxbcOffset != 176 || block1Len < 180 || block2Len < 4)
        {
            return null;
        }

        bool hasDxbc1 = dxbcOffset + 4 <= programData.Length
            && programData[dxbcOffset] == 0x44 && programData[dxbcOffset + 1] == 0x58
            && programData[dxbcOffset + 2] == 0x42 && programData[dxbcOffset + 3] == 0x43;
        bool hasDxbc2 = block1Len + 4 <= programData.Length
            && programData[block1Len] == 0x44 && programData[block1Len + 1] == 0x58
            && programData[block1Len + 2] == 0x42 && programData[block1Len + 3] == 0x43;

        if (!hasDxbc1 && !hasDxbc2)
        {
            return null;
        }

        byte[]? psDxbc = null;
        if (hasDxbc1)
        {
            int psLen = block1Len - dxbcOffset;
            psDxbc = new byte[psLen];
            Buffer.BlockCopy(programData, dxbcOffset, psDxbc, 0, psLen);
        }

        byte[]? vsDxbc = null;
        if (hasDxbc2)
        {
            vsDxbc = new byte[block2Len];
            Buffer.BlockCopy(programData, block1Len, vsDxbc, 0, block2Len);
        }

        if (psDxbc == null || vsDxbc == null)
        {
            return null;
        }

        return (psDxbc, vsDxbc);
    }

    private void RunTool(string[] args, int timeoutMs, string toolDisplayName)
    {
        var psi = new ProcessStartInfo
        {
            FileName = args[0],
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _toolsDir
        };

        for (int i = 1; i < args.Length; i++)
        {
            psi.ArgumentList.Add(args[i]);
        }

        psi.Environment["PATH"] = _toolsDir + Path.PathSeparator + (psi.Environment.ContainsKey("PATH") ? psi.Environment["PATH"] : string.Empty);

        using var process = Process.Start(psi);
        if (process == null)
        {
            throw new InvalidOperationException($"Failed to start {toolDisplayName}.");
        }

        var stderr = new StringBuilder();
        string stdout = string.Empty;
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        process.BeginErrorReadLine();
        stdout = process.StandardOutput.ReadToEnd();

        if (!process.WaitForExit(timeoutMs))
        {
            try
            {
                process.Kill(true);
            }
            catch
            {
            }

            throw new TimeoutException($"{toolDisplayName} timed out after {timeoutMs}ms.");
        }

        if (process.ExitCode != 0)
        {
            string details = string.Join(Environment.NewLine, new[]
            {
                Truncate(stderr.ToString(), 1000),
                Truncate(stdout, 1000)
            }.Where(s => !string.IsNullOrWhiteSpace(s)));
            throw new InvalidOperationException($"{toolDisplayName} failed: {details}");
        }
    }

    private static ShaderFormat SniffShaderFormat(byte[] data)
    {
        if (data.Length < 4)
        {
            return ShaderFormat.Unknown;
        }

        if (LooksLikeDxilContainer(data))
        {
            return ShaderFormat.Dxil;
        }

        if (LooksLikeDxbc(data))
        {
            return ShaderFormat.Dxbc;
        }

        if (data.Length >= 4 && data[0] == (byte)'D' && data[1] == (byte)'X' && data[2] == (byte)'I' && data[3] == (byte)'L')
        {
            return ShaderFormat.Dxil;
        }

        uint magic = BitConverter.ToUInt32(data, 0);
        return magic == 0x07230203 ? ShaderFormat.SpirV : ShaderFormat.Unknown;
    }

    private static bool LooksLikeDxbc(byte[] data)
    {
        return data.Length >= 4 && data[0] == (byte)'D' && data[1] == (byte)'X' && data[2] == (byte)'B' && data[3] == (byte)'C';
    }

    private static bool LooksLikeDxilContainer(byte[] data)
    {
        if (!LooksLikeDxbc(data) || data.Length < 32)
        {
            return false;
        }

        int chunkCount = BitConverter.ToInt32(data, 28);
        if (chunkCount <= 0 || chunkCount > 256)
        {
            return false;
        }

        int chunkTableOffset = 32;
        if (chunkTableOffset + (chunkCount * 4) > data.Length)
        {
            return false;
        }

        for (int i = 0; i < chunkCount; i++)
        {
            int chunkOffset = BitConverter.ToInt32(data, chunkTableOffset + (i * 4));
            if (chunkOffset < 0 || chunkOffset + 8 > data.Length)
            {
                continue;
            }

            if (data[chunkOffset] == (byte)'D'
                && data[chunkOffset + 1] == (byte)'X'
                && data[chunkOffset + 2] == (byte)'I'
                && data[chunkOffset + 3] == (byte)'L')
            {
                return true;
            }
        }

        return false;
    }

    private static ShaderResourceType ConvertResourceType(ResourceType type)
    {
        return type switch
        {
            ResourceType.UniformBuffer => ShaderResourceType.ConstantBuffer,
            ResourceType.Texture => ShaderResourceType.Texture,
            ResourceType.Sampler => ShaderResourceType.Sampler,
            ResourceType.UAV => ShaderResourceType.UAV,
            ResourceType.StructuredBuffer => ShaderResourceType.StructuredBuffer,
            ResourceType.RWTexture => ShaderResourceType.RWTexture2D,
            ResourceType.RWBuffer => ShaderResourceType.RWBuffer,
            _ => ShaderResourceType.Unknown
        };
    }

    private static char GuessRegisterType(ShaderResourceType type)
    {
        return NormalizeResourceType(type) switch
        {
            ShaderResourceType.ConstantBuffer => 'b',
            ShaderResourceType.Sampler => 's',
            ShaderResourceType.UAV => 'u',
            ShaderResourceType.RWBuffer => 'u',
            ShaderResourceType.StorageBuffer => 'u',
            ShaderResourceType.StorageImage => 'u',
            _ => 't'
        };
    }

    private static ShaderResourceType NormalizeResourceType(ShaderResourceType type)
    {
        return type switch
        {
            ShaderResourceType.ConstantBuffer => ShaderResourceType.ConstantBuffer,
            ShaderResourceType.Sampler => ShaderResourceType.Sampler,
            ShaderResourceType.SamplerComparison => ShaderResourceType.Sampler,
            ShaderResourceType.UAV => ShaderResourceType.UAV,
            ShaderResourceType.RWBuffer => ShaderResourceType.RWBuffer,
            ShaderResourceType.RWStructuredBuffer => ShaderResourceType.RWBuffer,
            ShaderResourceType.RWByteAddressBuffer => ShaderResourceType.RWBuffer,
            ShaderResourceType.StorageBuffer => ShaderResourceType.StorageBuffer,
            ShaderResourceType.StorageImage => ShaderResourceType.StorageImage,
            ShaderResourceType.StructuredBuffer => ShaderResourceType.StructuredBuffer,
            ShaderResourceType.Texture => ShaderResourceType.Texture,
            ShaderResourceType.SampledImage => ShaderResourceType.Texture,
            ShaderResourceType.SRV => ShaderResourceType.Texture,
            ShaderResourceType.Buffer => ShaderResourceType.Texture,
            ShaderResourceType.ByteAddressBuffer => ShaderResourceType.Texture,
            ShaderResourceType.Texture2D => ShaderResourceType.Texture,
            ShaderResourceType.Texture2DArray => ShaderResourceType.Texture,
            ShaderResourceType.Texture3D => ShaderResourceType.Texture,
            ShaderResourceType.TextureCube => ShaderResourceType.Texture,
            ShaderResourceType.TextureCubeArray => ShaderResourceType.Texture,
            ShaderResourceType.Texture2DMS => ShaderResourceType.Texture,
            ShaderResourceType.RWTexture2D => ShaderResourceType.UAV,
            ShaderResourceType.RWTexture2DArray => ShaderResourceType.UAV,
            ShaderResourceType.RWTexture3D => ShaderResourceType.UAV,
            _ => ShaderResourceType.Unknown
        };
    }

    private static bool IsDescriptorTypeMatch(ShaderResourceType resourceType, string? descriptorType)
    {
        ShaderResourceType normalized = NormalizeResourceType(resourceType);
        return descriptorType switch
        {
            "UniformBuffer" => normalized == ShaderResourceType.ConstantBuffer,
            "StorageBuffer" => normalized == ShaderResourceType.StorageBuffer || normalized == ShaderResourceType.RWBuffer || normalized == ShaderResourceType.StructuredBuffer || normalized == ShaderResourceType.UAV,
            "Sampler" => normalized == ShaderResourceType.Sampler,
            "SampledImage" => normalized == ShaderResourceType.Texture || normalized == ShaderResourceType.UAV || normalized == ShaderResourceType.RWBuffer || normalized == ShaderResourceType.StructuredBuffer || normalized == ShaderResourceType.StorageBuffer,
            "StorageImage" => normalized == ShaderResourceType.UAV || normalized == ShaderResourceType.StorageImage,
            _ => true
        };
    }

    private static ResourceBinding CloneResource(ResourceBinding resource)
    {
        return new ResourceBinding
        {
            Name = resource.Name,
            Binding = resource.Binding,
            Set = resource.Set,
            Type = resource.Type,
            Tag = resource.Tag,
            RegisterType = resource.RegisterType,
        };
    }

    private static ConstantBuffer CloneConstantBuffer(ConstantBuffer constantBuffer)
    {
        return new ConstantBuffer
        {
            Name = constantBuffer.Name,
            UsedSize = constantBuffer.UsedSize,
            Partial = constantBuffer.Partial,
            CBParams = constantBuffer.CBParams.Select(parameter => new ConstantBufferParameter
            {
                ParamName = parameter.ParamName,
                Index = parameter.Index,
                ParamType = parameter.ParamType,
                Rows = parameter.Rows,
                Columns = parameter.Columns,
                IsMatrix = parameter.IsMatrix,
                ArraySize = parameter.ArraySize
            }).ToList(),
            StructParams = constantBuffer.StructParams.Select(structParameter => new StructParameter
            {
                Name = structParameter.Name,
                Index = structParameter.Index,
                ArraySize = structParameter.ArraySize,
                Size = structParameter.Size,
                CBParams = structParameter.CBParams.Select(parameter => new ConstantBufferParameter
                {
                    ParamName = parameter.ParamName,
                    Index = parameter.Index,
                    ParamType = parameter.ParamType,
                    Rows = parameter.Rows,
                    Columns = parameter.Columns,
                    IsMatrix = parameter.IsMatrix,
                    ArraySize = parameter.ArraySize
                }).ToList()
            }).ToList()
        };
    }

    private static List<ConstantBufferParameter> GetAllConstantBufferParameters(ConstantBuffer constantBuffer)
    {
        var result = new List<ConstantBufferParameter>(constantBuffer.CBParams);
        foreach (StructParameter structParameter in constantBuffer.StructParams)
        {
            result.AddRange(structParameter.CBParams);
        }

        return result;
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text.Substring(0, maxLength);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static bool IsDxbc2DxilUnavailable(Exception ex)
    {
        string message = ex.ToString();
        return message.Contains("REGDB_E_CLASSNOTREG", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Class not registered", StringComparison.OrdinalIgnoreCase)
            || message.Contains("类未注册", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
