# UE Shader Symbol Mapping Research

## Scope
本文件只记录两类内容：

1. 已经由 UE 源码确认的结构语义和生成路径
2. 已经由游戏导出数据确认存在的真理数据

不记录猜测性恢复方案，不记录基于硬编码布局的结论。

## Ground Rules
- 反编译最终符号恢复只能基于游戏数据真理，或明确属于引擎全局统一定义的源码真理
- 源码的用途是定位真理数据保存在什么结构、什么字段、什么生成路径中
- 源码不能替代游戏里真实保存的材质/collection/preshader 数据去直接恢复非全局符号

## Current Target
当前重点分析四类 CB 符号来源：

- `View`
- `OpaqueBasePass`
- `MaterialCollection0`
- `Material`

以及它们的成员符号是否能被严格恢复。

## Global Uniform Buffer Truth
以下属于引擎全局统一定义，可直接使用源码真理。

### `View`
源码注册：

```cpp
IMPLEMENT_STATIC_AND_SHADER_UNIFORM_BUFFER_STRUCT(FViewUniformShaderParameters, "View", View);
```

文件：
- `Engine/Source/Runtime/Engine/Private/SceneView.cpp:49`

结论：
- `View` 作为 CB 名称属于引擎全局统一定义

### `OpaqueBasePass`
源码注册：

```cpp
IMPLEMENT_STATIC_UNIFORM_BUFFER_STRUCT(FOpaqueBasePassUniformParameters, "OpaqueBasePass", SceneTextures);
```

文件：
- `Engine/Source/Runtime/Renderer/Private/BasePassRendering.cpp:96`

结论：
- `OpaqueBasePass` 作为 CB 名称属于引擎全局统一定义

## Material Parameter Collection Truth
`MaterialCollectionN` 不能拍脑袋恢复，必须先闭合游戏资产链。

### Source Meaning
源码确认 `FUniformExpressionSet` 中保存 collection 的 `StateId` 列表：

```cpp
void FUniformExpressionSet::SetParameterCollections(const TArray<UMaterialParameterCollection*>& InCollections)
{
    ParameterCollections.Empty(InCollections.Num());
    for (int32 CollectionIndex = 0; CollectionIndex < InCollections.Num(); CollectionIndex++)
    {
        ParameterCollections.Add(InCollections[CollectionIndex]->StateId);
    }
}
```

文件：
- `Engine/Source/Runtime/Engine/Private/Materials/MaterialUniformExpressions.cpp:331-339`

结论：
- `FUniformExpressionSet.ParameterCollections[i]` 的语义就是第 `i` 个 `UMaterialParameterCollection` 的 `StateId`

### Game Data Truth Chain
当前样例 `MI_Cliff_small_ground_level` / `M_Cliffs` 已确认以下闭环：

1. `UniformExpressionSet.ParameterCollections`
   - 保存 GUID
2. `ParameterCollectionInfos`
   - 用同一个 `StateId` 指向具体 collection 资产
3. `MaterialParameterCollection` 资产 JSON
   - 保存完整参数树：`ScalarParameters` / `VectorParameters`

样例已确认：
- `B5E511F4-423C469C-F5123096-934949E1`
  -> `Level_material_parameters`

样例文件：
- `Content/Oni_Project/Materials/MI_Cliff_small_ground_level.json`
- `Content/Oni_Project/Materials/M_Cliffs.json`
- `Content/Oni_Project/Materials/Material_functions/Level_material_parameters.json`

结论：
- collection 身份树和参数树都在游戏数据里
- `MaterialCollection0` 是否严格等于 `ParameterCollections[0]` 仍需额外证据闭合
- 当前样例只有一个 collection，所以 `MaterialCollection0` 对应唯一候选，但按最高标准仍应视为“强支持，未完全闭合”

### Current Runtime Analysis Status
- 离线分析输出现在会显式打印三层 collection 候选来源：
  - `MaterialChain.ParameterCollectionInfos`
  - `ScannedFromChain.ParameterCollectionInfos`
  - `ScannedFromExportRoot.ParameterCollectionInfos`
- 这些输出的目的仅是证明 `StateId -> collection asset path` 的数据链是否闭合
- 这些输出不代表已经证明 `MaterialCollectionN` 与 `ParameterCollections[index]` 的编号对应关系

## Material Uniform Buffer Truth
`Material` 不能再用源码直接重建字段树。必须区分三类真理数据：

1. 布局树
2. 参数名字树
3. preshader 写入桥

### Source Generation of `Material`
源码中 `FUniformExpressionSet::CreateBufferStruct()` 负责生成 `Material` uniform buffer 的布局。

关键代码：

```cpp
new(Members) FShaderParametersMetadata::FMember(TEXT("PreshaderBuffer"), ... , UniformPreshaderBufferSize, NULL);
...
UniformBufferLayoutInitializer = FRHIUniformBufferLayoutInitializer(TEXT("Material"));
...
FShaderParametersMetadata(..., TEXT("Material"), TEXT("MaterialUniforms"), TEXT("Material"), ...)
```

文件：
- `Engine/Source/Runtime/Engine/Private/Materials/MaterialUniformExpressions.cpp:341-523`

结论：
- `Material` 作为 CB 名是固定生成结果
- 但非全局字段恢复仍必须依赖游戏实际保存的数据，不可直接凭源码布局下结论

### Game Data: Layout Tree
游戏导出数据中已经保存 `Material` 的布局树：

- `UniformExpressionSet.UniformBufferLayoutInitializer`
  - `Name`
  - `Resources[]`
    - `MemberOffset`
    - `MemberType`
  - `ConstantBufferSize`

这在样例中已经实际存在：
- `MI_Cliff_small_ground_level.json`
- `M_Cliffs.json`
- `UnifiedShaderMetadata.json`

结论：
- `Material` 的资源布局树已经是游戏数据真理
- `UniformBufferLayoutInitializer.ConstantBufferSize` 也是游戏数据真理，可直接作为该 `Material` constant buffer 的已用大小

### Game Data: Parameter Name Tree
当前已确认两个名字源：

#### 1. `RuntimeEntries[*].ParameterInfoSet`
`UMaterialInterface` 对缓存数据的解释逻辑：

```csharp
runtimeEntries[0].ParameterInfoSet -> ScalarValues
runtimeEntries[1].ParameterInfoSet -> VectorValues
runtimeEntries[3].ParameterInfoSet -> TextureValues
```

文件：
- `FModel/CUE4Parse/CUE4Parse/UE4/Assets/Exports/Material/UMaterialInterface.cs:140-168`

结论：
- `RuntimeEntries.ParameterInfoSet` 是材质参数名字树与值数组的对应表
- 它不是 `Material` buffer 字段表本身

#### 2. `FrozenArchive.ScriptNames`
统一导出中已经能看到大量真实材质参数名，例如：

- `AO input`
- `Curvature input`
- `Normal input`
- `Specular detail`
- `Specular softness`
- `Roughness dullness`
- `UV Scale Near`

结论：
- frozen archive 中保存了真实 script name 集合
- 每个名字还有 `Patches`，说明它们对应 memory image 中的实际写入点

### Preshader Bridge
这是当前 `Material` 成员恢复最关键的桥。

#### `FUniformExpressionSet`
当前 UE 5.x 中 `FUniformExpressionSet` 同时保存：

- `UniformNumericParameters`
- `UniformTextureParameters`
- `UniformExternalTextureParameters`
- `UniformTextureCollectionParameters`
- `UniformPreshaders`
- `UniformPreshaderFields`
- `UniformPreshaderData`
- `ParameterCollections`
- `UniformBufferLayoutInitializer`

