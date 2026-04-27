# UE5.1 Shipping Cook — Shader Symbol Persistence

Authoritative map of which shader-related names survive a default
shipping cook (no `r.Shaders.ExtraData`, no `r.Shaders.GenerateSymbols`,
no `r.Shaders.Symbols`). All claims below are direct UE 5.1.1 source
quotes from `E:\UnrealEngine-5.1.1-release`, with file:line refs.

This doc is the source-truth gate for any "where does name X come
from" decision the decompiler makes. **Do not extend the recovery
pipeline with a name source unless its persistence has an entry below.**

## TL;DR

In a default shipping cook on the **D3D** target:

- The DXBC `RDEF` chunk is **stripped** before write-out.
  → `Material_Texture2D_0`, `View_PerlinNoise3DTexture` etc. are
    *not* recoverable from bytecode reflection.
- `FShaderParameterMap` (the only place compile-time names lived
  on the shader side) is *not* serialized into the cooked archive.
- The `'n'` optional-data block (shader source filename) is gated
  on `CFLAG_ExtraShaderData` — **stripped** by default.
- The `'p'` (packed counts), `'m'` (UAV mask), `'x'` (features),
  and **`'u'` (uniform-buffer name list)** optional blocks are
  emitted **unconditionally** on the D3D path. *(The 'u' block
  being unconditional contradicts a common reading of the source —
  see footnote.)*
- `FShaderResourceTable` (`ResourceTableBits` + four packed maps +
  `ResourceTableLayoutHashes`) is part of the main per-shader
  serialization — **always present**.
- `FShaderParameterBindings` and `FShaderParameterMapInfo` survive
  in the frozen memory image — **indices only, no names**.
- Material `.uasset`s carry the full `FUniformExpressionSet`
  including parameter names (`FHashedMaterialParameterInfo` /
  `FScriptName`) — **always present**.
- Engine-defined uniform-buffer layouts (`View`,
  `FOpaqueBasePassUniformParameters`, …) are baked into the engine
  binary by C++ macros — **never written to cooked data**. Recovery
  requires a hard-coded mirror of UE source.

## Per-source-block verdict

### `'u'` — `FShaderCodeUniformBuffers` (uniform buffer name list)

```cpp
// Engine/Source/Developer/Windows/ShaderFormatD3D/Private/D3DShaderCompiler.inl:601-607
// Generate the final Output
FMemoryWriter Ar(Output.ShaderCode.GetWriteAccess(), true);
Ar << SRT;
PostSRTWriterCallback(Ar);
Ar.Serialize(CompressedData->GetBufferPointer(), CompressedData->GetBufferSize());

// Append data that is generate from the shader code and assist the usage, mostly needed for DX12
{
    Output.ShaderCode.AddOptionalData(PackedResourceCounts);
    Output.ShaderCode.AddOptionalData(FShaderCodeUniformBuffers::Key, UniformBufferNameBytes.GetData(), UniformBufferNameBytes.Num());
    AddOptionalDataCallback(Output.ShaderCode);
}
```

The whole block at lines 603–607 is **unconditional** — no flag
gate. `'p'`, `'u'`, and the `AddOptionalDataCallback` (which adds
`'m'`) all run for every D3D shader.

Verdict: **PERSISTS — D3D path, every shipping shader.**

Footnote: there is *also* a `'u'` key emission in
`MetalShaderCompiler.cpp:737`, but on Metal that key carries an
unrelated payload (`DebugCode.UncompressedSize`). Key letters are
not globally unique across platforms — read by platform.

### `'n'` — `FShaderCodeName` (shader source file name)

```cpp
// D3DShaderCompiler.inl:621-624
if (Input.Environment.CompilerFlags.Contains(CFLAG_ExtraShaderData))
{
    Output.ShaderCode.AddOptionalData(FShaderCodeName::Key, TCHAR_TO_UTF8(*Input.GenerateShaderName()));
}
```

`CFLAG_ExtraShaderData` is added only when `r.Shaders.ExtraData=1`
(see `Engine/Source/Runtime/Engine/Private/ShaderCompiler/ShaderCompiler.cpp:5830-5840`,
which calls `ShouldEnableExtraShaderData` →
`Engine/Source/Runtime/RenderCore/Private/ShaderCore.cpp:450-454`).
Default in shipping: false.

Verdict: **STRIPPED unless the project explicitly opts in.**

### `'p'` — `FShaderCodePackedResourceCounts`

