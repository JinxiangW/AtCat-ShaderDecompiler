namespace Ruri.ShaderTools.Spirv;

internal sealed class StructuredCBufferRewriter
{
    private const ushort OpIAdd = 128;
    private const ushort OpISub = 130;
    private const ushort OpIMul = 132;
    private const ushort OpConstantNull = 46;
    private const ushort OpShiftLeftLogical = 196;
    private const ushort OpBitwiseOr = 197;
    private readonly Dictionary<(int Set, int Binding), string> _resolvedBufferNames = new();

    public bool LastRewriteApplied { get; private set; }
    public string LastRewriteSummary { get; private set; } = string.Empty;

    public string? GetResolvedBufferName(int set, int binding)
    {
        return _resolvedBufferNames.TryGetValue((set, binding), out string? name) ? name : null;
    }

    public byte[] Rewrite(byte[] spirv, ShaderSymbolData metadata)
    {
        LastRewriteApplied = false;
        _resolvedBufferNames.Clear();

        var summary = new List<string>();
        var module = SpirvModule.Parse(spirv);
        ModuleAnalysis analysis = AnalyzeModule(module);
        ConstantMaps constants = BuildConstantMaps(module);
        TypeInfo types = AnalyzeTypes(module, analysis);

        summary.Add($"Metadata resources={metadata.GetResourceBindingCount()}, constantBuffers={metadata.ConstantBuffers.Count}");
        summary.Add($"Analyzed decoratedIds={analysis.SetBindingById.Count}, variables={analysis.VariablePointerTypes.Count}, pointers={analysis.PointerTypes.Count}, structs={analysis.StructMembers.Count}, arrays={analysis.ArrayTypes.Count}");

        List<FlatUniformBufferInfo> flatBuffers = BuildFlatUniformBuffers(metadata, analysis, summary);
        if (flatBuffers.Count == 0)
        {
            LastRewriteSummary = summary.Count == 0
                ? "No flat uniform buffers matched metadata bindings."
                : string.Join(Environment.NewLine, summary);
            return spirv;
        }

        var rewrites = new List<BufferRewritePlan>();
        foreach (FlatUniformBufferInfo flatBuffer in flatBuffers)
        {
            StructuredBufferLayout? layout = BuildStructuredLayout(flatBuffer);
            if (layout == null)
            {
                summary.Add($"[{flatBuffer.Metadata.Name}] layout build failed");
                continue;
            }

            if (!IsValidFlatUniformBuffer(flatBuffer, layout))
            {
                summary.Add($"[{flatBuffer.Metadata.Name}] layout does not fit flat buffer: stride={flatBuffer.ArrayStride}, arrayLength={flatBuffer.ArrayLength}, usedRegisters={layout.MaxUsedRegisterCount}");
                continue;
            }

            var memberTypeIds = new List<uint>(layout.Members.Count);
            bool typeResolutionFailed = false;
            foreach (StructuredMemberLayout member in layout.Members)
            {
                uint memberTypeId = ResolveMemberTypeId(module, types, member);
                if (memberTypeId == 0)
                {
                    typeResolutionFailed = true;
                    break;
                }

                member.ResolvedTypeId = memberTypeId;
                // Pre-resolve a scalar-of-the-same-component type for vec / matrix / scalar members
                // so component access chains can produce ptr-scalar results.
                member.ScalarTypeId = member.LogicalType.Kind switch
                {
                    LogicalTypeKind.Scalar => memberTypeId,
                    LogicalTypeKind.Vector or LogicalTypeKind.Matrix => EnsureScalarType(module, types, member.LogicalType.ScalarKind),
                    _ => 0
                };
                if (member.LogicalType.Kind == LogicalTypeKind.Matrix)
                {
                    member.ColumnVectorTypeId = EnsureVectorType(module, types, member.LogicalType.ScalarKind, member.LogicalType.Rows);
                }
                memberTypeIds.Add(memberTypeId);
            }

            if (typeResolutionFailed)
            {
                summary.Add($"[{flatBuffer.Metadata.Name}] member type resolution failed");
                continue;
            }

            EnsureTranslationConstants(module, types, constants, layout);

            if (!CanRewriteAllAccessChains(module, flatBuffer, layout, constants, out string? validationFailure))
            {
                summary.Add($"[{flatBuffer.Metadata.Name}] rewrite validation failed: {validationFailure}");
                continue;
            }

            var plan = new BufferRewritePlan
            {
                Info = flatBuffer,
                Layout = layout,
                NewStructTypeId = module.AllocateId(),
                NewPointerTypeId = module.AllocateId(),
                MemberTypeIds = memberTypeIds
            };

            rewrites.Add(plan);
            _resolvedBufferNames[(flatBuffer.Metadata.Set, flatBuffer.Metadata.Binding)] = flatBuffer.Metadata.Name;
            InsertStructuredType(module, plan);
            InsertStructuredNames(module, plan);
            summary.Add($"[{flatBuffer.Metadata.Name}] rewrite planned with {layout.Members.Count} members");
        }

        if (rewrites.Count == 0)
        {
            LastRewriteSummary = summary.Count == 0 ? "No rewrites planned." : string.Join(Environment.NewLine, summary);
            return spirv;
        }

        RewriteVariablesAndAccessChains(module, rewrites, constants);
        LastRewriteApplied = true;
        LastRewriteSummary = string.Join(Environment.NewLine, summary);
        return module.ToBytes();
    }

    private static ModuleAnalysis AnalyzeModule(SpirvModule module)
    {
        var analysis = new ModuleAnalysis();
        foreach (SpirvInstruction instruction in module.Instructions)
        {
            switch (instruction.OpCode)
            {
                case SpvOpCode.OpDecorate when instruction.Words.Length >= 4:
                    {
                        uint targetId = instruction[1];
                        uint decoration = instruction[2];
                        if (decoration == SpvOpCode.DecorationDescriptorSet)
                        {
                            int set = (int)instruction[3];
                            (int? Set, int? Binding) existing = analysis.SetBindingById.TryGetValue(targetId, out var value) ? value : (null, null);
                            analysis.SetBindingById[targetId] = (set, existing.Binding);
                        }
                        else if (decoration == SpvOpCode.DecorationBinding)
                        {
                            int binding = (int)instruction[3];
                            (int? Set, int? Binding) existing = analysis.SetBindingById.TryGetValue(targetId, out var value) ? value : (null, null);
                            analysis.SetBindingById[targetId] = (existing.Set, binding);
                        }
                        else if (decoration == SpvOpCode.DecorationArrayStride)
                        {
                            analysis.ArrayStrides[targetId] = instruction[3];
                        }

                        break;
                    }
                case SpvOpCode.OpTypePointer when instruction.Words.Length >= 4:
                    analysis.PointerTypes[instruction[1]] = (instruction[2], instruction[3]);
                    break;
                case SpvOpCode.OpVariable when instruction.Words.Length >= 4:
                    analysis.VariablePointerTypes[instruction[2]] = instruction[1];
                    break;
                case SpvOpCode.OpTypeStruct when instruction.Words.Length >= 3:
                    analysis.StructMembers[instruction[1]] = instruction.Words.Skip(2).ToArray();
                    break;
                case SpvOpCode.OpTypeVector when instruction.Words.Length >= 4:
                    analysis.VectorShapes[instruction[1]] = (instruction[2], instruction[3]);
                    break;
                case SpvOpCode.OpTypeArray when instruction.Words.Length >= 4:
                    analysis.ArrayTypes[instruction[1]] = (instruction[2], instruction[3]);
                    break;
                case SpvOpCode.OpConstant when instruction.Words.Length >= 4:
                    analysis.Constants[instruction[2]] = instruction[3];
                    break;
            }
        }

        return analysis;
    }

    private static List<FlatUniformBufferInfo> BuildFlatUniformBuffers(ShaderSymbolData metadata, ModuleAnalysis analysis, List<string> summary)
    {
        var result = new List<FlatUniformBufferInfo>();
        foreach (BufferBinding resource in metadata.ConstantBufferBindings)
        {
            ConstantBuffer? constantBuffer = metadata.GetConstantBufferByName(resource.Name);
            if (constantBuffer == null)
            {
                summary.Add($"[{resource.Name}] no USC constant buffer metadata found");
                continue;
            }

            int resourceSet = metadata.GetSetIdFor(resource.Index, ShaderResourceType.ConstantBuffer);
            uint? variableId = analysis.SetBindingById
                .Where(static entry => entry.Value.Set.HasValue && entry.Value.Binding.HasValue)
                    .Where(entry => entry.Value.Set == resourceSet && entry.Value.Binding == resource.Index)
                .Select(entry => entry.Key)
                .FirstOrDefault(id =>
                    analysis.VariablePointerTypes.TryGetValue(id, out uint candidatePointerTypeId) &&
                    analysis.PointerTypes.TryGetValue(candidatePointerTypeId, out (uint StorageClass, uint TypeId) candidatePointerInfo) &&
                    candidatePointerInfo.StorageClass == SpvOpCode.StorageClassUniform);

            if (variableId == 0)
            {
                summary.Add($"[{resource.Name}] no decorated id for set={resourceSet} binding={resource.Index}");
                continue;
            }

            if (!analysis.VariablePointerTypes.TryGetValue(variableId.Value, out uint pointerTypeId))
            {
                summary.Add($"[{resource.Name}] decorated id {variableId.Value} is not an OpVariable");
                continue;
            }

            if (!analysis.PointerTypes.TryGetValue(pointerTypeId, out (uint StorageClass, uint TypeId) pointerInfo) || pointerInfo.StorageClass != SpvOpCode.StorageClassUniform)
            {
                summary.Add($"[{resource.Name}] variable {variableId.Value} is not a uniform pointer");
                continue;
            }

            if (!analysis.StructMembers.TryGetValue(pointerInfo.TypeId, out uint[]? wrapperMembers) || wrapperMembers.Length != 1)
            {
                summary.Add($"[{resource.Name}] variable {variableId.Value} is not a single-member wrapper struct");
                continue;
            }

            uint arrayTypeId = wrapperMembers[0];
            if (!analysis.ArrayTypes.TryGetValue(arrayTypeId, out (uint ElementTypeId, uint LengthId) arrayInfo) ||
                !analysis.Constants.TryGetValue(arrayInfo.LengthId, out uint arrayLength))
            {
                summary.Add($"[{resource.Name}] wrapper member is not a fixed array type");
                continue;
            }

            int arrayStride = analysis.ArrayStrides.TryGetValue(arrayTypeId, out uint strideValue)
                ? checked((int)strideValue)
                : 16;

            result.Add(new FlatUniformBufferInfo
            {
                VariableId = variableId.Value,
                PointerTypeId = pointerTypeId,
                StructTypeId = pointerInfo.TypeId,
                ArrayTypeId = arrayTypeId,
                ElementTypeId = arrayInfo.ElementTypeId,
                ArrayLength = checked((int)arrayLength),
                ArrayStride = arrayStride,
                Metadata = new FlatResourceBinding
                {
                    Name = resource.Name,
                    Binding = resource.Index,
                    Set = resourceSet,
                },
                ConstantBuffer = constantBuffer
            });
        }

        return result;
    }

