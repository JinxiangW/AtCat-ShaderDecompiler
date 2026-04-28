# Ruri.ShaderDecompiler — 当前任务、问题、目标

> 单一事实来源。`Source/Ruri.ShaderDecompiler/` 下的 `README.md` /
> `CURRENT_LIMITATIONS.md` / `SHADER_MAPPING_RESEARCH.md` /
> `UE_SHIPPING_NAME_TRUTH.md` / `Targets/Oni_Valley_VFX.md` 已合并至本
> 文件。后续修改只更新这里。
>
> **SPIR-V 阶段调试与修复方法论 → [SPIRV_DEBUG_PLAYBOOK.md](SPIRV_DEBUG_PLAYBOOK.md)**。
> 任何"反编译失败"类 bug 进场前先把那份 playbook 过一遍,里面有完整的
> "看 stderr → spirv-dis → 比对 → 定位 → 修复 → 回归"工作流、SPIR-V 操
> 作数布局速查、bug archetype 速查表、和已踩过的所有坑清单。
>
> **UE 贴图/采样器 binding 名字还原 → [UE_TEXTURE_BINDING_TRUTH.md](UE_TEXTURE_BINDING_TRUTH.md)**。
> SRT token 流的语义、`FUniformExpressionSet::CreateBufferStruct()` 重放、
> 引擎 UB / loose param 的 closed-world 上限,全在那。

---

## 0. 项目路径(本机)

| Item | Path |
| --- | --- |
| 仓库根 | `D:\Ruri\Github\FractalTools\Ruri-RipperHook` |
| 反编译器源码 | `Source\Ruri.ShaderDecompiler\` |
| FModel 钩子源码 | `Source\Ruri.FModelHook\` |
| AssetRipper 钩子(Unity 端,参考) | `Source\Ruri.RipperHook\` |
| FModel 二进制 + 导出根 | `FModel\FModel\bin\Debug\net8.0-windows\win-x64\Output\Exports` |
| Oni Valley 导出 | `…\Output\Exports\Oni_Valley_VFX\` |
| 反编译器二进制 | `Source\Ruri.ShaderDecompiler\bin\Debug\Ruri.ShaderDecompiler.exe` |
| FModel 自动导出钩子 | `Source\Ruri.FModelHook\Game\SBUE\AutoExport\UE_ShaderDecompiler_AutoExport_Hook.cs` |
| 游戏目录 | `D:\GameStudy\OniValleyDemo` |
| UE 5.1 源码 | `D:\GameStudy\UnrealEngine-5.1.1-release` |

---

## 1. 当前最优先问题(Active Bug)

### 1.1 现象 — Material CB 被压成单个匿名 float4 数组

**样例**:
- 材质: `Output\Exports\Oni_Valley_VFX\Content\Oni_Project\Materials\M_Bamboo_tree.json`
- 反编译产物 HLSL: `Decompiled\Oni_Valley_VFX_SM5\M_Bamboo_tree_PS_1904.hlsl`
- 反编译产物 metadata: `Decompiled\Oni_Valley_VFX_SM5\M_Bamboo_tree_PS_1904.metadata.json`

实际输出的 HLSL CB 是这个样子,显然是错的:

```hlsl
cbuffer View : register(b0)              { float4 View_1_m0[236] : packoffset(c0); };
cbuffer LocalVF : register(b1)           { float4 LocalVF_1_m0[1] : packoffset(c0); };
cbuffer MaterialCollection0 : register(b2) { float4 MaterialCollection0_1_m0[6] : packoffset(c0); };
cbuffer Material : register(b3)          { float4 Material_1_Tree_sway_softness[16] : packoffset(c0); };
```

但同一 shader 的 `metadata.json` 已经携带了 12 个**正确命名**、**正确字节
偏移** 的 Material 成员:

```
SelectionColor             @ 0    float4
10.Normal intensity        @ 16   float
Bamboo base dark tones     @ 48   float4
Bamboo base mid tones      @ 64   float4
Bamboo base light tones    @ 112  float4
Bamboo dark tones          @ 144  float4
Bamboo mid tones           @ 160  float4
Bamboo light tones         @ 208  float4
Roughness dullness         @ 236  float
Sway resistance            @ 244  float
Tree sway offset           @ 252  float
Tree sway softness         @ 256  float
Size = 320 (= 20 * 16)
```

但 HLSL 里 cbuffer 成员被合并成了 `float4 Material_1_Tree_sway_softness[16]`
—— **取了最后一个 patched 成员的名字** 当做整个数组名,没有按 byte
offset 拆成独立成员。`View` / `LocalVF` / `MaterialCollection0` 同样问题
(后三个 metadata 缺少 layout,所以保留 array 形态可接受;但 `Material`
有完整 layout 也照样压成了一个数组,这是核心 bug)。

### 1.2 GPU 16-byte 单位 — SPV 容忍空洞和动态索引

> **更正以前的错误判断**: SPV 的 cbuffer 是**可以容忍空洞和动态索引的**,
> 不需要 padding,不需要把成员铺满。问题不在"有没有空洞",而在"绑定本
> 身是否精确正确"。**绑定一旦填错,无论如何也修不好;绑定填对了,空洞
> 自然 OK**。

正面证据(EndField fixture):

`Testing/Assets/Shaders/Output/EndField/litpoly.shader.sub0.pass0.blob2.HGBuffer/unitybinary.hlsl`:

```hlsl
cbuffer UnityPerMaterial : register(b1)
{
    float UnityPerMaterial_1_NormalScale       : packoffset(c0);     // c0.x
    float UnityPerMaterial_1_RoughnessMin      : packoffset(c0.z);   // c0.y 是空洞
    float UnityPerMaterial_1_RoughnessMax      : packoffset(c0.w);
    float UnityPerMaterial_1_OcclusionStrength : packoffset(c1);     // c2..c7 是空洞
    float4 UnityPerMaterial_1_BaseColor        : packoffset(c8);
};
```

`Testing/Assets/Shaders/Output/EndField/litpoly.shader.sub0.pass0.blob1.HGBuffer/unitybinary.hlsl`:

```hlsl
cbuffer UnityInstancing_SRP_UnityPerDraw : register(b1)
{
    UnityPerDrawArray UnityInstancing_SRP_UnityPerDraw_1_UnityPerDrawArray[256u] : packoffset(c0);
};
```

— 反编译前是 `cb[tmp + 5]` 这种**动态索引**访问,只要符号回填正确,
spirv-cross 直出整齐数组,不需要 padding 也不需要展开。

结论 —— 也是项目当前最关键的判据:

- 空洞: SPV 直接接受,packoffset 跳过即可。
- 动态索引: SPV 直接接受,只要被索引的成员是正确大小/类型的数组即可。
- 真正会让反编译失败的,是**绑定本身的精度**:Set/Binding/字节偏移/
  Rows/Columns/IsMatrix/ArraySize 任何一项与 SPIR-V 实际访问形态不一致,
  整个 CB 就拆不出来。
- 因此调试方向必须先是 **"回填的符号是否真被 shader 引用,引用形态是
  否一致"**,而不是去补空洞或加 padding。

### 1.3 实际可疑点 — M_Bamboo_tree Material CB 元数据精度

`StructuredCBufferRewriter.cs` 在 M_Bamboo_tree 上的失败摘要(rewrite.txt):

```
[Material] rewrite validation failed: unsupported access translation for
  resultId=273 slotConst=2 slotDynamic=0 stride=0 extra=[] op=65
  words=[393281,136,273,46,96,93]
