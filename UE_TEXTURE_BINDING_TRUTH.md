# UE5.1 Shipping Cook — Texture / Sampler Binding Name Recovery

Authoritative map of where texture/sampler bindings (`Texture2D T<n> : register(t<n>)`,
`SamplerState S<n> : register(s<n>)`) come from in the cooked archive,
which ones are name-recoverable, and which hit the closed-world ceiling.
All claims below are direct UE 5.1.1 source quotes from
`D:\GameStudy\UnrealEngine-5.1.1-release`, with file:line refs.

This is a sibling to [`UE_SHIPPING_NAME_TRUTH.md`](UE_SHIPPING_NAME_TRUTH.md)
(which covers cbuffer / uniform-buffer member name recovery). Same project
rules apply: any name we print must be reproducible byte-for-byte from
cooked-archive bytes via the documented UE 5.1 semantics. **Hardcoded
mirrors of engine C++ (e.g. View / OpaqueBasePass member tables) are
banned** — they silently rot across UE versions and modded engines.

## TL;DR

Three binding mechanisms produce `register(t<n>)` / `register(s<n>)` /
`register(u<n>)` slots in cooked HLSL. Only the first is name-recoverable.

| Mechanism | Source of truth | Names? |
| --- | --- | --- |
| **SRT-bound via Material UB** (`'u'` block lists `Material`, SRT token has `ub == MaterialIndex`) | Material `.uasset`'s `UniformExpressionSet` + replay of `FUniformExpressionSet::CreateBufferStruct()` ordering | ✅ yes — full typed names (`Material_Texture2D_0`, `Material_Texture2D_0Sampler`, …) |
| **SRT-bound via engine UB** (`View`, `OpaqueBasePass`, `IndirectLightingCache`, …) | C++ `BEGIN_SHADER_PARAMETER_STRUCT(...)` macros, baked into engine binary | ❌ no — project rule forbids hardcoded engine source mirror; placeholder `<UBName>_SRV<resourceIndex>` |
| **Loose, via `FShaderParameterBindings.ResourceParameters[]`** (per-shader-class C++ struct field) | C++ `BEGIN_SHADER_PARAMETER_STRUCT_*` macros for each shader class, baked into engine binary | ❌ no — frozen image keeps only `(ByteOffset, BaseIndex, BaseType)`; the `FShaderParameterMap` that *had* names is dropped during cook (see §5.5 of CLAUDE.md); placeholder `T<n>` (spirv-cross default) |

In practice, on a typical `Material` pixel shader in a UE 5.1 shipping
cook on D3D, **most texture slots are loose** (Mechanism 3). Only a
handful of slots come through the SRT (Mechanism 1 or 2). Mechanism 1
(Material-via-SRT) does happen — that's why the layout replay below
matters — but on this Oni_Valley_VFX cook every Material PS we
sampled has 0 SRT-bound Material textures and 1 SRT-bound View SRV.

**The recoverability ceiling for a typical cooked Material PS texture
list is therefore not hardcodeable** without violating the project rule.
The honest output is: name what we can prove, leave the rest as
spirv-cross's `T<n>`.

## How the SRT walks the per-UB token streams

`FBaseShaderResourceTable` (`Engine/Source/Runtime/RenderCore/Public/ShaderCore.h:381-432`)
holds four packed uint32 maps:

```cpp
struct FBaseShaderResourceTable {
    uint32 ResourceTableBits;
    TArray<uint32> ShaderResourceViewMap;     // textures + SRVs (token streams)
    TArray<uint32> SamplerMap;
    TArray<uint32> UnorderedAccessViewMap;
    TArray<uint32> ResourceTableLayoutHashes;
};
```

Each map is laid out as:
- `Map[0..NumUniformBuffers-1]` — header: per-UB offsets into `Map`,
  `0` means the UB has no resources of this kind in this shader
- `Map[Map[ub]..]` — token stream for that UB, walked while
  `FRHIResourceTableEntry::GetUniformBufferIndex(token) == ub`

