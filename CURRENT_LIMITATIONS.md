# Current Limitations — UE Shader Symbol Recovery

Authoritative list of what this decompiler can and cannot recover for
a shipping UE5 cook on the D3D path. Keep in sync with
`UE_SHIPPING_NAME_TRUTH.md` (source-truth verdict matrix) and
`Targets/<game>.md` (per-game iteration log).

**Project rule** (set by the project owner): do **not** hard-code
symbol names from C++ engine source. UE versions diverge between
patches; custom-engine forks diverge harder. Hard-coded layout tables
silently rot and outright fabricate names for any game running an
engine that has moved members around. Every name must trace back to a
byte that exists in the cooked game files we were handed.

## What we can recover from cooked data

- **Uniform buffer names** (`View`, `Material`, `TranslucentBasePass`,
  `OpaqueBasePass`, `LumenCardScene`, `VirtualShadowMap`, …).
  Source: `'u'` optional-data block
  (`FShaderCodeUniformBuffers`) appended to every shader's tail by
  `D3DShaderCompiler.inl:603-607`. Unconditional on D3D — present in
  every shipped shader on the D3D target.
- **CB binding slots** (`b0`, `b1`, …).
  Source: same `'u'` block — index in the array is the bind slot.
- **SRT bind-index ↔ UB-resource-index mapping**.
  Source: `FBaseShaderResourceTable` (`ShaderResourceViewMap`,
  `SamplerMap`, `UnorderedAccessViewMap`) per shader, decoded by
  `FRHIResourceTableEntry` packing. Always present.
- **Per-UB resource counts** (how many resources a UB has, e.g.
  Material has N textures + N samplers + …).
  Source: `FUniformExpressionSet.UniformBufferLayoutInitializer.Resources[*]`
  for the Material UB; per-UB SRT entry counts for engine UBs.
- **Material UB texture / sampler typed flat names** (`Material_Texture2D_0`,
  `Material_Texture2D_0Sampler`, `Material_TextureCube_0`,
  `Material_VirtualTexturePhysical_0`, …).
  Source: synthesized from `FUniformExpressionSet.UniformTextureParameters[Type].Length`
  (cooked counts) using the **regular** typed-counter pattern UE always
  emits (Standard2D → Cube → Array2D → ArrayCube → Volume → External →
  Virtual). The pattern itself is not version-specific — it is a
  universal naming convention shared across UE versions, expressed as
  a fixed-rule replay of `CreateBufferStruct()`.
- **User-facing material parameter names** (`"UV Scale Near"`,
  `"Normal input"`, `"Specular detail"`, …).
  Source: `FUniformExpressionSet.UniformNumericParameters[i].ParameterInfo.Name`
  / `UniformTextureParameters[Type][i].ParameterInfo.Name` /
  `UniformExternalTextureParameters[i].ParameterName` in the cooked
  material `.uasset`.
- **Material constant-buffer numeric members** at proven byte offsets
  (the preshader-bridge slice).
  Source: the `Parameter` opcode in `UniformPreshaderData` referenced
  by `UniformPreshaders[*]` writing into `UniformPreshaderFields[*].BufferOffset`.

## What we cannot recover (closed-world ceiling)

- **Engine UB member names** — `View_PerlinNoise3DTexture`,
  `OpaqueBasePass_PreIntegratedGFTexture`,
  `LumenCardScene_AlbedoAtlas`, etc.
  These live only in C++ engine source as
  `BEGIN_GLOBAL_SHADER_PARAMETER_STRUCT` macro expansions baked into
  the shipping engine binary. They are not serialized to any cooked
  file the runtime then loads. The DXBC RDEF chunk that would have
  carried them at the t/s/u register layer is stripped via
  `D3DStripShader(D3DCOMPILER_STRIP_REFLECTION_DATA)` for every shader
  whose `CFLAG_GenerateSymbols` flag is unset (the shipping default).
  The pipeline emits a UB-context placeholder instead
  (`<UB>_SRV<i>` / `<UB>_Sampler<i>` / `<UB>_UAV<i>`), so the human
  reader still knows which UB owns the slot and at what UB-internal
  resource index. Naming the actual member would require either:
  - a different cooked-data path that we have not yet found
    (active-research; if you do find one, **fix this doc**); or
  - hard-coding from engine source — explicitly banned by project rule.