参考：
- `FModel/CUE4Parse/CUE4Parse/UE4/Assets/Exports/Material/MaterialResourceTypes.cs:556-577`

#### `FMaterialUniformPreshaderField`
当前结构语义：

```csharp
public struct FMaterialUniformPreshaderField
{
    public uint BufferOffset, ComponentIndex;
    public EShaderValueType Type;
}
```

文件：
- `FModel/CUE4Parse/CUE4Parse/UE4/Assets/Exports/Material/MaterialResourceTypes.cs:837-842`

结论：
- 这是 preshader 结果写入 `Material` buffer 的偏移桥

#### `FMaterialUniformPreshaderHeader`
当前已确认字段：

- `OpcodeOffset`
- `OpcodeSize`
- UE5.1 变体还包含：
  - `FieldIndex`
  - `NumFields`

文件：
- `FModel/CUE4Parse/CUE4Parse/UE4/Assets/Exports/Material/MaterialResourceTypes.cs:782-834`

结论：
- 它定义了每段 preshader opcode 作用于哪些 field

#### `FillUniformBuffer()` 消费逻辑
源码明确说明：

1. 遍历 `UniformPreshaders`
2. 用 `UniformPreshaderData` 执行 `EvaluatePreshader()`
3. 用 `UniformPreshaderFields` 将结果写进 `PreshaderBuffer`
4. 写入位置由 `BufferOffset` 决定

文件：
- `Engine/Source/Runtime/Engine/Private/Materials/MaterialUniformExpressions.cpp:748-880`

结论：
- `UniformPreshaders` + `UniformPreshaderFields` + `UniformPreshaderData` 是 `Material` 的真实写入桥

## Shader Binding Truth

### Source Structure And Runtime Use
源码确认 `FShader` frozen 内容里正式保存两份不同语义的 binding 数据：

- `Bindings : FShaderParameterBindings`
- `ParameterMapInfo : FShaderParameterMapInfo`

文件：
- `Engine/Source/Runtime/RenderCore/Public/Shader.h:721-776`
- `Engine/Source/Runtime/RenderCore/Public/Shader.h:943-947`

其中：

- `FShaderParameterBindings.ResourceParameters[*] = { ByteOffset, BaseIndex, BaseType }`
- `FShaderParameterBindings.BindlessResourceParameters[*] = { ByteOffset, GlobalConstantOffset, BaseType }`
- `FShaderParameterBindings.GraphUniformBuffers[*] = { BufferIndex, ByteOffset }`
- `FShaderParameterBindings.ParameterReferences[*] = { BufferIndex, ByteOffset }`

源码运行时消费路径：

- `SetShaderParameters(..., const FShaderParameterBindings& Bindings, ...)`
- 对 `Bindings.ResourceParameters` 逐项调用 RHI 绑定
- 绑定时直接使用 `BaseIndex`
- `ByteOffset` 用于从参数结构体里读取对应资源句柄

文件：
- `Engine/Source/Runtime/RenderCore/Public/ShaderParameterStruct.h:171-208`
- `Engine/Source/Runtime/RenderCore/Private/ShaderParameterStruct.cpp:507-568`

结论：
- `FShaderParameterBindings` 是运行时实际使用的静态 binding 结构
- 它比 `ParameterMapInfo` 更接近“最终会绑定到哪个槽位”的运行时真理
- 但它仍然不包含 compiler parameter name，如 `Material_Texture2D_0`

### Source Generation Path
源码确认 `FShaderParameterBindings` 不是独立反射名字表，而是由 `FShaderParameterMap` 配合 shader parameter struct 元数据生成：

- `FShaderParameterBindings::BindForLegacyShaderParameters(...)`
- 内部 `FShaderParameterStructBindingContext::Bind(...)`
- `ParametersMap->FindParameterAllocation(ElementShaderBindingName)`
- 命中后写入 `Bindings.Parameters / ResourceParameters / GraphUniformBuffers / ParameterReferences`

文件：
- `Engine/Source/Runtime/RenderCore/Private/ShaderParameterStruct.cpp:35-239`
- `Engine/Source/Runtime/RenderCore/Private/ShaderParameterStruct.cpp:242-320`

结论：
- `Bindings` 的上游仍然是 compile-time `FShaderParameterMap.ParameterMap`
- 但 frozen/cooked 最终持久化的是剥离名字后的 `Bindings`
- 因而 `Bindings` 可证明运行时确实静态持有 slot 级 binding map
- 但单靠 `Bindings` 仍无法反推出具体是哪个 compiler parameter name 占了该 `BaseIndex`

### Game Data Truth
CUE4Parse 当前已经按 frozen `FShader` 正式布局读取：

- `FShader.Bindings`
- `FShader.ParameterMapInfo`

文件：
- `FModel/CUE4Parse/CUE4Parse/UE4/Assets/Exports/Material/MaterialResourceTypes.cs:185-240`

结论：
- `Bindings` 不是只存在源码里的概念，游戏 cooked/frozen 数据中确实保存了它
- 当前工具应优先把样例 shader-side `Bindings` 明细导出出来，再判断样例上是否存在可继续闭合 `Material_Texture2D_k -> Tn/Sn/Un` 的桥

### Current Unclosed Point
当前已经闭合：

- `Material.Texture2D_k / Material.VirtualTexturePhysical_k`
- `FShaderParameterMap.ParameterMap` 会生成运行时 `FShaderParameterBindings`
- `FShaderParameterBindings` 会在 cooked/frozen 数据中持久化

当前仍未闭合：

- `Bindings.ResourceParameters.BaseIndex`
  -> 对应哪一个 compiler parameter name
- compiler parameter name
  -> final `Tn / Sn / Un`

删除旧近似结论：

- 不能再说“当前样例 shader-side 只有 `ParameterMapInfo` 可看”
- 现在已确认还应看 `FShader.Bindings`
- 但也不能把 `Bindings.BaseIndex` 直接命名成某个 `Material_Texture2D_k`，除非游戏数据里再找到名字桥
- 不能硬编码任意项目导出根前缀去做材质路径归一化；路径桥只能依据传入参数与数据中实际存在的 `Content/...` / 对象路径形态归一化

### `FMaterialPreshaderData`
真实结构名：`UE::Shader::FPreshaderData`

源码：
- `Engine/Source/Runtime/Engine/Public/Shader/Preshader.h:92-149`

保存内容：
- `Names`
- `StructTypes`
- `StructComponentTypes`
- `Data`

对应导出字段：
- `Names`
- `NamesOffset`（旧版本资产）
- `StructTypes`
- `StructComponentTypes`
- `Data`

结论：
- 这不是“直接成员名表”
- 它是 preshader opcode / 类型 / 名字引用数据本体

### Where Parameter Names Enter Preshader Data
源码确认 `FPreshaderData` 支持写入 `FHashedMaterialParameterInfo`：

```cpp
template<>
FPreshaderData& Write<FHashedMaterialParameterInfo>(const FHashedMaterialParameterInfo& Value)
{
    return Write(Value.Name).Write(Value.Index).Write(Value.Association);
}
```

文件：
- `Engine/Source/Runtime/Engine/Public/Shader/Preshader.h:140-142`

读取时：

```cpp
template<>
FHashedMaterialParameterInfo ReadPreshaderValue<FHashedMaterialParameterInfo>(...)
```

文件：
- `Engine/Source/Runtime/Engine/Private/Shader/Preshader.cpp:311-317`

