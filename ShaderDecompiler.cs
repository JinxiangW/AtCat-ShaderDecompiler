using System.Diagnostics;
using System.Text;
using Ruri.ShaderTools.Spirv;
using Ruri.ShaderTools.Unreal;

namespace Ruri.ShaderTools;

public enum ShaderArchitecture
{
    Unknown = 0,
    Dxbc,
    Dxil,
    SpirV,
}

public sealed class DecompileResult
{
    public bool Success { get; set; }
    public string? SourceCode { get; set; }
    public string SourceLanguage { get; set; } = "hlsl";
    public string SourceFileExtension { get; set; } = ".hlsl";
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

public sealed class ShaderDecompiler : IDisposable
{
    private const int TimeoutMs = 30000;

    private readonly SpirvPatcher _patcher = new();
    private readonly StructuredCBufferRewriter _rewriter = new();
    private readonly string? _toolsDir;
    private bool _disposed;

    public string TempDir { get; set; }

    private enum SpirvStage { Unknown = 0, Vertex, TessControl, TessEvaluation, Geometry, Fragment, Compute }
    private readonly record struct TempFiles(string Dxbc, string Dxil, string Spirv, string Hlsl, string Glsl);
    private readonly record struct Pipeline(byte[] Code, ShaderArchitecture Format, ShaderSymbolData Metadata, UnrealShaderParser.UnrealMetadata? Unreal);
    private readonly record struct Source(string Text, string Language, string Extension);

    public ShaderDecompiler(string? tempDir = null, string? toolsDir = null)
    {
        TempDir = tempDir ?? AppDomain.CurrentDomain.BaseDirectory;
        _toolsDir = FindToolsDirectory(toolsDir);
    }

    public DecompileResult Decompile(byte[] binary, ShaderArchitecture format = ShaderArchitecture.Unknown, ShaderSymbolData? metadata = null, uint shaderModel = 51)
    {
        if (binary == null || binary.Length == 0) return Fail("Shader binary is empty.");
        if (string.IsNullOrWhiteSpace(_toolsDir)) return Fail("Decompiler tools not found. Expected dxbc2dxil.exe, dxil-spirv.exe, and spirv-cross.exe.");

        TempFiles temp = Temps();
        byte[]? lastSpirv = null;
        try
        {
            Pipeline p = Pipe(binary, format, metadata);
            byte[] spv = Spv(p.Format, p.Code, temp);
            lastSpirv = spv;
            byte[] rewritten;
            try
            {
                rewritten = _rewriter.Rewrite(spv, p.Metadata);
                lastSpirv = rewritten;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Structured CBuffer rewrite failed.{Environment.NewLine}{DescribeBuiltInDecorations(spv)}", ex);
            }

            byte[] patched;
            try
            {
                patched = Patch(rewritten, p.Metadata);
                lastSpirv = patched;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"SPIR-V patch failed.{Environment.NewLine}{DescribePatchPlan(rewritten, p.Metadata)}{Environment.NewLine}{DescribeBuiltInDecorations(rewritten)}", ex);
            }

            Source src;
            try
            {
                src = Emit(patched, p.Metadata.EntryPoint, shaderModel, temp);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"SPIR-V emission failed after patch.{Environment.NewLine}{DescribePatchPlan(patched, p.Metadata)}{Environment.NewLine}{DescribeBuiltInDecorations(patched)}", ex);
            }

