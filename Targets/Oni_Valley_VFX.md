# Target: Oni_Valley_VFX (UE 5.x)

Per-target record for the UE5 demo `Oni_Valley_VFX`. Updated each
loop iteration with metrics + the iteration's narrowest delta.

## Inputs
- Game directory: `D:\Games\OniValleyDemo`
- Pak / IoStore root: `D:\Games\OniValleyDemo\Oni_Valley_VFX\Content\Paks`
  - `Oni_Valley_VFX-Windows.pak` / `.utoc` / `.ucas`
  - `global.utoc` / `.ucas`
- Mappings: `D:\Games\OniValleyDemo\Oni_Valley_VFX\Binaries\Win64\Oni_Valley_VFX-Win64-Shipping-Mappings.usmap`
- Engine version: UE 5.1.x (matches `E:\UnrealEngine-5.1.1-release`)

## Output Layout (the canonical default)
- Export root:
  `D:\Ruri\Dev\GitProject\FractalTools\Ruri-RipperHook\Source\Ruri.FModelHook\bin\Debug\Output\Exports\Oni_Valley_VFX`
- Per the FModel hook, the unified metadata lands at
  `<root>/UnifiedShaderMetadata.json`.
- Per-library sidecars under `<root>/Content/`:
  - `ShaderArchive-<libname>-<sm>.assetinfo.json`
  - `ShaderArchive-<libname>-<sm>.stableinfo.json`
  - `ShaderArchive-<libname>-<sm>.ushaderlib`
  - `ShaderArchive-<libname>-<sm>.ushaderbytecode` (raw FModel export)
- Decompiled HLSL goes under
  `<root>/Decompiled/<libname>-<sm>/` (one `.hlsl` + `.metadata.json` +
  `.spv` per shader).

## Existing Libraries (at this iteration)
| Library | Size | SM |
| --- | --- | --- |
| `ShaderArchive-Global-PCD3D_SM5-PCD3D_SM5.ushaderlib` | 37 MB | SM5 |
| `ShaderArchive-Global-PCD3D_SM6-PCD3D_SM6.ushaderlib` | 38 MB | SM6 |
| `ShaderArchive-Oni_Valley_VFX-PCD3D_SM5-PCD3D_SM5.ushaderlib` | 44 MB | SM5 |
| `ShaderArchive-Oni_Valley_VFX-PCD3D_SM6-PCD3D_SM6.ushaderlib` | 69 MB | SM6 |

The SM5 Oni_Valley_VFX library is the primary fixture; SM6 will be the
secondary regression once SM5 is clean.

## Representative Sample Materials (for verification)
- `MI_Cliff_small_ground_level` — has `Material` CB with named scalar
  parameters + virtual texture references; closure already proven in
  `SHADER_MAPPING_RESEARCH.md`.
- `M_Cliffs` — base material; provides `ParameterCollectionInfos` and
  `CachedExpressionData.ReferencedTextures`.

## Symbol Recovery Status
Tracked per loop. Update with hard counts each iteration.

| Iteration | UB names recovered (SRT) | Material textures named | Engine SRV slots named | `_RegisterSpace` count |
| --- | --- | --- | --- | --- |
| 0 (baseline) | 0 (no SRT decode) | partial via UeShaderSymbolBuilder | 0 | many |
| 1 | all UBs in 'u' optional block ✅ | placeholder `Material_SRVN` | placeholder `<UB>_SRVN` (e.g. `LumenCardScene_SRV0`, `View_SRV49`) | **0** across 16004 shaders ✅ |
| 2 | unchanged ✅ | typed names plumbed (gated on FModel material `.json` exports) | `View / OpaqueBasePass / SceneTextures / LumenCardScene / VirtualShadowMap / RenderVolumetricCloudParameters` member names from UE 5.1.1 source | running... (see iteration log) |
| 3 (research) | locked verdict matrix in `UE_SHIPPING_NAME_TRUTH.md` | confirmed: typed flat names = engine-generated, recoverable from material `.uasset` only — no shipping-cook-side compiler name truth | confirmed: never in cooked data, hardcoded mirror is the only path | n/a — no decompile run this iteration |

Rules:
- **UB names recovered (SRT)**: count of distinct uniform buffer
  bindings whose name comes from `FShaderCodeUniformBuffers` and
  whose individual texture/sampler/SRV/UAV slots got a flat
  `<UB>_<member>` name via SRT decode.