结论：
- preshader data 确实可以携带参数身份，不只是数值字节码

### `EPreshaderOpcode::Parameter`
源码枚举值顺序中 `Parameter` 位于：

```cpp
ConstantZero = 1,
Constant = 2,
Parameter = 3,
```

文件：
- `Engine/Source/Runtime/Engine/Public/Shader/Preshader.h:19-75`

运行时消费：

```cpp
case EPreshaderOpcode::Parameter:
    EvaluateParameter(Stack, UniformExpressionSet, ReadPreshaderValue<uint16>(Data), Context);
```

文件：
- `Engine/Source/Runtime/Engine/Private/Shader/Preshader.cpp:835-845`

`EvaluateParameter()` 再用 `ParameterIndex` 去 `UniformExpressionSet->GetNumericParameter(ParameterIndex)`。

文件：
- `Engine/Source/Runtime/Engine/Private/Shader/Preshader.cpp:420-459`

结论：
- 如果 bytecode 中存在 `Parameter` opcode，就能建立：
  `ParameterIndex -> UniformNumericParameters[ParameterIndex] -> ParameterInfo`

## Current Exporter Truth
当前统一导出已经显式导出了以下桥字段：

- `MaterialShaderMapContent.UniformExpressionSet.UniformPreshaders`
- `MaterialShaderMapContent.UniformExpressionSet.UniformPreshaderFields`
- `MaterialShaderMapContent.UniformExpressionSet.UniformPreshaderData`
- `MaterialShaderMapContent.UniformExpressionSet.UniformNumericParameters`
- `MaterialShaderMapContent.UniformExpressionSet.UniformTextureParameters`
- `MaterialShaderMapContent.UniformExpressionSet.UniformExternalTextureParameters`
- `MaterialShaderMapContent.UniformExpressionSet.UniformTextureCollectionParameters`
- `MaterialShaderMapContent.UniformExpressionSet.ParameterCollections`
- `MaterialShaderMapContent.UniformExpressionSet.UniformBufferLayoutInitializer`
- `MemoryImageResult.ScriptNames`

导出位置：
- `Source/Ruri.FModelHook/Game/SBUE/ShaderDecompiler/UnifiedShaderMetadataExporter.cs`

当前统一导出包装层已对齐到以下源码语义：

- `MaterialInterfaces`
- `ShaderCodeArchives`
- `LoadedShaderMaps`
- `ShaderMapPointerTable`
- `MemoryImageResult`
- `MaterialShaderMapContent`

不再保留旧包装名：

- `Materials`
- `Libraries`
- `ShaderMaps`
- `PointerTable`
- `FrozenArchive`
- `Content`

## Shader Resource Binding Truth Chain

## Shader Map Identity Truth

### Source-aligned names
当前正式 metadata 中必须区分两种不同语义的 hash，不能再混用：

- `PackageShaderMapHashes`
  - 来源：package store entry / shader library side
  - 语义：库侧 shader map identity
- `CookedShaderMapIdHash`
  - 来源：`LoadedShaderMap.ShaderMapId.CookedShaderMapIdHash`
  - 语义：`FMaterialShaderMap` cooked shader map id

结论：
- `PackageShaderMapHashes` != `CookedShaderMapIdHash`
- 当前源码真理尚未闭合两者之间的桥
- 在桥闭合前，禁止把其中一者直接当作另一者使用

### Removed incorrect implementation
以下旧实现已确认错误并应删除：

- 用 package/store 侧 `ShaderMapHashes` 直接匹配 `LoadedShaderMap.ShaderMapIdHash`
- 用 library 侧 shader map hash 直接筛选材质 `LoadedMaterialResources[*].LoadedShaderMap`
- 在 sidecar stable truth 导出里，用 library shader map hash 去聚合 `LoadedShaderMaps[*]` 下的 shader truth

结论：
- 这类逻辑会把两套不同 hash 空间混成同一语义
- 必须删除，而不是保留兼容路径

### Source Truth: `FShaderParameterMap`
源码确认 shader 编译期资源分配先进入 `FShaderParameterMap`。

关键入口：

```cpp
void FShaderParameterMap::AddParameterAllocation(
    const TCHAR* ParameterName,
    uint16 BufferIndex,
    uint16 BaseIndex,
    uint16 Size,
    EShaderParameterType ParameterType)
```

文件：
- `Engine/Source/Runtime/RenderCore/Private/ShaderCore.cpp`

结论：
- `FShaderParameterMap` 保存的是编译后 shader parameter allocation 真值
- 其中至少包括：
  - `ParameterName`
  - `BufferIndex`
  - `BaseIndex`
  - `Size`
  - `ParameterType`
- 这一步仍然保留名字真理；名字键就是 `ParameterName`

进一步源码确认：

```cpp
ParameterMap.Add(ParameterName, FParameterAllocation(BufferIndex, BaseIndex, Size, ParameterType));
```

以及：

```cpp
TOptional<FParameterAllocation> FShaderParameterMap::FindParameterAllocation(const FString& ParameterName) const
```

文件：
- `Engine/Source/Runtime/RenderCore/Private/ShaderCore.cpp`

结论：
- 编译期真正的“资源符号表”是 `FShaderParameterMap.ParameterMap`
- 这里的键就是 shader compiler 产出的 `ParameterName`
- 如果某个资源名字在后续 cooked 数据中消失，它的最后明确名字真理位置就在这里

### Source Truth: cooked shader `ParameterMapInfo`
源码确认 `FShader::BuildParameterMapInfo(...)` 会从 `FShaderParameterMap` 固化出 cooked shader 保存的资源绑定表。

文件：
- `Engine/Source/Runtime/RenderCore/Private/Shader.cpp`

当前已确认该表包含：
- `UniformBuffers`
- `TextureSamplers`
- `SRVs`

结论：
- `ParameterMapInfo.TextureSamplers[*]` / `ParameterMapInfo.SRVs[*]` 比材质侧 `UniformTextureParameters` 更接近 shader 最终资源 binding 真理
- 这里保存的是 shader 编译结果资源分配，不是材质参数分类表
- 但这里已经不再保存 `ParameterName`

源码关键行为：

```cpp
else if (TMemoryImageArray<FShaderResourceParameterInfo>* ParameterInfoArray = GetResourceParameterMap(ParamValue.Type))
{
    ParameterInfoArray->Emplace(ParamValue.BaseIndex, ParamValue.BufferIndex, ParamValue.Type);
}
```

而 `FShaderResourceParameterInfo` 只包含：
- `BaseIndex`
- `BufferIndex`
- `Type`

文件：
- `Engine/Source/Runtime/RenderCore/Private/Shader.cpp`
- `Engine/Source/Runtime/RenderCore/Public/Shader.h`

结论：
- `BuildParameterMapInfo()` 从 `FShaderParameterMap` 投影到 cooked `ParameterMapInfo` 时，已经主动丢弃 `ParameterName`
- 因此仅靠 cooked `ParameterMapInfo.TextureSamplers` / `SRVs` 不能恢复资源名字真理，只能恢复 shader-side slot/type 真理

### Source Truth: `FShaderParameterBindings`
源码确认 `FShaderParameterBindings::BindForLegacyShaderParameters(...)` 会拿：

- `FShaderParameterMap`
- `FShaderParametersMetadata`

构建：

- `Parameters`
- `ResourceParameters`

文件：
- `Engine/Source/Runtime/RenderCore/Private/ShaderParameterStruct.cpp`

当前已确认 `ResourceParameters` 至少包含：
- `ByteOffset`
- `BaseIndex`
- `BaseType`

