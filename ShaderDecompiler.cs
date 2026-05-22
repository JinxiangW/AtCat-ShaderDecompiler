using System.Diagnostics;
using System.Text;
using Newtonsoft.Json;
using Ruri.ShaderTools.Spirv;

namespace Ruri.ShaderTools;

public enum ShaderArchitecture
{
    Unknown = 0,
    Dxbc,
    Dxil,
    SpirV,
}

// Engine-agnostic decompile knobs. The decompiler does NOT know about
// UE / Unity / any specific engine — callers (e.g. the FModel UE hook
// or the AssetRipper Unity exporter) pre-build a complete `Metadata`
// and pass it in. The decompiler then runs the universal pipeline:
//   format conversion → SPIR-V rewrite → symbol injection → spirv-cross
public sealed class DecompileOptions
{
    public ShaderArchitecture Format { get; init; } = ShaderArchitecture.Unknown;
    public SerializedProgramData? Metadata { get; init; }
    public UnityShaderMetadata? UnityMetadata { get; init; }
    public uint ShaderModel { get; init; } = 51;

    // Optional escape hatch. Invoked after the structured-cbuffer rewrite
    // and BEFORE universal SpirvPatcher symbol injection. Receives the
    // rewritten SPIR-V (read-only) and the metadata (mutate-in-place to
    // add bindings). The actual symbol-name injection still runs inside
    // the decompiler — this is purely for metadata accumulation that
    // requires SPIR-V context (e.g. UE's Material UB texture-name
    // inference from OpSampledImage pairs, where you can only name a
    // texture once you've matched it to an already-named sampler in a
    // sampled-image pair). Unity's caller doesn't need this.
    public Action<byte[], SerializedProgramData>? MetadataEnricher { get; init; }

    // When set, a failed Decompile call writes every intermediate
    // artifact (input binary, pre-rewrite SPIR-V, post-rewrite SPIR-V,
    // post-patch SPIR-V, metadata, structured-rewrite summary, captured
    // tool stderr, error stack trace) under this directory using
    // `DebugDumpStem` as the file-name prefix.
    public string? DebugDumpDirectory { get; init; }
    public string? DebugDumpStem { get; init; }
}

public sealed class DecompileResult
{
    public bool Success { get; set; }
    public string? SourceCode { get; set; }
    public string SourceLanguage { get; set; } = "hlsl";
    public string SourceFileExtension { get; set; } = ".hlsl";
    public string? ErrorMessage { get; set; }
    public byte[]? IntermediateSpirv { get; set; }

    // Per-stage SPIR-V — populated for both success and failure so callers
    // can diff stages offline. PreRewriteSpirv: format conversion output.
    // PostRewriteSpirv: after StructuredCBufferRewriter (this is what the
    // MetadataEnricher callback sees). PostPatchSpirv: after SpirvPatcher
    // symbol-name injection.
    public byte[]? PreRewriteSpirv { get; set; }
    public byte[]? PostRewriteSpirv { get; set; }
    public byte[]? PostPatchSpirv { get; set; }
    public string? FailedStage { get; set; }
    public string? LastToolStderr { get; set; }
    public string? PatchPlanDescription { get; set; }
    public string? BuiltInDecorationsDescription { get; set; }
    public string? DebugDumpDirectory { get; set; }
    public string? StructuredRewriteSummary { get; set; }
    public SerializedProgramData? FinalMetadata { get; set; }
    public UnityShaderMetadata? FinalUnityMetadata { get; set; }
}

public sealed class ShaderDecompiler : IDisposable
{
    private const int TimeoutMs = 30000;

    private readonly SpirvPatcher _patcher = new();
    private readonly StructuredCBufferRewriter _rewriter = new();
    private readonly string? _toolsDir;
    private bool _disposed;
    // Stderr/stdout from the most recent failed external tool call
    // (dxbc2dxil / dxil-spirv / spirv-cross). Captured so failure dumps
    // can include the actual upstream message — e.g. spirv-cross's
    // "Shader model 5.1 or higher is required ..." which would otherwise
    // only land on Console.Error.
    private string? _lastToolFailureLog;

    public string TempDir { get; set; }

    private enum SpirvStage
    {
        Unknown = 0,
        Vertex,
        TessControl,
        TessEvaluation,
        Geometry,
        Fragment,
        Compute,
        RayGeneration,
        Intersection,
        AnyHit,
        ClosestHit,
        Miss,
        Callable,
        Task,
        Mesh,
    }
    private readonly record struct TempFiles(string Dxbc, string Dxil, string Spirv, string Hlsl, string Glsl);
    private readonly record struct Source(string Text, string Language, string Extension);

    public ShaderDecompiler(string? tempDir = null, string? toolsDir = null)
    {
        TempDir = tempDir ?? AppDomain.CurrentDomain.BaseDirectory;
        _toolsDir = FindToolsDirectory(toolsDir);
    }

    public DecompileResult Decompile(byte[] binary, ShaderArchitecture format = ShaderArchitecture.Unknown, SerializedProgramData? metadata = null, uint shaderModel = 51)
        => Decompile(binary, new DecompileOptions { Format = format, Metadata = metadata, ShaderModel = shaderModel });

    /// <summary>
    /// Batch entry point: decompile any number of shaders. A single request takes the
    /// fast serial path on this instance; multiple requests fan out across an internal
    /// worker pool whose concurrency scales against system CPU usage (capped at 80% by
    /// default, configurable via <paramref name="cpuUsageCapPercent"/>). Each worker
    /// owns its own <see cref="ShaderDecompiler"/>, so callers don't need to think about
    /// instance reuse or thread safety.
    /// </summary>
    /// <param name="requests">(binary, options) pairs to decompile. Result array index
    /// matches request array index.</param>
    /// <param name="onProgress">Optional per-completion callback. Fires on a worker
    /// thread; serialise external state yourself.</param>
    /// <param name="maxConcurrency">Hard cap on concurrent jobs. ≤ 0 → ProcessorCount × 2.</param>
    /// <param name="cpuUsageCapPercent">Stop spawning new jobs when total system CPU
    /// usage rises above this threshold. Defaults to 80.</param>
    public DecompileResult[] Decompile(
        IReadOnlyList<(byte[] Binary, DecompileOptions Options)> requests,
        Action<int, DecompileResult>? onProgress = null,
        int maxConcurrency = 0,
        int cpuUsageCapPercent = 80,
        CancellationToken cancellationToken = default)
    {
        if (requests is null) throw new ArgumentNullException(nameof(requests));
        if (requests.Count == 0) return Array.Empty<DecompileResult>();

        DecompileResult[] results = new DecompileResult[requests.Count];

        // Single-request fast path: reuse the calling instance, skip the pool plumbing.
        if (requests.Count == 1)
        {
            var (binary, opts) = requests[0];
            DecompileResult r = Decompile(binary, opts);
            results[0] = r;
            onProgress?.Invoke(0, r);
            return results;
        }

        BatchExecutor.Run(this, requests, results, onProgress, maxConcurrency, cpuUsageCapPercent, cancellationToken);
        return results;
    }