- **Material textures named**: `Material_Texture2D_<i>` /
  `Material_Texture2D_<i>Sampler` etc. injected.
- **View_* recovered**: how many `View_<member>` flat names match the
  static engine layout.
- **Sample HLSL clean?**: `_RegisterSpace0` token count drops below 5 on
  a representative pixel shader.

## Driver Commands
1. Build (do this every iteration before re-running):
   ```
   dotnet build "Source/Ruri.ShaderDecompiler/Ruri.ShaderDecompiler.csproj" -c Debug
   dotnet build "Source/Ruri.FModelHook/Ruri.FModelHook.csproj" -c Debug
   ```
2. If `UnifiedShaderMetadata.json` missing under
   `<exportRoot>/UnifiedShaderMetadata.json`, run the auto-driver:
   ```
   "Source/Ruri.FModelHook/bin/Debug/Ruri.FModelHook.exe" \
     --auto-decompile "D:\Games\OniValleyDemo"
   ```
3. Otherwise just run the offline decompiler against a chosen library:
   ```
   "Source/Ruri.ShaderDecompiler/bin/Debug/net8.0/Ruri.ShaderDecompiler.exe" \
     "<exportRoot>/Content/ShaderArchive-Oni_Valley_VFX-PCD3D_SM5-PCD3D_SM5.ushaderlib" \
     "<exportRoot>/Decompiled/Oni_Valley_VFX-SM5" \
     --mapping "<exportRoot>/UnifiedShaderMetadata.json"
   ```

## Verification Snippets
After each iteration, `cd <exportRoot>/Decompiled/Oni_Valley_VFX-SM5`
and run:
```
grep -c '_RegisterSpace' *.hlsl | sort -t':' -k2 -n -r | head
grep -c 'Material_'      *.hlsl | sort -t':' -k2 -n -r | head
grep -c 'View_'          *.hlsl | sort -t':' -k2 -n -r | head
grep -c 'OpaqueBasePass_' *.hlsl | sort -t':' -k2 -n -r | head
```

Pick the same shader index across iterations as the canonical fixture.

## It. 6 (latest) — `MI_Cliff_Parent_PS_3500` material-symbol round

Surgical run via `--shader-index 3500` on the SM5 Oni_Valley library.
Material: `Oni_Valley_VFX/Content/Oni_Project/Materials/MI_Cliff_Parent`.
Cross-reference: user-supplied `MI_Cliff_large.json` (a child instance
of the same parent material).

### What changed
- Deleted `Unreal/EngineUniformBuffers.cs` (was hard-coded UE-source
  layouts of `View` / `OpaqueBasePass` / `TranslucentBasePass` / etc.).
  Banned by project rule. Symbolizer now falls through to UB-context
  placeholder for engine UBs.
- New `Unreal/UeUnifiedMaterialReader.cs` — reads
  `MaterialInterfaces[<path>].LoadedShaderMaps[*].MaterialShaderMapContent.UniformExpressionSet`
  directly out of the auto-export hook's
  `UnifiedShaderMetadata.json`, so we no longer require per-material
  `*.uasset.json` files (which the FModel UI only writes when the user
  manually clicks Save Properties on every material).
- `Unreal/UeShaderSymbolInputsReader.cs`:
  - `ParseMaterialParameterInfo` now accepts both shapes
    (`{ ParameterInfo: { Name } }` from per-material JSON, and the
    flattened `{ ParameterName }` shape from the unified metadata).
  - Material-CB extraction now populates `VectorParams`
    (`[Name, Type, ByteOffset, RowCount, ColumnCount]`) instead of
    raw `CBParams`. `RefreshCompatibilityViews` regenerates `CBParams`
    from `VectorParams`+`MatrixParams`, and the SPIR-V structured
    rewriter consumes the typed arrays. Direct `CBParams` adds were
    being preserved by the data layer but hidden from the rewriter,
    so named members never reached the HLSL output.
  - Added `ReadFromUniformExpressionSet(materialPath, shaderPlatform, ues)`
    public entry so the new unified reader can hand a UES JsonElement
    straight in without faking the `LoadedMaterialResources` wrapper.
- `Program.cs` — material lookup now tries `UeUnifiedMaterialReader`
  first, falls back to the old per-material-JSON path. Material
  layout from the unified reader is plumbed through to the SRT
  symbolizer for typed Material-UB SRT names if any survive.