结论：
- `Bindings.ResourceParameters[].ByteOffset` 是 shader parameter struct 内真实成员偏移
- `Bindings.ResourceParameters[].BaseIndex` / `BaseType` 属于 shader 资源绑定真理链的一部分
- 成员恢复必须按真实 `ByteOffset` 解释，不能按 ordinal/index fallback
- 这一步同样不保留 `ParameterName`

源码关键行为：

```cpp
TOptional<FParameterAllocation> ParameterAllocation = ParametersMap->FindParameterAllocation(ElementShaderBindingName);
```

说明 `BindForLegacyShaderParameters()` 绑定时确实使用了完整 shader binding name 去查 `FShaderParameterMap`。

但最终写入 `FResourceParameter` 时只保留：

```cpp
Parameter.BaseIndex = (uint8)ParameterAllocation->BaseIndex;
Parameter.ByteOffset = ByteOffset + ArrayElementId * SHADER_PARAMETER_POINTER_ALIGNMENT;
Parameter.BaseType = BaseType;
```

文件：
- `Engine/Source/Runtime/RenderCore/Private/ShaderParameterStruct.cpp`

结论：
- `Bindings.ResourceParameters` 是“shader parameter struct 成员偏移 -> shader 资源槽/资源基类”的真值表
- 它不是名字表
- 真正名字只在查表那一刻存在于 `FShaderParameterMap`

### Source Truth: shader parameter struct member names are compile-time lookup keys
源码确认 `BindForLegacyShaderParameters()` 会从 `FShaderParametersMetadata` 递归生成 shader binding name：

- 普通成员：`MemberPrefix + Member.GetName()`
- 资源数组：`Name_0`, `Name_1`, ...
- `UBMT_REFERENCED_STRUCT` / `UBMT_RDG_UNIFORM_BUFFER`：改用 `GetShaderVariableName()`

然后用这个名字调用：

```cpp
ParametersMap->FindParameterAllocation(ElementShaderBindingName)
```

结论：
- 资源名字是否能恢复，关键取决于是否还能拿到编译期 `ElementShaderBindingName -> FParameterAllocation`
- 一旦只剩 cooked `ParameterMapInfo` / `Bindings.ResourceParameters`，就只剩 slot/type/byte offset，不再有名字本体

### Source Truth: `Material` texture resource member names are fixed by `CreateBufferStruct()`
源码确认 `FUniformExpressionSet::CreateBufferStruct()` 会为 `Material` uniform buffer 直接生成 texture/sampler 成员名：

```cpp
Texture2D_%d
Texture2D_%dSampler
TextureCube_%d
TextureCube_%dSampler
Texture2DArray_%d
Texture2DArray_%dSampler
TextureCubeArray_%d
TextureCubeArray_%dSampler
VolumeTexture_%d
VolumeTexture_%dSampler
ExternalTexture_%d
ExternalTexture_%dSampler
```

文件：
- `Engine/Source/Runtime/Engine/Private/Materials/MaterialUniformExpressions.cpp:370-470`

结论：
- `Material.Texture2D_k` / `Material.VolumeTexture_k` / `Material.ExternalTexture_k` 这组成员名不是猜测，而是源码固定生成
- 它们对应的 typed index 真理来自 `UniformTextureParameters[...]` / `UniformExternalTextureParameters` 在 `CreateBufferStruct()` 中的实际顺序
- 但这一步只闭合到 `Material` uniform buffer 成员名，不等于已经闭合到最终 `Tn/Sn/U0`

### Source Truth: translator emits `Material.<ResourceClass>_<TypedIndex>`
源码确认 `FHLSLMaterialTranslator::AccessUniformExpression()` 对 texture uniform expression 生成的访问文本是：

```cpp
FormattedCode.Appendf(TEXT("Material.%s_%u"), BaseName, TextureInputIndex);
```

其中 `BaseName` 已确认可取：
- `Texture2D`
- `TextureCube`
- `Texture2DArray`
- `TextureCubeArray`
- `VolumeTexture`
- `ExternalTexture`

文件：
- `Engine/Source/Runtime/Engine/Private/Materials/HLSLMaterialTranslator.cpp:3333-3373`

结论：
- 对 texture/external texture 来说，translator 侧真实中间资源名是 `Material.<ResourceClass>_<TypedIndex>`
- 这与 `CreateBufferStruct()` 生成的 `Material` 成员名在 `ResourceClass + TypedIndex` 维度上严格对齐
- 因此当前已闭合：
  - `ParameterName`
  - `TextureIndex`
  - `ReferencedTextures[TextureIndex]`
  - `Material.<ResourceClass>_<TypedIndex>`
- 当前尚未闭合：
- `Material.<ResourceClass>_<TypedIndex>`
- `compiler ParameterName`
- cooked `ParameterMapInfo` / `Bindings`
- final `Tn/Sn/U0`

### Source Truth: D3D preprocessing flattens `Material.` access to `Material_`
源码确认 D3D 编译前会执行 `RemoveUniformBuffersFromSource()`，把 uniform buffer 结构成员访问扁平化：

```cpp
// Replace all uniform buffer struct member references (View.WorldToClip) with a flattened name
// (View_WorldToClip)
void RemoveUniformBuffersFromSource(...)
```

关键行为：

```cpp
const FString& UniformBufferName = It.Key();
FString UniformBufferAccessString = UniformBufferName + TEXT(".");
...
SearchPtr[i] = MemberNameGlobal[i];
```

文件：
- `Engine/Source/Developer/ShaderCompilerCommon/Private/ShaderCompilerCommon.cpp:824-913`

结论：
- 对 D3D 路径，`Material.Texture2D_0` 会在编译前被扁平化为 `Material_Texture2D_0`
- 同理：
  - `Material.Texture2D_0Sampler` -> `Material_Texture2D_0Sampler`
  - `Material.VirtualTexturePhysical_2` -> `Material_VirtualTexturePhysical_2`
  - `Material.VirtualTexturePhysical_2Sampler` -> `Material_VirtualTexturePhysical_2Sampler`

### Source Truth: reflection `BindDesc.Name` is written directly into `Output.ParameterMap`
源码确认 D3D reflection 枚举 `ShaderDesc.BoundResources` 后，直接把 `BindDesc.Name` 写入 `Output.ParameterMap`：

```cpp
HandleReflectedShaderResource(FString(BindDesc.Name), BindDesc.BindPoint, Output);
HandleReflectedShaderSampler(FString(BindDesc.Name), BindDesc.BindPoint, Output);
HandleReflectedShaderUAV(FString(BindDesc.Name), BindDesc.BindPoint, Output);
```

而 `HandleReflected*` 的实现是：

```cpp
CompilerOutput.ParameterMap.AddParameterAllocation(
    *ResourceName,
    BindOffset,
    ReflectionSlot,
    BindCount,
    EShaderParameterType::SRV);
```

文件：
- `Engine/Source/Developer/Windows/ShaderFormatD3D/Private/D3DShaderCompiler.inl:427-519`
- `Engine/Source/Developer/ShaderCompilerCommon/Private/ShaderCompilerCommon.cpp:735-783`

结论：
- compiler 侧真正进入 `FShaderParameterMap.ParameterMap` 的资源名字就是 reflection 返回的 `BindDesc.Name`
- 对 D3D material-generated resources，这一步与 `RemoveUniformBuffersFromSource()` 组合后，强支持 compiler 名字形态为：
  - `Material_Texture2D_k`
  - `Material_Texture2D_kSampler`
  - `Material_VirtualTexturePhysical_k`
  - `Material_VirtualTexturePhysical_kSampler`