    public DecompileResult Decompile(byte[] binary, DecompileOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (binary == null || binary.Length == 0) return Fail("Shader binary is empty.");
        if (string.IsNullOrWhiteSpace(_toolsDir)) return Fail("Decompiler tools not found. Expected dxbc2dxil.exe, dxil-spirv.exe, and spirv-cross.exe.");

        _lastToolFailureLog = null;
        SerializedProgramData metadata = options.Metadata ?? new SerializedProgramData();
        ShaderArchitecture format = Detect(options.Format, binary);

        // Engine-level SM auto-bump. Any DXIL-bearing input — whether
        // raw bitcode (`BC\xC0\xDE`), a bare DXIL chunk, or a DXBC
        // container with a `DXIL`/`ILDB`/`ILDN` chunk inside — was
        // produced by DXC targeting SM 6.0+. Passing the default SM 5.1
        // to spirv-cross's HLSL backend on these inputs causes the
        // backend to reject any feature it gates on SM version, e.g.
        //   * "Wave ops requires SM 6.0 or higher"
        //   * "Sampling non-float textures is not supported in HLSL SM < 6.7"
        // Both are real failure modes observed on the X6Game shipping
        // cook (35/36 of the Global archive _failures). We bump the
        // effective shader model to 67 here so callers don't all have
        // to remember to set the right value — bumping is harmless for
        // SM 6.x containers because spirv-cross uses the model to gate
        // *intrinsic emission*, never to reject input. We bump UPWARDS
        // only: if the caller explicitly requested 6.6 / 6.8 / etc., we
        // keep that.
        // Engine-level SM auto-bump.
        //
        // spirv-cross's HLSL backend uses the `--shader-model` value to
        // gate **what intrinsics it's willing to emit**, NOT to validate
        // input. Passing the legacy default of 5.1 makes it reject any
        // SPV that uses a feature added later, whether or not the source
        // shader was actually authored for SM 6.x:
        //   * "Wave ops requires SM 6.0 or higher" — even SM 5.x inputs
        //     can produce SPV with subgroup ops via dxil-spirv
        //     translation
        //   * "Sampling non-float textures is not supported in HLSL SM < 6.7" —
        //     fires on classic SM5 DXBC shaders that sample uint/sint
        //     textures via Texture<uint>.Sample, because the SPV
        //     OpImageSampleImplicitLod op carries the non-float format
        //     regardless of source SM
        //   * "Mesh shader emission requires SM 6.5 or higher"
        //   * "Variable-rate shading requires SM 6.4 or higher"
        // ...etc. Every gate is "intrinsic emission requires SM ≥ N".
        //
        // We bump to **67** unconditionally for the HLSL emit path
        // because:
        //   1. spirv-cross's SM gates only EXPAND what's emittable — there
        //      is no gate that REQUIRES SM ≤ N to function. So bumping
        //      can't lose us a feature, only unlock more.
        //   2. The shader-model number lands in `register(t0, space0)` /
        //      `[shader("compute")]`-style annotations that consumers
        //      rarely care about; the HLSL syntax produced is otherwise
        //      identical to SM5 for shaders that don't use SM6+ intrinsics.
        //   3. Any caller that wants a specific newer model (6.6 / 6.8 /
        //      future) can ASK for it via DecompileOptions.ShaderModel
        //      and we keep that — the bump is bounded by `< 67`.
        // Environment override `RURI_SHADER_DEBUG=1` traces what fires.
        uint shaderModel = options.ShaderModel;
        if (shaderModel < 67)
        {
            shaderModel = 67;
        }

        TempFiles temp = Temps();
        byte[]? preRewrite = null;
        byte[]? postRewrite = null;
        byte[]? postPatch = null;
        string failedStage = "init";
        try
        {
            failedStage = "format-to-spirv";
            byte[] spv = ConvertToSpirv(format, binary, temp);
            preRewrite = spv;

            byte[] rewritten;
            try
            {
                failedStage = "structured-cbuffer-rewrite";
                rewritten = _rewriter.Rewrite(spv, metadata);
                postRewrite = rewritten;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Structured CBuffer rewrite failed.{Environment.NewLine}{DescribeBuiltInDecorations(spv)}", ex);
            }

            try
            {
                failedStage = "metadata-enricher";
                options.MetadataEnricher?.Invoke(rewritten, metadata);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"MetadataEnricher threw: {ex.Message}", ex);
            }

            byte[] patched;
            try
            {
                failedStage = "spirv-symbol-patch";
                patched = Patch(rewritten, metadata);
                postPatch = patched;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"SPIR-V patch failed.{Environment.NewLine}{DescribePatchPlan(rewritten, metadata)}{Environment.NewLine}{DescribeBuiltInDecorations(rewritten)}", ex);
            }