### Result on shader 3500

| Slot | Before this iteration | After |
| --- | --- | --- |
| `t2` | `View_PrimitiveSceneData` (FAKE — hard-coded from UE source) | `View_SRV45` (UB-context placeholder, source-truth honest) |
| `t3` | `View_SkyIrradianceEnvironmentMap` (FAKE) | `View_SRV49` (placeholder) |
| `t4`–`t14` (11 Texture2D) | anonymous | unchanged (loose bindings, not in SRT) |
| `cbuffer Material` body | flat `float4 Material_1_m0[N]` | last-name-wins still on HLSL surface (collapsed array), but the metadata sidecar now carries the **12 correctly named** vector params: `SelectionColor` (b0), `UV Scale Near` (b16), `Normal blend` (b28), `Blend Sharpness (S)` (b32), `Blend Bias (S)` (b40), `Curvature cavity intensity` (b64), `Curvature highlights intensity` (b72), `AO tint` (b80), `AO tint power` (b108), `Specular detail` (b112), `Specular softness` (b116), `Roughness dullness` (b120) |

All 12 names are **byte-identical** matches to the
`ScalarParameterValues` and `VectorParameterValues` arrays in
`MI_Cliff_large.json` — the user's cross-reference. Names trace from
`UnifiedShaderMetadata.json`'s
`UniformExpressionSet.UniformNumericParameters[i].ParameterName`
through `UniformPreshaders[i]` → `UniformPreshaderFields[i].BufferOffset`,
which is the only proven source-truth bridge for material CB members
in shipping cooks.

### Known follow-up

- **HLSL `cbuffer` body is one collapsed array, not 12 named members.**
  Cause: the cooked DXBC has the Material UB bytecode-side as a single
  `float4 m0[N]` array (because RDEF was stripped and the only
  reflection that survived is the flat array), so the SPIR-V module
  has one struct member to begin with. Our SPIR-V structured-CB
  rewriter currently *renames* the existing single member with the
  last patched name; it does not split a one-member float4 array
  into N members at proven byte offsets. The metadata sidecar already
  has the right shape — the next iteration should teach the rewriter
  to break a flat array into named members at known byte offsets.
- **Material UB textures (T4–T14) stay anonymous.** The Material UB
  in this shader's SRT has zero entries (the SRT's `ResourceTableBits`
  for the Material UB index is unset). They're loose
  `FShaderParameterBindings.ResourceParameters` slots — UE's other
  binding path. Recovering names there is the open lead in
  `CURRENT_LIMITATIONS.md` (CUE4Parse `Bindings.ResourceParameters`
  deserialization investigation).

## Iteration Log
- **It. 0** — Baseline established; SRT decoder, engine UB layouts,
  Material texture naming, and auto-decompile driver all missing.