Each token packs `(BindIndex, ResourceIndex, UniformBufferIndex)` per
`Engine/Source/Runtime/RHI/Public/RHIDefinitions.h:1497-1542`:
- `BindIndex   = token & 0xFF`           — shader register (`t<bindIndex>` / `s<bindIndex>` / …)
- `ResourceIndex = (token >> 8) & 0xFFFF` — index into the UB's `Resources[]` list
- `UniformBufferIndex = (token >> 24) & 0xFF` — slot of the bound UB (matches `'u'` block index)
- `0xFFFFFFFF` is a stream terminator (decodes to `ub=255` which doesn't match any real UB).

The walk is identical to the runtime code in `D3D12Commands.cpp:1514-1602`
(`SetShaderResourcesFromBuffer_*`):

```cpp
const uint32 BufferOffset = ResourceMap[BufferIndex];
if (BufferOffset > 0)
{
    const uint32* RESTRICT ResourceInfos = &ResourceMap[BufferOffset];
    uint32 ResourceInfo = *ResourceInfos++;
    do {
        const uint16 ResourceIndex = FRHIResourceTableEntry::GetResourceIndex(ResourceInfo);
        const uint8  BindIndex     = FRHIResourceTableEntry::GetBindIndex(ResourceInfo);
        // ... bind Buffer->ResourceTable[ResourceIndex] at register BindIndex ...
        ResourceInfo = *ResourceInfos++;
    } while (FRHIResourceTableEntry::GetUniformBufferIndex(ResourceInfo) == BufferIndex);
}
```

Decoder mirror: `Source/Ruri.ShaderDecompiler/Unreal/UeShaderResourceTableDecoder.cs`.

Important: `ResourceIndex` indexes into `Buffer->ResourceTable[]`, which
in cooked data is `FRHIUniformBufferLayoutInitializer.Resources[]` —
**only resource-typed members** (`UBMT_TEXTURE` / `UBMT_SRV` /
`UBMT_SAMPLER` / `UBMT_UAV`) get an entry. Numeric leading members
(`UBMT_UINT32` / `UBMT_FLOAT32`) are skipped. `CreateBufferStruct()`
ordering preserves this — see below.

## Material UB resource layout (recoverable, Mechanism 1)

`FUniformExpressionSet::CreateBufferStruct()` in
`Engine/Source/Runtime/Engine/Private/Materials/MaterialUniformExpressions.cpp:341-503`
emits the Material UB's resource members in a fixed deterministic
order. Replaying that order gives us the exact name for every
ResourceIndex we see in an SRT token with `ub == MaterialUBIndex`.

Order (every block is conditional on its count > 0; absent blocks emit
zero entries; numeric leading members skipped because they don't go
into `Resources[]`):

```
for each Standard2D[i]    : Texture2D_<i>                    (UBMT_TEXTURE)
                            Texture2D_<i>Sampler             (UBMT_SAMPLER)
for each Cube[i]          : TextureCube_<i>                  (TEXTURE)
                            TextureCube_<i>Sampler           (SAMPLER)
for each Array2D[i]       : Texture2DArray_<i>               (TEXTURE)
                            Texture2DArray_<i>Sampler        (SAMPLER)
for each ArrayCube[i]     : TextureCubeArray_<i>             (TEXTURE)
                            TextureCubeArray_<i>Sampler      (SAMPLER)
for each Volume[i]        : VolumeTexture_<i>                (TEXTURE)
                            VolumeTexture_<i>Sampler         (SAMPLER)
for each External[i]      : ExternalTexture_<i>              (TEXTURE)
                            ExternalTexture_<i>Sampler       (SAMPLER)  // UE: MediaTextureSamplerNames printf "ExternalTexture_%dSampler"
for each VTStack[i]       : VirtualTexturePageTable0_<i>     (TEXTURE)
                            VirtualTexturePageTable1_<i>     (TEXTURE)  // only when Stack.NumLayers > 4
                            VirtualTexturePageTableIndirection_<i> (TEXTURE)
for each Virtual[i]       : VirtualTexturePhysical_<i>       (UBMT_SRV !)  // not TEXTURE; supports sRGB/non-sRGB aliasing
                            VirtualTexturePhysical_<i>Sampler(SAMPLER)
Wrap_WorldGroupSettings                                       (SAMPLER, unconditional)
Clamp_WorldGroupSettings                                      (SAMPLER, unconditional)
```

**Sources of the per-block counts** (all in the material `.uasset`'s
`UniformExpressionSet`):
- `UniformTextureParameters[Standard2D|Cube|Array2D|ArrayCube|Volume|Virtual]`
  — each is an array of `FMaterialTextureParameterInfo`; count is the
  array length per type.
- `UniformExternalTextureParameters` — array of
  `FMaterialExternalTextureParameterInfo`.
- `VTStacks` — array of `FMaterialVirtualTextureStack`. Each stack has
  `LayerUniformExpressionIndices` (8-element fixed array, INDEX_NONE for
  unused). `NumLayers` = count of non-INDEX_NONE entries; gates whether
  `VirtualTexturePageTable1_<i>` is emitted.

Implementation: `Source/Ruri.ShaderDecompiler/Unreal/MaterialUniformBufferLayout.cs`
(replays `CreateBufferStruct()` with the counts above) and
`Source/Ruri.ShaderDecompiler/Unreal/UeShaderSymbolInputsReader.cs::ReadMaterialResourceCounts`
(reads counts from `UniformExpressionSet`). Both are plumbed into
`UeShaderResourceTableSymbolizer.ResolveResourceName` through
`UeMaterialUniformBufferLayout.ResolveResourceName(record)` which
returns `Material_<MemberName>` for an SRT record with `ub == MaterialIndex`.

### Cross-check with `Resources[]` length

The replay's output length **must equal**
`UniformBufferLayoutInitializer.Resources.Num()` from the cooked
material — that's the runtime layout the engine itself uses.
Mismatches are the canary for layout-reader bugs and should be a hard
error. (`M_Bamboo_tree.json`: 2 Standard2D + Wrap + Clamp = 6, matches
`Resources[].length=6`.)

## Engine UB resources (NOT recoverable, Mechanism 2)

Engine-defined uniform buffers (`View`, `OpaqueBasePass`,
`IndirectLightingCache`, `InstanceCulling`, `LocalVF`, `LumenCardScene`,
`VirtualShadowMap`, `RenderVolumetricCloudParameters`, …) declare their
member layout via
`BEGIN_GLOBAL_SHADER_PARAMETER_STRUCT(...)` /
`BEGIN_SHADER_PARAMETER_STRUCT_*` macros at C++ compile time. Example:

```cpp
// Engine/Source/Runtime/Engine/Private/SceneView.cpp:49
IMPLEMENT_STATIC_AND_SHADER_UNIFORM_BUFFER_STRUCT(FViewUniformShaderParameters, "View", View);
```

`FShaderParametersMetadataRegistration` puts these in a runtime
registry that lives **inside the engine binary** (`UnrealEditor.exe` /
`<Project>-Win64-Shipping.exe`). The cooked archive does **not**
contain the member layout — only the UB **name** survives via the
`'u'` optional block (per `UE_SHIPPING_NAME_TRUTH.md`).

Closed-world verdict (already established 11-path inspection in
`UE_SHIPPING_NAME_TRUTH.md` "Closed-world verification"): there is
**no path** in default D3D shipping cook that carries engine-UB member
names. Pipeline cache, optional blocks, asset registry, IoStore
metadata, runtime shader factory — none of them.

**Decompiler output**: typed placeholder
`<UBName>_<RegisterClass><ResourceIndex>`
(`View_SRV45` / `View_Sampler39` / `OpaqueBasePass_SRV12` etc.). The
ResourceIndex is the *cooked* index into the engine UB's
`Resources[]`, so the binding can still be cross-checked against the
target UE binary's `FShaderParametersMetadata` registry by anyone with
matching engine source.

## Loose parameters (NOT recoverable, Mechanism 3)

Per-shader-class `BEGIN_SHADER_PARAMETER_STRUCT(...)` declarations
generate a CPU-side memory layout. At cook time:

- `FShaderParameterMap` carries `(Name → BaseIndex, BufferIndex, …)`
  for every parameter the compiler reflection picked up. **Has names.**
- `FShader::BuildParameterMapInfo()` translates that into
  `FShaderParameterMapInfo.{TextureSamplers,SRVs,LooseParameterBuffers}`,
  keeping `(BaseIndex, BufferIndex, Type)` only. **Names dropped.**
- `FShaderParameterBindings::BindForLegacyShaderParameters()` populates
  `Bindings.ResourceParameters[]` with `(ByteOffset, BaseIndex, BaseType)`.
  ByteOffset indexes into the shader's *own* parameter struct (not
  into any UB). **Names dropped.**