- **Loose `FShaderParameter*` resources** — slots bound through a
  per-shader-class `BEGIN_SHADER_PARAMETER_STRUCT` rather than a UB.
  Survive in the frozen image as
  `FShaderParameterBindings.ResourceParameters[*]` with
  `(ByteOffset, BaseIndex, BaseType)` triples — **no name**. CUE4Parse
  also currently reports those arrays as empty for material shaders
  on the SM5 / SM6 platform (typeHash `0x1` / `0x3` stub) — pending
  investigation, but even when fully populated the on-disk struct
  carries indices and types only.
  HLSL fallback: keep spirv-cross's `T<n>` / `_RegisterSpaceN[…]` so
  the slot is at least visible. Do not invent.
- **Compiler resource names** like `Material_Texture2D_0` written by
  `RemoveUniformBuffersFromSource` and stored in
  `FShaderParameterMap.ParameterMap`. The map is not serialized to the
  cooked archive (only used during compile + then discarded), and the
  RDEF chunk that mirrored it is stripped (above).
- **Shader source filename** (`BasePassPixelShader.usf`) via
  `'n'` optional block / `FShaderCodeName`. Gated on
  `CFLAG_ExtraShaderData` (= `r.Shaders.ExtraData=1`). Off by default
  in shipping. Oni Valley confirmed off (no `'n'` block in any of the
  exported `.ushaderbytecode` files).
- **Shader entry-point name** (`MainPS`). Never serialized.
- **Vertex-factory class name** (`FLocalVertexFactory`). Stored as a
  64-bit `FHashedName` in `FVertexFactoryTypeDependency` — only the
  hash, not the name.
- **Sampler names**. The cooked `FBaseShaderResourceTable.SamplerMap`
  carries bind index + UB-resource index but no name string — and
  `ShaderSymbolData.Samplers` holds no name field anyway, so the
  pipeline emits `sampler_<bindIndex>` everywhere.

## Anti-patterns (banned)

- Hard-coding engine UB member tables from
  `Engine/Source/Runtime/Engine/Public/SceneView.h` (or any other
  engine header) and shipping that table inside the decompiler.
- Inferring a member name from its register class + index alone (e.g.
  "the 3rd texture in `View` is always `PerlinNoiseGradientTexture`")
  — this is the same hard-coding wearing a different hat.
- Producing a typed name like `Material_Texture2D_3` for a slot whose
  UB index in the SRT does not actually point to the Material UB.
- Marking a placeholder name as if it were authoritative. Placeholders
  always carry `_SRV` / `_Sampler` / `_UAV` / `_Resource` infixes so
  it is unambiguous from the HLSL output that the recovery is
  incomplete.

## How to extend (the only sanctioned path)

1. Find a cooked-data byte that carries the name you want to recover.
   Quote the UE source line that writes it, the line that reads it,
   and the cvar/flag (if any) that gates it.
2. Add an entry to `UE_SHIPPING_NAME_TRUTH.md`'s verdict matrix with
   that quote.
3. Implement a reader in the offline decompiler that pulls the name
   from that byte.
4. If the byte is conditional (gated on a cvar that some games have
   off), make the reader degrade to a placeholder rather than fail.
5. If you cannot find such a byte, the gap stays a gap. Document it
   here. Don't hard-code.

## Active research items

- Whether any cooked-data path beyond the current verdict matrix
  carries engine-UB member names (e.g. pipeline cache,
  IoStore container metadata, `r.Shaders.IncludeSource=1` `.usf`
  source preserved in the cook). Investigation in progress; results
  will be appended to `UE_SHIPPING_NAME_TRUTH.md`.
- Whether CUE4Parse fully deserializes
  `FShaderParameterBindings.ResourceParameters` /
  `FShaderParameterMapInfo.{TextureSamplers,SRVs}`, or whether the
  empty arrays observed on shipped material shaders represent a real
  gap in the cooked data vs. a parser gap.
