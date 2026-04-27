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
- **Open in v2** (next iteration's target):
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