Same call site as `'u'`. Unconditional.
Carries `(UsageFlags, NumSamplers, NumSRVs, NumCBs, NumUAVs)`.

Verdict: **PERSISTS, every shipping shader.**

### `'m'` — `FShaderCodeResourceMasks`

```cpp
// D3DShaderCompiler.cpp:1153-1156
auto AddOptionalDataCallback = [&](FShaderCode& ShaderCode)
{
    Output.ShaderCode.AddOptionalData(ResourceMasks);
};
```

Invoked from the unconditional block above.

Verdict: **PERSISTS, every shipping shader.**

### `'x'` — `FShaderCodeFeatures`

Emitted by the same callback path; unconditional.

Verdict: **PERSISTS.**

### `'v'` — `FShaderCodeVendorExtension`

```cpp
// D3DShaderCompiler.inl:609-619
if (VendorExtensions.Num() > 0)
{
    ...
    Output.ShaderCode.AddOptionalData(FShaderCodeVendorExtension::Key, ...);
}
```

Verdict: **PERSISTS only when the shader uses a vendor extension.**

### `'6'` — `FShaderCodeSm6Flag`

Emitted only for SM6 shaders. Always emitted in that case.

Verdict: **PERSISTS for SM6-targeted shipping shaders.**

## DXBC / DXIL reflection chunk

```cpp
// D3DShaderCompiler.cpp:1115-1140
if (Input.Environment.CompilerFlags.Contains(CFLAG_GenerateSymbols))
{
    CompressedData = Shader;
}
else if (D3DStripShaderFunc)
{
    Result = D3DStripShaderFunc(Shader->GetBufferPointer(),
        Shader->GetBufferSize(),
        D3DCOMPILER_STRIP_REFLECTION_DATA | D3DCOMPILER_STRIP_DEBUG_INFO | D3DCOMPILER_STRIP_TEST_BLOBS,
        CompressedData.GetInitReference());
    ...
}
else
{
    // D3DStripShader is not guaranteed to exist
    // e.g. the open-source DXIL shader compiler does not currently implement it
    CompressedData = Shader;
}
```

`D3DStripShaderFunc` resolves out of `d3dcompiler_47.dll`. On
Windows it is virtually always available. `CFLAG_GenerateSymbols`
is gated by `r.Shaders.GenerateSymbols` / `r.Shaders.Symbols` —
default false for shipping.

Verdict (DXBC): **REFLECTION_DATA stripped → resource binding
names like `Material_Texture2D_0` are gone from the bytecode.**

Verdict (DXIL): same code path. Same outcome unless the open-source
DXIL compiler is in use, in which case `D3DStripShaderFunc` is null
and the reflection survives. Production DXC usage in UE 5.1 still
goes through the same strip step.

## Per-shader frozen image (always survives, no names)

`FShaderParameterBindings` (`Shader.h:721-776`):

```cpp
struct FParameter      { uint16 BufferIndex, BaseIndex, ByteOffset, ByteSize; };
struct FResourceParameter { uint16 ByteOffset; uint8 BaseIndex; EUniformBufferBaseType BaseType; };
struct FBindlessResourceParameter { uint16 ByteOffset, GlobalConstantOffset; EUniformBufferBaseType BaseType; };
struct FParameterStructReference  { uint16 BufferIndex, ByteOffset; };
LAYOUT_FIELD(TMemoryImageArray<FParameter>, Parameters);
LAYOUT_FIELD(TMemoryImageArray<FResourceParameter>, ResourceParameters);
LAYOUT_FIELD(TMemoryImageArray<FBindlessResourceParameter>, BindlessResourceParameters);
LAYOUT_FIELD(TMemoryImageArray<FParameterStructReference>, GraphUniformBuffers);
LAYOUT_FIELD(TMemoryImageArray<FParameterStructReference>, ParameterReferences);
LAYOUT_FIELD_INITIALIZED(uint32, StructureLayoutHash, 0);
LAYOUT_FIELD_INITIALIZED(uint16, RootParameterBufferIndex, kInvalidBufferIndex);
```

No name field. Indices and types only.

Verdict: **PERSISTS, no names.**

`FShaderParameterMapInfo` (`Shader.h:284-312`):