The cooked archive contains only the latter two — the named
`FShaderParameterMap` is not serialized.

The C++ parameter struct definitions (the only place names live) are
in engine source headers, baked into the engine binary. This is the
same constraint as Mechanism 2, applied per shader class instead of
per global UB. Same project rule applies — no hardcoded mirror.

**Decompiler output**: spirv-cross's default `T<n>` for textures, no
named replacement.

### What about `MemoryImageResult.ScriptNames` patches?

`FMaterialShaderMap` serializes a `MemoryImageResult` whose
`ScriptNames` carry FName patches. Per CLAUDE.md §5.6 those
patches land on **material parameter identity FNames** (the `Name`
field of `FMaterialNumericParameterInfo` /
`FMaterialTextureParameterInfo`, SavedLayoutSize=28), not on shader
resource binding identifiers. They are not a name source for
loose-bound textures.

### What about RenderDoc captures?

A RenderDoc capture of a running game records DXBC blobs in their
**uncompressed, runtime-validated** form, which usually still has the
RDEF chunk if the runtime didn't strip it. That would yield names like
`Material_Texture2D_0` / `View_PerlinNoise3DTexture` directly. But:
- This is **per-capture** rather than per-engine-version, so it
  doesn't generalize.
- It requires the user to have an RDC and to feed it in as a separate
  source — it's not implied by the cooked archive.