```

`slotConst=2` 表示 shader 在常量索引 2(byte 32, c2)读取 Material CB。
但当前 metadata 在 byte 32 没有任何成员:

```
SelectionColor       @ 0   float4    (覆盖 c0)
10.Normal intensity  @ 16  float     (单 float,只覆盖 c1.x)
Bamboo base dark tones @ 48 float4   (从 c3 开始)
```

按 §1.2 的判据,这不是"空洞 → rewriter 应该容忍",而是 **"shader 实
际访问 c2,但 metadata 里 byte 32 没有任何已证实的成员"** —— 候选解释
按可能性排序:

1. **`10.Normal intensity` 实际是 float4**(占 c1 一整个),只是 UE 端
   reader 把它填成了 scalar(typed wrong)。"`10.`"前缀也是奇怪信号,
   像是导出阶段把 ParameterIndex 串到名字里。
2. **byte 32 处真有一个未导出的成员**(可能是 padding 槽,或某个名字被
   reader 漏掉)。这种情况要靠**反向引用核对**确认: 在 shader 里搜
   `Material[2]` 全部访问形态,反推这个槽位是几路、什么类型。
3. shader 读取了未初始化/常量 0 区域(罕见,但 UE preshader buffer 末段
   有这种行为)。

无论哪一种,**核心动作**都是:

> 在拿 metadata 推 rewriter 之前,先按 §2.1 的反向核对流程,逐成员验
> 证它在 SPIR-V 里到底被怎么访问。引用形态与回填类型不符的成员,要么修
> reader,要么标 anonymous 留空,而**不是**让 rewriter 去吃错误数据。

### 1.4 元数据多视图非冗余 — 旧判断更正

之前曾把 `VectorParams` / `CBParams` / `AllNumericParams` 同时出现一个
名字判定为"重复填充"。这是误读,**不要按这个去删**:

- `Type` 字段是**标量类型**(`Float`/`Int`/`UInt`/`Bool`),不是形状。
- 形状由 `RowCount` × `ColumnCount` × `IsMatrix` 决定。
- `SelectionColor`(`Float`+RowCount=4+IsMatrix=false)= `float4` 向量。
  按 schema:
  - `VectorParams`: 向量 / 标量(非矩阵)
  - `MatrixParams`: 矩阵
  - `CBParams`: 三类合并的扁平视图,按 `ParamName` + `ByteOffset` 列出
  - `AllNumericParams`: 同样的合并视图,字段命名与 numeric param 表一致
- EndField fixture 也同时有这四组,且工作正常 — 这是**多视图**,不是冗余。

真要去重,目标只能是 **rewriter 内部不要把同一字节偏移的成员重复加进
struct layout**(目前是从 `CBParams` 单源读取,没问题)。reader 端不
要动。

---

## 2. 当前任务目标

### 2.1 短期 — 反向核对 metadata 精度,再动 rewriter

排序为**先核对 metadata 精度,再修 rewriter**:

1. 选一个回归 fixture(EndField blob1/blob2 已知大体工作),先**反向核
   对 metadata**: 把所有 SPIR-V 中对该 CB 变量的 OpAccessChain 列出,
   每条 access 还原出实际访问的 (ByteOffset, ComponentMask, Stride),
   再与 metadata `(ByteOffset, RowCount, IsMatrix, ArraySize)` 对账。
2. 不一致的成员 — 标到一个表里,先不要进 rewriter,留 anonymous。
3. 一致的成员 — 让 rewriter 严格按 metadata 拆 cbuffer 字段。
4. 然后再回 UE 端,检查为什么有些成员的形状被 reader 填错(典型是
   `10.Normal intensity` 这种"`10.`"前缀污染、scalar 被写成 vec4 等)。

### 2.2 已知 fixture 和当前行为

**EndField LitPoly**(本轮**正面 fixture**,不要让任何修改让它退化):

- `Testing/Assets/Shaders/UnityBinary/EndField/litpoly.shader.sub0.pass0.blob1.HGBuffer.dxbc.bin` + `.metadata.json` (vertex)
- `Testing/Assets/Shaders/UnityBinary/EndField/litpoly.shader.sub0.pass0.blob2.HGBuffer.dxbc.bin` + `.metadata.json` (fragment)
- 现产物分别在 `…Output/EndField/<name>/unitybinary.hlsl`
- blob2 的 ShaderVariablesGlobal 已是 6 命名成员,完全正确(c32/c44/c57/c81/c101/c108)
- blob2 的 UnityPerMaterial 已正确演示 c0.z / c0.w / c1 / c8 这种带空洞的 packoffset
- blob1 的 UnityInstancing_SRP_UnityPerDraw 已正确演示 `[256u]` 数组上的动态索引
- blob1 的 ShaderVariablesGlobal 当前是 5 个成员,**首成员被错误命名为
  `GlobalMipBias`(应该是 `_NonJitteredViewNoTransProjMatrix`)**,且
  scalar `_GlobalMipBias` @ c108 缺失 — **这是个具体可调查的样例**。
  rewrite.txt: `[ShaderVariablesGlobal] rewrite planned with 5 members`
  (而 blob2 同名 CB 是 6 members,metadata 也几乎相同,差异点本身是
  线索。)

**Unity Deferred Clustered Lights**(辅助 fixture):
- `Testing/Assets/Shaders/UnityBinary/Ruri/Hidden_Ruri Render Pipeline_ClusterDeferred.shader.sub0.pass0.blob27 ...`
- 产物 `…Output/Ruri/.../unitybinary.hlsl`
- `$Globals`、`LightShadows`、`urp_ZBinBuffer`、`urp_TileBuffer` 都
  正确;`AdditionalLights` 因 4 个并列 256-长 float4 数组在
  `CanRewriteAllAccessChains` 校验阶段失败(`unsupported access chain
  parse for resultId=1165 op=65 words=[393281,78,1165,22,53,1161]`),
  整个 CB 退化为 single-array 形态。

**UE Oni_Valley_VFX `M_Bamboo_tree_PS_1904`**(本轮**反面 fixture**):
- 输入路径 §0
- HLSL: `cbuffer Material { float4 Material_1_Tree_sway_softness[16] };` —
  退化形态
- rewrite.txt 失败原因: `slotConst=2`(byte 32)在 metadata 里没有命名
  成员(§1.3 已分析)。
- **下一步行动**: 在 `M_Bamboo_tree.json` 实际找到 byte 32 真实属于哪
  个 ParameterInfo / 哪个 typed parameter,确认 `10.Normal intensity`
  到底是 scalar 还是更宽的成员。

### 2.3 长期(README roadmap)

1. 完善 Unity 支持 → 直接生成 ShaderLab(不只是 per-pass HLSL)。
2. 统一 UE / Unity 的反编译输出为 ShaderLab。
3. SPIR-V → spirv-cross HLSL → DXBC 重新编译优化指令数 → 重新反编译为更
   可读 HLSL。
4. UE 端符号管线重构(behaviour-byte-identical,删冗余,统一命名)。

---

## 3. 文件分工

### 3.1 本任务可改动

- `Source/Ruri.ShaderDecompiler/Spirv/StructuredCBufferRewriter.cs` —— 核
  心修复点,2139 行,负责 SPIR-V 中 CB 成员拆分/命名注入。
- `Source/Ruri.ShaderDecompiler/Unreal/UeShaderSymbolInputsReader.cs` ——
  metadata `VectorParams` / `CBParams` / `AllNumericParams` 三处填充的来源,
  需去冗余。
- `Source/Ruri.ShaderDecompiler/Unreal/UeShaderSymbolBuilder.cs` —— UE 侧
  symbol 合流入口。
- `Source/Ruri.ShaderDecompiler/Unreal/MaterialUniformBufferLayout.cs`、
  `UeShaderResourceTableDecoder.cs`、`UeShaderResourceTableSymbolizer.cs`
  —— UB layout 重放与 SRT 解码。
- `Source/Ruri.ShaderDecompiler/Targets/Oni_Valley_VFX.md` —— 已并入本文件,
  迭代日志改在本文件 §9 续写。

### 3.2 不许动

- `Source/Ruri.RipperHook/...` —— Unity 侧 AssetRipper 端是参考实现,不要
  动它的 metadata 导出格式或语义。
- `FModel/CUE4Parse/...` —— 第三方 fork,只读不改。

### 3.3 假设可错(可改动)

- `Source/Ruri.ShaderDecompiler/**` 整个项目都还在补完阶段,任何文件都
  假设可能有 bug:
  - `ShaderDecompiler.cs` 主 pipeline 顺序、阶段划分、错误处理 — 都可
    重排。
  - `Spirv/SpirvPatcher.cs`、`Spirv/SpirvModule.cs`、`Spirv/StructuredCBufferRewriter.cs`、
    `Spirv/SpvInstructionTraits.cs` — SPIR-V 解析/重写整个层都可改。
  - `Unreal/**` 全部 reader / decoder / symbolizer / builder — 都可
    改,且**已知** UE 端 metadata 有错误(byte offset、scalar/vector
    typed 错位、ParameterInfo 名字泄漏前缀)。
- `Source/Ruri.FModelHook/Game/SBUE/**` UE 导出钩子也可改。

### 3.3 项目硬规则

- **绝不硬编码 UE C++ 源码里的 UB 成员表** —— 不同 UE 版本/魔改版会偏
  移,硬编码会静默捏造名字。所有名字必须能追到 cooked 数据中真实存在的
  字节。
- 加新名字来源前,先在 §4 的 "持久化裁定矩阵" 加一条带 UE 源码引用的条
  目;查不到的,gap 就是 gap,留 anonymous 占位,不许猜。
- placeholder 一定带 `_SRV`/`_Sampler`/`_UAV`/`_Resource` 中缀,从 HLSL
  上一眼能看出"未完全恢复"。

---

## 4. 持久化裁定矩阵(D3D shipping cook,UE5.1)

> 来自 `UE_SHIPPING_NAME_TRUTH.md`。所有引用都来自 `D:\GameStudy\UnrealEngine-5.1.1-release`。

### TL;DR

- **DXBC `RDEF` chunk 已剥离**(`D3DStripShader(STRIP_REFLECTION_DATA)`,
  `D3DShaderCompiler.cpp:1115-1140`,默认 shipping)→ `Material_Texture2D_0`
  / `View_PerlinNoise3DTexture` 等编译期资源名 **不在 bytecode 里**。
- `FShaderParameterMap`(唯一保存编译期 `ParameterName` 的位置)**不进
  cooked archive**。
- `'n'`(shader 源文件名)受 `CFLAG_ExtraShaderData` 闸,默认 shipping 关。
- `'p'`(资源数量)、`'m'`(UAV mask)、`'x'`(features)、**`'u'`(UB 名字
  列表)** 在 D3D 路径上**无条件**写出 (`D3DShaderCompiler.inl:603-607`)。
- `FBaseShaderResourceTable`(`ResourceTableBits` + 四个 packed map +
  `ResourceTableLayoutHashes`)总是出现。
- `FShaderParameterBindings` / `FShaderParameterMapInfo` 在 frozen image
  幸存,**只有 indices/types,无名字**。
- 材质 `.uasset` 携带完整 `FUniformExpressionSet`,所有 ParameterInfo 名
  字都在 → **总是有**。
- 引擎定义的 UB 布局(`View`/`OpaqueBasePass`/...)是 C++ 宏展开,只在
  引擎二进制里 → **永不写盘**,不能从 cooked 数据恢复。

### 矩阵

| 符号类 | shipping cook 是否保留 | 真理来源 | reader 状态 |
| --- | --- | --- | --- |
| UB 名字(`Material`/`View`/`OpaqueBasePass`/...) | ✅ D3D 无条件 | `'u'` optional block | ✅ `UnrealShaderParser` |
| `Material_Texture2D_0`(编译期资源名) | ❌ RDEF 剥离 | 重建 Material UB layout | ✅ replay `CreateBufferStruct()` ordering(需有 `.uasset`) |
| `View_PerlinNoise3DTexture` 等 | ❌ RDEF 剥离 | 仅 C++ 引擎源码 | ❌ 项目规则禁止硬编码 → placeholder |
| 材质参数用户名(`"Normal input"`) | ✅ 在 `.uasset` | `FUniformExpressionSet.UniformTextureParameters[Type][i].ParameterInfo.Name` | ⚠ 名字已抽取,尚未 annotate 到资源 binding |
| 材质数值参数名(`"UV Scale Near"`) | ✅ 在 `.uasset` | `FUniformExpressionSet.UniformNumericParameters[i].ParameterInfo.Name` | ✅ Material CB 成员已通过 `UeShaderSymbolInputsReader` 引入 |
| Bind-point ↔ UB-resource-index | ✅ 无条件 | SRT token streams | ✅ `UeShaderResourceTableDecoder.cs` |
| Bind-point ↔ struct-byte-offset | ✅ frozen | `FShaderParameterBindings.ResourceParameters[]` | ⚠ 已导出未接入 HLSL |
| Shader 源文件名(`BasePassPixelShader.usf`) | ❌ shipping 默认关 | `'n'` | 不依赖 |
| Shader entry-point(`MainPS`) | ❌ 从未序列化 | 无路径 | 不依赖 |
| Vertex factory(`FLocalVertexFactory`) | hash only | `FVertexFactoryTypeDependency.HashedName` (uint64) | 可未来反查 hash |

### 关键源码引用

- `'u'` 写出: `Engine/Source/Developer/Windows/ShaderFormatD3D/Private/D3DShaderCompiler.inl:601-607`(无条件,与 `'p'`/`'m'`/callback 一起)
- `'n'` 写出: 同文件 :621-624(gated on `CFLAG_ExtraShaderData`)
- DXBC strip: `Engine/Source/Developer/Windows/ShaderFormatD3D/Private/D3DShaderCompiler.cpp:1115-1140`
- SRT 序列化: `Engine/Source/Runtime/RenderCore/Public/ShaderCore.h:381-432`
- SRT 运行时解码: `Engine/Source/Runtime/D3D12RHI/Private/D3D12Commands.cpp` 的资源表 walk
- `FRHIResourceTableEntry` 位布局: `Engine/Source/Runtime/RHI/Public/RHIDefinitions.h`
  - `BindIndex   = token & 0xFF`
  - `ResourceIndex = (token >> 8) & 0xFFFF`
  - `UniformBufferIndex = (token >> 24) & 0xFF`
- `Material` 内成员命名: `FUniformExpressionSet::CreateBufferStruct()` 在
  `Engine/Source/Runtime/Engine/Private/Materials/MaterialUniformExpressions.cpp:341-426`
  固定生成 `Texture2D_%d` / `Texture2D_%dSampler` / `TextureCube_%d` /
  `Texture2DArray_%d` / `VolumeTexture_%d` / `ExternalTexture_%d` 等。
- 翻译器访问形态: `FHLSLMaterialTranslator::AccessUniformExpression()` 在
  `Engine/Source/Runtime/Engine/Private/Materials/HLSLMaterialTranslator.cpp:3333-3373`
  生成 `Material.<Class>_<TypedIndex>`。
- D3D 扁平化: `RemoveUniformBuffersFromSource()` 在
  `Engine/Source/Developer/ShaderCompilerCommon/Private/ShaderCompilerCommon.cpp:824-913`
  将 `View.WorldToClip` → `View_WorldToClip`,`Material.Texture2D_0` →
  `Material_Texture2D_0`。

### 关闭项(2026-04-28)

11 条候选路径全部确认不携带引擎 UB 成员名(pipeline cache、所有 optional
block、`FRHIUniformBufferLayout` 序列化、IoStore 容器元数据、
AssetRegistry.bin、运行时 shader-factory、`r.Shaders.IncludeSource`、
`'n'` block、`MemoryImageResult.ScriptNames`、Oni Valley 项目自定义资产)。
**结论关闭世界**: 默认 D3D shipping cook 不保存引擎 UB 成员名。

仅剩三条理论路径全部超出离线管线范围: 静态反射游戏 `.exe` `.data` 段、
项目自带查找表、硬编码引擎版本(项目规则禁止)。

---

## 5. UE 符号映射真理链(节选,详见旧 `SHADER_MAPPING_RESEARCH.md`)

### 5.1 全局 UB 名字真理(C++ 源码闭合)

```cpp
// Engine/Source/Runtime/Engine/Private/SceneView.cpp:49
IMPLEMENT_STATIC_AND_SHADER_UNIFORM_BUFFER_STRUCT(FViewUniformShaderParameters, "View", View);

// Engine/Source/Runtime/Renderer/Private/BasePassRendering.cpp:96
IMPLEMENT_STATIC_UNIFORM_BUFFER_STRUCT(FOpaqueBasePassUniformParameters, "OpaqueBasePass", SceneTextures);
```

`View` / `OpaqueBasePass` 这种名字属于全局唯一约定,可作引擎源码真理。
**成员名**则禁止硬编码(项目规则)。

### 5.2 `Material` UB 真理

- 布局树: `UniformExpressionSet.UniformBufferLayoutInitializer.{Resources[],ConstantBufferSize}` 在 `.uasset` 中。
- 名字树: `UniformNumericParameters[*].ParameterInfo.Name` /
  `UniformTextureParameters[Type][i].ParameterInfo.Name` /
  `UniformExternalTextureParameters[*].ParameterName` —— 在 `.uasset`。
- 数值参数到 buffer offset 的 preshader 桥:
  - `UniformPreshaders[i]` 指向一段 opcode
  - 该 opcode 仅一条 `EPreshaderOpcode::Parameter`(=3),且仅写一个
    `UniformPreshaderFields[i]` 时:
    `ParameterIndex → UniformNumericParameters[index].ParameterInfo.Name`
    `BufferOffset = UniformPreshaderFields[i].BufferOffset`
  - 其它非平凡 opcode 暂不闭合,留 anonymous。
  - 已在 `MI_Cliff_small_ground_level` 上静态闭合 15 个名字(SelectionColor /
    UV Scale Near / Normal blend / Blend Sharpness (S) / Blend Bias (S) /
    RVT Height blend / RVT blend falloff / Side contrast / Curvature
    cavity intensity / Curvature highlights intensity / AO tint / AO
    tint power / Specular detail / Specular softness / Roughness dullness)。

### 5.3 Material 资源成员命名(replay `CreateBufferStruct()`)

UE 5.1.1 `MaterialUniformExpressions.cpp:370-470` 固定生成顺序:

```
Texture2D_<i> / Texture2D_<i>Sampler  (i = 0..UniformTextureParameters[Standard2D].Count-1)
TextureCube_<i> / TextureCube_<i>Sampler
Texture2DArray_<i> / Texture2DArray_<i>Sampler
TextureCubeArray_<i> / TextureCubeArray_<i>Sampler
VolumeTexture_<i> / VolumeTexture_<i>Sampler
ExternalTexture_<i> / ExternalTexture_<i>Sampler
VirtualTexturePhysical_<i> / VirtualTexturePhysical_<i>Sampler  (来自 VirtualTextureLayer)
```

D3D 扁平化后实际写入 HLSL 的形态是 `Material_Texture2D_0` 而不是
`Material.Texture2D_0`。

### 5.4 ParameterCollections

`FUniformExpressionSet.ParameterCollections[i]` = 第 i 个
`UMaterialParameterCollection` 的 `StateId`(GUID)。`MaterialCollectionN`
是否严格 = `ParameterCollections[N]` 仍未严格闭合,实践中样例只有一个
collection,所以"强支持但未完全闭合"。

### 5.5 `FShaderParameterBindings.ResourceParameters` 真理

每项 `(ByteOffset, BaseIndex, BaseType)`,**无名字**。生成路径:

`FShaderParameterMap.ParameterMap` (有名字) →
`FShader::BuildParameterMapInfo()` (丢名字) →
`FShaderParameterBindings::BindForLegacyShaderParameters()`(用 shader
parameter struct 元数据 + ParameterMap 查名,但只保留 BaseIndex/BaseType)

cooked 落盘只剩 `Bindings.ResourceParameters` 和 `ParameterMapInfo`,
名字桥断了。**这是当前 t/s/u 槽位仍 anonymous 的原因**,不是数据没导出
出来,是数据本身就没保存名字。

### 5.6 `MemoryImageResult.ScriptNames` patch

- `ScriptNames[*].Patches[*].Offset` 命中的是 frozen object 中**材质参数
  身份结构**(`FMaterialNumericParameterInfo` / `FMaterialTextureParameterInfo`)
  的 `Name` 字段,SavedLayoutSize=28。
- 对样例 `MI_Cliff_small_ground_level` 已逐 byte 确认 patch 落在材质参数
  名,而不是 shader resource binding。
- 因此 ScriptNames patch 不是 `Tn/Sn/U0` 名字桥。

---

## 6. 当前限制(节选,详见旧 `CURRENT_LIMITATIONS.md`)

### 6.1 可恢复

- UB 名字 / CB binding 槽位 / SRT bind-index ↔ UB-resource-index / 每 UB
  资源数 / Material 资源 typed flat 名 / 用户面材质参数名 / Material CB
  preshader 桥可证明的数值偏移。

### 6.2 不可恢复(closed-world ceiling)

- 引擎 UB 成员名(`View_PerlinNoise3DTexture` 等)。pipeline 现在退化为
  `<UB>_SRV<i>` / `<UB>_Sampler<i>` / `<UB>_UAV<i>` placeholder。
- 不依赖 UB 的 `FShaderParameter*` 散绑资源(每 shader 类的
  `BEGIN_SHADER_PARAMETER_STRUCT`)—— frozen 后只剩 (ByteOffset, BaseIndex,
  BaseType) 三元组,无名字。HLSL 退化保留 spirv-cross 默认的 `T<n>` /
  `_RegisterSpaceN[…]`,不许编。
- `Material_Texture2D_0` 编译期 reflection 名(已在 RDEF strip 时丢失)。
- Shader 源文件名 / entry-point / vertex factory 字符串。
- Sampler 名 —— 只有 bind index,统一 `sampler_<bindIndex>`。

### 6.3 Anti-patterns(违规)

- 硬编码引擎 UB 成员表(任何 SceneView.h / BasePassRendering.h)。
- 用 register class + index 推成员名("View 的第 3 个 texture 一定是
  PerlinNoiseGradientTexture")。
- `Material_Texture2D_3` 写在 SRT UB index 不指向 Material 的槽上。
- placeholder 不带 infix 让人误以为是真名。

### 6.4 加新名字来源的唯一合法路径

1. 找到 cooked 数据里某个 byte 携带这个名字。
2. 在 §4 矩阵里加一行,引用 UE 源码"写入位置"+"读取位置"+"是否 cvar 闸"。
3. 写一个 reader 从这个 byte 取名字。
4. 闸位条件不满足时,reader 退化到 placeholder,不允许 fail。
5. 如果完全没 byte 来源,gap 留作 gap,在 §6.2 加一条。

---

## 7. Pipeline(端到端)

### 7.1 UE 端导出(头部)

`Ruri.FModelHook.exe`(WPF + 对 FModel 的 detour)→ FModel UI 启动
→ `UE_ShaderDecompiler_Hook` 钩子在材质/shader archive 导出时触发
→ `UnifiedShaderMetadataExporter.ExportAll(...)` 写出
`<exportRoot>\UnifiedShaderMetadata.json` + 每个 library 的
`.assetinfo.json` / `.stableinfo.json` / `.ushaderlib` /
`.ushaderbytecode`。

非交互模式:
```
Ruri.FModelHook.exe --auto-export-cook [--shader-only] [--no-quit] [--ready-timeout-sec <int>]
```
hook `[RetargetMethod(typeof(MainWindow), "OnLoaded", true, false)]` 在
WPF 主窗口载入完成时,off-thread poll 直到 CUE4Parse provider `Files.Count`
稳定,然后 dispatcher 调 `vm.ExportData(...)` 触发现有 shader 钩子。
hook 文件: `Source\Ruri.FModelHook\Game\SBUE\AutoExport\UE_ShaderDecompiler_AutoExport_Hook.cs`。

### 7.2 离线反编译

```
Ruri.ShaderDecompiler.exe \
    <lib.ushaderlib> \
    <outDir> \
    --mapping <UnifiedShaderMetadata.json>
```

每 shader 流程:

1. 解析 SRT(`UnrealShaderParser` + `UeShaderResourceTableDecoder`)。
2. 通过 `UnifiedShaderMetadataResolver` 找到 shader 所属材质的
   `UniformExpressionSet`。
3. `UeShaderSymbolInputsReader` 把材质数据解析为 `ShaderSymbolData`。
4. `UeShaderResourceTableSymbolizer` 把 SRT 解码出的 (UBIndex,
   ResourceIndex, BindIndex, RegisterType) + UB layout 翻成命名 binding。
5. `UeShaderSymbolBuilder` 合流为最终的输入 `ShaderSymbolData`。
6. 走核心 pipeline: `dxbc → dxbc2dxil → dxil-spirv → SpirvPatcher` 注入
   符号 → `StructuredCBufferRewriter` 重写 cbuffer 结构 → `spirv-cross`
   → HLSL。

### 7.3 一次完整自测试(参考)

```bash
dotnet build "D:/Ruri/Github/FractalTools/Ruri-RipperHook/Source/Ruri.ShaderDecompiler/Ruri.ShaderDecompiler.csproj" -c Debug
dotnet build "D:/Ruri/Github/FractalTools/Ruri-RipperHook/Source/Ruri.FModelHook/Ruri.FModelHook.csproj" -c Debug

# 若导出根缺 UnifiedShaderMetadata.json:
"D:/Ruri/Github/FractalTools/Ruri-RipperHook/Source/Ruri.FModelHook/bin/Debug/Ruri.FModelHook.exe" --auto-export-cook --shader-only

# 反编译 SM5 lib:
"D:/Ruri/Github/FractalTools/Ruri-RipperHook/Source/Ruri.ShaderDecompiler/bin/Debug/Ruri.ShaderDecompiler.exe" \
  "D:/Ruri/Github/FractalTools/Ruri-RipperHook/FModel/FModel/bin/Debug/net8.0-windows/win-x64/Output/Exports/Oni_Valley_VFX/Content/ShaderArchive-Oni_Valley_VFX-PCD3D_SM5-PCD3D_SM5.ushaderlib" \
  "D:/Ruri/Github/FractalTools/Ruri-RipperHook/FModel/FModel/bin/Debug/net8.0-windows/win-x64/Output/Exports/Oni_Valley_VFX/Decompiled/Oni_Valley_VFX_SM5" \
  --mapping "D:/Ruri/Github/FractalTools/Ruri-RipperHook/FModel/FModel/bin/Debug/net8.0-windows/win-x64/Output/Exports/Oni_Valley_VFX/UnifiedShaderMetadata.json"
```

---

## 8. 验证 — 看一个 HLSL 是否变好了

```bash
DIR=".../Decompiled/Oni_Valley_VFX_SM5"
ls "$DIR"/*.hlsl | wc -l                                           # total
grep -l "_RegisterSpace" "$DIR"/*.hlsl 2>/dev/null | wc -l        # ↓ better
grep -l "Material_"      "$DIR"/*.hlsl 2>/dev/null | wc -l        # ↑ better
grep -l "View_"          "$DIR"/*.hlsl 2>/dev/null | wc -l        # ↑ better
grep -l "OpaqueBasePass_" "$DIR"/*.hlsl 2>/dev/null | wc -l       # ↑ better
```

Unity 回归保证:
- `Source/Ruri.ShaderDecompiler/Testing/Assets/Shaders/UnityBinary/EndField/litpoly.shader.sub0.pass0.blob1.HGBuffer.dxbc.bin` (+ `.metadata.json`)
- `…/blob2.HGBuffer.dxbc.bin` (+ `.metadata.json`)
- 这两个动态索引样例(`cb[tmp+5]` 等)必须保持原行为,任何 rewriter 修改不许让它们退化。
- Unity Deferred Clustered Lights 是当前 partial-CB 测试 fixture(§2.1)。

---

## 9. Targets / Iteration Log

### 9.1 Oni_Valley_VFX

- 游戏: `D:\GameStudy\OniValleyDemo`
- Pak/IoStore: `D:\GameStudy\OniValleyDemo\Oni_Valley_VFX\Content\Paks`
  (`Oni_Valley_VFX-Windows.pak` / `.utoc` / `.ucas`,`global.utoc/.ucas`)
- Mappings: `…\Oni_Valley_VFX\Binaries\Win64\Oni_Valley_VFX-Win64-Shipping-Mappings.usmap`
- 引擎: UE 5.1.x
- 当前 4 个 library:
  - `ShaderArchive-Global-PCD3D_SM5-PCD3D_SM5.ushaderlib` (37 MB)
  - `ShaderArchive-Global-PCD3D_SM6-PCD3D_SM6.ushaderlib` (38 MB)
  - `ShaderArchive-Oni_Valley_VFX-PCD3D_SM5-PCD3D_SM5.ushaderlib` (44 MB) — **主 fixture**
  - `ShaderArchive-Oni_Valley_VFX-PCD3D_SM6-PCD3D_SM6.ushaderlib` (69 MB)
- 代表性参考材质:
  - `MI_Cliff_small_ground_level` — Material CB 命名标量 + 虚拟纹理引用 (闭合)
  - `M_Cliffs` — 父材质,提供 `ParameterCollectionInfos` + `CachedExpressionData.ReferencedTextures`
  - `M_Bamboo_tree` — **当前 active bug fixture**(§1.1):metadata 12
    成员命名正确但 HLSL 仍单数组

### 9.2 度量(每轮迭代记录)

| Iter | UB 名 | Material 纹理命名 | 引擎 SRV 槽命名 | `_RegisterSpace` |
| --- | --- | --- | --- | --- |
| 0 baseline | 0 | partial(builder) | 0 | many |
| 1 | all UBs in 'u' ✅ | placeholder `Material_SRVN` | placeholder `<UB>_SRVN`(`LumenCardScene_SRV0`/`View_SRV49`) | **0** / 16004 ✅ |
| 2 | unchanged | typed names ready(gated on `.json`) | 引擎 UB 成员名硬编码方案被禁,降级 placeholder | n/a |
| 3 (research) | 锁定 §4 矩阵 | typed flat 名仅由材质 `.uasset` 重建 | 永远不可从 cooked 恢复 | n/a |
| 6 (`MI_Cliff_Parent_PS_3500`) | 同上 | 12 个数值参数命名进入 metadata,但 HLSL 仍 `Material_1_m0[N]`(rewriter 不拆) | placeholder | 0 |
| 11 (`M_Bamboo_tree_PS_1904`) | ✅ View/LocalVF/MaterialCollection0/Material 全部命名 | metadata 12 命名进 `Material`,HLSL 仍 `float4 Material_1_Tree_sway_softness[16]`(rewriter 因 byte 32 metadata 无成员退化) | placeholder | **0** / 4121 ✅ |
| **当前 12 (`M_Bamboo_tree_PS_1904`)** | 同上 | **22 命名成员**全部 packoffset 正确,`SelectionColor_xyz @ c2` 等 swizzled-view 槽位全部到位 ✅ | placeholder | **0** / 4121 ✅ |

### 9.3 已落地的迭代(摘录)

- **It. 1** — SRT decoder + UB-name binding + `--auto-decompile` 一键
  driver(`Program.cs` 加 `--auto-decompile <exportRoot>`,扫
  `*.ushaderlib` 自动反编)。4 lib / 16004 HLSL / 0 库级失败。
- **It. 3** — 关闭世界裁定矩阵(§4)落地,纠正三处旧假设:
  - `'u'` 是 D3D 路径无条件,不受 `CFLAG_ExtraShaderData` 闸。
  - DXBC RDEF 在 shipping `STRIP_REFLECTION_DATA` 后丢失,`Material_Texture2D_0`
    等编译期资源名 **不在 bytecode**,只能从材质 `.uasset` 重建。
  - `'n'` shader 源文件名 shipping 默认关。
- **It. 4** — `UE_ShaderDecompiler_AutoExport_Hook` 落地。无头驱动 FModel
  导出与 UI 完全等价,`UnifiedShaderMetadata.json` 字节相同。
- **It. 6** — 删除 `EngineUniformBuffers.cs` 硬编码表(违反项目规则);
  新增 `UeUnifiedMaterialReader.cs` 直接从 `UnifiedShaderMetadata.json`
  读取 UES,不再要求逐材质 `*.uasset.json`(FModel UI 默认不写);
  `UeShaderSymbolInputsReader.ParseMaterialParameterInfo` 同时接受
  `{ParameterInfo:{Name}}` 与 `{ParameterName}` 两种 shape;
  Material CB 抽取改填 `VectorParams`(原直接填 `CBParams` 走不到
  rewriter)。`MI_Cliff_Parent_PS_3500` 的 12 个名字字节级与
  `MI_Cliff_large.json` 对齐。**已知 follow-up**: HLSL 仍是单数组,因为
  rewriter 不拆 → 这就是当前要修的 bug(§1.1)。

### 9.4 It. 7 — Member()+IAdd 双 bug 修复

两处 SPIR-V 端 bug 一并修掉,Deferred Clustered Lights `AdditionalLights`
首次完整展开。

#### Bug 1 — `ShaderDecompiler.cs:Member()` last-name-wins

```csharp
// 旧
private static int? Member(SpirvBindingInfo binding, int byteOffset)
    => binding.MemberOffsets.FirstOrDefault(pair => pair.Value == (uint)byteOffset).Key;
```

`KeyValuePair<int,uint>` 是结构体,`FirstOrDefault` 没匹配时返回
`default` = `(0, 0)`。`.Key = 0` 隐式转 `int?` 不为 null,`if (... is int i)`
匹配通过,**所有未命中字节偏移的 CBParam 都被错误地写到 member 0**,后写
覆盖前写。

后果:
- M_Bamboo_tree 的 12 个 CBParam 全部排队覆盖 member 0,最后一个名字
  `Tree sway softness` 胜出 → `Material_1_Tree_sway_softness[16]`。
- EndField blob1 重写后新 struct 5 个成员,scalar `_GlobalMipBias` @ 1728
  没在新 struct 的 `MemberOffsets` 里 → 误命中 member 0 → 把 c32 的
  matrix 名字盖成 `_GlobalMipBias`。

修法: 显式 foreach + return null。

#### Bug 2 — `StructuredCBufferRewriter.TryDecomposeLinearIndexExpression` IAdd 二元都动态硬失败

`%1161 = OpIAdd %453 %1160`(都动态)— `TryDecomposeLinearIndexExpression`
旧版在 IAdd / IMul / Shl 二元都不是常量时直接 `return false`,导致整个
`TryParseFlatAccessChain` 失败,整 CB 重写被拒。但**普通**未识别 op 反倒
走默认 fallback 返回 `dynamicIndexId=valueId, stride=1, offset=0` 成功。
这种不一致让"运行时下标本身就由两个动态值合成"的常见场景全部走不通。

修法: IAdd / ISub / IMul / Shl 二元全动态时不再 return false,落到末尾的
默认 fallback,把整个表达式当作不透明 `dynamicIndexId=valueId`。

后果:
- `m0[%1161]` parse 成 `{ dynId=1161, stride=1, offset=0 }` → 命中 member
  0 (Position @ register 0)。
- `m0[%1161 + uint_512]` parse 成 `{ dynId=1161, stride=1, offset=512 }`
  → 命中 member 2 (Attenuation @ register 512)。
- `m0[%1161 + uint_768]` → member 3 (SpotDir @ register 768)。
- `m0[%1232 + uint_256]` 同理映射到 Color。

#### 验证结果

| Fixture | Before | After |
| --- | --- | --- |
| Deferred Clustered `AdditionalLights` | `_AdditionalLightsSpotDir[1024]`(末位污染) | 4 命名 `[256]` 成员全展开 ✅ |
| Deferred Clustered `$Globals/LightShadows/urp_*` | OK | OK 不退化 ✅ |
| EndField blob1 `ShaderVariablesGlobal` | 首成员被 `_GlobalMipBias` 错命名 | 5 成员名字正确(`_NonJitteredViewNoTransProjMatrix` @ c32) ✅ |
| EndField blob2 `ShaderVariablesGlobal` / `UnityPerMaterial` | 6 / 5 成员 OK | 6 / 5 成员 OK 不退化 ✅ |
| UE `M_Bamboo_tree_PS_1904.Material` | `_Tree_sway_softness[16]`(末位污染) | `_SelectionColor[16]`(首成员,rewriter 仍因 byte 32 在 metadata 里缺成员而退化) ⚠ 需 UE 端 reader 修复 |

跑单 shader 命令(用户提示):
```
Ruri.ShaderDecompiler.exe <lib.ushaderlib> <outDir> --mapping <UnifiedShaderMetadata.json> --shader-index <N>
```

`--shader-index` 接收 shader 编号(M_Bamboo_tree_PS_1904 即 N=1904),
全表只反编译目标 shader,几秒内完成,适合 fixture 单测。

### 9.5 It. 8 — spirv-cross "Cannot subdivide a scalar value" 链式修复

新 fixture(Unity 端,直接报 `SPIRV-Cross threw an exception: Cannot
subdivide a scalar value!`):
- `Testing/Assets/Shaders/UnityBinary/Ruri/TextMeshPro_Distance Field.shader.sub0.pass0.blob1..dxbc.bin`(vertex)
- `Testing/Assets/Shaders/UnityBinary/Ruri/Ruri_Scene_Lit.shader.sub0.pass0.blob5.GBuffer.dxbc.bin`(HS,既报 subdivide 又报 InvocationId)

之前 AI 走 GLSL fallback 绕过,但根因在 SPIR-V rewriter 的多个类型/标记
错位。本轮一次性修通:

#### Bug A — `CountResultUses` 把字面量当成 id 计数

`OpMemberDecorate %_Globals_0 18 Offset 496` 的 `496` 是 byte 偏移**字面
量**,但 `CountResultUses` 不分 literal/id,统统 `uses[operand]++`。当字
面量数值恰好与某个真实 SSA id 相同时,该 id 的 use 计数被多算 → load 的
`compositeUsers != totalUsers` 判定失败 → load 不被 NOP → 旧 vec4 load 留
在新 ptr-scalar 之上 → spirv-cross 崩。

修法: 跳过纯 metadata / 装饰指令(`OpName`/`OpMemberName`/`OpDecorate`/
`OpMemberDecorate`/`OpString`/`OpSource*`/`OpLine*`/`OpExecutionMode*`/
`OpEntryPoint`/`OpCapability`/`OpExtension*`/`OpMemoryModel`/
`OpDecorationGroup`/`OpGroupDecorate*`),它们不构成数据流引用。
新加 `IsLiteralBearingMetadataOp()` 网关。

#### Bug B — vec4 成员 BARE 访问被误加 component 索引

`TranslateMemberAccess` 在 `Vector` 分支无条件 `CreateTranslation(member.ResolvedTypeId, relativeComponentIndex)`,
即使是裸取整个 vec4 也加 `[0]` 索引。结果 `OpAccessChain ptr-vec4 var memberIdx 0` 下钻到 scalar 但仍声明 ptr-vec4 → spirv-cross 崩。

修法: 区分 `extraIndices.Count == 0`(裸取,不加索引,返回 ptr-vec4)和
`>0`(分量取,加索引,返回 ptr-scalar)。`StructuredMemberLayout` 加
`ScalarTypeId` 字段,在 `ResolveMemberTypeId` 时按
`member.LogicalType.ScalarKind` 预解析。

#### Bug C — matrix 成员 column 访问类型错位

同样问题:`matrix[col]` 应该返回 ptr-vec(rowCount),不是 ptr-matrix。
`matrix[col][component]` 应该返回 ptr-scalar。

修法: `StructuredMemberLayout` 加 `ColumnVectorTypeId` 字段,在
`ResolveMemberTypeId` 时通过 `EnsureVectorType` 预解析。Matrix 分支按
extras 数量分流到 column-vec / scalar 类型。
`TranslateDynamicArrayMemberAccess` 矩阵分支同样修。

#### Bug D — `CanRewriteViaCompositeExtracts` 路径残留无效 access chain

当 bare 访问无法直译但每个 `OpCompositeExtract` 都能直译时,旧逻辑保留
原 access chain 不动。但 OpVariable 的指针类型已经被改成新 struct,旧 access chain `OpAccessChain ptr-vec4 var %0 %register` 走的是新 struct 不存在的旧 flat 数组,spirv-cross 验证失败。

修法: `RewriteLoadsAndCompositeExtracts` 末尾加最终清理 — 重新统计
`CountResultUses`,把 `rewrittenAccessChains` 里 use=0 的 access chain
全部 NOP 掉。

#### Bug E (顺手) — `RewrittenLoadInfo.InstructionIndex` 在 inserts 后失效

虽然在当前数据上没观察到 stale,但概念上不正确:`module.Instructions.Insert`
会让所有缓存的 index 失效。改为直接存 `SpirvInstruction` 引用(class,
List.Insert 不影响引用)。

#### Bug F — 失败时丢失 IntermediateSpirv 阻碍调试

之前失败路径返回的 `DecompileResult` 没有 spv,`unitybinary.error.txt`
只有异常 trace,无法 spirv-dis 看具体哪条指令崩。修为 `Decompile` 在
catch 中也填充 `IntermediateSpirv` 和 `StructuredRewriteSummary`,Program.cs
在失败时一并写出 `unitybinary.spv` + `unitybinary.rewrite.txt`。这是稳定改进。

#### 验证结果

| Fixture | Before | After |
| --- | --- | --- |
| TextMeshPro `$Globals` | spirv-cross HLSL 崩 | 23 命名成员全部 packoffset 正确,scalar/vec4/matrix/int 混合,空洞容忍 ✅ |
| Ruri_Scene_Lit GBuffer (HS) | HLSL+GLSL 都崩(subdivide) | HLSL 仍因 `InvocationId` builtin 不支持崩,但 GLSL fallback 工作(原有路径)✅ |
| Deferred Clustered AdditionalLights | 4 命名 `[256]` 全展开 | 不退化 ✅ |
| EndField blob1 / blob2 ShaderVariablesGlobal / UnityPerMaterial / UnityInstancing_SRP_UnityPerDraw | 全部命名 OK | 不退化 ✅ |
| UE M_Bamboo_tree | `Material_1_SelectionColor[16]`(rewriter 退化) | 不退化 ✅ |

TextMeshPro 实际产物片段:

```hlsl
cbuffer _Globals : register(b0)
{
    float _Globals_1_FaceDilate : packoffset(c4);
    float _Globals_1_OutlineSoftness : packoffset(c4.y);
    float _Globals_1_OutlineWidth : packoffset(c6);
    column_major float4x4 _Globals_1_EnvMatrix : packoffset(c11);
    float _Globals_1_WeightNormal : packoffset(c22.y);
    float _Globals_1_WeightBold : packoffset(c22.z);
    float _Globals_1_ScaleRatioA : packoffset(c22.w);
    float _Globals_1_VertexOffsetX : packoffset(c23.z);
    float _Globals_1_VertexOffsetY : packoffset(c23.w);
    float4 _Globals_1_ClipRect : packoffset(c26);
    float _Globals_1_MaskSoftnessX : packoffset(c27);
    float _Globals_1_MaskSoftnessY : packoffset(c27.y);
    float _Globals_1_GradientScale : packoffset(c28);
    float _Globals_1_ScaleX : packoffset(c28.y);
    float _Globals_1_ScaleY : packoffset(c28.z);
    float _Globals_1_PerspectiveFilter : packoffset(c28.w);
    float _Globals_1_Sharpness : packoffset(c29);
    float4 _Globals_1_FaceTex_ST : packoffset(c30);
    float4 _Globals_1_OutlineTex_ST : packoffset(c31);
    float _Globals_1_UIMaskSoftnessX : packoffset(c32);
    float _Globals_1_UIMaskSoftnessY : packoffset(c32.y);
    int _Globals_1_UIVertexColorAlwaysGammaSpace : packoffset(c32.z);
};
```

—— 23 个命名成员、混合标量/向量/矩阵/int、5 处空洞 packoffset(c4.y / c22.* / c23.* / c27.* / c28.* / c32.*)全部一次性正确。

### 9.6 It. 9 — 死 access chain cleanup 鲁棒化 + GLSL fallback 接通 harness

新 fixture: `D:\RJ01512143_1.0Output\ExportedProject\Assets\Shader` 下的
TextMeshPro 多变体 + Ruri_Scene_Lit blob5/blob6。

#### Bug G — final cleanup 用 use-count 不可靠

It.8 §Bug D 的 cleanup 通过 `CountResultUses` 找 use=0 的 access chain 并
NOP。问题: SPIR-V 不少 op 的操作数槽是**字面量**而非 id —— `OpExtInst`
的指令编号、`OpCompositeExtract` 的字面索引、`OpVectorShuffle` 的分量索引
等。It.8 §Bug A 只处理了纯 metadata 类(OpDecorate / OpName 等),没覆盖
这些数据流 op 里的字面量。

实测: TextMeshPro blob7 的 access chain `%81` 没有任何 OpLoad 用,但被
`%uint_81` 之类(实际是 `OpExtInst result type set 81 ...` 的 GLSL.std.450
`Fma=81` 字面量)误算为 use,cleanup 跳过 → 死 access chain 留在
模块里 → spirv-cross 验证失败。

修法: 不再算 generic use count,改成**只看 OpLoad 是不是真有 live
consumer**。`rewrittenAccessChains` 里 access chain 在 `aliveAccessChainConsumers`
集合中(被 live OpLoad 用)就保留,否则 NOP。这样自动避开所有字面量噪声,
也不需要为每个 SPIR-V op 维护操作数布局表。

#### Bug H — Ruri.RipperHook harness 用 `HlslSource` 丢掉 GLSL fallback

`ShaderRuriDecompileExporter.DecompilePasses` 从 `DecompileResult.HlslSource`
取源码,但对 tessellation / geometry stage 的 GLSL fallback,`HlslSource`
是 null(因为 `Result()` 里 `HlslSource = source.Language == "hlsl" ? text : null`)。
导致 blob5/blob6 这类反编译实际成功,但 harness 输出 "No decompiled source generated"。

修法: 改用 `decompiled.SourceCode`,语言无关、HLSL/GLSL 都拿得到。

#### 验证结果

| Fixture | Before | After |
| --- | --- | --- |
| TextMeshPro blob7 `$Globals` | spirv-cross HLSL `Cannot subdivide a scalar value` | 9 命名成员 + 6 处空洞 packoffset 一次过 ✅ |
| Ruri_Scene_Lit blob5/blob6 (HS/DS) | harness 写 "No decompiled source generated" | GLSL fallback 文本写入 block.Source ✅ |
| EndField blob1/blob2 / Deferred Clustered / TextMeshPro blob1 / UE M_Bamboo_tree | 全部命名 OK | 不退化 ✅ |

TextMeshPro blob7 实际产物片段:

```hlsl
cbuffer _Globals : register(b0)
{
    float _Globals_1_FaceUVSpeedX : packoffset(c2);
    float _Globals_1_FaceUVSpeedY : packoffset(c2.y);
    float4 _Globals_1_FaceColor : packoffset(c3);
    float _Globals_1_OutlineSoftness : packoffset(c4.y);
    float _Globals_1_OutlineUVSpeedX : packoffset(c4.z);
    float _Globals_1_OutlineUVSpeedY : packoffset(c4.w);
    float4 _Globals_1_OutlineColor : packoffset(c5);
    float _Globals_1_OutlineWidth : packoffset(c6);
    float _Globals_1_ScaleRatioA : packoffset(c22.w);
};
```

### 9.7 It. 10 — cbuffer 数组 stride 16-byte 对齐

新 fixture(全部触发同一个 spirv-cross HLSL 错误):
- `Hidden_Ruri Render Pipeline_ClusterDeferred.shader.*.dxbc.bin`(`LightCookies` CB 含 `_AdditionalLightsCookieEnableBits` scalar float[8])
- `Hidden_TerrainEngine_Details_UniversalPipeline_WavingDoublePass.shader.*.dxbc.bin`(`AdditionalLights` CB 含 `_AdditionalLightsLayerMasks` scalar float[256])
- `Ruri_Scene_LitWrapper.shader.*.dxbc.bin`(同上)

报错:
```
SPIRV-Cross threw an exception: cbuffer ID 43 (name: LightCookies),
  member index 1 (name: _AdditionalLightsCookieEnableBits) cannot be
  expressed with either HLSL packing layout or packoffset.
```

#### Bug I — `ResolveMemberTypeId` 数组步长丢了 cbuffer 16-byte 对齐

`HLSL cbuffer` 强制规定:**任何数组成员的每元素都跨一个 16-byte 寄存器
槽**(`float arr[8]` 在 cbuffer 中占 128 字节,8 个 vec4 槽,每槽只用 .x)。

旧代码对 scalar/vec2/vec3 数组用 `DeclaredByteSize / ArrayLength`,
对 scalar 算成 `4*N/N = 4`、对 vec2 算成 8、对 vec3 算成 12。结果设
`ArrayStride=4/8/12`,这种紧打包数组**HLSL cbuffer 表达不了**,
spirv-cross 直接拒。只有 vec4(`Rows==4`)恰好被特判到 16,所以之前
EndField/Deferred Clustered 那批 vec4 数组没崩。

修法:`ResolveMemberTypeId` 的数组步长改为按 SPIR-V cbuffer 规则:
- Struct 成员: `StructByteSize`
- Matrix 成员: `Columns * 16`
- 其它(scalar/vec2/vec3/vec4): 一律 `16`
- 最低保 `Math.Max(stride, 16)`(原本是 `Math.Max(stride, 4)`)

#### 验证

| Fixture | Before | After |
| --- | --- | --- |
| ClusterDeferred LightCookies(7 成员混合 matrix4x4 / scalar[8] / scalar / matrix4x4[256] / vec4[256] / scalar[256]) | spirv-cross HLSL 拒 | 全部命名 packoffset 正确,空洞 c11.y / c11.z 也正确 ✅ |
| WavingDoublePass AdditionalLights(`_AdditionalLightsLayerMasks[256]` scalar 数组 @ c1280) | 同样拒 | 5 个数组成员全 packoffset ✅ |
| LitWrapper(同上) | 同样拒 | ✅ |
| 三 shader 全 444 个变体 | 大量同类失败 | **0 失败** ✅ |
| EndField blob1/2 / Deferred Clustered blob27 / TextMeshPro blob7 / UE M_Bamboo_tree | 命名 OK | 不退化 ✅ |

ClusterDeferred LightCookies 实际产物:

```hlsl
cbuffer LightCookies : register(b5)
{
    column_major float4x4 LightCookies_1_MainLightWorldToLight                 : packoffset(c0);
    float                  LightCookies_1_AdditionalLightsCookieEnableBits[8u] : packoffset(c4);
    float                  LightCookies_1_MainLightCookieTextureFormat         : packoffset(c11.y);
    float                  LightCookies_1_AdditionalLightsCookieAtlasTextureFormat : packoffset(c11.z);
    column_major float4x4  LightCookies_1_AdditionalLightsWorldToLights[256]   : packoffset(c12);
    float4                 LightCookies_1_AdditionalLightsCookieAtlasUVRects[256] : packoffset(c1036);
    float                  LightCookies_1_AdditionalLightsLightTypes[256]      : packoffset(c1292);
};
```

—— 注意 `_MainLightCookieTextureFormat` @ c11.y 和 `_AdditionalLightsCookieAtlasTextureFormat` @ c11.z 与 scalar 数组最后一个元素 `EnableBits[7]` @ c11.x 共享同一个 c11 寄存器,这是 HLSL cbuffer 标准玩法,现在能正确生成。

### 9.8 It. 11 — tess/geom 报错噪声降级为说明性 note

`Emit()` 之前对 HS/DS/GS 也是先尝试 HLSL,失败后再走 GLSL 回退。问题是
HLSL 这次失败的 stderr `spirv-cross failed: SPIRV-Cross threw an exception:
Unsupported builtin in HLSL: 8` 会原样打印,看上去像 bug,实际上是
**spirv-cross HLSL backend 长期未实现的 stage 限制**(InvocationId /
TessCoord / 双 entry point / patch-constant function 等)。GLSL backend
对 tess/geom 是支持的,所以回退路径就是预期路径,不是 regression。

修法:
- `TryEmit` / `Run` 加 `quiet` 参数。
- `Emit()` 对 tess/geom stage 调 `TryEmit(... quiet=true)` 走 HLSL,
  失败时**不**打 `spirv-cross failed: ...`,改成单行说明:
  ```
  [spirv-cross note] TessControl stage: HLSL backend lacks tessellation/
  geometry builtins (InvocationId / TessCoord / patch-constant emission).
  Falling back to GLSL output -- this is the expected path for this stage,
  not a regression.
  ```
- 然后 GLSL fallback 走 `quiet=false`,如果 GLSL 也失败(真 bug),原样
  打印 stderr。
- 非 tess/geom stage 不受影响,HLSL 失败仍然 loud。

### 9.9 It. 12 — UE Material CB 缺失 swizzled-view 槽位

§1.3 / §9.9.1 的 `M_Bamboo_tree_PS_1904.Material` 反面 fixture 闭合。

#### 根因

`UeShaderSymbolInputsReader.ReadMaterialConstantBuffer` 旧版只接受
`IsSingleParameterWrite(opcodeSize == 3, opcode == 0x03)` 这种**裸**
`Parameter(N)` 形式的 preshader,把任何更长 opcode 流(swizzle / unary /
clamp / append)都跳掉。

实际数据(本 fixture):24 个 preshaders,12 个是裸 `030N00`,12 个是带
swizzle / Saturate / Rcp / Clamp 的复合表达式。被丢掉的那 12 个里包含
**field[4] BufferOffset=8(byte 32)Float3 = `Parameter(0) +
ComponentSwizzle(.xyz)`**,而 shader 的 `slotConst=2` 访问就在这里 → 没成
员 → rewriter 整 CB 退化为 `float4 Material_1_Tree_sway_softness[16]` 单数组。

UE 5.1.1 源码确认(`Engine/Source/Runtime/Engine/Public/Shader/Preshader.h:19-75` 与
`Engine/Source/Runtime/Engine/Private/Shader/Preshader.cpp:649-655`):
- `EPreshaderOpcode::Parameter`(=3) 后面跟 `uint16 ParameterIndex`,共
  3 字节。
- `EPreshaderOpcode::ComponentSwizzle`(=36 / `0x24`) 后面跟
  `uint8 NumElements, IndexR, IndexG, IndexB, IndexA`,共 6 字节。
- 所以 `Parameter(N) + ComponentSwizzle(.xyz)` = 3 + 6 = 9 字节(`030N00 24 03 00 01 02 ff`)。
- 一元 `Saturate`(=25 / `0x19`)、`Rcp`(=22 / `0x16`)等 1 字节,所以
  `Parameter(N) + Saturate` = 3 + 1 = 4 字节。

#### 修法

`ReadMaterialConstantBuffer` 改为:
1. **总是**用 `field[FieldIndex].BufferOffset * 4` 与 `field[FieldIndex].Type`
   作为槽位的权威 `(byteOffset, type)` — 这两个数无论 preshader 多复杂都
   一定正确(它们是 GPU 端实际写入的位置和宽度,UE 在 cook 时已固定)。
2. 命名**只在能闭合解码时**用参数名,否则匿名 — **诚实规则**: 一个 slot
   的名字必须能由 opcode 流的**每一个 byte**解释成"runtime VM 写到这个
   字节偏移的精确表达式",才允许写出来。任何 byte 没账(未识别 opcode、
   多步表达式、二元算术、多 Parameter 拉取),整槽**降级为 `f_<byteOffset>`
   匿名**。这样任何打印出来的名字所描述的 runtime 字节内容都可以由 UE 5.1
   公开源码的语义**逐字节复现** — 不靠猜。
   - **`Parameter(N)` 裸 (size 3)** → `parameters[N].Name`。slot 字节 ==
     参数值。
   - **`Parameter(N) + ComponentSwizzle(NumE,R,G,B,A)` (size 3+6=9)** →
     `<paramName>_<xyzw...>` (e.g. `SelectionColor_xyz`、`SelectionColor_w`)。
     按 `Preshader.cpp:649-655` 的 swizzle 语义,slot 的 4 个 component
     位置上是 `Param.[indices[i]]` 的对应分量值,共 NumE 个。
   - **`Parameter(N) + UnaryOpInPlace` (size 3+1=4)** → `<paramName>_<op>`
     (e.g. `Roughness_dullness_sat`、`Sway_resistance_rcp`)。覆盖
     `Rcp/Saturate/Abs/Floor/Ceil/Round/Trunc/Sign/Frac/Fractional/Neg`。
   - **任何其它形态**(Constants 入栈、Clamp、Append、Add/Sub/Mul/Div、
     Less/Greater 等 binary 比较、Cross、Dot、TextureSize、Min/Max 等等) →
     `f_<byteOffset>` 完全匿名。这些是材质 HLSL translator 在该 byte 处
     物化的**派生值**,我们不在材质参数表里能找到对应符号,所以不命名。
3. byte offset + type 来自 `UniformPreshaderFields[i].BufferOffset/Type`
   绝对真理 — 无论 opcode 多复杂,GPU 实际写入位置和宽度都是这两个数。

`IsSingleParameterWrite` 被删除(已无调用方)。`DerivePreshaderName()` +
`SwizzleSuffix()` helper 只用 `Preshader.h` 公开的 opcode 编号
(Parameter=3, Rcp=22, Saturate=25, Abs=26, Floor=27, Ceil=28, Round=29,
Trunc=30, Sign=31, Frac=32, Fractional=33, ComponentSwizzle=36, Neg=45),
不依赖任何引擎特定值或硬编码。

#### 验证

| Fixture | Before | After |
| --- | --- | --- |
| **`M_Bamboo_tree_PS_1904.Material`** | `float4 Material_1_SelectionColor[16]`(rewriter 退化为单数组,因为 byte 32 metadata 缺成员) | **22 成员**全部 packoffset 正确(21 个由 opcode 流闭合解码命名:11 裸 Parameter + swizzled `_xyz`/`_w` 视图 + unary `_sat`/`_rcp` 视图 + 1 匿名 `f_20`(Clamp 表达式不闭合))✅ |
| `M_Bamboo_tree_PS_1904.rewrite.txt` | `[Material] rewrite validation failed: unsupported access translation for resultId=273 slotConst=2 ...` | `[Material] rewrite planned with 22 members` ✅ |
| EndField blob1 / blob2 (Unity) | 5 / 6 + 5 名字 | 5 / 6 + 5 不退化 ✅ |
| Deferred Clustered blob27 (Unity) | 10 + 4 + 1 + 1 + 10 名字 | 10 + 4 + 1 + 1 + 10 不退化 ✅ |

`M_Bamboo_tree_PS_1904.hlsl` Material CB 实际产物(每名字都可由 opcode 流
逐字节复现):

```hlsl
cbuffer Material : register(b3)
{
    float4 Material_1_SelectionColor             : packoffset(c0);    // Parameter(0)
    float  Material_1_0_Normal_intensity         : packoffset(c1);    // Parameter(1)
    float  Material_1_f_20                       : packoffset(c1.y);  // Parameter(1) + Const0 + Const4 + Clamp -- 不闭合
    float  Material_1_SelectionColor_w           : packoffset(c1.z);  // Parameter(0) + ComponentSwizzle(NumE=1, R=3)
    float3 Material_1_SelectionColor_xyz         : packoffset(c2);    // Parameter(0) + ComponentSwizzle(NumE=3, R=0,G=1,B=2)
    float4 Material_1_Bamboo_base_dark_tones     : packoffset(c3);    // Parameter(2)
    float4 Material_1_Bamboo_base_mid_tones      : packoffset(c4);    // Parameter(3)
    float3 Material_1_Bamboo_base_mid_tones_xyz  : packoffset(c5);    // Parameter(3) + ComponentSwizzle(.xyz)
    float3 Material_1_Bamboo_base_dark_tones_xyz : packoffset(c6);    // Parameter(2) + ComponentSwizzle(.xyz)
    float4 Material_1_Bamboo_base_light_tones    : packoffset(c7);    // Parameter(4)
    float3 Material_1_Bamboo_base_light_tones_xyz: packoffset(c8);    // Parameter(4) + ComponentSwizzle(.xyz)
    float4 Material_1_Bamboo_dark_tones          : packoffset(c9);    // Parameter(5)
    float4 Material_1_Bamboo_mid_tones           : packoffset(c10);   // Parameter(6)
    float3 Material_1_Bamboo_mid_tones_xyz       : packoffset(c11);   // Parameter(6) + ComponentSwizzle(.xyz)
    float3 Material_1_Bamboo_dark_tones_xyz      : packoffset(c12);   // Parameter(5) + ComponentSwizzle(.xyz)
    float4 Material_1_Bamboo_light_tones         : packoffset(c13);   // Parameter(7)
    float3 Material_1_Bamboo_light_tones_xyz     : packoffset(c14);   // Parameter(7) + ComponentSwizzle(.xyz)
    float  Material_1_Roughness_dullness         : packoffset(c14.w); // Parameter(8)
    float  Material_1_Roughness_dullness_sat     : packoffset(c15);   // Parameter(8) + Saturate
    float  Material_1_Sway_resistance            : packoffset(c15.y); // Parameter(9)
    float  Material_1_Sway_resistance_rcp        : packoffset(c15.z); // Parameter(9) + Rcp
    float  Material_1_Tree_sway_offset           : packoffset(c15.w); // Parameter(10)
};
```

每个名字都对应一段**完全闭合**的 opcode 流:opcode bytes 加 payload bytes
等于 preshader 的 OpcodeSize,无残余 byte。`Material_1_f_20` 是唯一不闭合
案例 — 那段 16 byte preshader 是 `Parameter(1) + Const(Float1=0) +
Const(Float1=4) + Clamp`,Clamp 是 ternary,我们不在 reader 里实现表达式
闭合,所以名字降级匿名。

byte offset + type 全部从 `UniformPreshaderFields[i].BufferOffset/Type` 来,
不靠任何硬编码。

`Tree_sway_softness`(param 11,byte 256, c16)未出现是因为该 shader 的
SPV flat 数组覆盖范围是 16 个 float4(到 byte 256),不会读到 c16 —
是 SPV reflection 的正确表现,不是 reader bug。

#### 已闭合的旧遗留

- §1.1 / §1.3 / §9.9.1 — `M_Bamboo_tree_PS_1904.Material` 在 byte 32 缺
  成员的根因找到并修复。`10.Normal intensity` 的 "`10.`" 前缀确认是
  材质设计师起的真实名字(在 `M_Bamboo_tree.json` 里就是这样存的),不是
  reader 污染,无需"修"。

### 9.10 It. 13 — Material UB resource layout 修复匹配 UE 5.1 源码

回应用户对**贴图名字还原 bindings**的研究请求。

#### 旧 layout 的偏差(以 UE 5.1 源码为准)

`MaterialUniformBufferLayout.cs` 旧版的 `BuildResourceMemberNames` 与
`Engine/Source/Runtime/Engine/Private/Materials/MaterialUniformExpressions.cpp:341-503`
有四处偏差:

1. **Wrap_WorldGroupSettings + Clamp_WorldGroupSettings 缺失** — 这两个
   sampler 由 `CreateBufferStruct` **无条件**追加在所有 typed 段之后(line
   499-503),旧 layout 完全没生成 → SRT 命中这两个尾部 sampler 时
   `ResolveResourceName` 返回 null,降级为 `<UB>_Sampler<index>` placeholder。
2. **VTStack 页表段错位** — UE 把 VTStack 的 PageTable0/PageTable1/
   Indirection 三个 TEXTURE 槽放在 ExternalTexture 之后、Virtual physical 之前
   (line 473-486),旧 layout 把它放最后并简化为只 SRV(误以为是
   `VirtualTexturePhysical` 的副产物)。
3. **VTStack 计数维度错** — 旧 `VirtualPageTable` 计数复用了
   `VirtualPhysical`(`UniformTextureParameters[Virtual]` 的长度),但 UE 的
   VTStack 页表数 = `VTStacks.Num()`,且每个 stack 的 `PageTable1` 是否输
   出取决于 `Stack.NumLayers > 4`。两者完全独立。
4. **`UBMT_SRV` vs `UBMT_TEXTURE` 类型语义** — `VirtualTexturePhysical_<i>`
   是 `UBMT_SRV`(line 493)而非 TEXTURE,UE 注释解释为支持 sRGB / non-sRGB
   的别名访问。旧 layout 把它当作 TEXTURE 段(虽然命名仍正确,但下游
   register class 推断会偏)。

#### 修法

`Source/Ruri.ShaderDecompiler/Unreal/MaterialUniformBufferLayout.cs` 重写
`BuildResourceMemberNames`,严格按 `CreateBufferStruct()` line 419-503 的
顺序铺出每段 resource 名。`MaterialResourceCounts` 加
`VirtualTextureStackLayerCounts: IReadOnlyList<int>?` 字段(每个 VTStack
的 `NumLayers`,gates `PageTable1_<i>` 的输出),旧的 `VirtualPhysical` /
`VirtualPageTable` 二选一被替换为正交的 `Virtual` + `VirtualTextureStackLayerCounts`。

`Source/Ruri.ShaderDecompiler/Unreal/UeShaderSymbolInputsReader.cs::ReadMaterialResourceCounts`
新加 `ReadVirtualTextureStackNumLayers` helper,从
`UniformExpressionSet.VTStacks[i]` 提取 `NumLayers`(优先读 `NumLayers`
字段,降级为对 `LayerUniformExpressionIndices` 数 `>= 0` 的元素 — UE 用
INDEX_NONE 表示未占用 layer)。

#### 验证

- `M_Bamboo_tree_PS_1904`: Material 的 `UniformBufferLayoutInitializer.Resources[]`
  长度 = 6(2 Standard2D × (TEX+SAMPLER) + Wrap + Clamp = 4 + 2 = 6)。旧
  layout 只产 4 个名字,SRT 命中 ResourceIndex=3 时返回
  `Material_Texture2D_1Sampler` ✓。修后产 6 个名字,Resources[] 长度对账
  完全一致。SRT `Material[3] = sampler bind=4` → `Material_Texture2D_1Sampler`
  (即 "Bamboo base maps" 的 sampler)。
- Unity 回归(EndField blob1/blob2)无变化 — 该路径仅 UE 端 Material UB
  使用,Unity 不走。

#### `M_Bamboo_tree_PS_1904` 的 binding 名字现状(每槽来源)

```
register(t0..t10):
  t2  = Buffer<uint4> View_SRV45     <- SRT, View UB[45], engine UB placeholder ✓
  rest = Texture2D/3D T<n>            <- loose params (FShaderParameterBindings.ResourceParameters[])
                                          frozen image 只剩 (ByteOffset, BaseIndex, BaseType),
                                          名字源 FShaderParameterMap 在 cook 时已 drop -> closed world

register(s0..s4):
  s0 = sampler_0       <- loose, closed world
  s1 = sampler_1       <- SRT, View[39] -> placeholder "View_Sampler39" 但 sampler 名字目前在
                          ShaderSymbolData.EnumerateResourceBindings 被硬编码为 "sampler_<index>",
                          resolved name 不进 HLSL(设计选择,可改)
  s2 = sampler_2       <- SRT, OpaqueBasePass[43] -> 同上
  s3 = sampler_3       <- SRT, IndirectLightingCache[3] -> 同上
  s4 = sampler_4       <- SRT, Material[3] = "Texture2D_1Sampler"(本 fixture 唯一 Mechanism 1
                          可还原槽位 -> 当前显示 sampler_4,可改为 Material_Texture2D_1Sampler)
```

—— 16 个 binding 槽里**只有 1 个** (`sampler_4`) 的源是材质 UB SRT,其余
全部是 engine UB SRT(closed-world 上限)或 loose params(closed-world 上
限)。这与 UE 5.1 D3D shipping cook 的典型 Material PS 模式一致:大多数
texture 走 loose,SRT 上 Material 的 binding 极少。

#### Closed-world 三机制总结(详 `UE_TEXTURE_BINDING_TRUTH.md`)

1. **SRT-bound via Material UB** → `CreateBufferStruct()` 重放,**可还原**。
2. **SRT-bound via engine UB** (View / OpaqueBasePass / …) → 引擎 C++ 源
   码 macro 闭合,cooked 不存,**不可还原** → placeholder
   `<UBName>_<RegClass><ResIdx>`。
3. **Loose params** (`FShaderParameterBindings.ResourceParameters[]`) →
   per-shader-class C++ 参数 struct,frozen 后 `(ByteOffset, BaseIndex,
   BaseType)` 三元组无名,**不可还原** → spirv-cross `T<n>`。

唯一未做的可改进项:Mechanism 1 的 sampler 名字目前被
`ShaderSymbolData.EnumerateResourceBindings` 重打成 `sampler_<index>`,
resolved name 没流到 HLSL — 是设计选择,等用户拍。

### 9.11 当前轮遗留(open in v14)

1. **EndField blob1 `ShaderVariablesGlobal` 6 → 5 layout 缩水** — §9.9 的
   旧 §2 项;blob2 同 metadata 是 6 个,差异在 SPIR-V access。需要确认是
   不是 `BuildStructuredLayout` 看了 `flatBuffer.ArrayLength`,而 vertex
   shader 的 SPIR-V flat 数组比 fragment 的短(只覆盖访问到的范围),导致
   `maxAvailableByteOffset = ArrayLength * 16` 把 byte 1728 排除。
2. **EndField blob1 `UnityInstancing_SRP_UnityPerDraw` 仍然 rewrite
   validation failed** — `unsupported access translation for resultId=255
   slotConst=1 slotDynamic=248 stride=16 op=65`。CLAUDE.md §2.2 称该 fixture
   "已正确演示 [256u] 数组上的动态索引",但当前 rewrite log 显示失败 —
   需要核对是不是历史 baseline 从来就没真正成功(只是 HLSL 退化为
   `UnityPerDrawArray ... [256u]` 单数组的形态被认为是"对",而 rewrite
   plan 实际上失败了)。本轮**不**因这条改 SPIR-V 阶段任何东西(否则就是
   refactor),先记录。
3. **Multi-field preshader 暂未支持** — UE preshader 可以 `NumFields > 1`
   写到多个 field 槽(struct 输出)。当前 reader 仍跳过这类。Material CB
   罕见使用,但其它 UB 可能有。先记录。

---

## 10. Loop discipline

- 每轮 ONE focused improvement,别 refactor。
- 每轮跑 §7 / §8 验证,把数字写到 §9.2 表格新增一行。
- 失败时:把失败摘要写到 §9.3,转方向,不要静默扩大范围。
- 不动 Unity 端导出器或别的游戏 hook。
- Auto mode 开,但破坏性操作(改 SPIR-V patcher、删导出、force-push)必须
  先问。

---

## 11. README(原 README.md 全部内容)

**Ruri.ShaderDecompiler** 是一个通用的 Shader 反编译库,用于将编译后的
Shader 二进制还原为**高可读性的 HLSL 代码**。

项目核心目标是解决 Shader 反编译中**变量名丢失**的问题,通过**跨引擎通
用方案**,重建**符号信息(Symbols)**与**字节码逻辑(Bytecode)**之间的
关联。

> ✅️ 项目状态: 基本中间层和符号注入已完成,只需在引擎端构建 metadata 传
> 入即可带符号反编译。

### Roadmap

- [ ] 完善 Unity 支持 — 当前已实现 UE 元数据解析;Unity 已初步支持但还
  没有实现直接生成为 ShaderLab。
- [ ] 统一反编译输出为 ShaderLab。
- [ ] SPIR-V → spirv-cross 出的"狗屎 HLSL" → 重新编译到 DXBC 让编译器
  优化指令数量 → 重新反编译回更可读 HLSL。

### 核心原理

1. **Shader 二进制并未"销毁"语义信息** — GPU 端 Shader Binary
   (DXBC/DXIL/SPIR-V)只是为性能移除了符号信息,留下寄存器与槽位绑定;
   不是数据不可逆丢失。原理上 DXBC 也支持符号注入,但没有稳定偏官方性
   质的反编译器,所以放弃。
2. **引擎运行时必然保留符号映射** — 无论 Unity / UE,为支持 CPU 侧按变
   量名设置参数,引擎运行时必须保留 `变量名 ↔ 绑定槽位` 映射。
3. **本库工作本质** — 不依赖猜测或模式匹配:解析引擎侧元数据(Unity
   Bindings / UE SRT)→ 把符号信息重新注入 SPIR-V → 重建可读、可维护的
   高级 Shader 代码。本质是符号与逻辑的重组过程。

### 当前特性

1. **统一中间层(SPIR-V)** — 无论输入是 DXBC / SPIR-V / DXIL 都统一转
   到 SPIR-V。DXIL 路线已可进入结构化恢复流程,但其设计会导致 CB 表达
   趋于平坦化,当前主要问题集中在结构还原精度与命名收敛(即 §1 的
   bug),并非不可实现。
2. Unity 端由 AssetRipper 解析并填充 metadata 即可完美还原符号反编译
   (在私有仓库完成)。
3. UE 端由 CUE4Parse 解析并填充 metadata,但 UE 剔除过于严重(§4 矩阵
   的 closed-world 上限)+ 屎山代码,优先级偏低。