            Source src;
            try
            {
                failedStage = "spirv-cross-emit";
                src = Emit(patched, metadata.EntryPoint, shaderModel, temp);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"SPIR-V emission failed after patch.{Environment.NewLine}{DescribePatchPlan(patched, metadata)}{Environment.NewLine}{DescribeBuiltInDecorations(patched)}", ex);
            }

            return new DecompileResult
            {
                Success = true,
                SourceCode = src.Text,
                SourceLanguage = src.Language,
                SourceFileExtension = src.Extension,
                IntermediateSpirv = patched,
                PreRewriteSpirv = preRewrite,
                PostRewriteSpirv = postRewrite,
                PostPatchSpirv = postPatch,
                StructuredRewriteSummary = _rewriter.LastRewriteSummary,
                FinalMetadata = metadata,
                FinalUnityMetadata = options.UnityMetadata,
            };
        }
        catch (Exception ex)
        {
            DecompileResult fail = Fail(ex.ToString());
            fail.FailedStage = failedStage;
            fail.PreRewriteSpirv = preRewrite;
            fail.PostRewriteSpirv = postRewrite;
            fail.PostPatchSpirv = postPatch;
            fail.IntermediateSpirv = postPatch ?? postRewrite ?? preRewrite;
            fail.StructuredRewriteSummary = _rewriter.LastRewriteSummary;
            fail.LastToolStderr = _lastToolFailureLog;

            byte[]? planSpirv = postPatch ?? postRewrite ?? preRewrite;
            if (planSpirv != null)
            {
                fail.PatchPlanDescription = DescribePatchPlan(planSpirv, metadata);
                fail.BuiltInDecorationsDescription = DescribeBuiltInDecorations(planSpirv);
            }

            if (!string.IsNullOrWhiteSpace(options.DebugDumpDirectory))
            {
                try
                {
                    fail.DebugDumpDirectory = WriteFailureDump(options, binary, fail, metadata);
                }
                catch (Exception dumpEx)
                {
                    Console.Error.WriteLine($"[ShaderDecompiler] Failed to write failure dump: {dumpEx.Message}");
                }
            }

            return fail;
        }
        finally
        {
            Delete(temp.Dxbc); Delete(temp.Dxil); Delete(temp.Spirv); Delete(temp.Hlsl); Delete(temp.Glsl);
        }
    }

    private string WriteFailureDump(DecompileOptions options, byte[] inputBinary, DecompileResult fail, SerializedProgramData metadata)
    {
        string dir = options.DebugDumpDirectory!;
        Directory.CreateDirectory(dir);
        string stem = SanitizeStem(options.DebugDumpStem) ?? $"shader_{DateTime.Now:yyyyMMdd_HHmmss_fff}";
        string basePath = Path.Combine(dir, stem);

        File.WriteAllBytes(basePath + ".input.bin", inputBinary);
        if (fail.PreRewriteSpirv is { Length: > 0 } a) File.WriteAllBytes(basePath + ".01.pre-rewrite.spv", a);
        if (fail.PostRewriteSpirv is { Length: > 0 } b) File.WriteAllBytes(basePath + ".02.post-rewrite.spv", b);
        if (fail.PostPatchSpirv is { Length: > 0 } c) File.WriteAllBytes(basePath + ".03.post-patch.spv", c);

        StringBuilder error = new();
        error.AppendLine($"Failed stage: {fail.FailedStage ?? "<unknown>"}");
        error.AppendLine();
        error.AppendLine("Error:");
        error.AppendLine(fail.ErrorMessage ?? "<no message>");
        if (!string.IsNullOrWhiteSpace(fail.LastToolStderr))
        {
            error.AppendLine();
            error.AppendLine("Last tool stderr/stdout:");
            error.AppendLine(fail.LastToolStderr);
        }
        File.WriteAllText(basePath + ".error.txt", error.ToString(), new UTF8Encoding(false));

        if (!string.IsNullOrWhiteSpace(fail.PatchPlanDescription))
            File.WriteAllText(basePath + ".patch-plan.txt", fail.PatchPlanDescription!, new UTF8Encoding(false));
        if (!string.IsNullOrWhiteSpace(fail.BuiltInDecorationsDescription))
            File.WriteAllText(basePath + ".builtin-decorations.txt", fail.BuiltInDecorationsDescription!, new UTF8Encoding(false));
        if (!string.IsNullOrWhiteSpace(fail.StructuredRewriteSummary))
            File.WriteAllText(basePath + ".rewrite-summary.txt", fail.StructuredRewriteSummary!, new UTF8Encoding(false));

        try
        {
            File.WriteAllText(basePath + ".metadata.json", JsonConvert.SerializeObject(metadata, Formatting.Indented), new UTF8Encoding(false));
        }
        catch
        {
            // Metadata might contain non-serialisable types; don't bury the rest of the dump.
        }

        return basePath;
    }

    private static string? SanitizeStem(string? stem)
    {
        if (string.IsNullOrWhiteSpace(stem)) return null;
        char[] invalid = Path.GetInvalidFileNameChars();
        StringBuilder sb = new(stem.Length);
        foreach (char c in stem) sb.Append(invalid.Contains(c) ? '_' : c);
        return sb.ToString();
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

    private static ShaderArchitecture Detect(ShaderArchitecture format, byte[] code)
        => format switch
        {
            // DXBC magic + DXIL chunk → DXIL takes precedence over DXBC.
            ShaderArchitecture.Dxbc when IsDxil(code) => ShaderArchitecture.Dxil,
            ShaderArchitecture.Dxbc or ShaderArchitecture.Dxil or ShaderArchitecture.SpirV => format,
            _ when IsDxil(code) => ShaderArchitecture.Dxil,
            _ when IsDxbc(code) => ShaderArchitecture.Dxbc,
            _ when IsSpirv(code) => ShaderArchitecture.SpirV,
            _ => ShaderArchitecture.Unknown,
        };

    private byte[] ConvertToSpirv(ShaderArchitecture format, byte[] code, TempFiles temp)
        => format switch
        {
            ShaderArchitecture.Dxbc => DxbcToSpv(code, temp),
            ShaderArchitecture.Dxil => DxilToSpv(code, temp.Dxil, temp.Spirv, false),
            ShaderArchitecture.SpirV => code,
            _ => throw new InvalidOperationException($"Unsupported shader format: {format}"),
        };

    private byte[] DxbcToSpv(byte[] dxbc, TempFiles temp)
    {
        if (!IsDxbc(dxbc)) throw new InvalidOperationException("Input does not contain a valid DXBC payload.");
        File.WriteAllBytes(temp.Dxbc, dxbc);
        if (Run(new[] { Tool("dxbc2dxil.exe"), temp.Dxbc, "-o", temp.Dxil, "-emit-bc" }, "dxbc2dxil") && File.Exists(temp.Dxil))
            return DxilToSpv(File.ReadAllBytes(temp.Dxil), temp.Dxil, temp.Spirv, true);
        if (!Run(BuildDxilSpirvArgs(Tool("dxil-spirv.exe"), temp.Dxbc, temp.Spirv, rawLlvm: false), "dxil-spirv (DXBC fallback)") || !File.Exists(temp.Spirv))
            throw new InvalidOperationException("Failed to convert DXBC to SPIR-V.");
        return File.ReadAllBytes(temp.Spirv);
    }

    private byte[] DxilToSpv(byte[] dxil, string tempDxil, string tempSpv, bool rawLlvm)
    {
        File.WriteAllBytes(tempDxil, dxil);
        if (!Run(BuildDxilSpirvArgs(Tool("dxil-spirv.exe"), tempDxil, tempSpv, rawLlvm), "dxil-spirv") || !File.Exists(tempSpv))
            throw new InvalidOperationException("dxil-spirv did not produce a SPIR-V file.");
        return File.ReadAllBytes(tempSpv);
    }

    // Centralised dxil-spirv command-line builder. The `--ssbo-uav` and
    // `--ssbo-srv` flags force UAV/SRV resources to be emitted as
    // `StorageClassStorageBuffer` (SSBOs) rather than typed buffers.
    // This is the workaround for the "Raw 64-bit load-store was used,
    // which must be implemented with SSBO, UBO or BDA" error that
    // dxil-spirv throws when a shader (most commonly raygen RT shaders
    // performing 64-bit atomic counter / hit-record load-store) uses
    // 64-bit raw access on a resource the default lowering can't fit
    // into the typed-buffer/BDA path.
    //
    // The fix lives in dxil-spirv's `emit_raw_buffer_load_instruction` /
    // `emit_raw_buffer_store_instruction` (opcodes/dxil/dxil_buffer.cpp):
    // 64-bit raw access requires either StorageBuffer storage class OR
    // PhysicalStorageBuffer (BDA). When `--ssbo-uav --ssbo-srv` route
    // both classes through SSBO, the conversion succeeds.
    //
    // Verified harmless for shaders that DON'T need this — dxil-spirv
    // produces byte-identical SPV output (13684 bytes pre/post on a
    // sample SM 6.0 compute shader). spirv-cross's downstream HLSL emit
    // is also unaffected (same 7973-byte HLSL out).
    //
    // This used to be the last-stuck-failure of the whole X6Game cook
    // (`Ungrouped_RG_005416`, raw 64-bit BDA-blocked raygen). Now fixed
    // at the engine layer so every caller benefits without per-call
    // tuning.
    private static string[] BuildDxilSpirvArgs(string toolPath, string inputPath, string outputPath, bool rawLlvm)
    {
        var args = new List<string>
        {
            toolPath, inputPath, "--output", outputPath,
            "--ssbo-uav", "--ssbo-srv",
        };
        if (rawLlvm) args.Add("--raw-llvm");
        return args.ToArray();
    }

    private byte[] Patch(byte[] spirv, SerializedProgramData metadata)
    {
        if (metadata.GetResourceBindingCount() == 0) return spirv;
        IReadOnlyList<SpirvBindingInfo> bindings = _patcher.AnalyzeBindingsDetailed(spirv);
        List<(uint Id, string Name)> names = BuildNamePatches(bindings, metadata);
        List<(uint TypeId, uint MemberIndex, string Name)> members = BuildMemberPatches(bindings, metadata);
        return names.Count == 0 && members.Count == 0 ? spirv : _patcher.PatchByIds(spirv, names, members);
    }

    private List<(uint Id, string Name)> BuildNamePatches(IReadOnlyList<SpirvBindingInfo> bindings, SerializedProgramData metadata)
    {
        // Variables get the metadata name verbatim. The cbuffer struct type gets the
        // DXC-style `type.<Name>` alias — spirv-cross sanitises the dot to `_` so the
        // emitted block keyword reads `cbuffer type_<Name>`. The two strings differ,
        // which is what prevents spirv-cross's name-uniquify pass from injecting the
        // `_1` suffix that used to bleed into member names (`UnityPerMaterial_1_X`).
        // We always patch the struct type — for rewritten cbuffers, rewriter has
        // already set the same `type.<Name>` alias so this is idempotent; for the
        // skipped cbuffers (layout-fit fails — e.g. `UnityInstancing_SRP_UnityPerDraw`
        // with its 65536-byte instancing array), this is the only chance to give the
        // block a real name instead of spirv-cross's synthetic `CBNUBO` fallback.
        //
        // Cross-binding name collision: UE shaders frequently expose the
        // SAME logical UB name (e.g. "Material") at two different binding
        // points — one is the actual material constant buffer (v4float[N]),
        // the other is a bindless-resource-table index buffer (uint[N]).
        // The original SPV gives them distinct struct types but the SRT-
        // derived metadata lists both under "Material". Patching both
        // struct types with the same `type.Material` alias is what makes
        // spirv-cross fail with "cbuffer ID X member 0 (_m0) cannot be
        // expressed with HLSL packing" — the second cbuffer gets the
        // first's layout assumptions inherited via the shared name.
        //
        // We disambiguate by remembering which struct types and variable
        // names we've already emitted, and suffix later occurrences with
        // `_b<binding>` (e.g. `Material_b5`, `type.Material_b5`). Single-
        // binding cases are unaffected so the existing fixture set stays
        // byte-identical.
        List<(uint Id, string Name)> result = new();
        HashSet<uint> patchedIds = new();
        Dictionary<string, int> variableNameSeen = new(StringComparer.Ordinal);
        Dictionary<string, int> structAliasSeen = new(StringComparer.Ordinal);
        foreach (var resource in metadata.EnumerateResourceBindings().Where(static r => !string.IsNullOrWhiteSpace(r.Name)))
            foreach (SpirvBindingInfo binding in MatchBindings(bindings, resource))
            {
                if (patchedIds.Contains(binding.Id)) continue;

                string baseName = ResolveName(resource, binding);
                string variableName = baseName;
                if (variableNameSeen.TryGetValue(baseName, out int prevCount) && prevCount > 0)
                {
                    variableName = $"{baseName}_b{binding.Binding}";
                }
                variableNameSeen[baseName] = variableNameSeen.GetValueOrDefault(baseName) + 1;

                result.Add((binding.Id, variableName));
                patchedIds.Add(binding.Id);
                if (binding.DescriptorType == "UniformBuffer" && binding.StructTypeId is > 0)
                {
                    string structAliasBase = "type." + baseName;
                    string structAlias = structAliasBase;
                    if (structAliasSeen.TryGetValue(structAliasBase, out int prevStruct) && prevStruct > 0)
                    {
                        structAlias = $"{structAliasBase}_b{binding.Binding}";
                    }
                    structAliasSeen[structAliasBase] = structAliasSeen.GetValueOrDefault(structAliasBase) + 1;

                    if (!patchedIds.Contains(binding.StructTypeId.Value))
                    {
                        result.Add((binding.StructTypeId.Value, structAlias));
                        patchedIds.Add(binding.StructTypeId.Value);
                    }
                }
            }

        // Sampler synthesis: Unity's metadata typically leaves Samplers empty and
        // expresses the link via TextureParameters[i].SamplerIndex. For each sampler
        // variable in the SPIR-V we synthesise a name that Unity ShaderLab accepts
        // — anything else triggers
        //   `Unrecognized sampler 'sampler_N' - does not match any texture and is
        //    not a recognized inline name (should contain filter and wrap modes).`
        // when Unity's shader importer sees the SamplerState declaration.
        //
        // Two valid forms:
        //   * `sampler<TextureName>` — pairs the sampler with that texture's
        //     import-settings sampler state (Unity's default convention; multiple
        //     SampleN calls in HLSL can still reference it from any texture).
        //   * `sampler<filter><wrap>` inline name (e.g. `sampler_LinearClamp`) —
        //     Unity creates a static sampler with the named filter/wrap combo.
        // When the SPIR-V sampler binds N>=1 textures we pick the *first* (lowest
        // metadata index) texture's name; the sampler is shared in HLSL anyway.
        // When no texture targets the slot at all, fall back to the inline form
        // `sampler_LinearClamp` — Unity always accepts it and gives sane defaults.
        //
        // We override any earlier patch on a sampler id: AzurPromilia (and
        // potentially other Unity custom-engine builds) carries samplers in
        // metadata with names like `"sampler_3"` straight from the bytecode
        // reflection. Those names are accurate at the SPIR-V level but Unity's
        // shader importer rejects them, so we always replace with a synthesised
        // Unity-compatible name regardless of whether the main resource loop
        // already wrote a metadata name for the sampler.
        // Track names already given to a sampler in this shader so multiple
        // texture-less samplers don't all collapse to the same inline fallback —
        // spirv-cross would uniquify them as `sampler_LinearClamp_1` /
        // `_LinearClamp_2`, and Unity's shader importer rejects any sampler
        // whose name isn't a recognised inline filter+wrap combo.
        // Two passes over the sampler bindings:
        //   * Pass 1 assigns each sampler the NEXT available inline-form name
        //     from `InlineSamplerPool` (sampler_LinearClamp, sampler_LinearRepeat,
        //     sampler_LinearMirror, …). This guarantees Unity's shader-importer
        //     filter+wrap parser can still recognise the prefix even for
        //     samplers we'll go on to pair with a texture.
        //   * Pass 2 appends the paired texture's name when the sampler binds
        //     N>=1 textures (deterministic across re-runs because we pick the
        //     lowest-Index texture).
        //
        // Combining both gives `sampler_LinearRepeat_Material_Normal` form —
        // the wrap-mode prefix the user wants to keep, plus the texture
        // association so the rewritten HLSL is still self-documenting.
        // Samplers with no texture link stay at the bare inline form.
        List<SpirvBindingInfo> samplerBindings = bindings.Where(b => b.DescriptorType == "Sampler").ToList();
        HashSet<string> samplerNamesUsed = new(StringComparer.Ordinal);
        foreach (SpirvBindingInfo binding in samplerBindings)
        {
            string inlineName = NextInlineSamplerName(samplerNamesUsed);
            samplerNamesUsed.Add(inlineName);
            string? pairedSuffix = DerivePairedTextureSuffix(binding.Set, binding.Binding, metadata);
            string name = pairedSuffix is null ? inlineName : $"{inlineName}_{pairedSuffix}";
            // Strip any earlier patch and re-emit so the synthesised name wins.
            if (patchedIds.Contains(binding.Id))
            {
                int existing = result.FindIndex(p => p.Id == binding.Id);
                if (existing >= 0) result.RemoveAt(existing);
            }
            result.Add((binding.Id, name));
            patchedIds.Add(binding.Id);
        }

        return result;
    }

    // Look up the texture(s) bound to this sampler slot and return the
    // SUFFIX (no `sampler_` prefix) — the caller stitches it onto the
    // inline form. Returns null when no texture targets this sampler;
    // the caller leaves the bare inline form in that case.
    private static string? DerivePairedTextureSuffix(int set, int binding, SerializedProgramData metadata)
    {
        List<TextureParameter> linked = metadata.TextureParameters
            .Where(t => metadata.GetSetIdFor(t.Index, ShaderResourceType.Texture) == set && t.SamplerIndex == binding && !string.IsNullOrWhiteSpace(t.Name))
            .OrderBy(t => t.Index)
            .ToList();
        if (linked.Count == 0) return null;
        string texName = linked[0].Name;
        return texName.StartsWith('_') ? texName[1..] : texName;
    }

    // Pool of Unity-recognised inline sampler names — pairs of Filter (Point /
    // Linear / Trilinear) and Wrap (Clamp / Repeat / Mirror / MirrorOnce). Unity
    // parses each name verbatim to build the static sampler state, so any name
    // outside this pool fails the importer's "is not a recognized inline name"
    // check. We walk them in order so the first unbound sampler gets the safest
    // default (`sampler_LinearClamp`); only when that's taken do we move on.
    private static readonly string[] InlineSamplerPool =
    {
        "sampler_LinearClamp",
        "sampler_LinearRepeat",
        "sampler_LinearMirror",
        "sampler_LinearMirrorOnce",
        "sampler_PointClamp",
        "sampler_PointRepeat",
        "sampler_PointMirror",
        "sampler_PointMirrorOnce",
        "sampler_TrilinearClamp",
        "sampler_TrilinearRepeat",
        "sampler_TrilinearMirror",
        "sampler_TrilinearMirrorOnce",
    };

    private static string NextInlineSamplerName(HashSet<string> usedNames)
    {
        foreach (string candidate in InlineSamplerPool)
        {
            if (!usedNames.Contains(candidate)) return candidate;
        }
        // Past the pool: anisotropic variants are also recognised, but we
        // arrive here only when 12 distinct unbound samplers already exist —
        // exceedingly rare. Anisotropic + level form lets us keep growing.
        for (int aniso = 2; aniso <= 16; aniso *= 2)
        {
            foreach (string filterWrap in InlineSamplerPool)
            {
                string candidate = filterWrap + "_aniso" + aniso;
                if (!usedNames.Contains(candidate)) return candidate;
            }
        }
        // Beyond that we give up — appended counter is non-recognised but at
        // least uniqueness is preserved. Unity will still error, but the
        // diagnostic will point at the count rather than at a silent collision.
        return "sampler_LinearClamp_overflow_" + usedNames.Count;
    }

    private List<(uint TypeId, uint MemberIndex, string Name)> BuildMemberPatches(IReadOnlyList<SpirvBindingInfo> bindings, SerializedProgramData metadata)
    {
        List<(uint TypeId, uint MemberIndex, string Name)> result = new();
        foreach (var resource in metadata.EnumerateResourceBindings().Where(static r => r.RegisterType == 'b' && !string.IsNullOrWhiteSpace(r.Name)))
            foreach (SpirvBindingInfo binding in MatchBindings(bindings, resource).Where(static b => b.DescriptorType == "UniformBuffer" && b.StructTypeId is > 0))
            {
                ConstantBufferParameter? cb = metadata.GetConstantBufferByName(ResolveName(resource, binding));
                if (cb == null) continue;
                result.AddRange(MemberPatches(binding, cb));
            }
        return result;
    }

    private static IEnumerable<SpirvBindingInfo> MatchBindings(IReadOnlyList<SpirvBindingInfo> bindings, (string Name, int Binding, int Set, ShaderResourceType Type, char RegisterType) resource)
        => bindings.Where(binding => binding.Set == resource.Set && binding.Binding == resource.Binding && DescriptorMatches(resource.RegisterType, binding.DescriptorType));

    private string ResolveName((string Name, int Binding, int Set, ShaderResourceType Type, char RegisterType) resource, SpirvBindingInfo binding)
        => binding.DescriptorType == "UniformBuffer" ? _rewriter.GetResolvedBufferName(resource.Set, resource.Binding) ?? resource.Name : resource.Name;

    private static IEnumerable<(uint TypeId, uint MemberIndex, string Name)> MemberPatches(SpirvBindingInfo binding, ConstantBufferParameter cb)
    {
        List<NumericShaderParameter> all = AllNumericParameters(cb);
        if (binding.StructMemberCount == 1 && all.Count > 0 && all.All(static p => p.IsMatrix && p.RowCount == 4 && p.ColumnCount == 4))
            return new[] { (binding.StructTypeId!.Value, 0u, string.Join("_", all.Select(static p => p.Name ?? string.Empty))) };

        // BLANKET-FILTER + DEDUPE pass: walk EVERY member-index of this cbuffer
        // struct type, choose its final name, then apply sanitize-first
        // collision dedup ACROSS THE WHOLE CBUFFER.
        //
        // Naming priority per member-index:
        //   1. Seed CB name at the matching byte offset (StructParameter or
        //      AllNumericParameters with non-whitespace Name)
        //   2. Existing OpMemberName from the input module (sanitised)
        //   3. Stable placeholder `f_<offset>` when the cook stripped the
        //      member name AND seed metadata doesn't cover this offset
        //
        // After name selection we run a per-cbuffer-type dedup so any
        // collisions (two seed entries with the same author name, or two
        // cook names that collapse to the same HLSL identifier after
        // sanitisation) get an `_at_<offset>` disambiguator. The dedup
        // applies to EVERY cbuffer with seed CB metadata uniformly — no
        // hardcoded UB-name filter; the decision to blanket-process is
        // gated purely on whether `cb` is non-null at the caller.
        uint typeId = binding.StructTypeId!.Value;
        Dictionary<int, string?> seedByOffset = new();
        foreach (StructParameter p in cb.StructParameters)
        {
            if (!string.IsNullOrWhiteSpace(p.Name) && !seedByOffset.ContainsKey(p.Index)) seedByOffset[p.Index] = p.Name;
        }
        foreach (NumericShaderParameter p in cb.AllNumericParameters)
        {
            if (!string.IsNullOrWhiteSpace(p.Name) && !seedByOffset.ContainsKey(p.Index)) seedByOffset[p.Index] = p.Name;
        }

        List<(uint TypeId, uint MemberIndex, string Name)> result = new();
        HashSet<string> seenNames = new(StringComparer.Ordinal);
        // Visit members in member-index order so dedup `_at_<offset>` is
        // deterministic and matches the SPIR-V emit order.
        IEnumerable<KeyValuePair<int, uint>> ordered = binding.MemberOffsets.OrderBy(static kv => kv.Key);
        foreach (KeyValuePair<int, uint> kv in ordered)
        {
            int memberIndex = kv.Key;
            int byteOffset = (int)kv.Value;

            string? candidate = null;
            if (seedByOffset.TryGetValue(byteOffset, out string? seedName)) candidate = seedName;
            else if (binding.CurrentMemberNames.TryGetValue(memberIndex, out string? cookName) && !string.IsNullOrEmpty(cookName)) candidate = cookName;

            string sanitized = SanitizeHlslMember(candidate);
            if (string.IsNullOrEmpty(sanitized)) sanitized = $"f_{byteOffset}";

            string final;
            if (seenNames.Add(sanitized)) final = sanitized;
            else
            {
                string disamb = $"{sanitized}_at_{byteOffset}";
                seenNames.Add(disamb);
                final = disamb;
            }
            result.Add((typeId, (uint)memberIndex, final));
        }
        return result;
    }

    // Sanitize candidate to a HLSL-safe identifier MATCHING spirv-cross's
    // own emit-side rule: non-alphanumeric chars become `_`, then runs of
    // consecutive underscores collapse to a single `_`, and trailing `_`
    // is trimmed. CRITICAL: spirv-cross collapses underscore runs in HLSL
    // output. Without mirroring that here, dedup keyed on the raw replaced
    // string sees `AO_________` (CJK author name "AO对自发光的遮蔽强度") and
    // `AO__` ("AO强度") as DIFFERENT, but spirv-cross emits both as `AO_`
    // and the HLSL gets duplicate `Material_AO_` declarations — an actual
    // compile error in the user-facing artifact. By collapsing here, both
    // collapse to `AO_` at dedup time, the collision is caught, and the
    // second occurrence gets `_at_<offset>` disambiguation.
    //
    // Encoding-agnostic: any non-alphanumeric character (CJK, Arabic,
    // emoji, punctuation, whitespace) goes through the same `_`
    // replacement → collapse pipeline. No hardcoded character lists.
    private static string SanitizeHlslMember(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        var sb = new System.Text.StringBuilder(raw.Length);
        bool lastUnderscore = false;
        foreach (char c in raw)
        {
            bool isAlnum = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
            if (isAlnum)
            {
                sb.Append(c);
                lastUnderscore = false;
            }
            else if (!lastUnderscore)
            {
                sb.Append('_');
                lastUnderscore = true;
            }
            // else: skip — runs collapse to single `_`
        }
        // Trim leading AND trailing `_`. spirv-cross's HLSL emit also
        // collapses underscore runs ACROSS the cbuffer-variable prefix
        // boundary, so `<Var>_<Member>` with Member="_AO" comes out as
        // `<Var>_AO` — the leading `_` of the member name gets fused into
        // the prefix-member separator. Trimming both sides of the member
        // form makes dedup see what spirv-cross will produce.
        int start = 0;
        while (start < sb.Length && sb[start] == '_') start++;
        int end = sb.Length;
        while (end > start && sb[end - 1] == '_') end--;
        if (end == start) return string.Empty;
        string body = sb.ToString(start, end - start);
        // Leading-digit guard for HLSL: prefix `_` so the identifier stays
        // valid. Dedup still sees the original collision shape because all
        // numeric-prefixed names get the same prefix uniformly.
        return (body[0] >= '0' && body[0] <= '9') ? "_" + body : body;
    }

    private static List<NumericShaderParameter> AllNumericParameters(ConstantBufferParameter cb)
    {
        List<NumericShaderParameter> result = new(cb.AllNumericParameters);
        foreach (StructParameter s in cb.StructParameters) result.AddRange(s.AllNumericMembers);
        return result;
    }

    private static int? FindMemberIndex(SpirvBindingInfo binding, int byteOffset)
    {
        foreach (KeyValuePair<int, uint> pair in binding.MemberOffsets)
            if (pair.Value == (uint)byteOffset)
                return pair.Key;
        return null;
    }

    private Source Emit(byte[] spirv, string? preferredEntryPoint, uint shaderModel, TempFiles temp)
    {
        (SpirvStage stage, string? entryPoint) = ResolveEntry(spirv, preferredEntryPoint);
        // spirv-cross's HLSL backend has well-documented gaps that GLSL covers:
        //   * tess / geom — InvocationId / TessCoord / TessLevel* / patch-constant emission
        //   * raytracing (rgen/rint/rahit/rchit/rmiss/rcall) — RT builtins (5321/5322/5326/...)
        //   * mesh / task — primitive index / cull primitive builtins
        //   * cbuffers whose array members have `ArrayStride < 16` (UE bindless index
        //     tables — `cbuffer Material { uint _m0[N] }` with stride 4 — fail with
        //     "cbuffer ID X (name: ...) member 0 (name: _m0) cannot be expressed with
        //     either HLSL packing layout or packoffset"). spirv-cross's GLSL backend
        //     handles arbitrary strides fine.
        //   * uint8/uint16 storage usage that's legal in SPIR-V but not in shader-
        //     model-5.x HLSL.
        // For RT/mesh/task we go straight to GLSL (the HLSL attempt is guaranteed
        // to fail). For everything else we try HLSL first, then **always** try GLSL
        // as a fallback if HLSL fails — strictly more permissive than the previous
        // "tess/geom-only fallback" gate. The output language is reflected via
        // SourceLanguage / SourceFileExtension so consumers writing the file pick
        // the right extension.
        bool requiresGlsl = IsRaytracingOrMeshStage(stage);

        if (!requiresGlsl)
        {
            if (TryEmit(spirv, temp.Spirv, temp.Hlsl, entryPoint, stage, shaderModel, hlsl: true, quiet: true, out string? hlsl))
                return new(hlsl!, "hlsl", ".hlsl");
            // HLSL failed. Surface the captured stderr through Console.Error so
            // the diagnostic crumbs (which `quiet: true` above suppressed) still
            // make it into the per-shader failure dump path; the caller's
            // exception-driven dump uses _lastToolFailureLog as the source.
            if (!string.IsNullOrEmpty(_lastToolFailureLog))
            {
                Console.Error.WriteLine($"[spirv-cross note] HLSL emission failed for {stage} — falling back to GLSL. Underlying cause:");
                Console.Error.WriteLine(_lastToolFailureLog);
            }
        }
        else
        {
            string note = $"[spirv-cross note] {stage} stage: HLSL backend cannot represent raytracing/mesh builtins. Emitting GLSL output.";
            Console.Error.WriteLine(note);
        }

        if (TryEmit(spirv, temp.Spirv, temp.Glsl, entryPoint, stage, shaderModel, hlsl: false, quiet: false, out string? glsl))
            return new(glsl!, "glsl", ".glsl");

        throw new InvalidOperationException("Failed to decompile patched SPIR-V.");
    }

    private bool TryEmit(byte[] spirv, string tempSpv, string outputPath, string? entryPoint, SpirvStage stage, uint shaderModel, bool hlsl, bool quiet, out string? source)
    {
        source = null;
        File.WriteAllBytes(tempSpv, spirv);
        List<string> args = new() { Tool("spirv-cross.exe"), tempSpv, "--output", outputPath, hlsl ? "--hlsl" : "-V" };
        AppendStageArgs(args, entryPoint, stage);
        if (hlsl)
        {
            args.Add("--shader-model");
            args.Add(shaderModel.ToString());
            args.Add("--force-zero-initialized-variables");
        }
        else
        {
            // GLSL emission: always go through the modern Vulkan profile
            // (GLSL 460 + --vulkan-semantics). UE shipping SPV is
            // vulkan-flavoured anyway, and GLSL 460 is the lowest version
            // that accepts every SPIR-V feature spirv-cross might emit
            // (raytracing builtins, mesh-shader EXT, descriptor indexing,
            // shader_clock, control_flow_attribute, etc.). Without these
            // flags GLSL emit falls back to a pre-Vulkan profile that
            // rejects most of UE's bindless / RT shaders, defeating the
            // point of the HLSL-failure fallback.
            args.Add("--version");
            args.Add("460");
            args.Add("--vulkan-semantics");
        }
        if (!Run(args.ToArray(), "spirv-cross", quiet: quiet) || !File.Exists(outputPath)) { Delete(outputPath); return false; }
        source = File.ReadAllText(outputPath, Encoding.UTF8);
        Delete(outputPath);
        return true;
    }

    private static (SpirvStage Stage, string? EntryPoint) ResolveEntry(byte[] spirv, string? preferred)
    {
        SpirvModule module = SpirvModule.Parse(spirv);
        (SpirvStage Stage, string? EntryPoint)? first = null;
        foreach (SpirvInstruction i in module.Instructions)
        {
            if (i.OpCode != SpvOpCode.OpEntryPoint || i.Words.Length < 3) continue;
            string entry = ReadString(i.Words, 3);
            SpirvStage stage = ClassifyExecutionModel(i[1]);
            first ??= (stage, entry);
            if (!string.IsNullOrWhiteSpace(preferred) && string.Equals(entry, preferred, StringComparison.Ordinal)) return (stage, entry);
        }
        return first ?? (SpirvStage.Unknown, preferred);
    }

    // Numeric values come from the SPIR-V execution model enum. 0..6 are core graphics +
    // compute; 5267..5272 are KHR raytracing; 5313..5318 are the equivalent NV-flavoured
    // raytracing aliases (same semantics — UE shipping SPV from dxil-spirv emits NV
    // numbers; spirv-dis happens to print them with the KHR friendly names so the
    // discrepancy is invisible without looking at raw words). 5364..5365 are EXT mesh
    // shading; 5267..5272 NV-mesh-like (legacy) is unlikely from the UE pipeline.
    private static SpirvStage ClassifyExecutionModel(uint model) => model switch
    {
        0 => SpirvStage.Vertex,
        1 => SpirvStage.TessControl,
        2 => SpirvStage.TessEvaluation,
        3 => SpirvStage.Geometry,
        4 => SpirvStage.Fragment,
        5 => SpirvStage.Compute,
        5267 or 5313 => SpirvStage.RayGeneration,
        5268 or 5314 => SpirvStage.Intersection,
        5269 or 5315 => SpirvStage.AnyHit,
        5270 or 5316 => SpirvStage.ClosestHit,
        5271 or 5317 => SpirvStage.Miss,
        5272 or 5318 => SpirvStage.Callable,
        5364 => SpirvStage.Task,
        5365 => SpirvStage.Mesh,
        _ => SpirvStage.Unknown,
    };

    private static bool IsRaytracingOrMeshStage(SpirvStage stage) => stage is
        SpirvStage.RayGeneration
        or SpirvStage.Intersection
        or SpirvStage.AnyHit
        or SpirvStage.ClosestHit
        or SpirvStage.Miss
        or SpirvStage.Callable
        or SpirvStage.Task
        or SpirvStage.Mesh;

    private static string ReadString(IReadOnlyList<uint> words, int start)
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

    private static void AppendStageArgs(List<string> args, string? entryPoint, SpirvStage stage)
    {
        if (!string.IsNullOrWhiteSpace(entryPoint)) { args.Add("--entry"); args.Add(entryPoint); }
        string? stageArg = stage switch
        {
            SpirvStage.Vertex => "vert",
            SpirvStage.TessControl => "tesc",
            SpirvStage.TessEvaluation => "tese",
            SpirvStage.Geometry => "geom",
            SpirvStage.Fragment => "frag",
            SpirvStage.Compute => "comp",
            SpirvStage.RayGeneration => "rgen",
            SpirvStage.Intersection => "rint",
            SpirvStage.AnyHit => "rahit",
            SpirvStage.ClosestHit => "rchit",
            SpirvStage.Miss => "rmiss",
            SpirvStage.Callable => "rcall",
            SpirvStage.Task => "task",
            SpirvStage.Mesh => "mesh",
            _ => null,
        };
        if (stageArg != null) { args.Add("--stage"); args.Add(stageArg); }
    }

    private string DescribePatchPlan(byte[] spirv, SerializedProgramData metadata)
    {
        if (metadata.GetResourceBindingCount() == 0) return "Patch plan: metadata contained no resource bindings.";

        IReadOnlyList<SpirvBindingInfo> bindings = _patcher.AnalyzeBindingsDetailed(spirv);
        List<(uint Id, string Name)> names = BuildNamePatches(bindings, metadata);
        List<(uint TypeId, uint MemberIndex, string Name)> members = BuildMemberPatches(bindings, metadata);

        List<string> lines = new()
        {
            $"Patch plan: resourceBindings={metadata.GetResourceBindingCount()} matchedBindings={bindings.Count} opNames={names.Count} opMemberNames={members.Count}",
        };

        foreach ((uint id, string name) in names.Take(16)) lines.Add($"  OpName Id={id} Name={name}");
        if (names.Count > 16) lines.Add($"  ... {names.Count - 16} more OpName patches");

        foreach ((uint typeId, uint memberIndex, string name) in members.Take(16)) lines.Add($"  OpMemberName TypeId={typeId} MemberIndex={memberIndex} Name={name}");
        if (members.Count > 16) lines.Add($"  ... {members.Count - 16} more OpMemberName patches");

        return string.Join(Environment.NewLine, lines);
    }

    private static string DescribeBuiltInDecorations(byte[] spirv)
    {
        SpirvModule module = SpirvModule.Parse(spirv);
        Dictionary<uint, string> names = new();
        List<string> lines = new();

        foreach (SpirvInstruction instruction in module.Instructions)
        {
            if (instruction.OpCode == SpvOpCode.OpName && instruction.Words.Length >= 3)
            {
                string? name = ReadString(instruction.Words, 2);
                if (!string.IsNullOrWhiteSpace(name)) names[instruction[1]] = name;
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

        if (process.ExitCode == 0) return true;

        string failureLog = $"{name} failed (exit={process.ExitCode}): {Trim(stderr.ToString())}{(string.IsNullOrWhiteSpace(stdout) ? string.Empty : Environment.NewLine + Trim(stdout))}";
        // Always cache the failure log even when `quiet`, so a downstream
        // dump can include the actual upstream message even though the
        // console suppression skipped logging it.
        _lastToolFailureLog = failureLog;
        return quiet || Log(failureLog);
    }

    private bool Log(string error) { Debug.WriteLine(error); Console.Error.WriteLine(error); return false; }
    private string Tool(string name) => Path.Combine(_toolsDir!, name);

    private static bool DescriptorMatches(char registerType, string? descriptorType) => descriptorType switch
    {
        "UniformBuffer" => registerType == 'b',
        "Sampler" => registerType == 's',
        "SampledImage" => registerType == 't',
        // ByteAddressBuffer / StructuredBuffer SRVs (UE LocalVF vertex-fetch
        // buffers, GPUSkin, GeomCache, etc.) come through DXC as
        // StorageBuffer in SPIR-V, but bind on `t` registers, not `u`. So
        // accept both — UAVs still pick up `'u'` via the (Set,Binding)
        // pairing the surrounding MatchBindings does, and read-only SRVs
        // now get their names instead of falling through to spirv-cross's
        // synthetic `T<n>` defaults.
        "StorageBuffer" => registerType == 't' || registerType == 'u',
        "StorageImage" => registerType == 'u',
        _ => false,
    };

    private static bool IsDxbc(byte[] data) => data.Length >= 4 && data[0] == 'D' && data[1] == 'X' && data[2] == 'B' && data[3] == 'C';
    private static bool IsSpirv(byte[] data) => data.Length >= 4 && BitConverter.ToUInt32(data, 0) == SpvOpCode.MagicNumber;

    // DXIL detection: SM 5.x via dxbc2dxil produces a DXBC container with
    // an embedded `DXIL` chunk. SM 6.0+ shaders ship the same way from
    // UE's ShaderConductor / DXC pipeline (DXBC container holds the DXIL
    // module so the runtime can pick it up via the existing DXBC-style
    // chunk loader). Native bitcode (LLVM `BC\xC0\xDE` magic) is the rare
    // case where a tool emits raw DXIL outside the DXBC envelope — we
    // accept that too so a hand-extracted DXIL byte blob still routes to
    // the dxil-spirv path.
    private static bool IsDxil(byte[] data)
    {
        // Path 1 — bare LLVM bitcode magic (`BC \xC0\xDE`). dxil-spirv
        // accepts this directly as raw DXIL on its --raw-llvm path.
        if (data.Length >= 4 && data[0] == 0x42 && data[1] == 0x43 && data[2] == 0xC0 && data[3] == 0xDE) return true;

        // Path 2 — DXBC container with a DXIL chunk. Covers both SM 5.1
        // (via dxbc2dxil) and SM 6.0+ (DXC-emitted, container-wrapped).
        // Walk the DXBC chunk-offset table looking for a chunk whose
        // first 4 bytes are 'DXIL'. The chunk count is at offset 28; the
        // offset table starts at 32.
        if (!IsDxbc(data) || data.Length < 32) return false;
        int count = BitConverter.ToInt32(data, 28);
        if (count <= 0 || count > 256 || 32 + (count * 4) > data.Length) return false;
        for (int i = 0; i < count; i++)
        {
            int offset = BitConverter.ToInt32(data, 32 + (i * 4));
            if (offset < 0 || offset + 4 > data.Length) continue;
            if (data[offset] == 'D' && data[offset + 1] == 'X' && data[offset + 2] == 'I' && data[offset + 3] == 'L') return true;
            // ILDB / ILDN chunks are emitted by DXC alongside DXIL for
            // debug info — their presence is also a strong signal of an
            // SM 6.x container (and dxil-spirv handles those fine since
            // they're wrapped around the DXIL chunk).
            if (data[offset] == 'I' && data[offset + 1] == 'L' && data[offset + 2] == 'D' &&
                (data[offset + 3] == 'B' || data[offset + 3] == 'N')) return true;
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
