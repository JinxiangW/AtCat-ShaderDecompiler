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

- `Content.UniformExpressionSet.UniformPreshaders`
- `Content.UniformExpressionSet.UniformPreshaderFields`
- `Content.UniformExpressionSet.UniformPreshaderData`
- `Content.UniformExpressionSet.UniformNumericParameters`
- `Content.UniformExpressionSet.UniformTextureCollectionParameters`
- `Content.UniformExpressionSet.ParameterCollections`

导出位置：
- `Source/Ruri.FModelHook/Game/SBUE/ShaderDecompiler/UnifiedShaderMetadataExporter.cs`

## Current Offline Analysis Status
已添加离线命令：

```text
--analyze-preshader <UnifiedShaderMetadata.json> --material <material path>
```

当前状态：
- 命令可运行
- 能读出目标材质与 `ParameterCollections`
- 但对当前样例 `MI_Cliff_small_ground_level` / `M_Cliffs`，尚未从 preshader bytecode 中静态抓到可直接闭合的 `ParameterIndex -> BufferOffset`

这说明至少当前样例中：
- 要么 preshader 已被常量折叠
- 要么参数到 field 的映射不以简单线性 `Parameter` opcode 形式暴露
- 要么还需要继续分析 `FrozenArchive.ScriptNames.Patches` 与 memory image 的关系

## What Is Proven vs Not Proven

### Proven
- `View` / `OpaqueBasePass` 属于引擎全局统一 CB 名
- `ParameterCollections[i]` 保存的是第 `i` 个 collection 的 `StateId`
- `MaterialParameterCollection` 的参数树保存在游戏资产中
- `Material` 的布局树保存在 `UniformBufferLayoutInitializer` 中
- `Material` 的 preshader 写入桥保存在 `UniformPreshaders` 和 `UniformPreshaderFields` 中
- `FPreshaderData` 可以保存参数身份引用

### Not Yet Proven
- `ParameterCollections[0]` 必然等于 shader 中的 `MaterialCollection0`
- `MaterialCollection` 参数到 `float4[i].xyzw` 的显式映射
- `FrozenArchive.ScriptNames[k]` 到某个 `UniformPreshaderField.BufferOffset` 的一一对应
- `UniformNumericParameters[i]` 到某个最终 `Material` buffer offset 的直接闭环

## Recommended Next Work
下一步应继续做的不是猜测恢复，而是继续真理提取：

1. 追 `FrozenArchive.ScriptNames.Patches` 的生成与消费路径
2. 继续分析 preshader data 与 field 写入关系，尤其是非直接 `Parameter` opcode 的路径
3. 查 `MaterialCollection` 编号与 packing 是否在游戏数据中另有保存结构

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