- It's outside the "names recoverable from cooked data" promise of this
  decompiler.

Out-of-scope for the offline pipeline. Could be a separate
`--rdc-sidecar` mode if a user provides one.

## What this means for `M_Bamboo_tree_PS_1904`

```hlsl
Texture3D<uint4>  T0           : register(t0);   // loose param      -> closed world (no name)
Texture3D<float4> T1           : register(t1);   // loose param      -> closed world
Buffer<uint4>     View_SRV45   : register(t2);   // SRT, View UB     -> Mechanism 2 placeholder (correct)
Texture2D<float4> T3           : register(t3);   // loose param      -> closed world
Texture2D<float4> T4           : register(t4);   // loose param      -> closed world
Texture2D<float4> T5           : register(t5);   // loose param      -> closed world
Texture3D<float4> T6           : register(t6);   // loose param      -> closed world
Texture3D<float4> T7           : register(t7);   // loose param      -> closed world
Texture3D<float4> T8           : register(t8);   // loose param      -> closed world
Texture2D<float4> T9           : register(t9);   // loose param      -> closed world
Texture2D<float4> T10          : register(t10);  // loose param      -> closed world

SamplerState sampler_0         : register(s0);   // loose            -> closed world
SamplerState sampler_1         : register(s1);   // SRT, View[39]    -> Mechanism 2 placeholder
SamplerState sampler_2         : register(s2);   // SRT, OpaqueBasePass[43] -> Mechanism 2 placeholder
SamplerState sampler_3         : register(s3);   // SRT, IndirectLightingCache[3] -> Mechanism 2 placeholder
SamplerState sampler_4         : register(s4);   // SRT, Material[3] = Texture2D_1Sampler (Bamboo base maps) -> Mechanism 1 RECOVERABLE
```

— so out of 16 binding slots in this shader, exactly **1** has a
canonical recoverable name through Mechanism 1 (`sampler_4` →
`Material_Texture2D_1Sampler`, the sampler for the "Bamboo base maps"
material parameter).

This is representative of typical UE 5.1 D3D shipping cooks: the SRT
covers a small minority of slots, and engine-UB / loose dominate.

## What we currently print vs what's possible

| Slot class | Current output | Possible (per closed-world) | Gap |
| --- | --- | --- | --- |
| Material UB SRT-bound texture/SRV | `Material_<TypedName>` (after this fix) | `Material_<TypedName>` | none |
| Material UB SRT-bound sampler | `sampler_<bindIndex>` (hardcoded by `ShaderSymbolData.EnumerateResourceBindings`) | `Material_<TypedName>Sampler` | sampler name resolution is intentionally dropped — fixable |
| Engine UB SRT-bound texture/SRV | `<UBName>_SRV<resourceIndex>` | same | none (project rule ceiling) |
| Engine UB SRT-bound sampler | `sampler_<bindIndex>` | `<UBName>_Sampler<resourceIndex>` | same — sampler naming dropped |
| Loose texture/SRV | spirv-cross `T<n>` | same | none (closed-world ceiling) |
| Loose sampler | spirv-cross `S<n>` / `sampler_<n>` | same | none |

The only honest improvement remaining beyond Mechanism 1 typed names is
**propagating sampler names through `SamplerParameter`** — currently
`ShaderSymbolData.EnumerateResourceBindings` rebuilds samplers as
`sampler_<index>` regardless of any name we resolved upstream.
That's a deliberate design choice and can be revisited if the user
wants `Material_Texture2D_0Sampler` to land in HLSL.

## User-facing texture parameter names

`FMaterialTextureParameterInfo.ParameterInfo.Name` (per typed array of
`UniformTextureParameters[Type]`) carries the **author-facing name** of
each material texture (e.g. `"Normal"`, `"Bamboo base maps"`). These
correlate by index to the typed positions in `CreateBufferStruct()`:

- `UniformTextureParameters[Standard2D][i].ParameterInfo.Name` ↔
  `Material_Texture2D_<i>`'s author-facing name.

These can be emitted as **annotation comments** above the binding,
without overriding the canonical `Texture2D_<i>` name (which is what
the HLSL source actually used after `RemoveUniformBuffersFromSource`).
Not currently wired; would be a small additive change in
`UeShaderResourceTableSymbolizer` if the user wants it.

## Summary rule

If a slot's source is one of:
- **SRT token with `ub == MaterialIndex`** — name it via the
  `CreateBufferStruct()` replay. Source-truth.
- **SRT token with `ub == EngineUBIndex`** — print typed placeholder
  `<UBName>_<RegisterClass><ResourceIndex>`. Closed-world ceiling.
- **Anything else** — leave spirv-cross's default. Closed-world ceiling.

No exceptions. No hardcoded engine source mirrors.