```cpp
LAYOUT_FIELD(TMemoryImageArray<FShaderUniformBufferParameterInfo>, UniformBuffers);
LAYOUT_FIELD(TMemoryImageArray<FShaderResourceParameterInfo>, TextureSamplers);
LAYOUT_FIELD(TMemoryImageArray<FShaderResourceParameterInfo>, SRVs);
LAYOUT_FIELD(TMemoryImageArray<FShaderLooseParameterBufferInfo>, LooseParameterBuffers);
LAYOUT_FIELD(uint64, Hash);
// inner structs hold (BaseIndex, BufferIndex, Type) only — no names.
```

Verdict: **PERSISTS, no names.**

## SRT — `FBaseShaderResourceTable`

```cpp
// Engine/Source/Runtime/RenderCore/Public/ShaderCore.h:381-432
struct FBaseShaderResourceTable {
    uint32 ResourceTableBits;
    TArray<uint32> ShaderResourceViewMap;     // textures + SRVs (token streams)
    TArray<uint32> SamplerMap;
    TArray<uint32> UnorderedAccessViewMap;
    TArray<uint32> ResourceTableLayoutHashes; // per-UB layout hash, indexed by UBIndex
};
inline FArchive& operator<<(FArchive& Ar, FBaseShaderResourceTable& SRT) {
    Ar << SRT.ResourceTableBits;
    Ar << SRT.ShaderResourceViewMap;
    Ar << SRT.SamplerMap;
    Ar << SRT.UnorderedAccessViewMap;
    Ar << SRT.ResourceTableLayoutHashes;
    return Ar;
}
```

Always written before `'p' / 'u' / 'm'` etc. Each map is a
header-offset + token-stream layout per UB index. Each token
unpacks via `FRHIResourceTableEntry::Unpack` to
`(BindIndex, ResourceIndex, UniformBufferIndex)`.

Verdict: **PERSISTS — primary shader-side binding truth.**

## Material `.uasset` — `FUniformExpressionSet`

`FMaterialNumericParameterInfo` (`MaterialShared.h:448-465`),
`FMaterialTextureParameterInfo` (`MaterialShared.h:481-502`),
`FMaterialExternalTextureParameterInfo` (`MaterialShared.h:505-523`)
each carry a `FHashedMaterialParameterInfo` / `FScriptName` —
**named**.

`FRHIUniformBufferLayoutInitializer.Resources[]`
(`MaterialShared.h:660` plus `FRHIUniformBufferResource` in
`RHIDefinitions.h`): only `(MemberOffset, MemberType)`. **No name**.

`UE::Shader::FPreshaderData.Names`
(`MaterialShared.h:651`): named FNames referenced by preshader
opcodes.

Verdict: **PERSISTS — full source for material parameter names.**

## Engine-defined uniform buffer layouts

```cpp
// e.g. Engine/Source/Runtime/Engine/Private/SceneView.cpp:49
IMPLEMENT_STATIC_AND_SHADER_UNIFORM_BUFFER_STRUCT(FViewUniformShaderParameters, "View", View);
```

Layout members (`MaterialTextureBilinearWrapedSampler`,
`SharedBilinearClampedSampler`, `PerlinNoise3DTexture`, …) are
declared via `BEGIN_GLOBAL_SHADER_PARAMETER_STRUCT(...)` macros at
C++ compile time. `FShaderParametersMetadataRegistration` puts them
in a runtime registry that lives **inside the engine binary**.

There is no path that writes engine UB member names to the cooked
shader archive or to the package store. Recovery from cooked data
alone is impossible.

Verdict: **NEVER PERSISTS. Mirror the C++ source layout into a
static table inside the decompiler.** Already done for `View`,
`OpaqueBasePass`, `SceneTextures`, `LumenCardScene`,
`VirtualShadowMap`, `RenderVolumetricCloudParameters` in
`Unreal/EngineUniformBuffers.cs`.

## Final verdict matrix (decompiler-relevant)