    private static ConstantMaps BuildConstantMaps(SpirvModule module)
    {
        var result = new ConstantMaps();
        foreach (SpirvInstruction instruction in module.Instructions)
        {
            int? resultIdIndex = SpvInstructionTraits.GetResultIdIndex(instruction);
            if (resultIdIndex.HasValue)
            {
                result.Definitions[instruction[resultIdIndex.Value]] = instruction;
            }

            if (instruction.OpCode == SpvOpCode.OpConstant && instruction.Words.Length >= 4)
            {
                result.IdToValue[instruction[2]] = instruction[3];
                result.ValueToId[instruction[3]] = instruction[2];
            }

            if (instruction.OpCode == OpConstantNull && instruction.Words.Length >= 3)
            {
                result.IdToValue[instruction[2]] = 0;
                if (!result.ValueToId.ContainsKey(0))
                {
                    result.ValueToId[0] = instruction[2];
                }
            }
        }

        return result;
    }

    private static bool CanRewriteAllAccessChains(
        SpirvModule module,
        FlatUniformBufferInfo flatBuffer,
        StructuredBufferLayout layout,
        ConstantMaps constants,
        out string? failure)
    {
        failure = null;
        int accessChainCount = 0;

        foreach (SpirvInstruction instruction in module.Instructions)
        {
            if ((instruction.OpCode != SpvOpCode.OpAccessChain && instruction.OpCode != SpvOpCode.OpInBoundsAccessChain) || instruction.Words.Length < 4)
            {
                continue;
            }

            if (instruction[3] != flatBuffer.VariableId)
            {
                continue;
            }

            accessChainCount++;
            if (!TryParseFlatAccessChain(instruction, constants, out FlatAccessPath accessPath))
            {
                failure = $"unsupported access chain parse for resultId={instruction[2]} op={instruction.OpCode} words=[{string.Join(",", instruction.Words)}]";
                return false;
            }

            if (TranslateFlatAccess(layout, accessPath, constants) == null
                && !CanRewriteViaCompositeExtracts(module, instruction[2], layout, accessPath, constants))
            {
                failure = $"unsupported access translation for resultId={instruction[2]} slotConst={accessPath.Slot.ConstantRegisterOffset} slotDynamic={accessPath.Slot.DynamicIndexId} stride={accessPath.Slot.DynamicIndexStride} extra=[{string.Join(",", accessPath.ExtraIndices)}] op={instruction.OpCode} words=[{string.Join(",", instruction.Words)}]";
                return false;
            }
        }

        if (accessChainCount == 0)
        {
            failure = "no access chains found for variable";
            return false;
        }

        return true;
    }

    private static TypeInfo AnalyzeTypes(SpirvModule module, ModuleAnalysis analysis)
    {
        var info = new TypeInfo();
        foreach (SpirvInstruction instruction in module.Instructions)
        {
            switch (instruction.OpCode)
            {
                case SpvOpCode.OpTypeFloat when instruction.Words.Length >= 3 && instruction[2] == 32:
                    info.FloatTypeId = instruction[1];
                    break;
                case SpvOpCode.OpTypeInt when instruction.Words.Length >= 4 && instruction[2] == 32 && instruction[3] == 1:
                    info.IntTypeId = instruction[1];
                    break;
                case SpvOpCode.OpTypeInt when instruction.Words.Length >= 4 && instruction[2] == 32 && instruction[3] == 0:
                    info.UIntTypeId = instruction[1];
                    break;
                case SpvOpCode.OpTypeVector when instruction.Words.Length >= 4:
                    RecordVectorType(info, instruction[1], instruction[2], instruction[3]);
                    break;
                case SpvOpCode.OpTypeMatrix when instruction.Words.Length >= 4:
                    RecordMatrixType(info, analysis, instruction[1], instruction[2], instruction[3]);
                    break;
            }
        }

        return info;
    }

    private static void RecordVectorType(TypeInfo info, uint typeId, uint componentTypeId, uint componentCount)
    {
        if (componentTypeId == info.FloatTypeId)
        {
            info.FloatVectorTypeIds[(int)componentCount] = typeId;
        }
        else if (componentTypeId == info.IntTypeId)
        {
            info.IntVectorTypeIds[(int)componentCount] = typeId;
        }
        else if (componentTypeId == info.UIntTypeId)
        {
            info.UIntVectorTypeIds[(int)componentCount] = typeId;
        }
    }

    private static void RecordMatrixType(TypeInfo info, ModuleAnalysis analysis, uint typeId, uint vectorTypeId, uint columnCount)
    {
        if (!analysis.TryGetVectorShape(vectorTypeId, out uint componentTypeId, out uint rowCount) || componentTypeId != info.FloatTypeId)
        {
            return;
        }

        info.MatrixTypeIds[(checked((int)rowCount), checked((int)columnCount))] = typeId;
    }

    private static bool IsValidFlatUniformBuffer(FlatUniformBufferInfo flatBuffer, StructuredBufferLayout layout)
    {
        if (flatBuffer.ArrayStride != 16)
        {
            return false;
        }

        return layout.Members.Count > 0 && layout.Members.Any(member => member.RegisterOffset < flatBuffer.ArrayLength);
    }

    private static StructuredBufferLayout? BuildStructuredLayout(FlatUniformBufferInfo flatBuffer)
    {
        ConstantBuffer constantBuffer = flatBuffer.ConstantBuffer;
        bool hasNumeric = constantBuffer.VectorParams.Length > 0 || constantBuffer.MatrixParams.Length > 0;
        if (!hasNumeric && constantBuffer.StructParams.Length == 0)
        {
            return null;
        }

        var layout = new StructuredBufferLayout();
        var members = new List<StructuredMemberLayout>();
        int maxAvailableByteOffset = flatBuffer.ArrayLength * 16;

        foreach (NumericShaderParameter parameter in constantBuffer.AllNumericParams.OrderBy(static parameter => parameter.ByteOffset))
        {
            StructuredMemberLayout? member = TryCreateScalarOrVectorMember(parameter, maxAvailableByteOffset);
            if (member == null)
            {
                continue;
            }

            members.Add(member);
        }

        foreach (StructParameter structParameter in flatBuffer.ConstantBuffer.StructParams.OrderBy(static parameter => parameter.Index))
        {
            StructuredMemberLayout? member = TryCreateStructMember(structParameter, maxAvailableByteOffset);
            if (member == null)
            {
                continue;
            }

            members.Add(member);
        }

        members.Sort(static (left, right) => left.ByteOffset.CompareTo(right.ByteOffset));
        if (members.Count == 0)
        {
            return null;
        }

        int maxUsedByteOffset = 0;
        int maxReferencedByteOffset = 0;
        foreach (StructuredMemberLayout member in members)
        {
            layout.Members.Add(member);
            maxUsedByteOffset = Math.Max(maxUsedByteOffset, member.ByteOffset + GetMemberSpanBytes(member));
            maxReferencedByteOffset = Math.Max(maxReferencedByteOffset, GetReferencedByteEnd(member));
        }

        layout.RequiredRegisterCount = Math.Max(1, (maxUsedByteOffset + 15) / 16);
        layout.MaxUsedRegisterCount = Math.Max(1, (maxReferencedByteOffset + 15) / 16);
        return layout;
    }

    private static int GetReferencedByteEnd(StructuredMemberLayout member)
    {
        if (IsPaddingMember(member))
        {
            return 0;
        }

        if (member.LogicalType.Kind != LogicalTypeKind.Struct || member.LogicalType.StructMembers == null || member.LogicalType.StructMembers.Count == 0)
        {
            return member.ByteOffset + GetMemberSpanBytes(member);
        }

        int childEnd = 0;
        foreach (StructuredMemberLayout child in member.LogicalType.StructMembers)
        {
            childEnd = Math.Max(childEnd, GetReferencedByteEnd(child));
        }

        return member.ByteOffset + childEnd;
    }

    private static bool IsPaddingMember(StructuredMemberLayout member)
    {
        return !string.IsNullOrWhiteSpace(member.Name) && member.Name.StartsWith("_pad", StringComparison.Ordinal);
    }

    private static StructuredMemberLayout? TryCreateScalarOrVectorMember(NumericShaderParameter parameter, int maxAvailableByteOffset)
    {
        if (parameter.ByteOffset < 0 || parameter.ByteOffset >= maxAvailableByteOffset)
        {
            return null;
        }

        MemberLogicalType? logicalType = TryCreateLogicalTypeFromMetadata(parameter);
        if (logicalType == null)
        {
            return null;
        }

        return new StructuredMemberLayout
        {
            Name = parameter.Name ?? string.Empty,
            ByteOffset = parameter.ByteOffset,
            Metadata = parameter,
            LogicalType = logicalType,
            RegisterOffset = parameter.ByteOffset / 16,
            RegisterCount = GetRequiredRegisterCount(parameter.ByteOffset, logicalType)
        };
    }

    private static StructuredMemberLayout? TryCreateStructMember(StructParameter structParameter, int maxAvailableByteOffset)
    {
        bool hasMembers = structParameter.VectorMembers.Length > 0 || structParameter.MatrixMembers.Length > 0;
        if (structParameter.Index < 0 || structParameter.Index >= maxAvailableByteOffset || !hasMembers)
        {
            return null;
        }

        var childMembers = new List<StructuredMemberLayout>();
        int structEnd = Math.Min(maxAvailableByteOffset, structParameter.Index + Math.Max(structParameter.StructSize, 0));
        foreach (NumericShaderParameter child in structParameter.AllNumericMembers.OrderBy(static parameter => parameter.ByteOffset))
        {
            if (child.ByteOffset < structParameter.Index || child.ByteOffset >= structEnd)
            {
                continue;
            }

            MemberLogicalType? childType = TryCreateLogicalTypeFromMetadata(child);
            if (childType == null)
            {
                return null;
            }

            childMembers.Add(new StructuredMemberLayout
            {
                Name = child.Name ?? string.Empty,
                ByteOffset = child.ByteOffset - structParameter.Index,
                Metadata = child,
                LogicalType = childType,
                RegisterOffset = (child.ByteOffset - structParameter.Index) / 16,
                RegisterCount = GetRequiredRegisterCount(child.ByteOffset - structParameter.Index, childType)
            });
        }

        if (childMembers.Count == 0)
        {
            return null;
        }

        childMembers.Sort(static (left, right) => left.ByteOffset.CompareTo(right.ByteOffset));

        var logicalType = new MemberLogicalType
        {
            Kind = LogicalTypeKind.Struct,
            StructName = structParameter.Name,
            StructByteSize = childMembers.Max(static child => child.ByteOffset + GetMemberSpanBytes(child)),
            StructMembers = childMembers,
            ArrayLength = Math.Max(structParameter.ArraySize, 1),
            DeclaredByteSize = Math.Max(structParameter.ArraySize, 1) * childMembers.Max(static child => child.ByteOffset + GetMemberSpanBytes(child))
        };

        return new StructuredMemberLayout
        {
            Name = structParameter.Name,
            ByteOffset = structParameter.Index,
            LogicalType = logicalType,
            RegisterOffset = structParameter.Index / 16,
            RegisterCount = Math.Max(1, ((logicalType.StructByteSize * Math.Max(logicalType.ArrayLength, 1)) + 15) / 16)
        };
    }

    private static int GetMemberSpanBytes(StructuredMemberLayout member)
    {
        return member.LogicalType.Kind == LogicalTypeKind.Struct
            ? member.LogicalType.StructByteSize * Math.Max(member.LogicalType.ArrayLength, 1)
            : member.LogicalType.Kind == LogicalTypeKind.Matrix
            ? member.LogicalType.Columns * 16 * Math.Max(member.LogicalType.ArrayLength, 1)
            : member.LogicalType.DeclaredByteSize;
    }