- **It. 1** — SRT decoder + UB-name binding + auto-decompile driver
  landed.
  - New code:
    - `Unreal/UeShaderResourceTableDecoder.cs` — decodes
      `FRHIResourceTableEntry` packed uint32 stream from the
      `ShaderResourceViewMap` / `SamplerMap` /
      `UnorderedAccessViewMap` arrays, header-offset addressing per
      `D3D12Commands.cpp` runtime loop, into
      `(UBIndex, ResourceIndex, BindIndex, RegisterType)` records.
    - `Unreal/UeShaderResourceTableSymbolizer.cs` — turns those
      records plus the shader's `FShaderCodeUniformBuffers` ('u')
      block into named CB / Texture / Sampler / UAV bindings on the
      target `ShaderSymbolData`.
    - `Unreal/EngineUniformBuffers.cs` — empty engine UB layout
      table; lookup is a stub returning placeholder names. Populate
      incrementally as members are verified against samples.
    - `Unreal/MaterialUniformBufferLayout.cs` — replays
      `FUniformExpressionSet::CreateBufferStruct()` ordering once
      typed-array counts are passed in. Not wired in v1; resolver
      param is plumbed through but the per-shader material context
      isn't fed in yet.
  - Modified:
    - `Unreal/UeRuntimeShaderSymbolReader.cs` calls the symbolizer
      after seeding CB bindings.
    - `ShaderDecompiler.cs::Pipe` now merges runtime-fallback
      `TextureParameters` / `Samplers` / `Buffers` / `UAVs` into the
      caller's metadata (previously only CB bindings were merged, so
      SRT-decoded resources were silently dropped when material
      symbols were also supplied).
    - `Program.cs` adds `--auto-decompile <exportRoot>` mode that
      iterates every `*.ushaderlib` under `<exportRoot>/Content`,
      uses a sibling `UnifiedShaderMetadata.json` if present
      (otherwise sidecar-only resolution), and writes per-library
      output under `<exportRoot>/Decompiled/<libname>/`.
    - `Program.cs::ProcessUnrealLibrary` no longer hard-fails when
      `--mapping` is omitted; it logs a fallback note and continues
      with sidecar resolution only.
  - Run:
    ```
    Ruri.ShaderDecompiler.exe --auto-decompile \
      "...\Output\Exports\Oni_Valley_VFX"
    ```
    → 4 libraries processed in ~5 minutes, 16004 HLSL files written,
    0 library-level failures.
  - Verification (sample CS shader,
    `ShaderArchive-Oni_Valley_VFX-PCD3D_SM5-PCD3D_SM5/UnknownShader_CS_1209.hlsl`):
    - Named cbuffers: `$Globals` (b0), `View` (b1), `LumenCardScene`
      (b2), `DeferredLightUniforms` (b3), `VirtualShadowMap` (b4),
      `Material` (b5).
    - Named SRVs: `LumenCardScene_SRV0/1` (t0,t1),
      `VirtualShadowMap_SRV0/1` (t4,t5).
    - Anonymous `_RegisterSpace0[..]` tokens: **0** in this shader.
    - Aggregate: across the **4121** SM5 Oni_Valley shaders,
      **3970** carry at least one named UB / SRV binding; **0** carry
      any `_RegisterSpace` anonymity.
  - Verification (heavy CS shader, `UnknownShader_CS_649.hlsl`):
    4 named cbuffers (`_Globals`, `View`,
    `RenderVolumetricCloudParameters`, `Material`); 19 t-slot
    bindings (some named via SRT, some still anonymous because they
    are material-side bindings not in the runtime SRT — that's the
    next iteration's target); 11 s-slot samplers; 0 `_RegisterSpace`
    anonymity.
  - Regression: Unity LitPoly sample
    (`Testing/Assets/Shaders/UnityBinary/EndField/litpoly.shader.sub0.pass0.blob1.HGBuffer.dxbc.bin`)
    still produces fully named `cbuffer ShaderVariablesGlobal`,
    `UnityInstancing_SRP_UnityPerDraw_1_UnityPerDrawArray[256u]`,
    etc. Dynamic indexing into the array of structs works as before.
- **It. 3** — Source-truth research pass, no behaviour change.
  - New doc: `Source/Ruri.ShaderDecompiler/UE_SHIPPING_NAME_TRUTH.md`.
    Closed-world verdict matrix for default shipping cooks on the
    D3D target. Sourced from direct quotes of UE 5.1.1
    (`E:\UnrealEngine-5.1.1-release`).
  - Corrections vs the prior assumption set:
    - `'u'` (FShaderCodeUniformBuffers) is **unconditional** on the
      D3D path (`D3DShaderCompiler.inl:603-607`), not gated by
      `CFLAG_ExtraShaderData` as a partial reading suggested.
      That explains why iteration 1 saw UB names on every Oni
      Valley shader — that wasn't luck, that's the rule.
    - DXBC `RDEF` chunk is stripped via `D3DStripShader(STRIP_REFLECTION_DATA)`
      (`D3DShaderCompiler.cpp:1115-1140`) when
      `CFLAG_GenerateSymbols` is unset — i.e. always in shipping.
      So `Material_Texture2D_0` / `View_PerlinNoise3DTexture`
      compiler names are NOT in shipping bytecode reflection.
      Reconstructing them requires (a) Material UB layout from
      the `.uasset`'s `FUniformExpressionSet` plus replay of
      `CreateBufferStruct()` ordering, OR (b) a hard-coded mirror
      of engine UB layouts.
    - `'n'` (shader source filename) is gated on
      `CFLAG_ExtraShaderData` → stripped by default in shipping.
    - `FShaderParameterMap` (the only place compile-time names
      lived for a shader) is **not** serialized into the cooked
      archive at all.
    - `FShaderParameterBindings` and `FShaderParameterMapInfo`
      survive as frozen images but with **indices and types only,
      no names**.
    - Material `.uasset`s carry the full
      `FUniformExpressionSet.UniformNumericParameters[*].ParameterInfo.Name`
      / `UniformTextureParameters[Type][i].ParameterInfo.Name`
      / `UniformExternalTextureParameters[*].ParameterName` —
      always present, full user-facing names.
  - Implication for iteration 4:
    - The decompiler is now provably at the source-truth ceiling
      for what's directly in cooked shader bytecode. To get
      `Material_Texture2D_0` typed names, we need the material
      `.uasset` exported (FModel UI step).
    - Once FModel emits material JSONs + `UnifiedShaderMetadata.json`,
      iteration 2's plumbing activates and typed names land
      automatically. No code change required for that path.
    - The next code-side gain is annotating each
      `Material_Texture2D_<i>` resource with the user-facing
      parameter name (`"Normal input"` etc.) recovered from
      `UniformTextureParameters[Type][i].ParameterInfo.Name`.
      Cleanest spot: extend `UeMaterialUniformBufferLayout` to
      keep the per-typed-array names alongside the counts; the
      symbolizer then folds them into the binding's Name (or a
      sidecar comment) without changing the SPIR-V identifier.