### Source Truth: cooked shader output does not serialize `Output.ParameterMap` names in the default path
源码确认 `GenerateFinalOutput()` 最终写入 cooked shader code 的是：

- `SRT`
- native shader bytecode
- optional data：
  - `FShaderCodePackedResourceCounts`
  - `FShaderCodeUniformBuffers`
  - callback 追加数据
  - `FShaderCodeVendorExtension`
  - `FShaderCodeName`（仅 `CFLAG_ExtraShaderData`）

但不会写入 `Output.ParameterMap` 本体。

文件：
- `Engine/Source/Developer/Windows/ShaderFormatD3D/Private/D3DShaderCompiler.inl:522-633`

补充确认：

```cpp
ParameterMap.UpdateHash(HashState);
```

说明 `ParameterMap` 参与 output hash，但这不是序列化名字表。

文件：
- `Engine/Source/Runtime/RenderCore/Private/ShaderCore.cpp:567-583`

结论：
- 在默认无符号 cooked 路径中，compiler `ParameterName` 名字表不会随 shader code 一起落盘
- 因此仅靠 cooked shader code / `ushaderlib` 默认载荷，不能直接取回完整 `Output.ParameterMap.ParameterMap`

### Game Data Landing: `MemoryImageResult` is already exported
当前统一导出已确认保存：

- `MemoryImageResult.ScriptNames`
- `MemoryImageResult.MinimalNames`
- 它们各自的 `Patches`
- `MemoryImageResult.FrozenObjectBase64`
- `ShaderMapPointerTable`

文件：
- `Source/Ruri.FModelHook/Game/SBUE/ShaderDecompiler/UnifiedShaderMetadataExporter.cs`
- `FModel/CUE4Parse/CUE4Parse/UE4/Assets/Exports/Material/MaterialResourceTypes.cs`

结论：
- `MemoryImageResult` 不是猜测性候选，而是当前游戏导出中已经存在的正式数据块
- unified metadata 现已把 `FrozenObject` 以 `FrozenObjectBase64` 形式导出，便于按字节偏移做样例研究
- 这仍然只是研究导出，不代表 `Patch.Offset -> 真实结构字段` 已经自动闭合

### Source Truth: `MemoryImageResult` name patches are consumed by `FMemoryImageArchive.ReadFName()`
`FShaderMapBase.Deserialize()` 先加载 `FrozenArchive`，然后把 `FrozenArchive.FrozenObject` 包装成 `FMemoryImageArchive`，并把 `FrozenArchive.GetNames()` 注入为 `Names`：

```csharp
FrozenArchive = new FMemoryImageResult();
FrozenArchive.LoadFromArchive(Ar, PointerTable);

Content.Deserialize(new FMemoryImageArchive(new FByteArchive("FShaderMapContent", FrozenArchive.FrozenObject, Ar.Versions))
{
    Names = FrozenArchive.GetNames()
});
```

文件：
- `FModel/CUE4Parse/CUE4Parse/UE4/Assets/Exports/Material/MaterialResourceTypes.cs:75-83`

`FMemoryImageResult.GetNames()` 只是把 `ScriptNames[*].Patches[*].Offset` / `MinimalNames[*].Patches[*].Offset` 组装成 `Offset -> (FName, bool)`：

```csharp
foreach (var name in ScriptNames)
{
    foreach (var patch in name.Patches)
    {
        names[patch.Offset] = (name.Name, true);
    }
}
```

文件：
- `FModel/CUE4Parse/CUE4Parse/UE4/Assets/Exports/Material/MaterialResourceTypes.cs:1190-1209`

`FMemoryImageArchive.ReadFName()` 读取 frozen object 时，会直接按当前位置查 `Names[(int)Position]`，命中后跳过 script/minimal name 对应大小并返回该 `FName`：

```csharp
if (Names != null && Names.TryGetValue((int) Position, out (FName name, bool bIsScriptName) name))
{
    Position += name.bIsScriptName ? 12 : 8;
    return name.name;
}
```

文件：
- `FModel/CUE4Parse/CUE4Parse/UE4/Readers/FMemoryImageArchive.cs:276-285`

结论：
- `MemoryImageResult.ScriptNames / MinimalNames / Patches` 的源码已确认用途是 frozen memory image 中 `FName` 位置的补丁表
- 这条链当前只足以证明“某个 frozen object 偏移位置存放了某个 `FName`”
- 它**不是**已证明的 shader resource binding 名字表
- 若要继续判断它是否能通向 `T/S/U`，必须再证明这些 `FName` patch 命中的具体字段属于 resource binding 相关结构，而不是普通参数名或其他名字字段

### Sample Proof: `MI_Cliff_small_ground_level` patches land on material parameter identity structs, not shader resource bindings
对样例 `MI_Cliff_small_ground_level`，当前 unified metadata 已导出 `MemoryImageResult.FrozenObjectBase64`，可直接按 `ScriptNames[*].Patches[*]` 观察 frozen object 字节落点。

样例实测：

- 一组 numeric 参数名 patch offset 为：
  - `10328, 10356, 10384, ... , 10720`
- 它们严格按 `28` 字节步长递增
- 这与 `FMaterialNumericParameterInfo` 的 `SavedLayoutSize = 28` 完全吻合
- 对应名字正是样例已知 numeric 参数：
  - `SelectionColor`
  - `UV Scale Near`
  - `Normal blend`
  - `Blend Sharpness (S)`
  - `Blend Bias (S)`
  - `RVT Height blend`
  - `RVT blend falloff`
  - `Side contrast`
  - `Curvature cavity intensity`
  - `Curvature highlights intensity`
  - `AO tint`
  - `AO tint power`
  - `Specular detail`
  - `Specular softness`
  - `Roughness dullness`

源码：
- `FModel/CUE4Parse/CUE4Parse/UE4/Assets/Exports/Material/MaterialResourceTypes.cs:892-905`

结论：
- 这批 patch 已可严格闭合到 `FMaterialNumericParameterInfo.ParameterInfo.Name`

同一样例中，texture 参数相关 patch 也可对上材质参数身份结构，而不是 shader resource binding：

- `Normal input`
- `Curvature input`
- `AO input`
- `RVT Landscape height`

这些 patch 命中位置继续向后解码，可读出与样例材质真值一致的字段：

- `Index = -1`
- `Association = 2` (`GlobalParameter`)
- `TextureIndex = 4 / 3 / 2 / 5`

并且 patch 后续紧邻字节还能区分出：

- 普通 texture 参数的 `VirtualTextureLayerIndex = 255`
- `RVT Landscape height` 的 `VirtualTextureLayerIndex = 0`

这与样例现有材质侧 texture 参数真值完全一致。

源码：
- `FModel/CUE4Parse/CUE4Parse/UE4/Assets/Exports/Material/MaterialResourceTypes.cs:709-722`
- `FModel/CUE4Parse/CUE4Parse/UE4/Assets/Exports/Material/MaterialResourceTypes.cs:766-779`

结论：
- 这批 texture patch 已可严格闭合到 `FMaterialTextureParameterInfo.ParameterInfo.Name` 及其同结构中的 texture 参数身份字段
- 因此，对当前样例来说，`MemoryImageResult.ScriptNames.Patches` 闭合到的是材质参数身份结构
- 它仍然**没有**闭合到 compiler `ParameterName`，也没有闭合到最终 `T/S/U` resource binding name

### Current Debug Export Policy
当前离线样例导出允许在 HLSL 头注释中打印两类调试真理，但它们都不进入 canonical metadata：