    private static MemberLogicalType? TryCreateLogicalTypeFromMetadata(NumericShaderParameter parameter)
    {
        if (parameter.RowCount <= 0 || parameter.ColumnCount <= 0)
        {
            return null;
        }

        ScalarKind? scalarKind = TryResolveScalarKind(parameter.Type);
        if (scalarKind == null)
        {
            return null;
        }

        return new MemberLogicalType
        {
            Kind = parameter.IsMatrix
                ? LogicalTypeKind.Matrix
                : parameter.RowCount == 1 ? LogicalTypeKind.Scalar : LogicalTypeKind.Vector,
            ScalarKind = scalarKind.Value,
            Rows = parameter.RowCount,
            Columns = parameter.ColumnCount,
            ArrayLength = Math.Max(parameter.ArraySize, 1),
            DeclaredByteSize = GetDeclaredByteSize(parameter),
            UscIndex = parameter.ByteOffset,
            IsMatrix = parameter.IsMatrix
        };
    }

    private static ScalarKind? TryResolveScalarKind(ShaderParamType paramType)
    {
        return paramType switch
        {
            ShaderParamType.Float => ScalarKind.Float,
            ShaderParamType.Int => ScalarKind.Int,
            ShaderParamType.Bool => ScalarKind.UInt,
            ShaderParamType.UInt => ScalarKind.UInt,
            _ => null
        };
    }

    private static int GetDeclaredByteSize(NumericShaderParameter parameter)
    {
        int arrayLength = Math.Max(parameter.ArraySize, 1);
        if (parameter.IsMatrix)
        {
            return parameter.ColumnCount * 16 * arrayLength;
        }

        return parameter.RowCount * parameter.ColumnCount * arrayLength * 4;
    }

    private static int GetRequiredRegisterCount(int byteOffset, MemberLogicalType type)
    {
        if (type.Kind == LogicalTypeKind.Matrix)
        {
            return Math.Max(1, type.Columns * Math.Max(type.ArrayLength, 1));
        }

        int startRegister = byteOffset / 16;
        int endByteOffset = byteOffset + Math.Max(type.DeclaredByteSize, 4);
        int endRegister = Math.Max(startRegister + 1, (endByteOffset + 15) / 16);
        return endRegister - startRegister;
    }

    private static uint ResolveMemberTypeId(SpirvModule module, TypeInfo types, StructuredMemberLayout member)
    {
        MemberLogicalType logicalType = member.LogicalType;
        uint baseTypeId = logicalType.Kind switch
        {
            LogicalTypeKind.Scalar => EnsureScalarType(module, types, logicalType.ScalarKind),
            LogicalTypeKind.Vector => EnsureVectorType(module, types, logicalType.ScalarKind, logicalType.Rows),
            LogicalTypeKind.Matrix => EnsureMatrixType(module, types, logicalType.Rows, logicalType.Columns),
            LogicalTypeKind.Struct => EnsureStructType(module, types, member),
            _ => 0
        };

        if (baseTypeId == 0)
        {
            return 0;
        }

        if (logicalType.ArrayLength > 1)
        {
            // HLSL cbuffer rule: every array element starts on a 16-byte boundary regardless of
            // element size. So `float arr[8]` lands as 8 vec4 slots (128 bytes), with arr[i]
            // occupying only `.x` of each slot. Matrices use a column-vec4 stride; structs use
            // their own (caller-rounded) byte size.
            //
            // We can only emit this rewrite for cbuffer-bound members, so a smaller "tight" stride
            // (4 / 8 / 12 bytes) was always wrong — spirv-cross HLSL backend would reject it with
            //   "cbuffer ... cannot be expressed with either HLSL packing layout or packoffset".
            int stride = logicalType.Kind switch
            {
                LogicalTypeKind.Struct => logicalType.StructByteSize,
                LogicalTypeKind.Matrix => logicalType.Columns * 16,
                _ => 16, // scalar / vec2 / vec3 / vec4 arrays — all 16-byte stride in cbuffer.
            };
            return EnsureArrayType(module, types, baseTypeId, logicalType.ArrayLength, Math.Max(stride, 16));
        }

        return baseTypeId;
    }

    private static uint EnsureArrayType(SpirvModule module, TypeInfo types, uint elementTypeId, int arrayLength, int arrayStride)
    {
        uint lengthConstantId = FindOrCreateUIntConstant(module, types, checked((uint)arrayLength));
        foreach (SpirvInstruction instruction in module.Instructions)
        {
            if (instruction.OpCode == SpvOpCode.OpTypeArray && instruction.Words.Length >= 4 && instruction[2] == elementTypeId && instruction[3] == lengthConstantId)
            {
                return instruction[1];
            }
        }

        uint resultId = module.AllocateId();
        int decorationInsertIndex = module.FindFirstTypeInstructionIndex();
        while (decorationInsertIndex > 0 &&
               (module.Instructions[decorationInsertIndex - 1].OpCode == SpvOpCode.OpDecorate ||
                module.Instructions[decorationInsertIndex - 1].OpCode == SpvOpCode.OpMemberDecorate))
        {
            decorationInsertIndex--;
        }

        module.Instructions.Insert(decorationInsertIndex, new SpirvInstruction
        {
            OpCode = SpvOpCode.OpDecorate,
            Words =
            [
                SpvOpCode.MakeInstructionWord(SpvOpCode.OpDecorate, 4),
                resultId,
                SpvOpCode.DecorationArrayStride,
                (uint)arrayStride
            ]
        });

        module.Instructions.Insert(module.FindTypeSectionEndIndex(), new SpirvInstruction
        {
            OpCode = SpvOpCode.OpTypeArray,
            Words =
            [
                SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypeArray, 4),
                resultId,
                elementTypeId,
                lengthConstantId
            ]
        });

