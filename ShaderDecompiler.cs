using System.Diagnostics;
using System.Text;
using Ruri.ShaderTools.Spirv;
using Ruri.ShaderTools.Unreal;

namespace Ruri.ShaderTools;

/// <summary>
/// The input shader binary format.
/// </summary>
public enum ShaderArchitecture
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
    public string? StructuredRewriteSummary { get; set; }
    public ShaderSymbolData? FinalMetadata { get; set; }
    public IReadOnlyList<string>? UnrealOptionalDataKeys { get; set; }
    public IReadOnlyList<string>? UnrealUniformBufferNames { get; set; }
    public string? UnrealShaderCodePackedResourceCounts { get; set; }
    public string? UnrealShaderCodeResourceMasks { get; set; }
    public string? UnrealShaderCodeFeatures { get; set; }
    public string? UnrealShaderCodeName { get; set; }
    public string? UnrealShaderCodeVendorExtension { get; set; }
    public string? UnrealSm6Flag { get; set; }
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
        ShaderArchitecture format = ShaderArchitecture.Unknown,
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
            var unrealMetadata = bundle.EngineMetadata as Unreal.UnrealShaderParser.UnrealMetadata;
            string? recoveredShaderName = unrealMetadata?.ShaderName;

            if (format == ShaderArchitecture.Unknown)
            {
                format = bundle.Architecture switch
                {
                    ShaderArchitecture.Dxbc when LooksLikeDxilContainer(processingBinary) => ShaderArchitecture.Dxil,
                    ShaderArchitecture.Dxbc => ShaderArchitecture.Dxbc,
                    ShaderArchitecture.Dxil => ShaderArchitecture.Dxil,
                    ShaderArchitecture.SpirV => ShaderArchitecture.SpirV,
                    _ => SniffShaderFormat(processingBinary)
                };
            }

            ShaderSymbolData finalMetadata = MergeMetadata(bundle.Symbols, metadata);
            finalMetadata.RefreshCompatibilityViews();
            NormalizeConstantBuffersForAlignment(finalMetadata);
            finalMetadata.RefreshCompatibilityViews();

            byte[] spirv = format switch
            {
                ShaderArchitecture.Dxbc => ConvertDxbcToSpirv(processingBinary, tempDxbc, tempDxil, tempSpv),
                ShaderArchitecture.Dxil => ConvertDxilToSpirv(processingBinary, tempDxil, tempSpv),
                ShaderArchitecture.SpirV => processingBinary,
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
            // USC-driven metadata already describes the intended cbuffer structure. Forcing
            // spirv-cross to flatten UBOs destroys struct member recovery even when rewrite did
            // not trigger, so keep structured UBO emission enabled for all metadata-guided paths.
            string hlsl = CompileSpirVToHlsl(patchedSpirv, shaderModel, tempSpv, tempHlsl);

            return new DecompileResult
            {
                Success = true,
                HlslSource = hlsl,
                IntermediateSpirv = patchedSpirv,
                ShaderName = recoveredShaderName ?? finalMetadata.DebugName,
                StructuredRewriteSummary = _structuredCBufferRewriter.LastRewriteSummary,
                FinalMetadata = finalMetadata,
                UnrealOptionalDataKeys = unrealMetadata?.OptionalDataKeys,
                UnrealUniformBufferNames = unrealMetadata?.UniformBufferNames,
                UnrealShaderCodePackedResourceCounts = unrealMetadata?.ShaderCodePackedResourceCounts is Unreal.UnrealShaderParser.FShaderCodePackedResourceCounts packed
                    ? $"UsageFlags={packed.UsageFlags} NumSamplers={packed.NumSamplers} NumSRVs={packed.NumSRVs} NumCBs={packed.NumCBs} NumUAVs={packed.NumUAVs}"
                    : null,
                UnrealShaderCodeResourceMasks = unrealMetadata?.ShaderCodeResourceMasks is Unreal.UnrealShaderParser.FShaderCodeResourceMasks masks
                    ? $"UAVMask=0x{masks.UAVMask:X8}"
                    : null,
                UnrealShaderCodeFeatures = unrealMetadata?.ShaderCodeFeatures is Unreal.UnrealShaderParser.FShaderCodeFeatures features
                    ? $"CodeFeatures=0x{features.CodeFeatures:X2}"
                    : null,
                UnrealShaderCodeName = unrealMetadata?.ShaderCodeName?.Value,
                UnrealShaderCodeVendorExtension = unrealMetadata?.ShaderCodeVendorExtension != null
                    ? $"RawSize={unrealMetadata.ShaderCodeVendorExtension.RawData.Length}"
                    : null,
                UnrealSm6Flag = unrealMetadata?.IsSm6Shader.HasValue == true
                    ? unrealMetadata.IsSm6Shader.Value.ToString()
                    : null
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

    private ShaderSymbolData MergeMetadata(ShaderSymbolData? bundleSymbols, ShaderSymbolData? explicitMetadata)
    {
        var merged = new ShaderSymbolData();

        if (bundleSymbols != null)
        {
            merged = CloneMetadata(bundleSymbols);
        }

        if (explicitMetadata == null)
        {
            return merged;
        }

        if (!string.IsNullOrWhiteSpace(explicitMetadata.EntryPoint))
        {
            merged.EntryPoint = explicitMetadata.EntryPoint;
        }

        if (!string.IsNullOrWhiteSpace(explicitMetadata.DebugName))
        {
            merged.DebugName = explicitMetadata.DebugName;
        }

        foreach (BufferBinding binding in explicitMetadata.ConstantBufferBindings)
        {
            merged.ConstantBufferBindings.RemoveAll(existing =>
                existing.Set == binding.Set &&
                existing.Index == binding.Index &&
                string.Equals(existing.Name, binding.Name, StringComparison.Ordinal));
            merged.ConstantBufferBindings.Add(CloneBufferBinding(binding));
        }

        foreach (TextureParameter texture in explicitMetadata.TextureParameters)
        {
            merged.TextureParameters.RemoveAll(existing =>
                existing.Set == texture.Set &&
                existing.Index == texture.Index &&
                string.Equals(existing.Name, texture.Name, StringComparison.Ordinal));
            merged.TextureParameters.Add(CloneTextureParameter(texture));
        }

        foreach (SamplerParameter sampler in explicitMetadata.Samplers)
        {
            merged.Samplers.RemoveAll(existing => existing.Set == sampler.Set && existing.Index == sampler.Index);
            merged.Samplers.Add(CloneSamplerParameter(sampler));
        }

        foreach (BufferBinding buffer in explicitMetadata.Buffers)
        {
            merged.Buffers.RemoveAll(existing =>
                existing.Set == buffer.Set &&
                existing.Index == buffer.Index &&
                string.Equals(existing.Name, buffer.Name, StringComparison.Ordinal));
            merged.Buffers.Add(CloneBufferBinding(buffer));
        }

        foreach (UAVParameter uav in explicitMetadata.UAVs)
        {
            merged.UAVs.RemoveAll(existing =>
                existing.Set == uav.Set &&
                existing.Index == uav.Index &&
                string.Equals(existing.Name, uav.Name, StringComparison.Ordinal));
            merged.UAVs.Add(CloneUavParameter(uav));
        }

        foreach (var constantBuffer in explicitMetadata.ConstantBuffers)
        {
            merged.ConstantBuffers.RemoveAll(cb => string.Equals(cb.Name, constantBuffer.Name, StringComparison.Ordinal));
            merged.ConstantBuffers.Add(CloneConstantBuffer(constantBuffer));
        }

        merged.RefreshCompatibilityViews();

        return merged;
    }

    private static void NormalizeConstantBuffersForAlignment(ShaderSymbolData metadata)
    {
        foreach (ConstantBuffer constantBuffer in metadata.ConstantBuffers)
        {
            if (constantBuffer.CBParams.Count <= 1)
            {
                continue;
            }

            List<ConstantBufferParameter> ordered = constantBuffer.CBParams
                .OrderBy(static parameter => parameter.Index)
                .ToList();

            int firstSyntheticIndex = ordered.FindIndex(static parameter => !IsNaturalTopLevelParameter(parameter));
            if (firstSyntheticIndex < 0)
            {
                continue;
            }

            if (firstSyntheticIndex == 0)
            {
                int syntheticStructureIndex = constantBuffer.StructParams.Length;
                var wrappedStructs = new List<StructParameter>();
                FlushAlignmentGroup(
                    ordered.Select(CloneParameter).ToList(),
                    0,
                    Math.Max(constantBuffer.Size, ordered[0].Index),
                    wrappedStructs,
                    [],
                    ref syntheticStructureIndex);

                if (wrappedStructs.Count == 0)
                {
                    continue;
                }

                constantBuffer.CBParams = [];
                constantBuffer.StructParams = constantBuffer.StructParams.Concat(wrappedStructs).ToArray();
                continue;
            }

            List<ConstantBufferParameter> preservedTopLevel = ordered
                .Take(firstSyntheticIndex)
                .Select(CloneParameter)
                .ToList();

            List<ConstantBufferParameter> syntheticGroup = ordered
                .Skip(firstSyntheticIndex)
                .Select(CloneParameter)
                .ToList();

            if (syntheticGroup.Count == 0)
            {
                continue;
            }

            int structureIndex = constantBuffer.StructParams.Length;
            var syntheticStructs = new List<StructParameter>();
            FlushAlignmentGroup(
                syntheticGroup,
                syntheticGroup[0].Index,
                Math.Max(constantBuffer.Size, syntheticGroup[0].Index),
                syntheticStructs,
                preservedTopLevel,
                ref structureIndex);

            if (syntheticStructs.Count == 0)
            {
                continue;
            }

            constantBuffer.CBParams = preservedTopLevel.OrderBy(static parameter => parameter.Index).ToList();
            constantBuffer.StructParams = constantBuffer.StructParams.Concat(syntheticStructs).ToArray();
        }
    }

    private static bool IsNaturalTopLevelParameter(ConstantBufferParameter parameter)
    {
        if (parameter.Index % 16 != 0)
        {
            return false;
        }

        if (parameter.IsMatrix)
        {
            return true;
        }

        return parameter.Rows == 4 && parameter.Columns == 1 && Math.Max(parameter.ArraySize, 1) == 1;
    }

    private static void FlushAlignmentGroup(
        List<ConstantBufferParameter> group,
        int groupStart,
        int groupEnd,
        List<StructParameter> syntheticStructs,
        List<ConstantBufferParameter> remainingTopLevel,
        ref int structureIndex)
    {
        if (group.Count == 1)
        {
            remainingTopLevel.Add(group[0]);
            return;
        }

        syntheticStructs.Add(new StructParameter
        {
            Name = $"_DummyStruct{structureIndex++}",
            Index = groupStart,
            ArraySize = 1,
            StructSize = Math.Max(0, groupEnd - groupStart),
            CBParams = BuildAlignedSyntheticStructMembers(group, groupStart, groupEnd)
        });
    }

    private static List<ConstantBufferParameter> BuildAlignedSyntheticStructMembers(List<ConstantBufferParameter> group, int groupStart, int groupEnd)
    {
        var members = new List<ConstantBufferParameter>();
        int padIndex = 0;
        int cursor = groupStart;

        foreach (ConstantBufferParameter parameter in group.OrderBy(static parameter => parameter.Index))
        {
            if (parameter.Index > cursor)
            {
                AppendPaddingMembers(members, cursor, parameter.Index - cursor, ref padIndex);
            }

            members.Add(CloneParameter(parameter));
            cursor = parameter.Index + GetMetadataParameterByteSize(parameter);
        }

        if (cursor < groupEnd)
        {
            AppendPaddingMembers(members, cursor, groupEnd - cursor, ref padIndex);
        }

        return members;
    }

    private static void AppendPaddingMembers(List<ConstantBufferParameter> members, int startOffset, int byteCount, ref int padIndex)
    {
        int remaining = byteCount;
        int cursor = startOffset;
        while (remaining > 0)
        {
            int bytesUntilRegisterBoundary = 16 - (cursor % 16);
            if (bytesUntilRegisterBoundary == 16)
            {
                bytesUntilRegisterBoundary = 16;
            }

            int chunkBytes = Math.Min(remaining, bytesUntilRegisterBoundary);
            int componentCount = Math.Min(4, chunkBytes / 4);
            if (componentCount <= 0)
            {
                break;
            }

            members.Add(new ConstantBufferParameter
            {
                ParamName = $"_pad{padIndex++}",
                ParamType = ShaderParamType.Float,
                Rows = componentCount,
                Columns = 1,
                IsMatrix = false,
                ArraySize = 1,
                Index = cursor
            });

            int size = componentCount * 4;
            cursor += size;
            remaining -= size;
        }
    }

    private static int GetMetadataParameterByteSize(ConstantBufferParameter parameter)
    {
        if (parameter.IsMatrix)
        {
            return parameter.Columns * 16 * Math.Max(parameter.ArraySize, 1);
        }

        return parameter.Rows * parameter.Columns * Math.Max(parameter.ArraySize, 1) * 4;
    }

    private static ConstantBufferParameter CloneParameter(ConstantBufferParameter parameter)
    {
        return new ConstantBufferParameter
        {
            ParamName = parameter.ParamName,
            ParamType = parameter.ParamType,
            Rows = parameter.Rows,
            Columns = parameter.Columns,
            IsMatrix = parameter.IsMatrix,
            ArraySize = parameter.ArraySize,
            Index = parameter.Index
        };
    }

    private static int? FindMemberIndexByMetadata(SpirvBindingInfo match, int byteOffset)
    {
        if (byteOffset >= 0 && match.MemberOffsets.Count > 0)
        {
            foreach (var offsetKvp in match.MemberOffsets)
            {
                if (offsetKvp.Value == (uint)byteOffset)
                {
                    return offsetKvp.Key;
                }
            }

            return null;
        }

        return null;
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

    private string CompileSpirVToHlsl(byte[] spirv, uint shaderModel, string tempSpv, string tempHlsl)
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
        if (metadata.GetResourceBindingCount() == 0)
        {
            return spirv;
        }

        var detailedBindings = _patcher.AnalyzeBindingsDetailed(spirv);
        var patches = new List<(uint Id, string Name)>();
        var memberPatches = new List<(uint TypeId, uint MemberIndex, string Name)>();

        foreach (var resource in metadata.EnumerateResourceBindings())
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

                string resolvedResourceName = match.DescriptorType == "UniformBuffer"
                    ? _structuredCBufferRewriter.GetResolvedBufferName(resource.Set, resource.Binding) ?? resource.Name
                    : resource.Name;

                if (match.DescriptorType == "UniformBuffer" && match.StructTypeId.HasValue && match.StructTypeId.Value != 0)
                {
                    patches.Add((match.StructTypeId.Value, resolvedResourceName));
                    patches.Add((match.Id, resolvedResourceName));

                    ConstantBuffer? constantBuffer = metadata.ConstantBuffers.FirstOrDefault(cb => string.Equals(cb.Name, resolvedResourceName, StringComparison.Ordinal));
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

                        foreach (StructParameter structParameter in constantBuffer.StructParams.Where(s => !string.IsNullOrWhiteSpace(s.Name)))
                        {
                            int? structMemberIndex = FindMemberIndexByMetadata(match, structParameter.Index);

                            if (structMemberIndex.HasValue)
                            {
                                memberPatches.Add((match.StructTypeId.Value, (uint)structMemberIndex.Value, structParameter.Name));
                            }
                        }

                        foreach (var parameter in constantBuffer.CBParams.Where(p => !string.IsNullOrWhiteSpace(p.ParamName)))
                        {
                            int? targetIndex = FindMemberIndexByMetadata(match, parameter.Index);

                            if (targetIndex.HasValue)
                            {
                                memberPatches.Add((match.StructTypeId.Value, (uint)targetIndex.Value, parameter.ParamName));
                            }
                        }
                    }
                }

                patches.Add((match.Id, resolvedResourceName));
            }
        }

        if (patches.Count == 0 && memberPatches.Count == 0)
        {
            return spirv;
        }

        return _patcher.PatchByIds(spirv, patches, memberPatches);
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

    private static bool IsMetadataResourceMatch((string Name, int Binding, int Set, ShaderResourceType Type, char RegisterType) resource, string? descriptorType)
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

    private static ShaderArchitecture SniffShaderFormat(byte[] data)
    {
        if (data.Length < 4)
        {
            return ShaderArchitecture.Unknown;
        }

        if (LooksLikeDxilContainer(data))
        {
            return ShaderArchitecture.Dxil;
        }

        if (LooksLikeDxbc(data))
        {
            return ShaderArchitecture.Dxbc;
        }

        if (data.Length >= 4 && data[0] == (byte)'D' && data[1] == (byte)'X' && data[2] == (byte)'I' && data[3] == (byte)'L')
        {
            return ShaderArchitecture.Dxil;
        }

        uint magic = BitConverter.ToUInt32(data, 0);
        return magic == 0x07230203 ? ShaderArchitecture.SpirV : ShaderArchitecture.Unknown;
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

    private static ShaderSymbolData CloneMetadata(ShaderSymbolData metadata)
    {
        return new ShaderSymbolData
        {
            EntryPoint = metadata.EntryPoint,
            DebugName = metadata.DebugName,
            ConstantBuffers = metadata.ConstantBuffers.Select(CloneConstantBuffer).ToList(),
            ConstantBufferBindings = metadata.ConstantBufferBindings.Select(CloneBufferBinding).ToList(),
            TextureParameters = metadata.TextureParameters.Select(CloneTextureParameter).ToList(),
            Samplers = metadata.Samplers.Select(CloneSamplerParameter).ToList(),
            Buffers = metadata.Buffers.Select(CloneBufferBinding).ToList(),
            UAVs = metadata.UAVs.Select(CloneUavParameter).ToList(),
        };
    }

    private static BufferBinding CloneBufferBinding(BufferBinding binding)
    {
        return new BufferBinding
        {
            Name = binding.Name,
            NameIndex = binding.NameIndex,
            Index = binding.Index,
            Set = binding.Set,
            ArraySize = binding.ArraySize,
        };
    }

    private static TextureParameter CloneTextureParameter(TextureParameter texture)
    {
        return new TextureParameter
        {
            Name = texture.Name,
            NameIndex = texture.NameIndex,
            Index = texture.Index,
            Set = texture.Set,
            SamplerIndex = texture.SamplerIndex,
            MultiSampled = texture.MultiSampled,
            Dim = texture.Dim,
        };
    }

    private static SamplerParameter CloneSamplerParameter(SamplerParameter sampler)
    {
        return new SamplerParameter
        {
            Sampler = sampler.Sampler,
            Index = sampler.Index,
            Set = sampler.Set,
        };
    }

    private static UAVParameter CloneUavParameter(UAVParameter uav)
    {
        return new UAVParameter
        {
            Name = uav.Name,
            NameIndex = uav.NameIndex,
            Index = uav.Index,
            Set = uav.Set,
            OriginalIndex = uav.OriginalIndex,
        };
    }

    private static ConstantBuffer CloneConstantBuffer(ConstantBuffer constantBuffer)
    {
        return new ConstantBuffer
        {
            Name = constantBuffer.Name,
            NameIndex = constantBuffer.NameIndex,
            MatrixParams = constantBuffer.MatrixParams.Select(parameter => new MatrixParameter
            {
                Name = parameter.Name,
                NameIndex = parameter.NameIndex,
                Index = parameter.Index,
                ArraySize = parameter.ArraySize,
                Type = parameter.Type,
                RowCount = parameter.RowCount,
                ColumnCount = parameter.ColumnCount,
                IsMatrix = parameter.IsMatrix,
            }).ToArray(),
            VectorParams = constantBuffer.VectorParams.Select(parameter => new VectorParameter
            {
                Name = parameter.Name,
                NameIndex = parameter.NameIndex,
                Index = parameter.Index,
                ArraySize = parameter.ArraySize,
                Type = parameter.Type,
                RowCount = parameter.RowCount,
                ColumnCount = parameter.ColumnCount,
                IsMatrix = parameter.IsMatrix,
            }).ToArray(),
            Size = constantBuffer.Size,
            IsPartialCB = constantBuffer.IsPartialCB,
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
                NameIndex = structParameter.NameIndex,
                Index = structParameter.Index,
                ArraySize = structParameter.ArraySize,
                StructSize = structParameter.StructSize,
                MatrixMembers = structParameter.MatrixMembers.Select(parameter => new MatrixParameter
                {
                    Name = parameter.Name,
                    NameIndex = parameter.NameIndex,
                    Index = parameter.Index,
                    ArraySize = parameter.ArraySize,
                    Type = parameter.Type,
                    RowCount = parameter.RowCount,
                    ColumnCount = parameter.ColumnCount,
                    IsMatrix = parameter.IsMatrix,
                }).ToArray(),
                VectorMembers = structParameter.VectorMembers.Select(parameter => new VectorParameter
                {
                    Name = parameter.Name,
                    NameIndex = parameter.NameIndex,
                    Index = parameter.Index,
                    ArraySize = parameter.ArraySize,
                    Type = parameter.Type,
                    RowCount = parameter.RowCount,
                    ColumnCount = parameter.ColumnCount,
                    IsMatrix = parameter.IsMatrix,
                }).ToArray(),
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
            }).ToArray()
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