- `ParameterName -> TextureIndex -> ReferencedTextures identity`
- `Material.<ResourceMember> @ MemberOffset (MemberType)`

结论：
- 这些调试信息只用于核对桥是否闭合
- 在 `compiler ParameterName -> final Tn/Sn/U0` 未闭合前，禁止把它们写入正式 metadata 或最终资源名恢复结果

## Material Parameter API Truth

### Runtime parameter setters keep `FMaterialParameterInfo`
源码确认 `UMaterialInstanceDynamic` 的参数设置 API 并不是直接按 shader resource 名工作，而是先构造 `FMaterialParameterInfo`：

```cpp
void UMaterialInstanceDynamic::SetScalarParameterValue(FName ParameterName, float Value)
{
    FMaterialParameterInfo ParameterInfo(ParameterName);
    SetScalarParameterValueInternal(ParameterInfo, Value);
}

void UMaterialInstanceDynamic::SetTextureParameterValue(FName ParameterName, UTexture* Value)
{
    FMaterialParameterInfo ParameterInfo(ParameterName);
    SetTextureParameterValueInternal(ParameterInfo, Value);
}
```

文件：
- `Engine/Source/Runtime/Engine/Private/Materials/MaterialInstanceDynamic.cpp:129-248`

结论：
- UE 运行时材质参数 API 的真实入口是 `FMaterialParameterInfo`
- 这条 API 线证明的是“材质参数名身份真理”，不是“最终 shader 寄存器名真理”

### Runtime overrides are stored by `ParameterInfo`
源码确认 `UMaterialInstance::Set*ParameterValueInternal()` 最终把 override 写入按 `ParameterInfo` 索引的数组：

- `ScalarParameterValues`
- `VectorParameterValues`
- `TextureParameterValues`

关键行为：

```cpp
FTextureParameterValue* ParameterValue = GameThread_FindParameterByName(TextureParameterValues, ParameterInfo);
...
ParameterValue->ParameterInfo = ParameterInfo;
ParameterValue->ParameterValue = Value;
GameThread_UpdateMIParameter(this, *ParameterValue);
```

文件：
- `Engine/Source/Runtime/Engine/Private/Materials/MaterialInstance.cpp:3264-3449`

结论：
- 运行时覆写值的身份键仍然是 `FMaterialParameterInfo`
- 对 texture 来说，API 真理链可以闭合到：
  - `ParameterName`
  - `TextureParameterValues[*].ParameterInfo`
  - `TextureParameterValues[*].ParameterValue`

### `FUniformParameterOverrides` also keys texture overrides by `FHashedMaterialParameterInfo`
源码确认 `FUniformParameterOverrides` 用 `FHashedMaterialParameterInfo` 作为 texture override 的键：

```cpp
void SetTextureOverride(EMaterialTextureParameterType Type, const FHashedMaterialParameterInfo& ParameterInfo, UTexture* Texture);

TMap<FHashedMaterialParameterInfo, UTexture*> GameThreadTextureOverides[NumMaterialTextureParameterTypes];
TMap<FHashedMaterialParameterInfo, UTexture*> RenderThreadTextureOverrides[NumMaterialTextureParameterTypes];
```

文件：
- `Engine/Source/Runtime/Engine/Public/MaterialShared.h:525-558`

结论：
- render path 的 texture override 身份同样是 `FHashedMaterialParameterInfo`
- 这进一步证明 UE 材质系统核心使用的是“参数名身份”，而不是最终 `Tn/Sn`

### API truth does close to `TextureIndex`, but not to final `Tn/Sn`
源码确认 translator 可从 expression 取回：

```cpp
OutTextureIndex = TextureUniform->GetTextureIndex();
...
OutParameterName = TextureParameterUniform->GetParameterName();
```

文件：
- `Engine/Source/Runtime/Engine/Private/Materials/HLSLMaterialTranslator.cpp:3418-3452`

并且 `FMaterialTextureParameterInfo` 同时保存：
- `ParameterInfo`
- `TextureIndex`
- `SamplerSource`
- `VirtualTextureLayerIndex`

文件：
- `Engine/Source/Runtime/Engine/Public/MaterialShared.h:481-502`

结论：
- 这条 API/translator/source 链足以严格证明：
  - `SetTextureParameterValue(ParameterName, Value)` 操作的身份键
  - 编译期 texture uniform expression 的 `ParameterName -> TextureIndex`
- 但它仍然不能单独证明：
  - `ParameterName -> final compiler resource name`
  - `ParameterName -> final Tn/Sn/U0`

### Why this differs from Unity-style property setters
当前源码真理表明 UE 与 Unity 风格的“按最终 shader property handle 直接绑定”不同：

- UE 运行时 API 工作在 `FMaterialParameterInfo` / `FHashedMaterialParameterInfo` 层
- shader 最终资源分配仍要经过：
  - material uniform expression
  - translator generated member access
  - compiler `FShaderParameterMap`
  - cooked `ParameterMapInfo` / `Bindings`

结论：
- `SetTexture` / `SetScalar` / `SetVector` 这条 API 线非常重要
- 但它只能帮助闭合“材质参数身份真理”
- 如果没有编译期 `FShaderParameterMap.ParameterMap` 名字键或等价名字表，仍然不能把参数名直接写成最终 `T/S/U` 资源名

### Current Status Of This Chain
- 已从源码确认：
  - `FShaderParameterMap`
  - `FShader::BuildParameterMapInfo(...)`
  - `FShaderParameterBindings::BindForLegacyShaderParameters(...)`
    这三段组成 shader resource binding 真理主链
- 但当前离线恢复里，尚未把游戏导出中的：
  - `ParameterMapInfo.TextureSamplers`
  - `ParameterMapInfo.SRVs`
  - `Bindings.ResourceParameters`
  与材质侧：
  - `TextureIndex`
  - `ParameterInfo`
  - `ReferencedTextures`
  严格闭合成同一条可证明链
- 因此在这条桥闭合前：
  - canonical metadata 禁止写入 texture `tN/sN` binding
  - canonical metadata 禁止写入基于材质侧局部 ordinal 推出来的 texture resource 名
  - 材质侧 `UniformTextureParameters` 只能视为材质表达式身份线索，不能当作 shader 最终 binding 真理
- 更严格地说：
  - 如果拿不到编译期 `FShaderParameterMap.ParameterMap` 本体，就不能声称拿到了 shader 资源名字真理
  - cooked `ParameterMapInfo` / `Bindings.ResourceParameters` 只足以恢复 slot/type/offset 真理，不足以单独恢复资源名字真理
  - 默认 cooked shader code 也不会把 `Output.ParameterMap.ParameterMap` 名字表直接序列化进去

### Game Data Export Landing Confirmed
统一导出当前已确认把 shader-side 这条链直接保存到了每个 `UnifiedShader` 记录里：

- `LoadedShaderMaps[*].MaterialShaderMapContent.Shaders[*].Bindings`
- `LoadedShaderMaps[*].MaterialShaderMapContent.Shaders[*].ParameterMapInfo`
- `LoadedShaderMaps[*].MaterialShaderMapContent.OrderedMeshShaderMaps[*].Shaders[*].Bindings`
- `LoadedShaderMaps[*].MaterialShaderMapContent.OrderedMeshShaderMaps[*].Shaders[*].ParameterMapInfo`
- `LoadedShaderMaps[*].MemoryImageResult`
- `LoadedShaderMaps[*].ShaderMapPointerTable`