        return resultId;
    }

    private static uint FindOrCreateUIntConstant(SpirvModule module, TypeInfo types, uint value)
    {
        uint uintTypeId = EnsureUIntType(module, types);
        foreach (SpirvInstruction instruction in module.Instructions)
        {
            if (instruction.OpCode == SpvOpCode.OpConstant && instruction.Words.Length >= 4 && instruction[1] == uintTypeId && instruction[3] == value)
            {
                return instruction[2];
            }
        }

        uint resultId = module.AllocateId();
        module.Instructions.Insert(module.FindTypeSectionEndIndex(), new SpirvInstruction
        {
            OpCode = SpvOpCode.OpConstant,
            Words =
            [
                SpvOpCode.MakeInstructionWord(SpvOpCode.OpConstant, 4),
                uintTypeId,
                resultId,
                value
            ]
        });

        return resultId;
    }

    private static void EnsureTranslationConstants(SpirvModule module, TypeInfo types, ConstantMaps constants, StructuredBufferLayout layout)
    {
        int maxConstantValue = layout.Members.Count + 8;
        foreach (StructuredMemberLayout member in layout.Members)
        {
            AccumulateTranslationConstantRange(member, ref maxConstantValue);
        }

        for (uint value = 0; value <= maxConstantValue; value++)
        {
            uint constantId = FindOrCreateUIntConstant(module, types, value);
            constants.IdToValue[constantId] = value;
            constants.ValueToId[value] = constantId;
        }
    }

    private static void AccumulateTranslationConstantRange(StructuredMemberLayout member, ref int maxConstantValue)
    {
        maxConstantValue = Math.Max(maxConstantValue, member.RegisterCount);
        maxConstantValue = Math.Max(maxConstantValue, member.LogicalType.Rows);
        maxConstantValue = Math.Max(maxConstantValue, member.LogicalType.Columns);
        maxConstantValue = Math.Max(maxConstantValue, member.LogicalType.ArrayLength);
        maxConstantValue = Math.Max(maxConstantValue, member.LogicalType.SecondaryArrayLength);

        if (member.LogicalType.StructMembers != null)
        {
            maxConstantValue = Math.Max(maxConstantValue, member.LogicalType.StructMembers.Count);
            foreach (StructuredMemberLayout child in member.LogicalType.StructMembers)
            {
                AccumulateTranslationConstantRange(child, ref maxConstantValue);
            }
        }
    }

    private static uint EnsureScalarType(SpirvModule module, TypeInfo types, ScalarKind scalarKind)
    {
        return scalarKind switch
        {
            ScalarKind.Float => EnsureFloatType(module, types),
            ScalarKind.Int => EnsureIntType(module, types),
            ScalarKind.UInt => EnsureUIntType(module, types),
            _ => 0
        };
    }

    private static uint EnsureFloatType(SpirvModule module, TypeInfo types)
    {
        if (types.FloatTypeId != 0)
        {
            return types.FloatTypeId;
        }

        uint resultId = module.AllocateId();
        module.Instructions.Insert(module.FindTypeSectionEndIndex(), new SpirvInstruction
        {
            OpCode = SpvOpCode.OpTypeFloat,
            Words =
            [
                SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypeFloat, 3),
                resultId,
                32
            ]
        });
        types.FloatTypeId = resultId;
        return resultId;
    }

    private static uint EnsureIntType(SpirvModule module, TypeInfo types)
    {
        if (types.IntTypeId != 0)
        {
            return types.IntTypeId;
        }

        uint resultId = module.AllocateId();
        module.Instructions.Insert(module.FindTypeSectionEndIndex(), new SpirvInstruction
        {
            OpCode = SpvOpCode.OpTypeInt,
            Words =
            [
                SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypeInt, 4),
                resultId,
                32,
                1
            ]
        });
        types.IntTypeId = resultId;
        return resultId;
    }

    private static uint EnsureUIntType(SpirvModule module, TypeInfo types)
    {
        if (types.UIntTypeId != 0)
        {
            return types.UIntTypeId;
        }

        uint resultId = module.AllocateId();
        module.Instructions.Insert(module.FindTypeSectionEndIndex(), new SpirvInstruction
        {
            OpCode = SpvOpCode.OpTypeInt,
            Words =
            [
                SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypeInt, 4),
                resultId,
                32,
                0
            ]
        });
        types.UIntTypeId = resultId;
        return resultId;
    }

    private static uint EnsureVectorType(SpirvModule module, TypeInfo types, ScalarKind scalarKind, int componentCount)
    {
        if (componentCount == 1)
        {
            return EnsureScalarType(module, types, scalarKind);
        }

        Dictionary<int, uint> map = scalarKind switch
        {
            ScalarKind.Float => types.FloatVectorTypeIds,
            ScalarKind.Int => types.IntVectorTypeIds,
            ScalarKind.UInt => types.UIntVectorTypeIds,
            _ => throw new InvalidOperationException("Unsupported vector scalar kind.")
        };

        if (map.TryGetValue(componentCount, out uint existingTypeId) && existingTypeId != 0)
        {
            return existingTypeId;
        }

        uint componentTypeId = EnsureScalarType(module, types, scalarKind);
        uint resultId = module.AllocateId();
        module.Instructions.Insert(module.FindTypeSectionEndIndex(), new SpirvInstruction
        {
            OpCode = SpvOpCode.OpTypeVector,
            Words =
            [
                SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypeVector, 4),
                resultId,
                componentTypeId,
                (uint)componentCount
            ]
        });
        map[componentCount] = resultId;
        return resultId;
    }

    private static uint EnsureMatrixType(SpirvModule module, TypeInfo types, int rowCount, int columnCount)
    {
        if (types.MatrixTypeIds.TryGetValue((rowCount, columnCount), out uint existingTypeId) && existingTypeId != 0)
        {
            return existingTypeId;
        }

        uint vectorTypeId = EnsureVectorType(module, types, ScalarKind.Float, rowCount);
        uint resultId = module.AllocateId();
        module.Instructions.Insert(module.FindTypeSectionEndIndex(), new SpirvInstruction
        {
            OpCode = SpvOpCode.OpTypeMatrix,
            Words =
            [
                SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypeMatrix, 4),
                resultId,
                vectorTypeId,
                (uint)columnCount
            ]
        });
        types.MatrixTypeIds[(rowCount, columnCount)] = resultId;
        return resultId;
    }

    private static uint EnsureStructType(SpirvModule module, TypeInfo types, StructuredMemberLayout member)
    {
        if (member.ResolvedTypeId != 0)
        {
            return member.ResolvedTypeId;
        }

        List<StructuredMemberLayout>? children = member.LogicalType.StructMembers;
        if (children == null || children.Count == 0)
        {
            return 0;
        }

        var childTypeIds = new List<uint>(children.Count);
        foreach (StructuredMemberLayout child in children)
        {
            uint childTypeId = ResolveMemberTypeId(module, types, child);
            if (childTypeId == 0)
            {
                return 0;
            }

            child.ResolvedTypeId = childTypeId;
            // Mirror the top-level Rewrite() loop's pre-resolution of scalar/column
            // vector ids for vec/matrix/scalar children. Without this, a matrix
            // member nested inside a struct (e.g. UnityPerDrawArray.unity_ObjectToWorld)
            // hits TranslateMemberAccess's matrix branch with ColumnVectorTypeId == 0
            // and bails out — which is what was causing struct-of-struct-array
            // cbuffers like UnityInstancing_SRP_UnityPerDraw to fail rewrite.
            child.ScalarTypeId = child.LogicalType.Kind switch
            {
                LogicalTypeKind.Scalar => childTypeId,
                LogicalTypeKind.Vector or LogicalTypeKind.Matrix => EnsureScalarType(module, types, child.LogicalType.ScalarKind),
                _ => 0
            };
            if (child.LogicalType.Kind == LogicalTypeKind.Matrix)
            {
                child.ColumnVectorTypeId = EnsureVectorType(module, types, child.LogicalType.ScalarKind, child.LogicalType.Rows);
            }
            childTypeIds.Add(childTypeId);
        }

        uint structTypeId = module.AllocateId();
        int typeInsertIndex = module.FindFirstTypeInstructionIndex();
        int decorationInsertIndex = typeInsertIndex;
        while (decorationInsertIndex > 0 &&
               (module.Instructions[decorationInsertIndex - 1].OpCode == SpvOpCode.OpDecorate ||
                module.Instructions[decorationInsertIndex - 1].OpCode == SpvOpCode.OpMemberDecorate))
        {
            decorationInsertIndex--;
        }

        var decorations = new List<SpirvInstruction>();

        for (int childIndex = 0; childIndex < children.Count; childIndex++)
        {
            StructuredMemberLayout child = children[childIndex];
            decorations.Add(new SpirvInstruction
            {
                OpCode = SpvOpCode.OpMemberDecorate,
                Words =
                [
                    SpvOpCode.MakeInstructionWord(SpvOpCode.OpMemberDecorate, 5),
                    structTypeId,
                    (uint)childIndex,
                    SpvOpCode.DecorationOffset,
                    (uint)child.ByteOffset
                ]
            });

            if (child.LogicalType.Kind == LogicalTypeKind.Matrix)
            {
                decorations.Add(new SpirvInstruction
                {
                    OpCode = SpvOpCode.OpMemberDecorate,
                    Words =
                    [
                        SpvOpCode.MakeInstructionWord(SpvOpCode.OpMemberDecorate, 4),
                        structTypeId,
                        (uint)childIndex,
                        SpvOpCode.DecorationRowMajor
                    ]
                });
                decorations.Add(new SpirvInstruction
                {
                    OpCode = SpvOpCode.OpMemberDecorate,
                    Words =
                    [
                        SpvOpCode.MakeInstructionWord(SpvOpCode.OpMemberDecorate, 5),
                        structTypeId,
                        (uint)childIndex,
                        SpvOpCode.DecorationMatrixStride,
                        16
                    ]
                });
            }
        }

        module.Instructions.InsertRange(decorationInsertIndex, decorations);
        module.Instructions.Insert(module.FindTypeSectionEndIndex(), new SpirvInstruction
        {
            OpCode = SpvOpCode.OpTypeStruct,
            Words = new[]
            {
                SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypeStruct, (ushort)(2 + childTypeIds.Count)),
                structTypeId
            }.Concat(childTypeIds).ToArray()
        });

        if (!string.IsNullOrWhiteSpace(member.Name))
        {
            module.InsertDebugName(structTypeId, member.Name);
        }

        for (int childIndex = 0; childIndex < children.Count; childIndex++)
        {
            if (!string.IsNullOrWhiteSpace(children[childIndex].Name))
            {
                module.InsertDebugMemberName(structTypeId, (uint)childIndex, children[childIndex].Name);
            }
        }

        member.ResolvedTypeId = structTypeId;
        return structTypeId;
    }

    private static void InsertStructuredType(SpirvModule module, BufferRewritePlan rewrite)
    {
        int typeInsertIndex = module.FindFirstTypeInstructionIndex();
        int decorationInsertIndex = typeInsertIndex;
        while (decorationInsertIndex > 0 &&
               (module.Instructions[decorationInsertIndex - 1].OpCode == SpvOpCode.OpDecorate ||
                module.Instructions[decorationInsertIndex - 1].OpCode == SpvOpCode.OpMemberDecorate))
        {
            decorationInsertIndex--;
        }

        var decorations = new List<SpirvInstruction>
        {
            new()
            {
                OpCode = SpvOpCode.OpDecorate,
                Words =
                [
                    SpvOpCode.MakeInstructionWord(SpvOpCode.OpDecorate, 3),
                    rewrite.NewStructTypeId,
                    SpvOpCode.DecorationBlock
                ]
            }
        };

        for (int memberIndex = 0; memberIndex < rewrite.Layout.Members.Count; memberIndex++)
        {
            StructuredMemberLayout member = rewrite.Layout.Members[memberIndex];
            decorations.Add(new SpirvInstruction
            {
                OpCode = SpvOpCode.OpMemberDecorate,
                Words =
                [
                    SpvOpCode.MakeInstructionWord(SpvOpCode.OpMemberDecorate, 5),
                    rewrite.NewStructTypeId,
                    (uint)memberIndex,
                    SpvOpCode.DecorationOffset,
                    (uint)member.ByteOffset
                ]
            });

            if (member.LogicalType.Kind == LogicalTypeKind.Matrix)
            {
                decorations.Add(new SpirvInstruction
                {
                    OpCode = SpvOpCode.OpMemberDecorate,
                    Words =
                    [
                        SpvOpCode.MakeInstructionWord(SpvOpCode.OpMemberDecorate, 4),
                        rewrite.NewStructTypeId,
                        (uint)memberIndex,
                        SpvOpCode.DecorationRowMajor
                    ]
                });
                decorations.Add(new SpirvInstruction
                {
                    OpCode = SpvOpCode.OpMemberDecorate,
                    Words =
                    [
                        SpvOpCode.MakeInstructionWord(SpvOpCode.OpMemberDecorate, 5),
                        rewrite.NewStructTypeId,
                        (uint)memberIndex,
                        SpvOpCode.DecorationMatrixStride,
                        16
                    ]
                });
            }
        }

        module.Instructions.InsertRange(decorationInsertIndex, decorations);
        module.Instructions.Insert(module.FindTypeSectionEndIndex(), new SpirvInstruction
        {
            OpCode = SpvOpCode.OpTypeStruct,
            Words = new[]
            {
                SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypeStruct, (ushort)(2 + rewrite.MemberTypeIds.Count)),
                rewrite.NewStructTypeId
            }.Concat(rewrite.MemberTypeIds).ToArray()
        });

        module.Instructions.Insert(module.FindTypeSectionEndIndex(), new SpirvInstruction
        {
            OpCode = SpvOpCode.OpTypePointer,
            Words =
            [
                SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypePointer, 4),
                rewrite.NewPointerTypeId,
                SpvOpCode.StorageClassUniform,
                rewrite.NewStructTypeId
            ]
        });
    }

    private static void InsertStructuredNames(SpirvModule module, BufferRewritePlan rewrite)
    {
        // Struct type and variable need DIFFERENT alias strings — when spirv-cross HLSL
        // backend flattens a uniform block, both names go through one shared name cache,
        // and any collision triggers a `_1` suffix that bleeds into the member-name prefix
        // (e.g. `UnityPerMaterial_1_MainTex_ST`). We use DXC's `type.<BufferName>` form;
        // spirv-cross sanitises the dot to `_` for HLSL, so the emitted block keyword
        // reads `cbuffer type_UnityPerMaterial` while the variable keeps the unadorned
        // `UnityPerMaterial` (set later by SpirvPatcher) and members come out as
        // `UnityPerMaterial_<member>`.
        module.InsertDebugName(rewrite.NewStructTypeId, "type." + rewrite.Info.Metadata.Name);
        module.InsertDebugName(rewrite.Info.VariableId, $"__ruri_{rewrite.Info.Metadata.Name}_var");
        for (int memberIndex = 0; memberIndex < rewrite.Layout.Members.Count; memberIndex++)
        {
            string name = rewrite.Layout.Members[memberIndex].Name;
            if (!string.IsNullOrWhiteSpace(name))
            {
                module.InsertDebugMemberName(rewrite.NewStructTypeId, (uint)memberIndex, name);
            }
        }
    }

    private static void RewriteVariablesAndAccessChains(SpirvModule module, List<BufferRewritePlan> rewrites, ConstantMaps constants)
    {
        var rewriteByVariableId = rewrites.ToDictionary(static rewrite => rewrite.Info.VariableId);
        var pointerTypeByMemberTypeId = new Dictionary<uint, uint>();
        var rewrittenAccessChains = new Dictionary<uint, RewrittenAccessChainInfo>();

        foreach (BufferRewritePlan rewrite in rewrites)
        {
            foreach (uint memberTypeId in rewrite.MemberTypeIds)
            {
                if (!pointerTypeByMemberTypeId.ContainsKey(memberTypeId))
                {
                    pointerTypeByMemberTypeId[memberTypeId] = FindOrCreateUniformPointerType(module, memberTypeId);
                }
            }
        }

        foreach (SpirvInstruction instruction in module.Instructions)
        {
            if (instruction.OpCode == SpvOpCode.OpVariable && instruction.Words.Length >= 4)
            {
                if (rewriteByVariableId.TryGetValue(instruction[2], out BufferRewritePlan? rewrite))
                {
                    instruction[1] = rewrite.NewPointerTypeId;
                }

                continue;
            }

            if ((instruction.OpCode != SpvOpCode.OpAccessChain && instruction.OpCode != SpvOpCode.OpInBoundsAccessChain) || instruction.Words.Length < 4)
            {
                continue;
            }

            if (!rewriteByVariableId.TryGetValue(instruction[3], out BufferRewritePlan? plan) || !TryParseFlatAccessChain(instruction, constants, out FlatAccessPath accessPath))
            {
                continue;
            }

            StructuredAccessTranslation? translated = TranslateFlatAccess(plan.Layout, accessPath, constants);
            if (translated == null)
            {
                if (CanRewriteViaCompositeExtracts(module, instruction[2], plan.Layout, accessPath, constants))
                {
                    rewrittenAccessChains[instruction[2]] = new RewrittenAccessChainInfo
                    {
                        AccessChainResultId = instruction[2],
                        BaseVariableId = instruction[3],
                        InstructionOpCode = instruction.OpCode,
                        Plan = plan,
                        OriginalAccessPath = accessPath.Clone()
                    };
                }

                continue;
            }

            if (!pointerTypeByMemberTypeId.TryGetValue(translated.MemberTypeId, out uint pointerTypeId))
            {
                continue;
            }

            instruction.Words = new[]
            {
                SpvOpCode.MakeInstructionWord(instruction.OpCode, (ushort)(4 + translated.Indices.Count)),
                pointerTypeId,
                instruction[2],
                instruction[3]
            }.Concat(translated.Indices).ToArray();

            rewrittenAccessChains[instruction[2]] = new RewrittenAccessChainInfo
            {
                AccessChainResultId = instruction[2],
                BaseVariableId = instruction[3],
                InstructionOpCode = instruction.OpCode,
                Plan = plan,
                OriginalAccessPath = accessPath.Clone(),
                Translation = translated
            };
        }

        RewriteLoadsAndCompositeExtracts(module, rewrittenAccessChains, constants, pointerTypeByMemberTypeId);
    }

    private static void RewriteLoadsAndCompositeExtracts(
        SpirvModule module,
        Dictionary<uint, RewrittenAccessChainInfo> rewrittenAccessChains,
        ConstantMaps constants,
        Dictionary<uint, uint> uniformPointerTypes)
    {
        if (rewrittenAccessChains.Count == 0)
        {
            return;
        }

        var loadInfos = new Dictionary<uint, RewrittenLoadInfo>();
        // Maps OpBitcast result id → underlying tracked Load. Populated below as we walk the
        // module; lets a downstream OpCompositeExtract whose composite is the *bitcast* (not
        // the load itself) still resolve back to the load's structured-access metadata. The
        // canonical pattern is `Load v4float → Bitcast v4uint → CompositeExtract uint .y`
        // (HLSL `asuint(cb._m0[N]).y`, used to read bool members stored as uint or to sign-
        // pun a float).
        var bitcastToLoad = new Dictionary<uint, RewrittenLoadInfo>();
        // Bitcasts that participated in at least one rewrite. After the main pass any of
        // these whose result is no longer consumed by anyone live is itself dead and gets
        // NOPed in the structural cleanup; that in turn lets the underlying Load and
        // AccessChain be NOPed too.
        var processedBitcasts = new Dictionary<uint, SpirvInstruction>();

        for (int index = 0; index < module.Instructions.Count; index++)
        {
            SpirvInstruction instruction = module.Instructions[index];
            if (instruction.OpCode == SpvOpCode.OpLoad && instruction.Words.Length >= 4 && rewrittenAccessChains.TryGetValue(instruction[3], out RewrittenAccessChainInfo? accessInfo))
            {
                loadInfos[instruction[2]] = new RewrittenLoadInfo
                {
                    Instruction = instruction,
                    ResultId = instruction[2],
                    OriginalResultTypeId = instruction[1],
                    HasCompositeExtractUsers = false,
                    AccessChain = accessInfo
                };
                continue;
            }

            // Map Bitcasts that source from a tracked Load. The Bitcast must come AFTER its
            // source Load in module order (SSA), so this single-pass build is sound.
            if (instruction.OpCode == SpvOpCode.OpBitcast && instruction.Words.Length >= 4
                && loadInfos.TryGetValue(instruction[3], out RewrittenLoadInfo? bitcastSource))
            {
                bitcastToLoad[instruction[2]] = bitcastSource;
                continue;
            }

            if (instruction.OpCode == SpvOpCode.OpCompositeExtract && instruction.Words.Length >= 5)
            {
                // The composite operand might be either a tracked Load directly or a Bitcast
                // sitting between the Load and the extract. Both resolve to the same
                // structured-access plan; the extract's literal indices then narrow it to a
                // specific scalar member.
                RewrittenLoadInfo? loadInfo = null;
                if (loadInfos.TryGetValue(instruction[3], out RewrittenLoadInfo? directLoad))
                {
                    loadInfo = directLoad;
                }
                else if (bitcastToLoad.TryGetValue(instruction[3], out RewrittenLoadInfo? viaBitcast))
                {
                    loadInfo = viaBitcast;
                    // Track the Bitcast instruction itself so the cleanup pass can NOP it
                    // once all its consumers are gone. We re-find it by id; cheap because
                    // there's at most one Bitcast per result id.
                    foreach (SpirvInstruction maybeBitcast in module.Instructions)
                    {
                        if (maybeBitcast.OpCode == SpvOpCode.OpBitcast && maybeBitcast.Words.Length >= 3 && maybeBitcast[2] == instruction[3])
                        {
                            processedBitcasts[instruction[3]] = maybeBitcast;
                            break;
                        }
                    }
                }

                if (loadInfo == null)
                {
                    continue;
                }

                loadInfo.HasCompositeExtractUsers = true;

                FlatAccessPath directAccessPath = loadInfo.AccessChain.OriginalAccessPath.Clone();
                directAccessPath.ExtraIndices.AddRange(instruction.Words.Skip(4).Select(static value => checked((int)value)));

                StructuredAccessTranslation? translated = TranslateFlatAccess(loadInfo.AccessChain.Plan.Layout, directAccessPath, constants);
                if (translated == null || !uniformPointerTypes.TryGetValue(translated.MemberTypeId, out uint pointerTypeId))
                {
                    continue;
                }

                uint pointerResultId = module.AllocateId();
                module.Instructions.Insert(index, new SpirvInstruction
                {
                    OpCode = loadInfo.AccessChain.InstructionOpCode,
                    Words = new[]
                    {
                        SpvOpCode.MakeInstructionWord(loadInfo.AccessChain.InstructionOpCode, (ushort)(4 + translated.Indices.Count)),
                        pointerTypeId,
                        pointerResultId,
                        loadInfo.AccessChain.BaseVariableId
                    }.Concat(translated.Indices).ToArray()
                });
                module.Instructions.Insert(index + 1, new SpirvInstruction
                {
                    OpCode = SpvOpCode.OpLoad,
                    Words =
                    [
                        SpvOpCode.MakeInstructionWord(SpvOpCode.OpLoad, 4),
                        translated.MemberTypeId,
                        instruction[2],
                        pointerResultId
                    ]
                });

                index += 2;
                instruction.OpCode = SpvOpCode.OpNop;
                instruction.Words = [SpvOpCode.MakeInstructionWord(SpvOpCode.OpNop, 1)];
            }
        }

        // Loads whose only "consumer" was a CompositeExtract that read the whole vector (no
        // sub-component index — the access path is byte-exact for the vector-shaped member)
        // skip the inner-loop rewrite branch; they re-type the load in place. The legacy
        // logic guarded this with a use-count check; we now do it structurally inside the
        // dead-code pass below.
        foreach (RewrittenLoadInfo loadInfo in loadInfos.Values)
        {
            SpirvInstruction loadInstruction = loadInfo.Instruction;
            if (loadInstruction.OpCode != SpvOpCode.OpLoad || loadInstruction.Words.Length < 4)
            {
                continue;
            }

            if (!loadInfo.HasCompositeExtractUsers && loadInfo.AccessChain.Translation != null)
            {
                loadInstruction[1] = loadInfo.AccessChain.Translation.MemberTypeId;
            }
        }

        // Structural dead-code cleanup. Three cascades — Bitcasts that we routed through,
        // Loads we tracked, and the AccessChains feeding those Loads — each NOPed only when
        // the module no longer references the result id from any non-NOP id-bearing slot.
        // Use-count-based decisions break here for two reasons:
        //   1) Literal-bearing slots (OpConstant value words, OpExtInst's instruction enum,
        //      OpCompositeExtract's component indices, …) numerically alias real SSA ids.
        //   2) Counts go stale as we mutate the module; recomputing on every cascade is more
        //      expensive than the structural scan.
        // The IsLiteralBearingMetadataOp / IsLiteralValueConstantOp helpers cover (1).
        foreach (KeyValuePair<uint, SpirvInstruction> kvp in processedBitcasts)
        {
            SpirvInstruction bitcastInstr = kvp.Value;
            if (bitcastInstr.OpCode != SpvOpCode.OpBitcast)
            {
                continue;
            }

            if (!HasLiveIdConsumer(module, kvp.Key))
            {
                bitcastInstr.OpCode = SpvOpCode.OpNop;
                bitcastInstr.Words = [SpvOpCode.MakeInstructionWord(SpvOpCode.OpNop, 1)];
            }
        }

        foreach (RewrittenLoadInfo loadInfo in loadInfos.Values)
        {
            SpirvInstruction loadInstruction = loadInfo.Instruction;
            if (loadInstruction.OpCode != SpvOpCode.OpLoad || loadInstruction.Words.Length < 4)
            {
                continue;
            }

            if (!HasLiveIdConsumer(module, loadInfo.ResultId))
            {
                loadInstruction.OpCode = SpvOpCode.OpNop;
                loadInstruction.Words = [SpvOpCode.MakeInstructionWord(SpvOpCode.OpNop, 1)];
            }
        }

        // Final cleanup: any rewritten access chain whose Load (and Bitcast, if any) is now
        // NOP-d has no live consumer. Leaving it in place is unsafe — the variable's pointer
        // type was changed to the new struct, so an unrewritten old-style
        // `OpAccessChain ptr-vec4 var %0 %register` walks a type tree that no longer exists,
        // and spirv-cross fails validation with "Cannot subdivide a scalar value" (or
        // similar). The Bitcast and Load passes above already removed the chains that fed
        // these access chains, so a structural "no live consumer" check here finishes the
        // cascade.
        foreach (uint accessChainId in rewrittenAccessChains.Keys)
        {
            if (HasLiveIdConsumer(module, accessChainId))
            {
                continue;
            }

            foreach (SpirvInstruction inst in module.Instructions)
            {
                if ((inst.OpCode != SpvOpCode.OpAccessChain && inst.OpCode != SpvOpCode.OpInBoundsAccessChain) || inst.Words.Length < 3 || inst[2] != accessChainId)
                {
                    continue;
                }

                inst.OpCode = SpvOpCode.OpNop;
                inst.Words = [SpvOpCode.MakeInstructionWord(SpvOpCode.OpNop, 1)];
                break;
            }
        }
    }

    // Structural "is `targetId` consumed by any live (non-NOP) instruction in a real
    // id-bearing operand slot?" check. Used by the dead-code cascade in
    // RewriteLoadsAndCompositeExtracts to NOP Bitcasts → Loads → AccessChains in order.
    //
    // Skip the result-type / result-id slots of the consumer (those describe the consumer
    // itself, not consumption of `targetId`). Skip the whole instruction for ops whose
    // post-result words are pure literals (metadata + constant-definition ops) so
    // e.g. `%uint_<targetId> = OpConstant %uint <targetId>` doesn't read as a use of
    // `targetId`. Without these skips the literal value `<targetId>` numerically aliases
    // a real SSA id and a dead access chain stays alive forever.
    private static bool HasLiveIdConsumer(SpirvModule module, uint targetId)
    {
        foreach (SpirvInstruction instruction in module.Instructions)
        {
            if (instruction.OpCode == SpvOpCode.OpNop)
            {
                continue;
            }

            if (IsLiteralBearingMetadataOp(instruction.OpCode) || IsLiteralValueConstantOp(instruction.OpCode))
            {
                continue;
            }

            int? resultIdIndex = SpvInstructionTraits.GetResultIdIndex(instruction);
            int? resultTypeIndex = SpvInstructionTraits.GetResultTypeIdIndex(instruction);
            for (int operandIndex = 1; operandIndex < instruction.Words.Length; operandIndex++)
            {
                if ((resultIdIndex.HasValue && operandIndex == resultIdIndex.Value) || (resultTypeIndex.HasValue && operandIndex == resultTypeIndex.Value))
                {
                    continue;
                }

                if (instruction[operandIndex] == targetId)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsLiteralBearingMetadataOp(ushort opCode)
    {
        // Names, decorations, source/string blocks. None of these participate in data flow.
        // Opcode numbers are stable per the SPIR-V spec.
        return opCode == SpvOpCode.OpName              // 5
            || opCode == SpvOpCode.OpMemberName        // 6
            || opCode == SpvOpCode.OpDecorate          // 71
            || opCode == SpvOpCode.OpMemberDecorate    // 72
            || opCode == 73                            // OpDecorationGroup
            || opCode == 74                            // OpGroupDecorate
            || opCode == 75                            // OpGroupMemberDecorate
            || opCode == 7                             // OpString
            || opCode == 3                             // OpSource
            || opCode == 2                             // OpSourceContinued
            || opCode == 4                             // OpSourceExtension
            || opCode == 330                           // OpModuleProcessed
            || opCode == 8                             // OpLine
            || opCode == 317                           // OpNoLine
            || opCode == SpvOpCode.OpExecutionMode     // 16
            || opCode == 331                           // OpExecutionModeId
            || opCode == SpvOpCode.OpEntryPoint        // 15
            || opCode == SpvOpCode.OpCapability        // 17
            || opCode == 10                            // OpExtension
            || opCode == SpvOpCode.OpExtInstImport     // 11
            || opCode == SpvOpCode.OpMemoryModel;      // 14
    }

    private static bool IsLiteralValueConstantOp(ushort opCode)
    {
        // Constant-definition ops where every word past the result id is a literal (or
        // there are no further words at all). Skipping the whole instruction during use-
        // counting is safe: the only id reference is the result type at index 1, which the
        // caller already excludes.
        return opCode == 41   // OpConstantTrue
            || opCode == 42   // OpConstantFalse
            || opCode == 43   // OpConstant — value literal words (1 for 32-bit, 2 for 64-bit)
            || opCode == 45   // OpConstantSampler — three mode literals
            || opCode == 46   // OpConstantNull
            || opCode == 48   // OpSpecConstantTrue
            || opCode == 49   // OpSpecConstantFalse
            || opCode == 50;  // OpSpecConstant — like OpConstant
    }
    private static bool TryParseFlatAccessChain(SpirvInstruction instruction, ConstantMaps constants, out FlatAccessPath accessPath)
    {
        accessPath = null!;
        if (instruction.Words.Length < 4)
        {
            return false;
        }

        int slotOperandIndex = 4;
        if (instruction.Words.Length >= 6
            && TryParseSlotExpression(instruction[4], constants, out SlotExpression leadingIndex)
            && leadingIndex.DynamicIndexId == 0
            && leadingIndex.DynamicIndexStride == 0
            && leadingIndex.ConstantRegisterOffset == 0)
        {
            slotOperandIndex = 5;
        }

        if (!TryParseSlotExpression(instruction[slotOperandIndex], constants, out SlotExpression slotExpression))
        {
            return false;
        }

        var extraIndices = new List<int>();
        for (int operandIndex = slotOperandIndex + 1; operandIndex < instruction.Words.Length; operandIndex++)
        {
            if (!constants.IdToValue.TryGetValue(instruction[operandIndex], out uint value))
            {
                return false;
            }

            extraIndices.Add(checked((int)value));
        }

        accessPath = new FlatAccessPath
        {
            Slot = slotExpression,
            ExtraIndices = extraIndices
        };
        return true;
    }

    private static StructuredAccessTranslation? TranslateFlatAccess(StructuredBufferLayout layout, FlatAccessPath accessPath, ConstantMaps constants)
    {
        int componentIndex = accessPath.ExtraIndices.Count > 0 ? accessPath.ExtraIndices[0] : 0;
        if (accessPath.Slot.DynamicIndexId != 0)
        {
            return TranslateDynamicFlatAccess(layout, accessPath, constants, componentIndex);
        }

        int absoluteRegister = accessPath.Slot.ConstantRegisterOffset;
        int absoluteByteOffset = (absoluteRegister * 16) + (componentIndex * 4);
        for (int memberIndex = 0; memberIndex < layout.Members.Count; memberIndex++)
        {
            StructuredMemberLayout member = layout.Members[memberIndex];
            if (absoluteRegister < member.RegisterOffset || absoluteRegister >= member.RegisterOffset + member.RegisterCount)
            {
                continue;
            }

            if (!IsMemberByteMatch(member, absoluteByteOffset, accessPath.ExtraIndices))
            {
                continue;
            }

            StructuredAccessTranslation? logicalTranslation = TranslateMemberAccess(member, absoluteRegister, componentIndex, accessPath.ExtraIndices, constants);
            if (logicalTranslation == null || !TryGetConstantId(constants, (uint)memberIndex, out uint memberIndexConstantId))
            {
                continue;
            }

            var indices = new List<uint> { memberIndexConstantId };
            indices.AddRange(logicalTranslation.Indices);

            return new StructuredAccessTranslation
            {
                Indices = indices,
                MemberTypeId = logicalTranslation.MemberTypeId
            };
        }

        return null;
    }

    private static bool IsMemberByteMatch(StructuredMemberLayout member, int absoluteByteOffset, List<int> extraIndices)
    {
        int memberStart = member.ByteOffset;
        int memberEnd = memberStart + Math.Max(member.LogicalType.DeclaredByteSize, 4);
        if (member.LogicalType.Kind == LogicalTypeKind.Struct)
        {
            memberEnd = memberStart + (member.LogicalType.StructByteSize * Math.Max(member.LogicalType.ArrayLength, 1));
            return absoluteByteOffset >= memberStart && absoluteByteOffset < memberEnd;
        }

        if (member.LogicalType.Kind == LogicalTypeKind.Matrix)
        {
            return absoluteByteOffset >= memberStart && absoluteByteOffset < memberEnd;
        }

        if (member.LogicalType.ArrayLength > 1)
        {
            // Flat DXBC constant buffers commonly address 16-byte aligned arrays by register slot
            // without emitting an extra component index. Accept any byte inside the declared span and
            // let TranslateMemberAccess map the register delta back to the structured array index.
            return absoluteByteOffset >= memberStart && absoluteByteOffset < memberEnd;
        }

        return extraIndices.Count == 0
            ? absoluteByteOffset == memberStart
            : absoluteByteOffset >= memberStart && absoluteByteOffset < memberEnd;
    }

    private static StructuredAccessTranslation? TranslateMemberAccess(StructuredMemberLayout member, int absoluteRegister, int componentIndex, List<int> extraIndices, ConstantMaps constants)
    {
        int localRegister = absoluteRegister - member.RegisterOffset;
        int memberComponentOffset = (member.ByteOffset % 16) / 4;
        List<int> trailingIndices = extraIndices.Count > 1 ? extraIndices.Skip(1).ToList() : [];

        if (member.LogicalType.Kind == LogicalTypeKind.Struct)
        {
            int localByteOffset = (absoluteRegister * 16) + (componentIndex * 4) - member.ByteOffset;
            int structArrayLength = Math.Max(member.LogicalType.ArrayLength, 1);
            int structByteSize = Math.Max(member.LogicalType.StructByteSize, 1);
            int structElementIndex = localByteOffset / structByteSize;
            int elementLocalByteOffset = localByteOffset % structByteSize;
            if (structElementIndex < 0 || structElementIndex >= structArrayLength)
            {
                return null;
            }

            List<StructuredMemberLayout>? children = member.LogicalType.StructMembers;
            if (children == null)
            {
                return null;
            }

            for (int childIndex = 0; childIndex < children.Count; childIndex++)
            {
                StructuredMemberLayout child = children[childIndex];
                if (elementLocalByteOffset < child.ByteOffset || elementLocalByteOffset >= child.ByteOffset + GetMemberSpanBytes(child))
                {
                    continue;
                }

                if (!IsMemberByteMatch(child, elementLocalByteOffset, extraIndices))
                {
                    continue;
                }

                StructuredAccessTranslation? childTranslation = TranslateMemberAccess(
                    child,
                    member.RegisterOffset + ((absoluteRegister - member.RegisterOffset) - (structElementIndex * (structByteSize / 16))),
                    componentIndex,
                    extraIndices,
                    constants);

                if (childTranslation == null || !TryGetConstantId(constants, (uint)childIndex, out uint childIndexConstantId))
                {
                    continue;
                }

                var translatedIndices = new List<uint>();
                if (structArrayLength > 1)
                {
                    if (!TryGetConstantId(constants, (uint)structElementIndex, out uint structElementConstantId))
                    {
                        continue;
                    }

                    translatedIndices.Add(structElementConstantId);
                }

                translatedIndices.Add(childIndexConstantId);
                translatedIndices.AddRange(childTranslation.Indices);

                return new StructuredAccessTranslation
                {
                    Indices = translatedIndices,
                    MemberTypeId = childTranslation.MemberTypeId
                };
            }

            return null;
        }

        if (member.LogicalType.Kind == LogicalTypeKind.Matrix)
        {
            if (localRegister < 0 || localRegister >= member.LogicalType.Columns)
            {
                return null;
            }

            if (extraIndices.Count == 0)
            {
                // Column access: matrix[col] yields a vec(rowCount), not a matrix.
                return member.ColumnVectorTypeId != 0
                    ? CreateTranslation(constants, member.ColumnVectorTypeId, localRegister)
                    : null;
            }

            if (componentIndex < 0 || componentIndex >= member.LogicalType.Rows || trailingIndices.Count > 0 || member.ScalarTypeId == 0)
            {
                return null;
            }

            // Component access: matrix[col][component] yields a scalar.
            return CreateTranslation(constants, member.ScalarTypeId, localRegister, componentIndex);
        }

        if (member.RegisterCount == 1)
        {
            if (member.LogicalType.Kind == LogicalTypeKind.Scalar)
            {
                return componentIndex == memberComponentOffset && trailingIndices.Count == 0
                    ? CreateTranslation(constants, member.ResolvedTypeId)
                    : null;
            }

            if (member.LogicalType.Kind == LogicalTypeKind.Vector)
            {
                if (trailingIndices.Count > 0)
                {
                    return null;
                }

                // Bare access (no per-component extra index): return the whole vector ptr.
                // Adding a component index here was wrong — it would dive into the vector type but
                // keep the parent vec4 result type, producing an invalid `ptr-vec4 [..., component]`
                // access chain that spirv-cross rejects with "Cannot subdivide a scalar value".
                if (extraIndices.Count == 0)
                {
                    return componentIndex == memberComponentOffset
                        ? CreateTranslation(constants, member.ResolvedTypeId)
                        : null;
                }

                int relativeComponentIndex = componentIndex - memberComponentOffset;
                if (relativeComponentIndex < 0 || relativeComponentIndex >= member.LogicalType.Rows || member.ScalarTypeId == 0)
                {
                    return null;
                }

                // Per-component access: dive one level deeper, ptr-scalar result.
                return CreateTranslation(constants, member.ScalarTypeId, relativeComponentIndex);
            }
        }

        if (localRegister < 0 || localRegister >= member.RegisterCount || trailingIndices.Count > 0)
        {
            return null;
        }

        return extraIndices.Count > 0
            ? CreateTranslation(constants, member.ResolvedTypeId, localRegister, componentIndex)
            : CreateTranslation(constants, member.ResolvedTypeId, localRegister);
    }

    private static StructuredAccessTranslation? CreateTranslation(ConstantMaps constants, uint memberTypeId, params int[] indices)
    {
        var translatedIndices = new List<uint>(indices.Length);
        foreach (int index in indices)
        {
            if (!TryGetConstantId(constants, (uint)index, out uint constantId))
            {
                return null;
            }

            translatedIndices.Add(constantId);
        }

        return new StructuredAccessTranslation
        {
            Indices = translatedIndices,
            MemberTypeId = memberTypeId
        };
    }

    private static bool CanRewriteViaCompositeExtracts(
        SpirvModule module,
        uint accessChainResultId,
        StructuredBufferLayout layout,
        FlatAccessPath accessPath,
        ConstantMaps constants)
    {
        bool foundLoad = false;
        foreach (SpirvInstruction instruction in module.Instructions)
        {
            if (instruction.OpCode != SpvOpCode.OpLoad || instruction.Words.Length < 4 || instruction[3] != accessChainResultId)
            {
                continue;
            }

            foundLoad = true;
            uint loadResultId = instruction[2];

            // Build the set of "composite-extract-source ids" that resolve back to this Load.
            // The trivial case is the Load result itself. We also accept one hop through
            // OpBitcast — a common HLSL→SPIR-V pattern is `asuint(float4)` / `asfloat(uint4)`
            // which compiles to OpLoad → OpBitcast (preserves vector width, only changes the
            // scalar element type) → OpCompositeExtract. Without following the Bitcast we
            // misclassify the Load as having "no CompositeExtract users" and the entire CB
            // gets rejected, which the user sees as `UnityPerMaterial_m0[5]` instead of named
            // members.
            var compositeExtractSources = new HashSet<uint> { loadResultId };
            foreach (SpirvInstruction maybeBitcast in module.Instructions)
            {
                if (maybeBitcast.OpCode == SpvOpCode.OpBitcast
                    && maybeBitcast.Words.Length >= 4
                    && maybeBitcast[3] == loadResultId)
                {
                    compositeExtractSources.Add(maybeBitcast[2]);
                }
            }

            bool hasCompositeExtractUsers = false;
            foreach (SpirvInstruction user in module.Instructions)
            {
                if (user.OpCode != SpvOpCode.OpCompositeExtract || user.Words.Length < 5 || !compositeExtractSources.Contains(user[3]))
                {
                    continue;
                }

                hasCompositeExtractUsers = true;
                FlatAccessPath directAccessPath = accessPath.Clone();
                directAccessPath.ExtraIndices.AddRange(user.Words.Skip(4).Select(static value => checked((int)value)));
                if (TranslateFlatAccess(layout, directAccessPath, constants) == null)
                {
                    return false;
                }
            }

            if (!hasCompositeExtractUsers)
            {
                return false;
            }
        }

        return foundLoad;
    }

    private static StructuredAccessTranslation? TranslateDynamicFlatAccess(StructuredBufferLayout layout, FlatAccessPath accessPath, ConstantMaps constants, int componentIndex)
    {
        if (accessPath.Slot.DynamicIndexId == 0 || accessPath.Slot.DynamicIndexStride <= 0)
        {
            return null;
        }

        for (int memberIndex = 0; memberIndex < layout.Members.Count; memberIndex++)
        {
            StructuredMemberLayout member = layout.Members[memberIndex];
            if (member.LogicalType.Kind == LogicalTypeKind.Struct || Math.Max(member.LogicalType.ArrayLength, 1) <= 1)
            {
                continue;
            }

            int elementRegisterStride = GetDynamicElementRegisterStride(member);
            if (elementRegisterStride != accessPath.Slot.DynamicIndexStride)
            {
                continue;
            }

            int localRegisterOffset = accessPath.Slot.ConstantRegisterOffset - member.RegisterOffset;
            if (localRegisterOffset < 0 || localRegisterOffset >= elementRegisterStride)
            {
                continue;
            }

            if (!TryGetConstantId(constants, (uint)memberIndex, out uint memberIndexConstantId))
            {
                continue;
            }

            StructuredAccessTranslation? memberTranslation = TranslateDynamicArrayMemberAccess(member, localRegisterOffset, componentIndex, accessPath.ExtraIndices, accessPath.Slot.DynamicIndexId, constants);
            if (memberTranslation == null)
            {
                continue;
            }

            var indices = new List<uint> { memberIndexConstantId };
            indices.AddRange(memberTranslation.Indices);
            return new StructuredAccessTranslation
            {
                Indices = indices,
                MemberTypeId = memberTranslation.MemberTypeId
            };
        }

        for (int memberIndex = 0; memberIndex < layout.Members.Count; memberIndex++)
        {
            StructuredMemberLayout member = layout.Members[memberIndex];
            if (member.LogicalType.Kind != LogicalTypeKind.Struct || Math.Max(member.LogicalType.ArrayLength, 1) <= 1)
            {
                continue;
            }

            int elementRegisterStride = Math.Max(1, (member.LogicalType.StructByteSize + 15) / 16);
            int localRegisterOffset = accessPath.Slot.ConstantRegisterOffset - member.RegisterOffset;
            if (localRegisterOffset < 0 || localRegisterOffset >= elementRegisterStride)
            {
                continue;
            }

            if (elementRegisterStride != accessPath.Slot.DynamicIndexStride)
            {
                continue;
            }

            if (accessPath.ExtraIndices.Count > 1)
            {
                return null;
            }

            List<StructuredMemberLayout>? children = member.LogicalType.StructMembers;
            if (children == null)
            {
                return null;
            }

            int localByteOffset = (localRegisterOffset * 16) + (componentIndex * 4);
            for (int childIndex = 0; childIndex < children.Count; childIndex++)
            {
                StructuredMemberLayout child = children[childIndex];
                if (localByteOffset < child.ByteOffset || localByteOffset >= child.ByteOffset + GetMemberSpanBytes(child))
                {
                    continue;
                }

                if (!TryGetConstantId(constants, (uint)childIndex, out uint childIndexConstantId))
                {
                    continue;
                }

                StructuredAccessTranslation? childTranslation = TranslateMemberAccess(
                    child,
                    localRegisterOffset,
                    componentIndex,
                    accessPath.ExtraIndices,
                    constants);
                if (childTranslation == null)
                {
                    continue;
                }

                if (!TryGetConstantId(constants, (uint)memberIndex, out uint memberIndexConstantId))
                {
                    continue;
                }

                var indices = new List<uint> { memberIndexConstantId, accessPath.Slot.DynamicIndexId, childIndexConstantId };
                indices.AddRange(childTranslation.Indices);
                return new StructuredAccessTranslation
                {
                    Indices = indices,
                    MemberTypeId = childTranslation.MemberTypeId
                };
            }
        }

        return null;
    }

    private static int GetDynamicElementRegisterStride(StructuredMemberLayout member)
    {
        if (member.LogicalType.Kind == LogicalTypeKind.Matrix)
        {
            return Math.Max(1, member.LogicalType.Columns);
        }

        int elementByteSize = member.LogicalType.ArrayLength > 1
            ? Math.Max(4, member.LogicalType.DeclaredByteSize / Math.Max(member.LogicalType.ArrayLength, 1))
            : Math.Max(member.LogicalType.DeclaredByteSize, 4);
        return Math.Max(1, (elementByteSize + 15) / 16);
    }

    private static StructuredAccessTranslation? TranslateDynamicArrayMemberAccess(
        StructuredMemberLayout member,
        int localRegisterOffset,
        int componentIndex,
        List<int> extraIndices,
        uint dynamicIndexId,
        ConstantMaps constants)
    {
        if (extraIndices.Count > 1)
        {
            return null;
        }

        if (member.LogicalType.Kind == LogicalTypeKind.Matrix)
        {
            if (localRegisterOffset < 0 || localRegisterOffset >= member.LogicalType.Columns)
            {
                return null;
            }

            if (!TryGetConstantId(constants, (uint)localRegisterOffset, out uint registerConstantId))
            {
                return null;
            }

            if (extraIndices.Count == 0)
            {
                // Column access (no per-component extra): ptr-vec(rowCount) result.
                if (member.ColumnVectorTypeId == 0)
                {
                    return null;
                }

                return new StructuredAccessTranslation
                {
                    Indices = [dynamicIndexId, registerConstantId],
                    MemberTypeId = member.ColumnVectorTypeId
                };
            }

            if (componentIndex < 0 || componentIndex >= member.LogicalType.Rows || member.ScalarTypeId == 0 || !TryGetConstantId(constants, (uint)componentIndex, out uint componentConstantId))
            {
                return null;
            }

            // Component access: matrix[col][component] yields a scalar.
            return new StructuredAccessTranslation
            {
                Indices = [dynamicIndexId, registerConstantId, componentConstantId],
                MemberTypeId = member.ScalarTypeId
            };
        }

        if (localRegisterOffset != 0)
        {
            return null;
        }

        if (member.LogicalType.Kind == LogicalTypeKind.Scalar)
        {
            int memberComponentOffset = (member.ByteOffset % 16) / 4;
            return componentIndex == memberComponentOffset
                ? new StructuredAccessTranslation
                {
                    Indices = [dynamicIndexId],
                    MemberTypeId = member.ResolvedTypeId
                }
                : null;
        }

        if (member.LogicalType.Kind == LogicalTypeKind.Vector)
        {
            int memberComponentOffset = (member.ByteOffset % 16) / 4;

            if (extraIndices.Count == 0)
            {
                // Bare access into a dynamic-array vec4 member: ptr-vec4 result, only the array
                // index. Adding a component index here would invalidate the access chain (see
                // TranslateMemberAccess vec4 fix).
                return componentIndex == memberComponentOffset
                    ? new StructuredAccessTranslation
                    {
                        Indices = [dynamicIndexId],
                        MemberTypeId = member.ResolvedTypeId
                    }
                    : null;
            }

            int relativeComponentIndex = componentIndex - memberComponentOffset;
            if (relativeComponentIndex < 0 || relativeComponentIndex >= member.LogicalType.Rows || member.ScalarTypeId == 0 || !TryGetConstantId(constants, (uint)relativeComponentIndex, out uint componentConstantId))
            {
                return null;
            }

            return new StructuredAccessTranslation
            {
                Indices = [dynamicIndexId, componentConstantId],
                MemberTypeId = member.ScalarTypeId
            };
        }

        return null;
    }

    private static bool TryParseSlotExpression(uint operandId, ConstantMaps constants, out SlotExpression expression)
    {
        expression = null!;
        if (constants.IdToValue.TryGetValue(operandId, out uint constantValue))
        {
            expression = new SlotExpression { ConstantRegisterOffset = checked((int)constantValue) };
            return true;
        }

        if (TryDecomposeLinearIndexExpression(constants.Definitions, constants.IdToValue, operandId, out uint dynamicIndexId, out int dynamicStride, out int constantOffset))
        {
            expression = new SlotExpression
            {
                DynamicIndexId = dynamicIndexId,
                DynamicIndexStride = dynamicStride,
                ConstantRegisterOffset = constantOffset
            };
            return true;
        }

        return false;
    }

    private static bool TryDecomposeLinearIndexExpression(
        Dictionary<uint, SpirvInstruction> definitions,
        Dictionary<uint, uint> constants,
        uint valueId,
        out uint dynamicIndexId,
        out int dynamicStride,
        out int constantOffset)
    {
        dynamicIndexId = 0;
        dynamicStride = 0;
        constantOffset = 0;

        if (!definitions.TryGetValue(valueId, out SpirvInstruction? definition))
        {
            return false;
        }

        if ((definition.OpCode == OpIAdd || definition.OpCode == OpISub || definition.OpCode == OpBitwiseOr) && definition.Words.Length >= 5)
        {
            uint left = definition[3];
            uint right = definition[4];
            // OpBitwiseOr behaves identically to OpIAdd when the right-hand constant is
            // smaller than the alignment of the left-hand expression — DXC frequently
            // emits `(i << k) | c` (where c < 2^k) for `i * stride + c` because the
            // bits don't overlap, so `|` is equivalent to `+`. Required to recognise
            // Unity instancing access patterns of the form `cb[(instanceId << 4) | n]`.
            if (constants.TryGetValue(right, out uint rightConst) && TryDecomposeLinearIndexExpression(definitions, constants, left, out dynamicIndexId, out dynamicStride, out constantOffset))
            {
                constantOffset += definition.OpCode == OpISub ? -checked((int)rightConst) : checked((int)rightConst);
                return true;
            }

            if ((definition.OpCode == OpIAdd || definition.OpCode == OpBitwiseOr) && constants.TryGetValue(left, out uint leftConst) && TryDecomposeLinearIndexExpression(definitions, constants, right, out dynamicIndexId, out dynamicStride, out constantOffset))
            {
                constantOffset += checked((int)leftConst);
                return true;
            }

            // dynamic + dynamic: treat the whole expression as opaque dynamic id (stride=1, offset=0).
            // Required for shaders that compute the per-element index via two runtime values
            // (e.g. clusterIndex + lightOffset) and then add a constant per packed-array member offset.
        }

        if ((definition.OpCode == OpIMul || definition.OpCode == OpShiftLeftLogical) && definition.Words.Length >= 5)
        {
            uint left = definition[3];
            uint right = definition[4];
            if (constants.TryGetValue(right, out uint rightConst))
            {
                dynamicIndexId = left;
                dynamicStride = definition.OpCode == OpShiftLeftLogical ? 1 << checked((int)rightConst) : checked((int)rightConst);
                constantOffset = 0;
                return true;
            }

            if (definition.OpCode == OpIMul && constants.TryGetValue(left, out uint leftConst))
            {
                dynamicIndexId = right;
                dynamicStride = checked((int)leftConst);
                constantOffset = 0;
                return true;
            }

            // dynamic * dynamic: opaque dynamic id (stride=1, offset=0). Same rationale as IAdd.
        }

        dynamicIndexId = valueId;
        dynamicStride = 1;
        constantOffset = 0;
        return true;
    }

    private static uint FindOrCreateUniformPointerType(SpirvModule module, uint memberTypeId)
    {
        foreach (SpirvInstruction instruction in module.Instructions)
        {
            if (instruction.OpCode == SpvOpCode.OpTypePointer && instruction.Words.Length >= 4 && instruction[2] == SpvOpCode.StorageClassUniform && instruction[3] == memberTypeId)
            {
                return instruction[1];
            }
        }

        uint resultId = module.AllocateId();
        module.Instructions.Insert(module.FindTypeSectionEndIndex(), new SpirvInstruction
        {
            OpCode = SpvOpCode.OpTypePointer,
            Words =
            [
                SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypePointer, 4),
                resultId,
                SpvOpCode.StorageClassUniform,
                memberTypeId
            ]
        });
        return resultId;
    }

    private static bool TryGetConstantId(ConstantMaps constants, uint value, out uint constantId)
    {
        return constants.ValueToId.TryGetValue(value, out constantId);
    }

    private sealed class ModuleAnalysis
    {
        public Dictionary<uint, (int? Set, int? Binding)> SetBindingById { get; } = new();
        public Dictionary<uint, (uint StorageClass, uint TypeId)> PointerTypes { get; } = new();
        public Dictionary<uint, uint> VariablePointerTypes { get; } = new();
        public Dictionary<uint, uint[]> StructMembers { get; } = new();
        public Dictionary<uint, (uint ComponentTypeId, uint ComponentCount)> VectorShapes { get; } = new();
        public Dictionary<uint, (uint ElementTypeId, uint LengthId)> ArrayTypes { get; } = new();
        public Dictionary<uint, uint> Constants { get; } = new();
        public Dictionary<uint, uint> ArrayStrides { get; } = new();

        public bool TryGetVectorShape(uint vectorTypeId, out uint componentTypeId, out uint componentCount)
        {
            if (VectorShapes.TryGetValue(vectorTypeId, out (uint ComponentTypeId, uint ComponentCount) shape))
            {
                componentTypeId = shape.ComponentTypeId;
                componentCount = shape.ComponentCount;
                return true;
            }

            componentTypeId = 0;
            componentCount = 0;
            return false;
        }
    }

    private sealed class TypeInfo
    {
        public uint FloatTypeId { get; set; }
        public uint IntTypeId { get; set; }
        public uint UIntTypeId { get; set; }
        public Dictionary<int, uint> FloatVectorTypeIds { get; } = new();
        public Dictionary<int, uint> IntVectorTypeIds { get; } = new();
        public Dictionary<int, uint> UIntVectorTypeIds { get; } = new();
        public Dictionary<(int Rows, int Columns), uint> MatrixTypeIds { get; } = new();
    }

    private enum ScalarKind
    {
        Float,
        Int,
        UInt
    }

    private enum LogicalTypeKind
    {
        Scalar,
        Vector,
        Matrix,
        Struct
    }

    private sealed class MemberLogicalType
    {
        public LogicalTypeKind Kind { get; set; }
        public ScalarKind ScalarKind { get; set; }
        public int Rows { get; set; }
        public int Columns { get; set; }
        public int ArrayLength { get; set; }
        public int SecondaryArrayLength { get; set; }
        public int DeclaredByteSize { get; set; }
        public int UscIndex { get; set; }
        public bool IsMatrix { get; set; }
        public List<StructuredMemberLayout>? StructMembers { get; set; }
        public int StructByteSize { get; set; }
        public string StructName { get; set; } = string.Empty;
    }

    private sealed class StructuredMemberLayout
    {
        public string Name { get; set; } = string.Empty;
        public int ByteOffset { get; set; }
        public NumericShaderParameter Metadata { get; set; } = null!;
        public MemberLogicalType LogicalType { get; set; } = null!;
        public int RegisterOffset { get; set; }
        public int RegisterCount { get; set; }
        public uint ResolvedTypeId { get; set; }
        // Scalar component type id for non-scalar members. Required so that a per-component access
        // chain into a vec4 / matrix member ends up with the correct ptr-scalar result type instead
        // of inheriting the parent vec4 / matrix type id (which would produce an invalid module that
        // crashes spirv-cross with "Cannot subdivide a scalar value").
        public uint ScalarTypeId { get; set; }
        // Column vector type id for matrix members. `matrix[col]` returns a vec(rowCount), and the
        // access chain that selects a column needs ptr-vec(rowCount), not ptr-matrix.
        public uint ColumnVectorTypeId { get; set; }
    }

    private sealed class StructuredBufferLayout
    {
        public List<StructuredMemberLayout> Members { get; } = new();
        public int RequiredRegisterCount { get; set; }
        public int MaxUsedRegisterCount { get; set; }
    }

    private sealed class BufferRewritePlan
    {
        public FlatUniformBufferInfo Info { get; set; } = null!;
        public StructuredBufferLayout Layout { get; set; } = null!;
        public uint NewStructTypeId { get; set; }
        public uint NewPointerTypeId { get; set; }
        public List<uint> MemberTypeIds { get; set; } = new();
    }

    private sealed class StructuredAccessTranslation
    {
        public List<uint> Indices { get; set; } = new();
        public uint MemberTypeId { get; set; }
    }

    private sealed class RewrittenAccessChainInfo
    {
        public uint AccessChainResultId { get; set; }
        public uint BaseVariableId { get; set; }
        public ushort InstructionOpCode { get; set; }
        public BufferRewritePlan Plan { get; set; } = null!;
        public FlatAccessPath OriginalAccessPath { get; set; } = null!;
        public StructuredAccessTranslation? Translation { get; set; }
    }

    private sealed class RewrittenLoadInfo
    {
        // Direct reference to the OpLoad instruction. We can't cache its position because
        // RewriteLoadsAndCompositeExtracts inserts new (access chain + load) pairs while iterating,
        // which shifts every later position in module.Instructions. The class reference, however,
        // stays valid across List.Insert.
        public SpirvInstruction Instruction { get; set; } = null!;
        public uint ResultId { get; set; }
        public uint OriginalResultTypeId { get; set; }
        public bool HasCompositeExtractUsers { get; set; }
        public RewrittenAccessChainInfo AccessChain { get; set; } = null!;
    }

    private sealed class FlatAccessPath
    {
        public SlotExpression Slot { get; set; } = new();
        public List<int> ExtraIndices { get; set; } = new();

        public FlatAccessPath Clone()
        {
            return new FlatAccessPath
            {
                Slot = new SlotExpression
                {
                    ConstantRegisterOffset = Slot.ConstantRegisterOffset,
                    DynamicIndexId = Slot.DynamicIndexId,
                    DynamicIndexStride = Slot.DynamicIndexStride
                },
                ExtraIndices = ExtraIndices.ToList()
            };
        }
    }

    private sealed class SlotExpression
    {
        public int ConstantRegisterOffset { get; set; }
        public uint DynamicIndexId { get; set; }
        public int DynamicIndexStride { get; set; }
    }

    private sealed class ConstantMaps
    {
        public Dictionary<uint, uint> IdToValue { get; } = new();
        public Dictionary<uint, uint> ValueToId { get; } = new();
        public Dictionary<uint, SpirvInstruction> Definitions { get; } = new();
    }

    private sealed class FlatUniformBufferInfo
    {
        public uint VariableId { get; set; }
        public uint PointerTypeId { get; set; }
        public uint StructTypeId { get; set; }
        public uint ArrayTypeId { get; set; }
        public uint ElementTypeId { get; set; }
        public int ArrayLength { get; set; }
        public int ArrayStride { get; set; }
        public FlatResourceBinding Metadata { get; set; } = null!;
        public ConstantBuffer ConstantBuffer { get; set; } = null!;
    }

    private sealed class FlatResourceBinding
    {
        public string Name { get; set; } = string.Empty;
        public int Binding { get; set; }
        public int Set { get; set; }
    }
}