            return Result(src, patched, p.Metadata, p.Unreal);
        }
        catch (Exception ex)
        {
            DecompileResult fail = Fail(ex.ToString());
            // Attach the latest SPIR-V we managed to produce so callers can dump it for inspection
            // — `unitybinary.spv` next to `unitybinary.error.txt` lets us spirv-dis the exact module
            // that confused spirv-cross without re-running the pipeline.
            fail.IntermediateSpirv = lastSpirv;
            fail.StructuredRewriteSummary = _rewriter.LastRewriteSummary;
            return fail;
        }
        finally
        {
            Delete(temp.Dxbc); Delete(temp.Dxil); Delete(temp.Spirv); Delete(temp.Hlsl); Delete(temp.Glsl);
        }
    }

    public static string? FindToolsDirectory(string? overridePath = null)
        => !string.IsNullOrWhiteSpace(overridePath) && Directory.Exists(overridePath) && HasDirectTools(overridePath)
            ? overridePath
            : new[] { Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools"), AppDomain.CurrentDomain.BaseDirectory }
                .FirstOrDefault(static dir => !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) && HasDirectTools(dir));

    public static bool HasDirectTools(string dir)
        => File.Exists(Path.Combine(dir, "dxbc2dxil.exe"))
        && File.Exists(Path.Combine(dir, "dxil-spirv.exe"))
        && File.Exists(Path.Combine(dir, "spirv-cross.exe"));

    private TempFiles Temps()
    {
        string id = $"temp_{Guid.NewGuid():N}";
        return new(Path.Combine(TempDir, id + ".dxbc"), Path.Combine(TempDir, id + ".dxil"), Path.Combine(TempDir, id + ".spv"), Path.Combine(TempDir, id + ".hlsl"), Path.Combine(TempDir, id + ".glsl"));
    }

    private Pipeline Pipe(byte[] binary, ShaderArchitecture format, ShaderSymbolData? metadata)
    {
        byte[] nativeCode = UnrealShaderParser.Parse(binary, out ShaderArchitecture parsedArchitecture, out UnrealShaderParser.UnrealMetadata? unrealMetadata);
        ShaderSymbolData runtimeSymbols = UeRuntimeShaderSymbolReader.Read(unrealMetadata);
        ShaderSymbolData merged = metadata ?? runtimeSymbols;

        if (metadata != null)
        {
            MergeMissingBindings(merged.ConstantBufferBindings, runtimeSymbols.ConstantBufferBindings, static (a, b) => a.Set == b.Set && a.Index == b.Index);
            MergeMissingBindings(merged.TextureParameters, runtimeSymbols.TextureParameters, static (a, b) => a.Set == b.Set && a.Index == b.Index);
            MergeMissingBindings(merged.Samplers, runtimeSymbols.Samplers, static (a, b) => a.Set == b.Set && a.Index == b.Index);
            MergeMissingBindings(merged.Buffers, runtimeSymbols.Buffers, static (a, b) => a.Set == b.Set && a.Index == b.Index);
            MergeMissingBindings(merged.UAVs, runtimeSymbols.UAVs, static (a, b) => a.Set == b.Set && a.Index == b.Index);
        }

        return new(nativeCode, Detect(format == ShaderArchitecture.Unknown ? parsedArchitecture : format, nativeCode), merged, unrealMetadata);
    }

    private static void MergeMissingBindings<T>(List<T> target, IEnumerable<T> source, Func<T, T, bool> match)
    {
        foreach (T item in source)
            if (!target.Any(existing => match(existing, item)))
                target.Add(item);
    }

    private static ShaderArchitecture Detect(ShaderArchitecture format, byte[] code)
        => format switch
        {
            ShaderArchitecture.Dxbc when Dxil(code) => ShaderArchitecture.Dxil,
            ShaderArchitecture.Dxbc or ShaderArchitecture.Dxil or ShaderArchitecture.SpirV => format,
            _ when Dxil(code) => ShaderArchitecture.Dxil,
            _ when Dxbc(code) => ShaderArchitecture.Dxbc,
            _ when Spirv(code) => ShaderArchitecture.SpirV,
            _ => ShaderArchitecture.Unknown,
        };

    private byte[] Spv(ShaderArchitecture format, byte[] code, TempFiles temp)
        => format switch
        {
            ShaderArchitecture.Dxbc => DxbcToSpv(code, temp),
            ShaderArchitecture.Dxil => DxilToSpv(code, temp.Dxil, temp.Spirv, false),
            ShaderArchitecture.SpirV => code,
            _ => throw new InvalidOperationException($"Unsupported shader format: {format}")
        };

    private byte[] DxbcToSpv(byte[] dxbc, TempFiles temp)
    {
        if (!Dxbc(dxbc)) throw new InvalidOperationException("Input does not contain a valid DXBC payload.");
        File.WriteAllBytes(temp.Dxbc, dxbc);
        if (Run(new[] { Tool("dxbc2dxil.exe"), temp.Dxbc, "-o", temp.Dxil, "-emit-bc" }, "dxbc2dxil") && File.Exists(temp.Dxil))
            return DxilToSpv(File.ReadAllBytes(temp.Dxil), temp.Dxil, temp.Spirv, true);
        if (!Run(new[] { Tool("dxil-spirv.exe"), temp.Dxbc, "--output", temp.Spirv }, "dxil-spirv (DXBC fallback)") || !File.Exists(temp.Spirv))
            throw new InvalidOperationException("Failed to convert DXBC to SPIR-V.");
        return File.ReadAllBytes(temp.Spirv);
    }

    private byte[] DxilToSpv(byte[] dxil, string tempDxil, string tempSpv, bool rawLlvm)
    {
        File.WriteAllBytes(tempDxil, dxil);
        List<string> args = new() { Tool("dxil-spirv.exe"), tempDxil, "--output", tempSpv };
        if (rawLlvm) args.Add("--raw-llvm");
        if (!Run(args.ToArray(), "dxil-spirv") || !File.Exists(tempSpv)) throw new InvalidOperationException("dxil-spirv did not produce a SPIR-V file.");
        return File.ReadAllBytes(tempSpv);
    }

    private byte[] Patch(byte[] spirv, ShaderSymbolData metadata)
    {
        if (metadata.GetResourceBindingCount() == 0) return spirv;
        IReadOnlyList<SpirvBindingInfo> bindings = _patcher.AnalyzeBindingsDetailed(spirv);
        List<(uint Id, string Name)> names = Names(bindings, metadata);
        List<(uint TypeId, uint MemberIndex, string Name)> members = Members(bindings, metadata);
        return names.Count == 0 && members.Count == 0 ? spirv : _patcher.PatchByIds(spirv, names, members);
    }

    private List<(uint Id, string Name)> Names(IReadOnlyList<SpirvBindingInfo> bindings, ShaderSymbolData metadata)
    {
        List<(uint Id, string Name)> result = new();
        foreach (var resource in metadata.EnumerateResourceBindings().Where(static r => !string.IsNullOrWhiteSpace(r.Name)))
            foreach (SpirvBindingInfo binding in Match(bindings, resource))
            {
                string name = Name(resource, binding);
                result.Add((binding.Id, name));
                if (binding.DescriptorType == "UniformBuffer" && binding.StructTypeId is > 0) result.Add((binding.StructTypeId.Value, name));
            }
        return result;
    }

    private List<(uint TypeId, uint MemberIndex, string Name)> Members(IReadOnlyList<SpirvBindingInfo> bindings, ShaderSymbolData metadata)
    {
        List<(uint TypeId, uint MemberIndex, string Name)> result = new();
        foreach (var resource in metadata.EnumerateResourceBindings().Where(static r => r.RegisterType == 'b' && !string.IsNullOrWhiteSpace(r.Name)))
            foreach (SpirvBindingInfo binding in Match(bindings, resource).Where(static b => b.DescriptorType == "UniformBuffer" && b.StructTypeId is > 0))
            {
                ConstantBuffer? cb = metadata.GetConstantBufferByName(Name(resource, binding));
                if (cb == null) continue;
                result.AddRange(MemberPatches(binding, cb));
            }
        return result;
    }

    private IEnumerable<SpirvBindingInfo> Match(IReadOnlyList<SpirvBindingInfo> bindings, (string Name, int Binding, int Set, ShaderResourceType Type, char RegisterType) resource)
        => bindings.Where(binding => binding.Set == resource.Set && binding.Binding == resource.Binding && Match(resource.RegisterType, binding.DescriptorType));

    private string Name((string Name, int Binding, int Set, ShaderResourceType Type, char RegisterType) resource, SpirvBindingInfo binding)
        => binding.DescriptorType == "UniformBuffer" ? _rewriter.GetResolvedBufferName(resource.Set, resource.Binding) ?? resource.Name : resource.Name;

    private static IEnumerable<(uint TypeId, uint MemberIndex, string Name)> MemberPatches(SpirvBindingInfo binding, ConstantBuffer cb)
    {
        List<NumericShaderParameter> all = AllNumericParams(cb);
        if (binding.StructMemberCount == 1 && all.Count > 0 && all.All(static p => p.IsMatrix && p.RowCount == 4 && p.ColumnCount == 4))
            return new[] { (binding.StructTypeId!.Value, 0u, string.Join("_", all.Select(static p => p.Name ?? string.Empty))) };

        List<(uint TypeId, uint MemberIndex, string Name)> result = new();
        foreach (StructParameter p in cb.StructParams.Where(static p => !string.IsNullOrWhiteSpace(p.Name)))
            if (Member(binding, p.Index) is int i) result.Add((binding.StructTypeId!.Value, (uint)i, p.Name));
        foreach (NumericShaderParameter p in cb.AllNumericParams.Where(static p => !string.IsNullOrWhiteSpace(p.Name)))
            if (Member(binding, p.ByteOffset) is int i) result.Add((binding.StructTypeId!.Value, (uint)i, p.Name!));
        return result;
    }

    private static List<NumericShaderParameter> AllNumericParams(ConstantBuffer cb)
    {
        List<NumericShaderParameter> result = new(cb.AllNumericParams);
        foreach (StructParameter s in cb.StructParams) result.AddRange(s.AllNumericMembers);
        return result;
    }

    private static int? Member(SpirvBindingInfo binding, int byteOffset)
    {
        foreach (KeyValuePair<int, uint> pair in binding.MemberOffsets)
            if (pair.Value == (uint)byteOffset)
                return pair.Key;
        return null;
    }

    private Source Emit(byte[] spirv, string? preferredEntryPoint, uint shaderModel, TempFiles temp)
    {
        (SpirvStage stage, string? entryPoint) = Entry(spirv, preferredEntryPoint);
        // Hull / domain / geometry stages: spirv-cross HLSL backend does NOT implement the
        // stage-specific builtins (InvocationId / TessCoord / TessLevel*, patch-constant-function
        // emission, two-entry-point HS layout, etc.). Failing the HLSL attempt here is the
        // documented behavior of the upstream tool, not a defect in our pipeline — so we suppress
        // the noisy "spirv-cross failed" stderr on the first try, print one short clarifying note,
        // and go straight to the GLSL backend (which DOES support these stages cleanly).
        bool isTessOrGeom = stage is SpirvStage.TessControl or SpirvStage.TessEvaluation or SpirvStage.Geometry;

        if (TryEmit(spirv, temp.Spirv, temp.Hlsl, entryPoint, stage, shaderModel, hlsl: true, quiet: isTessOrGeom, out string? hlsl))
        {
            return new(hlsl!, "hlsl", ".hlsl");
        }

        if (isTessOrGeom)
        {
            Console.Error.WriteLine($"[spirv-cross note] {stage} stage: HLSL backend lacks tessellation/geometry builtins (InvocationId / TessCoord / patch-constant emission). Falling back to GLSL output -- this is the expected path for this stage, not a regression.");
            if (TryEmit(spirv, temp.Spirv, temp.Glsl, entryPoint, stage, shaderModel, hlsl: false, quiet: false, out string? glsl))
            {
                return new(glsl!, "glsl", ".glsl");
            }
        }

        throw new InvalidOperationException("Failed to decompile patched SPIR-V.");
    }

    private bool TryEmit(byte[] spirv, string tempSpv, string outputPath, string? entryPoint, SpirvStage stage, uint shaderModel, bool hlsl, bool quiet, out string? source)
    {
        source = null;
        File.WriteAllBytes(tempSpv, spirv);
        List<string> args = new() { Tool("spirv-cross.exe"), tempSpv, "--output", outputPath, hlsl ? "--hlsl" : "-V" };
        Args(args, entryPoint, stage);
        if (hlsl) { args.Add("--shader-model"); args.Add(shaderModel.ToString()); args.Add("--force-zero-initialized-variables"); }
        if (!Run(args.ToArray(), "spirv-cross", quiet: quiet) || !File.Exists(outputPath)) { Delete(outputPath); return false; }
        source = File.ReadAllText(outputPath, Encoding.UTF8);
        Delete(outputPath);
        return true;
    }

    private DecompileResult Result(Source source, byte[] spirv, ShaderSymbolData metadata, UnrealShaderParser.UnrealMetadata? unreal)
    {
        ShaderSymbolData finalMetadata = FinalizeMetadata(source.Text, spirv, metadata);
        DecompileResult result = new()
        {
            Success = true,
            SourceCode = source.Text,
            SourceLanguage = source.Language,
            SourceFileExtension = source.Extension,
            IntermediateSpirv = spirv,
            ShaderName = unreal?.ShaderName ?? metadata.DebugName,
            StructuredRewriteSummary = _rewriter.LastRewriteSummary,
            FinalMetadata = finalMetadata,
            UnrealOptionalDataKeys = unreal?.OptionalDataKeys,
            UnrealUniformBufferNames = unreal?.UniformBufferNames,
            UnrealShaderCodeName = unreal?.ShaderCodeName?.Value,
            UnrealSm6Flag = unreal?.IsSm6Shader?.ToString(),
        };

        if (unreal?.ShaderCodePackedResourceCounts is UnrealShaderParser.FShaderCodePackedResourceCounts packed)
            result.UnrealShaderCodePackedResourceCounts = $"UsageFlags={packed.UsageFlags} NumSamplers={packed.NumSamplers} NumSRVs={packed.NumSRVs} NumCBs={packed.NumCBs} NumUAVs={packed.NumUAVs}";
        if (unreal?.ShaderCodeResourceMasks is UnrealShaderParser.FShaderCodeResourceMasks masks)
            result.UnrealShaderCodeResourceMasks = $"UAVMask=0x{masks.UAVMask:X8}";
        if (unreal?.ShaderCodeFeatures is UnrealShaderParser.FShaderCodeFeatures features)
            result.UnrealShaderCodeFeatures = $"CodeFeatures=0x{features.CodeFeatures:X2}";
        if (unreal?.ShaderCodeVendorExtension != null)
            result.UnrealShaderCodeVendorExtension = $"RawSize={unreal.ShaderCodeVendorExtension.RawData.Length}";

        return result;
    }

    private static ShaderSymbolData FinalizeMetadata(string sourceText, byte[] spirv, ShaderSymbolData metadata)
    {
        return metadata;
    }

    private static (SpirvStage Stage, string? EntryPoint) Entry(byte[] spirv, string? preferred)
    {
        SpirvModule module = SpirvModule.Parse(spirv);
        (SpirvStage Stage, string? EntryPoint)? first = null;
        foreach (SpirvInstruction i in module.Instructions)
        {
            if (i.OpCode != SpvOpCode.OpEntryPoint || i.Words.Length < 3) continue;
            string entry = Str(i.Words, 3);
            SpirvStage stage = i[1] switch { 0 => SpirvStage.Vertex, 1 => SpirvStage.TessControl, 2 => SpirvStage.TessEvaluation, 3 => SpirvStage.Geometry, 4 => SpirvStage.Fragment, 5 => SpirvStage.Compute, _ => SpirvStage.Unknown };
            first ??= (stage, entry);
            if (!string.IsNullOrWhiteSpace(preferred) && string.Equals(entry, preferred, StringComparison.Ordinal)) return (stage, entry);
        }
        return first ?? (SpirvStage.Unknown, preferred);
    }

    private static string Str(IReadOnlyList<uint> words, int start)
    {
        List<byte> bytes = new();
        for (int i = start; i < words.Count; i++)
            for (int shift = 0; shift < 32; shift += 8)
            {
                byte value = (byte)((words[i] >> shift) & 0xFF);
                if (value == 0) return Encoding.UTF8.GetString(bytes.ToArray());
                bytes.Add(value);
            }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static void Args(List<string> args, string? entryPoint, SpirvStage stage)
    {
        if (!string.IsNullOrWhiteSpace(entryPoint)) { args.Add("--entry"); args.Add(entryPoint); }
        string? stageArg = stage switch { SpirvStage.Vertex => "vert", SpirvStage.TessControl => "tesc", SpirvStage.TessEvaluation => "tese", SpirvStage.Geometry => "geom", SpirvStage.Fragment => "frag", SpirvStage.Compute => "comp", _ => null };
        if (stageArg != null) { args.Add("--stage"); args.Add(stageArg); }
    }

    private string DescribePatchPlan(byte[] spirv, ShaderSymbolData metadata)
    {
        if (metadata.GetResourceBindingCount() == 0)
        {
            return "Patch plan: metadata contained no resource bindings.";
        }

        IReadOnlyList<SpirvBindingInfo> bindings = _patcher.AnalyzeBindingsDetailed(spirv);
        List<(uint Id, string Name)> names = Names(bindings, metadata);
        List<(uint TypeId, uint MemberIndex, string Name)> members = Members(bindings, metadata);

        var lines = new List<string>
        {
            $"Patch plan: resourceBindings={metadata.GetResourceBindingCount()} matchedBindings={bindings.Count} opNames={names.Count} opMemberNames={members.Count}"
        };

        foreach ((uint id, string name) in names.Take(16))
        {
            lines.Add($"  OpName Id={id} Name={name}");
        }

        if (names.Count > 16)
        {
            lines.Add($"  ... {names.Count - 16} more OpName patches");
        }

        foreach ((uint typeId, uint memberIndex, string name) in members.Take(16))
        {
            lines.Add($"  OpMemberName TypeId={typeId} MemberIndex={memberIndex} Name={name}");
        }

        if (members.Count > 16)
        {
            lines.Add($"  ... {members.Count - 16} more OpMemberName patches");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string DescribeBuiltInDecorations(byte[] spirv)
    {
        SpirvModule module = SpirvModule.Parse(spirv);
        var names = new Dictionary<uint, string>();
        var lines = new List<string>();

        foreach (SpirvInstruction instruction in module.Instructions)
        {
            if (instruction.OpCode == SpvOpCode.OpName && instruction.Words.Length >= 3)
            {
                string? name = Str(instruction.Words, 2);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names[instruction[1]] = name;
                }
            }
        }

        foreach (SpirvInstruction instruction in module.Instructions)
        {
            if (instruction.OpCode == SpvOpCode.OpDecorate && instruction.Words.Length >= 4 && instruction[2] == SpvOpCode.DecorationBuiltIn)
            {
                uint targetId = instruction[1];
                string targetName = names.TryGetValue(targetId, out string? name) ? name : $"id_{targetId}";
                lines.Add($"  OpDecorate targetId={targetId} name={targetName} BuiltIn={instruction[3]} offset={instruction.Offset}");
            }
            else if (instruction.OpCode == SpvOpCode.OpMemberDecorate && instruction.Words.Length >= 5 && instruction[3] == SpvOpCode.DecorationBuiltIn)
            {
                uint typeId = instruction[1];
                string typeName = names.TryGetValue(typeId, out string? name) ? name : $"id_{typeId}";
                lines.Add($"  OpMemberDecorate typeId={typeId} name={typeName} memberIndex={instruction[2]} BuiltIn={instruction[4]} offset={instruction.Offset}");
            }
        }

        return lines.Count == 0
            ? "BuiltIn decorations: none"
            : "BuiltIn decorations:" + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }

    private bool Run(string[] args, string name, bool quiet = false)
    {
        ProcessStartInfo psi = new()
        {
            FileName = args[0],
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _toolsDir,
        };

        for (int i = 1; i < args.Length; i++) psi.ArgumentList.Add(args[i]);
        psi.Environment["PATH"] = _toolsDir + Path.PathSeparator + (psi.Environment.ContainsKey("PATH") ? psi.Environment["PATH"] : string.Empty);

        using Process? process = Process.Start(psi);
        if (process == null) return Log($"Failed to start {name}.");

        StringBuilder stderr = new();
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
        process.BeginErrorReadLine();
        string stdout = process.StandardOutput.ReadToEnd();

        if (!process.WaitForExit(TimeoutMs))
        {
            try { process.Kill(true); } catch { }
            return Log($"{name} timed out after {TimeoutMs}ms.");
        }

        if (process.ExitCode == 0)
        {
            return true;
        }

        // `quiet` is set when the caller already knows this attempt is expected to fail and a
        // fallback path is queued (e.g. tess/geom HLSL → GLSL). Suppressing the stderr keeps the
        // log clean and avoids the user mistaking expected fallback for a real defect.
        return quiet || Log($"{name} failed: {Trim(stderr.ToString())}{(string.IsNullOrWhiteSpace(stdout) ? string.Empty : Environment.NewLine + Trim(stdout))}");
    }

    private bool Log(string error) { Debug.WriteLine(error); Console.Error.WriteLine(error); return false; }
    private string Tool(string name) => Path.Combine(_toolsDir!, name);
    private static bool Match(char registerType, string? descriptorType) => descriptorType switch { "UniformBuffer" => registerType == 'b', "Sampler" => registerType == 's', "SampledImage" => registerType == 't', "StorageBuffer" => registerType == 'u', "StorageImage" => registerType == 'u', _ => false };
    private static bool Dxbc(byte[] data) => data.Length >= 4 && data[0] == 'D' && data[1] == 'X' && data[2] == 'B' && data[3] == 'C';
    private static bool Spirv(byte[] data) => data.Length >= 4 && BitConverter.ToUInt32(data, 0) == SpvOpCode.MagicNumber;
    private static bool Dxil(byte[] data)
    {
        if (!Dxbc(data) || data.Length < 32) return false;
        int count = BitConverter.ToInt32(data, 28);
        if (count <= 0 || count > 256 || 32 + (count * 4) > data.Length) return false;
        for (int i = 0; i < count; i++)
        {
            int offset = BitConverter.ToInt32(data, 32 + (i * 4));
            if (offset >= 0 && offset + 4 <= data.Length && data[offset] == 'D' && data[offset + 1] == 'X' && data[offset + 2] == 'I' && data[offset + 3] == 'L') return true;
        }
        return false;
    }
    private static DecompileResult Fail(string message) => new() { Success = false, ErrorMessage = message };
    private static string Trim(string text) => string.IsNullOrEmpty(text) || text.Length <= 1000 ? text : text[..1000];
    private static void Delete(string path) { if (File.Exists(path)) File.Delete(path); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