- **It. 4** — Headless FModel-driver hook lands.
  - New file:
    `Source/Ruri.FModelHook/Game/SBUE/AutoExport/UE_ShaderDecompiler_AutoExport_Hook.cs`
    — registered with the new `GameType.UE_ShaderDecompiler_AutoExport`
    in `Source/Ruri.FModelHook/Core/GameType.cs`.
    Hook layout:
    - `Initialize()` reads `Environment.GetCommandLineArgs()` and
      sets static flags (`_autoExportRequested`, `_shaderOnly`,
      `_quitWhenDone`, `_readyTimeoutSec`). All CLI parsing is
      inside the hook — `Program.cs` stays clean.
    - `[RetargetMethod(typeof(MainWindow), "OnLoaded", true, false)]`
      detours the FModel main-window load event at entry. Detour
      spawns a `Task.Run` that polls
      `ApplicationService.ApplicationView.CUE4Parse.Provider`
      until `Files.Count` settles, then iterates target entries
      and marshals `vm.ExportData(entry, false)` back onto the
      WPF dispatcher — so the existing `UE_ShaderDecompiler` hook
      fires unchanged. No raw CUE4Parse re-bootstrap, no
      reimplementation of Oodle/Zlib/AES setup; the user's current
      FModel UserSettings drive the load.
  - CLI surface (parsed inside the hook, never in `Program.cs`):
    ```
    Ruri.FModelHook.exe --auto-export-cook
                        [--shader-only]
                        [--no-quit]
                        [--ready-timeout-sec <int>]
    ```
  - Verified parity with the user's manual FModel UI run:
    - Manual run produced `UnifiedShaderMetadata.json` (102765476 bytes,
      177 materials, Apr 28 05:54).
    - Headless `--auto-export-cook --shader-only` produced
      `UnifiedShaderMetadata.json` (102765476 bytes, 177 materials,
      Apr 28 06:11) — byte-identical size.
    - Same 4 `.ushaderlib` + `.assetinfo.json` + `.stableinfo.json`
      sidecars under `<exportRoot>/Content/`.
    - Hook log:
      `[AutoExport] Done. shaders=4 materials=0`.
  - Constraints respected: `UnifiedShaderMetadataExporter` was NOT
    refactored; the existing `UE_ShaderDecompiler` hook was NOT
    touched; `Program.cs` keeps its plain
    `InitializeHooks() + LaunchFModel()` shape; all CLI args live
    inside the hook module.
- **Open in v5** (next iteration's target):
  - Material UB texture flat names: pipe per-material
    `UniformTextureParameters[*].Count` from the unified metadata
    into `UeMaterialUniformBufferLayout` so SRT slot 0 of `Material`
    becomes `Material_Texture2D_0` (etc.) instead of
    `Material_SRV0`.
  - Engine UB members: populate
    `EngineUniformBuffers.KnownNames` with the `View`,
    `OpaqueBasePass`, `SceneTextures`, `LumenCardScene`,
    `VirtualShadowMap`, `RenderVolumetricCloudParameters`,
    `DeferredLightUniforms` member orders that show up here.
  - Investigate non-SRT-named t-slots in heavy shaders: those are
    direct material textures bound outside the UB resource table —
    decide whether to source their names from the cooked
    `Bindings.ResourceParameters` chain or to leave anonymous.
- (next iteration appends here)