对应导出器：
- `Source/Ruri.FModelHook/Game/SBUE/ShaderDecompiler/UnifiedShaderMetadataExporter.cs`

字段对齐当前已确认：

- `Bindings.ResourceParameters[*]`
  - `ByteOffset`
  - `BaseIndex`
  - `BaseType`
- `ParameterMapInfo.TextureSamplers[*]`
  - `BaseIndex`
  - `Size`
  - `BufferIndex`
  - `Type`
- `ParameterMapInfo.SRVs[*]`
  - `BaseIndex`
  - `Size`
  - `BufferIndex`
  - `Type`
- `MemoryImageResult.ScriptNames[*]`
  - `Name`
  - `Patches[*].Offset`
- `MemoryImageResult.MinimalNames[*]`
  - `Name`
  - `Patches[*].Offset`

结论：
- 游戏数据里已经保存了 shader-side binding 真理字段本体
- 当前缺的不是“有没有导出”，而是“如何把这些 shader-side 字段与材质侧 texture 身份链严格闭合”

### Current Observed Limitation In Sample Data
对样例 `MI_Cliff_small_ground_level` 当前可见的一个重要事实是：

- 很多 `UnifiedShader` 记录里的：
  - `Bindings.ResourceParameters`
  - `ParameterMapInfo.TextureSamplers`
  - `ParameterMapInfo.SRVs`
    可能为空数组

这说明至少对一部分样例 shader：
- 不能预设这三组字段一定会提供 texture 真桥
- 需要逐 shader、逐 stage、逐 mesh shader map 实际检查
- 空数组本身也是游戏数据真理，不能拿缺失值去猜测补全
- 即使这些数组非空，按源码也只能说明 shader-side slot/type 真理存在；不代表名字真理仍在 cooked 数据中

## Current Offline Analysis Status
已添加离线命令：

```text
--analyze-preshader <UnifiedShaderMetadata.json> --material <material path>
```

当前状态：
- 命令可运行
- 已能读出目标材质父链、`UniformBufferLayoutInitializer`、`UniformPreshaders`、`UniformPreshaderFields`
- 对样例 `MI_Cliff_small_ground_level`，已经静态闭合出一批 `ParameterIndex -> ParameterName -> BufferOffset`

当前样例已静态证明的直接写入包括：

- `SelectionColor -> BufferOffset 0`
- `UV Scale Near -> BufferOffset 17`
- `Normal blend -> BufferOffset 20`
- `Blend Sharpness (S) -> BufferOffset 21`
- `Blend Bias (S) -> BufferOffset 23`
- `RVT Height blend -> BufferOffset 39`
- `RVT blend falloff -> BufferOffset 40`
- `Side contrast -> BufferOffset 42`
- `Curvature cavity intensity -> BufferOffset 47`
- `Curvature highlights intensity -> BufferOffset 49`
- `AO tint -> BufferOffset 52`
- `AO tint power -> BufferOffset 59`
- `Specular detail -> BufferOffset 60`
- `Specular softness -> BufferOffset 61`
- `Roughness dullness -> BufferOffset 62`

这些结论成立的条件被严格限制为：

- 对应 preshader slice 的 opcode 仅包含 `EPreshaderOpcode::Parameter`
- 该 slice 仅写一个 `FMaterialUniformPreshaderField`
- 写入偏移直接取自 `UniformPreshaderFields[*].BufferOffset`

不满足上述条件的 slice 仍视为未静态闭合，不进入最终恢复结果。

当前实现修正：
- `UeMaterialJsonSymbolExtractor` 现在会直接保留这类已证明的 `ConstantBuffer("Material")`
- 不再要求在材质 JSON 提取阶段，局部 `metadata.Resources` 里已经先出现 `Material` 资源名
- 原因是 `Material` 资源名的真理入口之一来自 shader bundle 的 `UniformBufferNames`，它与材质侧 `ConstantBuffers` 的合流发生在最终 `MergeMetadata()` 阶段；在提取阶段提前依赖该资源会错误丢弃已证明成员

## What Is Proven vs Not Proven

### Proven
- `View` / `OpaqueBasePass` 属于引擎全局统一 CB 名
- `ParameterCollections[i]` 保存的是第 `i` 个 collection 的 `StateId`
- `MaterialParameterCollection` 的参数树保存在游戏资产中
- `M_Cliffs` 等基础材质资产中的 `ParameterCollectionInfos[*]` 可用同一 `StateId` 将 `ParameterCollections[*]` 追溯到具体 `MaterialParameterCollection` 资产身份
- `Material` 的布局树保存在 `UniformBufferLayoutInitializer` 中
- `Material` 的 preshader 写入桥保存在 `UniformPreshaders` 和 `UniformPreshaderFields` 中
- `FPreshaderData` 可以保存参数身份引用
- `Bindings.ResourceParameters[].ByteOffset` 是 shader parameter struct 真实成员偏移
- `ConstantBufferParameter.Index` 在当前恢复链中等价于字节偏移，不再引入并行的 `ByteOffset` 包装字段
- 最终成员恢复只允许按真实偏移解释，不再按 ordinal/index fallback 猜测
- 对 `MI_Cliff_small_ground_level`，一组 `Material` 数值参数已经通过 `Parameter opcode -> UniformNumericParameters -> UniformPreshaderFields.BufferOffset` 直接静态闭合
- 当前最终恢复链中的 `Material` CB 资源名仍以 shader bundle 自带的 `UniformBufferNames` 为真理入口，不额外猜测绑定槽位
- `Material` 已证明成员现在允许直接进入 `ShaderSymbolData.ConstantBuffers`，再由最终合流阶段与 `UniformBufferNames` 提供的真实 CB 资源名配对
- `FUniformExpressionSet::CreateBufferStruct()` 会为 `Material` 固定生成 `Texture2D_k` / `Texture2D_kSampler` / `VolumeTexture_k` / `ExternalTexture_k` 等资源成员名
- `FHLSLMaterialTranslator::AccessUniformExpression()` 对 texture/external texture 会生成 `Material.<ResourceClass>_<TypedIndex>` 访问文本，并与上述成员名在 `ResourceClass + TypedIndex` 上一致
- D3D 编译前 `RemoveUniformBuffersFromSource()` 会把 `Material.<Member>` 扁平化为 `Material_<Member>`
- D3D reflection 的 `BindDesc.Name` 会原样进入 `Output.ParameterMap`，`HandleReflectedShaderResource/Sampler/UAV` 不会重命名它
- 默认无符号 cooked 路径不会把 `Output.ParameterMap.ParameterMap` 名字表直接序列化进 shader code
- `MemoryImageResult.ScriptNames / MinimalNames / Patches / FrozenObjectBase64` 已在当前 unified metadata 导出中存在
- `ReferencedTextures` 真理入口当前只认材质资产顶层 `CachedExpressionData.ReferencedTextures`
- `TextureIndex -> GetReferencedTextures()[TextureIndex]` 已可用于恢复纹理资产身份，但只能在材质链上实际命中的 `CachedExpressionData.ReferencedTextures` 范围内生效
- 对样例 `MI_Cliff_small_ground_level -> MI_Cliff_Parent -> M_Cliffs`，父材质链扫描现已实际命中到 `M_Cliffs.CachedExpressionData.ReferencedTextures`
- 对样例 `MI_Cliff_small_ground_level_PS_3769.hlsl`，`ref_unresolvedref` 已被真实资产 identity 替换，例如 `ref_RVT_Landscape`、`ref_Ancient_grass_near_normal`、`ref_Ancient_rock_base_maps`、`ref_Ancient_dirt_base_maps`、`ref_Noise_clouds_2`
- 材质 JSON 注入链已不再按 `UniformTextureParameters` typed array ordinal 写入 `Texture2D` / `Texture3D` / `TextureCube` 等具体维度
- 对样例 `MI_Cliff_small_ground_level_PS_3769.spv` 的 `spirv-cross --reflect`，`separate_images[*].type` 已直接显示：
  - `binding 0 -> utexture3D`
  - `binding 1 -> texture3D`
  - `binding 7 -> texture3D`
  - `binding 8 -> texture3D`
  - `binding 9 -> texture3D`