| Symbol class | Persists in shipping cook? | Source of truth | Reader status |
| --- | --- | --- | --- |
| Uniform buffer name (`Material`, `View`, `OpaqueBasePass`, …) | ✅ unconditional on D3D | `'u'` optional block | done in `UnrealShaderParser` |
| `Material_Texture2D_0` (compiler resource name) | ❌ stripped from RDEF | reconstruct from Material UB layout | done — replay of `CreateBufferStruct()` ordering, gated on having the `.uasset` |
| `View_PerlinNoise3DTexture` etc. | ❌ stripped from RDEF | C++ engine source (hardcoded mirror) | done — `EngineUniformBuffers.cs` |
| Material parameter user name (`"Normal input"`) | ✅ in `.uasset` | `FUniformExpressionSet.UniformTextureParameters[Type][i].ParameterInfo.Name` | partially — names are extracted but not annotated onto resource bindings yet |
| Material numeric parameter name (`"UV Scale Near"`) | ✅ in `.uasset` | `FUniformExpressionSet.UniformNumericParameters[i].ParameterInfo.Name` | done — Material CB members in `UeShaderSymbolInputsReader` |
| Bind-point ↔ UB-resource-index map | ✅ unconditional | SRT token streams | done — `UeShaderResourceTableDecoder.cs` |
| Bind-point ↔ struct-byte-offset map | ✅ frozen | `FShaderParameterBindings.ResourceParameters[]` | currently exposed in unified metadata, not yet wired into HLSL output |
| Shader source filename (`BasePassPixelShader.usf`) | ❌ stripped | `'n'` block, gated | not relied on |
| Shader entry-point name (`MainPS`) | ❌ never serialized | nowhere recoverable | not relied on |
| Vertex factory name (`FLocalVertexFactory`) | hashed only | `FVertexFactoryTypeDependency.HashedName` (uint64 hash); name itself never serialized | could mirror the engine VF type registry to invert the hash |

## Implementation rule

If a symbol class is not in the matrix above, **do not** invent a
recovery for it. If a symbol class is in the matrix as "stripped",
the only honest fallback is a typed placeholder
(`<UB>_Texture2D_<i>` etc.) backed by the Material UB layout
replay, never a guessed compiler name.

The matrix is closed-world for the D3D shipping path. New entries
require a fresh source quote from `E:\UnrealEngine-5.1.1-release`.

## Closed-world verification (2026-04-28)

A second-pass research sweep specifically asked: is there **any**
cooked-data path beyond the matrix above that carries engine-UB
**member** names (`View_PerlinNoise3DTexture`,
`OpaqueBasePass_PreIntegratedGFTexture`, …)? Eleven candidate paths
inspected against UE 5.1.1 source:

| # | Path | Verdict |
| --- | --- | --- |
| 1 | `FShaderPipelineCache` / `.upipelinecache` (`PipelineFileCache.h:26-243`) | **NO** — stores PSO state + shader hashes, no UB layout member names |
| 2 | `'u'` `FShaderCodeUniformBuffers` (`ShaderCore.h:693-697`, `D3DShaderCompiler.inl:535-605`) | **NO** — `TArray<FString>` of UB *names* only, never member tables |
| 3 | Sibling optional blocks (`'p'`/`'m'`/`'x'`/`'n'`/`'v'`) | **NO** — none carry layout |
| 4 | `FRHIUniformBufferLayoutInitializer` serialization (`RHIResources.h:784-1030`) | **NO** — `Resources` is `(Offset, Type)` pairs only; the `Name` field is the *UB* name |
| 5 | IoStore container metadata | **NO** — payload chunks, no shader reflection |
| 6 | `AssetRegistry.bin` (`AssetRegistryArchive.h:18-82`) | **NO** — package/object metadata only |
| 7 | `FShaderFactory::LoadShader` runtime path (`Shader.cpp`) | **NO** — uses C++-static `FRHIUniformBufferLayout` pointers registered at engine boot, never disk-loaded |
| 8 | `r.Shaders.IncludeSource` `.usf` preservation | **NO** — does not exist in UE 5.1 cook output |
| 9 | `'n'` `FShaderCodeName` + `CFLAG_ExtraShaderData` | **NO** — only shader source filename; gated; default off |
| 10 | `FMaterialShaderMap.MemoryImageResult.ScriptNames` | **NO** for engine UB members — ScriptNames patches land on material parameter identity FNames only |
| 11 | Oni Valley project-side custom serialization | **NO** — standard UE 5.1 demo, no extra reflection assets |

**Final, project-binding answer: engine-UB member names are NOT
recoverable from a default shipping cook on the D3D path.** The
matrix above is closed-world for this engine version + cook target.

Three theoretical paths were noted as out-of-scope:
- Statically reflect the shipped game `.exe`'s `.data` section to
  find `FShaderParametersMetadata` C++ singletons. Per-game-specific
  rather than per-engine-version-specific, but expensive (PE parsing
  + symbol layout matching across optimisation levels) and fragile.
- Have the project ship a lookup table at cook time (non-standard).
- Hard-code an engine-version-specific mapping. **Banned by project
  rule** (see `CURRENT_LIMITATIONS.md`).
