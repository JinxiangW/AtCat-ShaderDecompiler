namespace Ruri.ShaderDecompiler.Spirv;

internal sealed class StructuredCBufferRewriter
{
    private const ushort OpIAdd = 128;
    private const ushort OpISub = 130;
    private const ushort OpIMul = 132;
    private const ushort OpShiftLeftLogical = 196;
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

        summary.Add($"Metadata resources={metadata.Resources.Count}, constantBuffers={metadata.ConstantBuffers.Count}");
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
                memberTypeIds.Add(memberTypeId);
            }

            if (typeResolutionFailed)
            {
                summary.Add($"[{flatBuffer.Metadata.Name}] member type resolution failed");
                continue;
            }

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
        foreach (ResourceBinding resource in metadata.Resources.Where(static resource => resource.RegisterType == 'b'))
        {
            ConstantBuffer? constantBuffer = metadata.ConstantBuffers.FirstOrDefault(cb => string.Equals(cb.Name, resource.Name, StringComparison.Ordinal));
            if (constantBuffer == null)
            {
                summary.Add($"[{resource.Name}] no USC constant buffer metadata found");
                continue;
            }

            uint? variableId = analysis.SetBindingById
                .Where(static entry => entry.Value.Set.HasValue && entry.Value.Binding.HasValue)
                .Where(entry => entry.Value.Set == resource.Set && entry.Value.Binding == resource.Binding)
                .Select(entry => entry.Key)
                .FirstOrDefault();

            if (variableId == 0)
            {
                summary.Add($"[{resource.Name}] no decorated id for set={resource.Set} binding={resource.Binding}");
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
                Metadata = resource,
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
            int? resultIdIndex = GetResultIdIndex(instruction.OpCode, instruction.Words.Length);
            if (resultIdIndex.HasValue)
            {
                result.Definitions[instruction[resultIdIndex.Value]] = instruction;
            }

            if (instruction.OpCode == SpvOpCode.OpConstant && instruction.Words.Length >= 4)
            {
                result.IdToValue[instruction[2]] = instruction[3];
                result.ValueToId[instruction[3]] = instruction[2];
            }
        }

        return result;
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

        return layout.MaxUsedRegisterCount > 0 && layout.MaxUsedRegisterCount <= flatBuffer.ArrayLength;
    }

    private static StructuredBufferLayout? BuildStructuredLayout(FlatUniformBufferInfo flatBuffer)
    {
        if (flatBuffer.ConstantBuffer.CBParams.Count == 0 && flatBuffer.ConstantBuffer.StructParams.Count == 0)
        {
            return null;
        }

        var layout = new StructuredBufferLayout();
        var members = new List<StructuredMemberLayout>();
        int maxAvailableByteOffset = flatBuffer.ArrayLength * 16;

        foreach (ConstantBufferParameter parameter in flatBuffer.ConstantBuffer.CBParams.OrderBy(static parameter => parameter.Index))
        {
            if (parameter.Index < 0 || parameter.Index >= maxAvailableByteOffset)
            {
                continue;
            }

            MemberLogicalType? logicalType = TryCreateLogicalTypeFromMetadata(parameter);
            if (logicalType == null)
            {
                return null;
            }

            members.Add(new StructuredMemberLayout
            {
                Metadata = parameter,
                LogicalType = logicalType,
                RegisterOffset = parameter.Index / 16,
                RegisterCount = GetRequiredRegisterCount(parameter.Index, logicalType)
            });
        }

        foreach (StructParameter structParameter in flatBuffer.ConstantBuffer.StructParams.OrderBy(static parameter => parameter.Index))
        {
            if (structParameter.Index < 0 || structParameter.Index >= maxAvailableByteOffset || structParameter.CBParams.Count == 0)
            {
                continue;
            }

            foreach (ConstantBufferParameter child in structParameter.CBParams.OrderBy(static parameter => parameter.Index))
            {
                if (child.Index < 0 || child.Index >= maxAvailableByteOffset)
                {
                    continue;
                }

                MemberLogicalType? logicalType = TryCreateLogicalTypeFromMetadata(child);
                if (logicalType == null)
                {
                    return null;
                }

                members.Add(new StructuredMemberLayout
                {
                    Metadata = child,
                    LogicalType = logicalType,
                    RegisterOffset = child.Index / 16,
                    RegisterCount = GetRequiredRegisterCount(child.Index, logicalType)
                });
            }
        }

        members.Sort(static (left, right) => left.Metadata.Index.CompareTo(right.Metadata.Index));
        if (members.Count == 0)
        {
            return null;
        }

        int maxUsedByteOffset = 0;
        foreach (StructuredMemberLayout member in members)
        {
            layout.Members.Add(member);
            maxUsedByteOffset = Math.Max(maxUsedByteOffset, member.Metadata.Index + GetMemberSpanBytes(member));
        }

        layout.RequiredRegisterCount = Math.Max(1, (maxUsedByteOffset + 15) / 16);
        layout.MaxUsedRegisterCount = layout.RequiredRegisterCount;
        return layout;
    }

    private static int GetMemberSpanBytes(StructuredMemberLayout member)
    {
        return member.LogicalType.Kind == LogicalTypeKind.Matrix
            ? member.LogicalType.Columns * 16 * Math.Max(member.LogicalType.ArrayLength, 1)
            : member.LogicalType.DeclaredByteSize;
    }

    private static MemberLogicalType? TryCreateLogicalTypeFromMetadata(ConstantBufferParameter parameter)
    {
        if (parameter.Rows <= 0 || parameter.Columns <= 0)
        {
            return null;
        }

        ScalarKind? scalarKind = TryResolveScalarKind(parameter.ParamType);
        if (scalarKind == null)
        {
            return null;
        }

        return new MemberLogicalType
        {
            Kind = parameter.IsMatrix
                ? LogicalTypeKind.Matrix
                : parameter.Rows == 1 ? LogicalTypeKind.Scalar : LogicalTypeKind.Vector,
            ScalarKind = scalarKind.Value,
            Rows = parameter.Rows,
            Columns = parameter.Columns,
            ArrayLength = Math.Max(parameter.ArraySize, 1),
            DeclaredByteSize = GetDeclaredByteSize(parameter),
            UscIndex = parameter.Index,
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

    private static int GetDeclaredByteSize(ConstantBufferParameter parameter)
    {
        int arrayLength = Math.Max(parameter.ArraySize, 1);
        if (parameter.IsMatrix)
        {
            return parameter.Columns * 16 * arrayLength;
        }

        return parameter.Rows * parameter.Columns * arrayLength * 4;
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
            _ => 0
        };

        if (baseTypeId == 0)
        {
            return 0;
        }

        if (logicalType.ArrayLength > 1)
        {
            int stride = logicalType.Kind == LogicalTypeKind.Matrix
                ? logicalType.Columns * 16
                : logicalType.Kind == LogicalTypeKind.Vector && logicalType.Rows == 4
                    ? 16
                    : logicalType.DeclaredByteSize / logicalType.ArrayLength;
            return EnsureArrayType(module, types, baseTypeId, logicalType.ArrayLength, Math.Max(stride, 4));
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
                    (uint)member.Metadata.Index
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
        module.InsertDebugName(rewrite.NewStructTypeId, rewrite.Info.Metadata.Name);
        module.InsertDebugName(rewrite.Info.VariableId, $"__ruri_{rewrite.Info.Metadata.Name}_var");
        for (int memberIndex = 0; memberIndex < rewrite.Layout.Members.Count; memberIndex++)
        {
            string name = rewrite.Layout.Members[memberIndex].Metadata.ParamName;
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

            if ((instruction.OpCode != SpvOpCode.OpAccessChain && instruction.OpCode != SpvOpCode.OpInBoundsAccessChain) || instruction.Words.Length < 5)
            {
                continue;
            }

            if (!rewriteByVariableId.TryGetValue(instruction[3], out BufferRewritePlan? plan) || !TryParseFlatAccessChain(instruction, constants, out FlatAccessPath accessPath))
            {
                continue;
            }

            StructuredAccessTranslation? translated = TranslateFlatAccess(plan.Layout, accessPath, constants);
            if (translated == null || !pointerTypeByMemberTypeId.TryGetValue(translated.MemberTypeId, out uint pointerTypeId))
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
        var compositeExtractUseCounts = new Dictionary<uint, int>();
        Dictionary<uint, int> totalUseCounts = CountResultUses(module);

        for (int index = 0; index < module.Instructions.Count; index++)
        {
            SpirvInstruction instruction = module.Instructions[index];
            if (instruction.OpCode == SpvOpCode.OpLoad && instruction.Words.Length >= 4 && rewrittenAccessChains.TryGetValue(instruction[3], out RewrittenAccessChainInfo? accessInfo))
            {
                loadInfos[instruction[2]] = new RewrittenLoadInfo
                {
                    InstructionIndex = index,
                    ResultId = instruction[2],
                    OriginalResultTypeId = instruction[1],
                    HasCompositeExtractUsers = false,
                    AccessChain = accessInfo
                };
                continue;
            }

            if (instruction.OpCode == SpvOpCode.OpCompositeExtract && instruction.Words.Length >= 5 && loadInfos.TryGetValue(instruction[3], out RewrittenLoadInfo? loadInfo))
            {
                loadInfo.HasCompositeExtractUsers = true;
                compositeExtractUseCounts[loadInfo.ResultId] = compositeExtractUseCounts.TryGetValue(loadInfo.ResultId, out int count) ? count + 1 : 1;

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

        foreach (RewrittenLoadInfo loadInfo in loadInfos.Values)
        {
            SpirvInstruction loadInstruction = module.Instructions[loadInfo.InstructionIndex];
            if (loadInstruction.OpCode != SpvOpCode.OpLoad || loadInstruction.Words.Length < 4)
            {
                continue;
            }

            if (!loadInfo.HasCompositeExtractUsers)
            {
                loadInstruction[1] = loadInfo.AccessChain.Translation.MemberTypeId;
                continue;
            }

            int compositeUsers = compositeExtractUseCounts.TryGetValue(loadInfo.ResultId, out int compositeCount) ? compositeCount : 0;
            int totalUsers = totalUseCounts.TryGetValue(loadInfo.ResultId, out int totalCount) ? totalCount : 0;
            if (compositeUsers == totalUsers)
            {
                loadInstruction.OpCode = SpvOpCode.OpNop;
                loadInstruction.Words = [SpvOpCode.MakeInstructionWord(SpvOpCode.OpNop, 1)];
            }
        }
    }

    private static Dictionary<uint, int> CountResultUses(SpirvModule module)
    {
        var uses = new Dictionary<uint, int>();
        foreach (SpirvInstruction instruction in module.Instructions)
        {
            int? resultIdIndex = GetResultIdIndex(instruction.OpCode, instruction.Words.Length);
            int? resultTypeIndex = GetResultTypeIdIndex(instruction.OpCode, instruction.Words.Length);
            for (int operandIndex = 1; operandIndex < instruction.Words.Length; operandIndex++)
            {
                if ((resultIdIndex.HasValue && operandIndex == resultIdIndex.Value) || (resultTypeIndex.HasValue && operandIndex == resultTypeIndex.Value))
                {
                    continue;
                }

                uint operand = instruction[operandIndex];
                uses[operand] = uses.TryGetValue(operand, out int count) ? count + 1 : 1;
            }
        }

        return uses;
    }

    private static int? GetResultTypeIdIndex(ushort opCode, int wordCount)
    {
        return opCode switch
        {
            SpvOpCode.OpConstant => wordCount >= 3 ? 1 : null,
            SpvOpCode.OpConstantComposite => wordCount >= 3 ? 1 : null,
            SpvOpCode.OpLoad => wordCount >= 3 ? 1 : null,
            SpvOpCode.OpAccessChain => wordCount >= 3 ? 1 : null,
            SpvOpCode.OpInBoundsAccessChain => wordCount >= 3 ? 1 : null,
            SpvOpCode.OpCompositeExtract => wordCount >= 3 ? 1 : null,
            OpIAdd => wordCount >= 3 ? 1 : null,
            OpISub => wordCount >= 3 ? 1 : null,
            OpIMul => wordCount >= 3 ? 1 : null,
            OpShiftLeftLogical => wordCount >= 3 ? 1 : null,
            _ => null
        };
    }

    private static int? GetResultIdIndex(ushort opCode, int wordCount)
    {
        return opCode switch
        {
            SpvOpCode.OpTypeVoid => wordCount >= 2 ? 1 : null,
            SpvOpCode.OpTypeBool => wordCount >= 2 ? 1 : null,
            SpvOpCode.OpTypeInt => wordCount >= 2 ? 1 : null,
            SpvOpCode.OpTypeFloat => wordCount >= 2 ? 1 : null,
            SpvOpCode.OpTypeVector => wordCount >= 2 ? 1 : null,
            SpvOpCode.OpTypeMatrix => wordCount >= 2 ? 1 : null,
            SpvOpCode.OpTypeImage => wordCount >= 2 ? 1 : null,
            SpvOpCode.OpTypeSampler => wordCount >= 2 ? 1 : null,
            SpvOpCode.OpTypeSampledImage => wordCount >= 2 ? 1 : null,
            SpvOpCode.OpTypeArray => wordCount >= 2 ? 1 : null,
            SpvOpCode.OpTypeRuntimeArray => wordCount >= 2 ? 1 : null,
            SpvOpCode.OpTypeStruct => wordCount >= 2 ? 1 : null,
            SpvOpCode.OpTypeOpaque => wordCount >= 2 ? 1 : null,
            SpvOpCode.OpTypePointer => wordCount >= 2 ? 1 : null,
            SpvOpCode.OpConstant => wordCount >= 3 ? 2 : null,
            SpvOpCode.OpConstantComposite => wordCount >= 3 ? 2 : null,
            SpvOpCode.OpVariable => wordCount >= 3 ? 2 : null,
            SpvOpCode.OpLoad => wordCount >= 3 ? 2 : null,
            SpvOpCode.OpAccessChain => wordCount >= 3 ? 2 : null,
            SpvOpCode.OpInBoundsAccessChain => wordCount >= 3 ? 2 : null,
            SpvOpCode.OpCompositeExtract => wordCount >= 3 ? 2 : null,
            OpIAdd => wordCount >= 3 ? 2 : null,
            OpISub => wordCount >= 3 ? 2 : null,
            OpIMul => wordCount >= 3 ? 2 : null,
            OpShiftLeftLogical => wordCount >= 3 ? 2 : null,
            _ => null
        };
    }

    private static bool TryParseFlatAccessChain(SpirvInstruction instruction, ConstantMaps constants, out FlatAccessPath accessPath)
    {
        accessPath = null!;
        if (instruction.Words.Length < 5)
        {
            return false;
        }

        int slotOperandIndex = 4;
        if (instruction.Words.Length >= 6 && constants.IdToValue.TryGetValue(instruction.Words[4], out uint firstValue) && firstValue == 0)
        {
            slotOperandIndex = 5;
        }

        if (!TryParseSlotExpression(instruction.Words[slotOperandIndex], constants, out SlotExpression slotExpression))
        {
            return false;
        }

        var extraIndices = new List<int>();
        for (int operandIndex = slotOperandIndex + 1; operandIndex < instruction.Words.Length; operandIndex++)
        {
            if (!constants.IdToValue.TryGetValue(instruction.Words[operandIndex], out uint value))
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

            List<int>? logicalIndices = TranslateMemberAccess(member, absoluteRegister, componentIndex, accessPath.ExtraIndices);
            if (logicalIndices == null || !TryGetConstantId(constants, (uint)memberIndex, out uint memberIndexConstantId))
            {
                continue;
            }

            var indices = new List<uint> { memberIndexConstantId };
            foreach (int logicalIndex in logicalIndices)
            {
                if (!TryGetConstantId(constants, (uint)logicalIndex, out uint logicalIndexConstantId))
                {
                    return null;
                }

                indices.Add(logicalIndexConstantId);
            }

            return new StructuredAccessTranslation
            {
                Indices = indices,
                MemberTypeId = member.ResolvedTypeId
            };
        }

        return null;
    }

    private static bool IsMemberByteMatch(StructuredMemberLayout member, int absoluteByteOffset, List<int> extraIndices)
    {
        int memberStart = member.Metadata.Index;
        int memberEnd = memberStart + Math.Max(member.LogicalType.DeclaredByteSize, 4);
        if (member.LogicalType.Kind == LogicalTypeKind.Matrix)
        {
            return absoluteByteOffset >= memberStart && absoluteByteOffset < memberEnd;
        }

        return extraIndices.Count == 0
            ? absoluteByteOffset == memberStart
            : absoluteByteOffset >= memberStart && absoluteByteOffset < memberEnd;
    }

    private static List<int>? TranslateMemberAccess(StructuredMemberLayout member, int absoluteRegister, int componentIndex, List<int> extraIndices)
    {
        int localRegister = absoluteRegister - member.RegisterOffset;
        int memberComponentOffset = (member.Metadata.Index % 16) / 4;
        List<int> trailingIndices = extraIndices.Count > 1 ? extraIndices.Skip(1).ToList() : [];

        if (member.LogicalType.Kind == LogicalTypeKind.Matrix)
        {
            if (localRegister < 0 || localRegister >= member.LogicalType.Columns)
            {
                return null;
            }

            if (extraIndices.Count == 0)
            {
                return [localRegister];
            }

            if (componentIndex < 0 || componentIndex >= member.LogicalType.Rows || trailingIndices.Count > 0)
            {
                return null;
            }

            return [localRegister, componentIndex];
        }

        if (member.RegisterCount == 1)
        {
            if (member.LogicalType.Kind == LogicalTypeKind.Scalar)
            {
                return componentIndex == memberComponentOffset && trailingIndices.Count == 0 ? [] : null;
            }

            if (member.LogicalType.Kind == LogicalTypeKind.Vector)
            {
                int relativeComponentIndex = componentIndex - memberComponentOffset;
                return relativeComponentIndex >= 0 && relativeComponentIndex < member.LogicalType.Rows && trailingIndices.Count == 0
                    ? [relativeComponentIndex]
                    : null;
            }
        }

        if (localRegister < 0 || localRegister >= member.RegisterCount || trailingIndices.Count > 0)
        {
            return null;
        }

        return extraIndices.Count > 0 ? [localRegister, componentIndex] : [localRegister];
    }

    private static StructuredAccessTranslation? TranslateDynamicFlatAccess(StructuredBufferLayout layout, FlatAccessPath accessPath, ConstantMaps constants, int componentIndex)
    {
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

        if ((definition.OpCode == OpIAdd || definition.OpCode == OpISub) && definition.Words.Length >= 5)
        {
            uint left = definition[3];
            uint right = definition[4];
            if (constants.TryGetValue(right, out uint rightConst) && TryDecomposeLinearIndexExpression(definitions, constants, left, out dynamicIndexId, out dynamicStride, out constantOffset))
            {
                constantOffset += definition.OpCode == OpISub ? -checked((int)rightConst) : checked((int)rightConst);
                return true;
            }

            if (definition.OpCode == OpIAdd && constants.TryGetValue(left, out uint leftConst) && TryDecomposeLinearIndexExpression(definitions, constants, right, out dynamicIndexId, out dynamicStride, out constantOffset))
            {
                constantOffset += checked((int)leftConst);
                return true;
            }

            return false;
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

            return false;
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
        Matrix
    }

    private sealed class MemberLogicalType
    {
        public LogicalTypeKind Kind { get; set; }
        public ScalarKind ScalarKind { get; set; }
        public int Rows { get; set; }
        public int Columns { get; set; }
        public int ArrayLength { get; set; }
        public int DeclaredByteSize { get; set; }
        public int UscIndex { get; set; }
        public bool IsMatrix { get; set; }
    }

    private sealed class StructuredMemberLayout
    {
        public ConstantBufferParameter Metadata { get; set; } = null!;
        public MemberLogicalType LogicalType { get; set; } = null!;
        public int RegisterOffset { get; set; }
        public int RegisterCount { get; set; }
        public uint ResolvedTypeId { get; set; }
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
        public StructuredAccessTranslation Translation { get; set; } = null!;
    }

    private sealed class RewrittenLoadInfo
    {
        public int InstructionIndex { get; set; }
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
        public ResourceBinding Metadata { get; set; } = null!;
        public ConstantBuffer ConstantBuffer { get; set; } = null!;
    }
}