- 对同一样例的 `MI_Cliff_small_ground_level_PS_3769.bindings.txt`，这些资源在 patched SPIR-V 绑定分析中都只是 `Type=SampledImage`，没有来自材质元数据的具体维度覆盖
- 因此，当前最终 HLSL 中仍出现的 `Texture3D<...>` 已可确认不是由材质 JSON 注入链写入，而是来自 patched SPIR-V 图像类型本体 / `spirv-cross` 对该类型的直译
- `FShaderParameterMap -> FShader::BuildParameterMapInfo(...) -> FShaderParameterBindings::BindForLegacyShaderParameters(...)` 是 shader resource binding 真理主链
- 统一导出已把这条主链对应的游戏数据字段保存到每个 `UnifiedShader`：`Bindings.ResourceParameters`、`ParameterMapInfo.TextureSamplers`、`ParameterMapInfo.SRVs`
- 编译期真正持有 `ParameterName` 的资源符号表是 `FShaderParameterMap.ParameterMap`
- cooked `ParameterMapInfo` / `Bindings.ResourceParameters` 都不会保留 `ParameterName`
- 在这条主链与游戏导出字段逐项对齐前，canonical metadata 只保留已闭合的 constant buffer 真理，不保留材质侧 texture binding/slot 恢复结果

### Not Yet Proven
- `ParameterCollections[0]` 必然等于 shader 中的 `MaterialCollection0`
- `MaterialCollection` 参数到 `float4[i].xyzw` 的显式映射
- `FrozenArchive.ScriptNames[k]` 到某个 `UniformPreshaderField.BufferOffset` 的一一对应
- 非单一 `Parameter` slice 的 `UniformNumericParameters[i]` 到最终 `Material` buffer offset 的静态闭环
- `UniformTextureParameters` 的 typed array ordinal 与最终 HLSL 资源维度 (`Texture2D` / `Texture3D` / `TextureCube` / `VirtualTexturePhysical`) 的一一对应
- `MemoryImageResult.ScriptNames / MinimalNames / Patches` 中哪些名字与 shader resource / parameter binding / frozen object 字段严格对应
- `MemoryImageResult` 是否保存了足以无符号恢复 `Material_Texture2D_k` / `Material_VirtualTexturePhysical_k` 的名字与 patch 对应关系
- 样例里当前出现的 `MemoryImageResult.ScriptNames` 是否仅覆盖普通 `FName` 参数字段，而完全不覆盖 resource binding 相关字段
- `ParameterMapInfo.TextureSamplers[*]` / `ParameterMapInfo.SRVs[*]` 与材质侧 `TextureIndex` / `ReferencedTextures` 的可证明一一对应
- `Bindings.ResourceParameters[*]` 中具体哪一项与某个材质 texture 参数身份严格对应
- 游戏侧是否还保存了可回收 `FShaderParameterMap.ParameterMap` 名字键的结构，或等价编译期名字表（不依赖符号编译）
- 为什么样例中一部分 `UnifiedShader` 记录的 `ParameterMapInfo.TextureSamplers` / `SRVs` / `Bindings.ResourceParameters` 为空，以及这些空值是否与 shader 类型、阶段或编译路径有关
- 样例中仍然存在的 `Texture3D<float4>` / `Texture3D<uint4>` 维度是否由 SPIR-V 类型本体或 `spirv-cross` 输出直接决定
- `dxil-spirv` 为什么把样例中一部分资产真值显然更像 `Texture2D` / `RuntimeVirtualTexture` 的资源生成为 `texture3D` / `utexture3D`

## Removed Conclusions
以下旧结论或旧路径不再保留，因为它们不能满足“源码真理或游戏数据真理”的标准：

- 按成员 ordinal / index 顺序回填 CB 成员名
- 基于硬编码材质布局恢复 `Material` / `MaterialCollectionN` 字段
- 将源码中的材质布局直接当作游戏资产布局真理
- 任何未被 sidecar、统一导出、材质资产或 collection 资产闭合的数据驱动恢复
- 在材质 JSON 提取阶段，因为局部 `metadata.Resources` 尚未包含 `Material` 资源名，就提前丢弃已证明的 `Material` constant buffer
- 递归搜索任意 `ReferencedTextures` 首个命中结果，并把它当作材质引用纹理真理表
- 按 `UniformTextureParameters` 的 typed array ordinal 直接推断资源维度，并将 `Texture2D` / `Texture3D` / `TextureCube` 等具体类型写入最终恢复结果
- 在正式输出头注释或 canonical metadata 中列出仅来自材质侧 `UniformTextureParameters` / `TextureParameterValues` 的 texture 参数名，造成其已闭合到 shader 真实 binding 的假象
- 将父材质 `ObjectPath` 直接压缩成裸资产名再去解析导出 JSON 路径
- 任何基于 `Texture2D_i == TextureParameterValues[i]` 或 `Texture2D_i == flat TextureParameters[i]` 的近似映射器
- 旧 `PreciseParameterMapper` 路径及其 `Material_Texture2D_i -> 参数名` 猜测映射

## Recommended Next Work
下一步应继续做的不是猜测恢复，而是继续真理提取：

1. 把游戏导出中的 `ParameterMapInfo.TextureSamplers` / `ParameterMapInfo.SRVs` / `Bindings.ResourceParameters` 与上述源码主链逐项对齐
2. 闭合 `TextureIndex / ParameterInfo / ReferencedTextures` 到 shader 真实 `BaseIndex` / `BaseType` 的桥
3. 追最终资源维度类型的真实来源，确认它究竟来自 SPIR-V 反射、SPIR-V 类型本体，还是后续 HLSL 生成路径
   - 当前已确认至少到 patched SPIR-V reflect 层，问题已存在
   - 下一步应定位 `dxil-spirv` 产出的图像维度，或评估是否存在可由游戏资产真值驱动的安全纠正路径
4. 基于 `FrozenObjectBase64` 对样例做 `Patch.Offset -> frozen object` 字节落点分析，再追 `FrozenArchive.ScriptNames.Patches` 的字段落点
5. 继续分析 preshader data 与 field 写入关系，尤其是非直接 `Parameter` opcode 的路径
6. 查 `MaterialCollection` 编号与 packing 是否在游戏数据中另有保存结构

## Naming Rules For This Research
本文件中只使用以下对齐源码的结构名，不再自创命名：

- `FUniformExpressionSet`
- `FRHIUniformBufferLayoutInitializer`
- `FMaterialUniformPreshaderHeader`
- `FMaterialUniformPreshaderField`
- `UE::Shader::FPreshaderData`
- `FHashedMaterialParameterInfo`
- `FMaterialNumericParameterInfo`
- `UMaterialParameterCollection`
- `ParameterCollections`
- `ScriptNames`
- `Patches`

避免使用没有源码出处的模糊命名，例如：
- “CB member table”
- “material field graph”
- “symbol blob”

除非能明确对应到 UE 真实结构。
