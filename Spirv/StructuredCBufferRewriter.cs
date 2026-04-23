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
        var analysis = AnalyzeModule(module);
        summary.Add($"Metadata resources={metadata.Resources.Count}, constantBuffers={metadata.ConstantBuffers.Count}");
        summary.Add($"Analyzed decoratedIds={analysis.SetBindingById.Count}, variables={analysis.VariablePointerTypes.Count}, pointers={analysis.PointerTypes.Count}, structs={analysis.StructMembers.Count}, arrays={analysis.ArrayTypes.Count}");
        var flatBuffers = BuildFlatUniformBufferMap(metadata, analysis, summary);
        var metadataBuffers = BuildMetadataBufferCandidates(metadata, summary);
        if (flatBuffers.Count == 0)
        {
            LastRewriteSummary = summary.Count == 0
                ? "No flat uniform buffers matched metadata bindings."
                : string.Join(Environment.NewLine, summary);
            return spirv;
        }

        var constants = BuildConstantMaps(module);
        var types = AnalyzeTypes(module, analysis);
        var rewrites = new List<BufferRewritePlan>();

        var assignedMetadataNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (FlatUniformBufferInfo flatBuffer in flatBuffers.Values)
        {
            if (!TryPlanRewrite(module, types, constants, flatBuffer, metadataBuffers, assignedMetadataNames, summary, out BufferRewritePlan? plan) || plan == null)
            {
                continue;
            }

            rewrites.Add(plan);
            assignedMetadataNames.Add(plan.Info.Metadata.Name);
            _resolvedBufferNames[(plan.Info.ActualSet, plan.Info.ActualBinding)] = plan.Info.Metadata.Name;
            InsertStructuredType(module, plan);
            InsertStructuredNames(module, plan);
            summary.Add($"[{plan.Info.Metadata.Name}] rewrite planned with {plan.Layout.Members.Count} members");
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
                            if (!analysis.SetBindingById.ContainsKey(targetId))
                            {
                                analysis.SetBindingById[targetId] = (set, null);
                            }
                            else
                            {
                                analysis.SetBindingById[targetId] = (set, analysis.SetBindingById[targetId].Binding);
                            }
                        }
                        else if (decoration == SpvOpCode.DecorationBinding)
                        {
                            int binding = (int)instruction[3];
                            if (!analysis.SetBindingById.ContainsKey(targetId))
                            {
                                analysis.SetBindingById[targetId] = (null, binding);
                            }
                            else
                            {
                                analysis.SetBindingById[targetId] = (analysis.SetBindingById[targetId].Set, binding);
                            }
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

    private static Dictionary<(int Set, int Binding), FlatUniformBufferInfo> BuildFlatUniformBufferMap(ShaderSymbolData metadata, ModuleAnalysis analysis, List<string> summary)
    {
        var result = new Dictionary<(int Set, int Binding), FlatUniformBufferInfo>();
        foreach (ResourceBinding resource in metadata.Resources.Where(r => r.RegisterType == 'b'))
        {
            ConstantBuffer? constantBuffer = metadata.ConstantBuffers.FirstOrDefault(cb => string.Equals(cb.Name, resource.Name, StringComparison.Ordinal));
            if (constantBuffer == null)
            {
                summary.Add($"[{resource.Name}] no USC constant buffer metadata found");
                continue;
            }

            var candidateIds = analysis.SetBindingById
                .Where(kvp => kvp.Value.Set == resource.Set && kvp.Value.Binding == resource.Binding)
                .Select(kvp => kvp.Key)
                .ToList();

            if (candidateIds.Count == 0)
            {
                summary.Add($"[{resource.Name}] no decorated id for set={resource.Set} binding={resource.Binding}");
                continue;
            }

            bool matched = false;
            foreach (uint candidateId in candidateIds)
            {
                if (!analysis.VariablePointerTypes.TryGetValue(candidateId, out uint pointerTypeId))
                {
                    continue;
                }

                if (!analysis.PointerTypes.TryGetValue(pointerTypeId, out var pointerInfo) || pointerInfo.StorageClass != SpvOpCode.StorageClassUniform)
                {
                    continue;
                }

                if (!analysis.StructMembers.TryGetValue(pointerInfo.TypeId, out uint[]? members) || members.Length != 1)
                {
                    summary.Add($"[{resource.Name}] candidate variable {candidateId} is not a single-member wrapper struct");
                    continue;
                }

                uint arrayTypeId = members[0];
                if (!analysis.ArrayTypes.TryGetValue(arrayTypeId, out var arrayInfo) ||
                    !analysis.Constants.TryGetValue(arrayInfo.LengthId, out uint arrayLength))
                {
                    summary.Add($"[{resource.Name}] candidate variable {candidateId} member is not a fixed array type");
                    continue;
                }

                int arrayStride = analysis.ArrayStrides.TryGetValue(arrayTypeId, out uint declaredArrayStride)
                    ? checked((int)declaredArrayStride)
                    : 16;

                result[(resource.Set, resource.Binding)] = new FlatUniformBufferInfo
                {
                    ActualSet = resource.Set,
                    ActualBinding = resource.Binding,
                    VariableId = candidateId,
                    PointerTypeId = pointerTypeId,
                    StructTypeId = pointerInfo.TypeId,
                    ArrayTypeId = arrayTypeId,
                    ElementTypeId = arrayInfo.ElementTypeId,
                    ArrayLength = checked((int)arrayLength),
                    ArrayStride = arrayStride,
                    Metadata = resource,
                    ConstantBuffer = constantBuffer
                };

                matched = true;
                break;
            }

            if (!matched)
            {
                summary.Add($"[{resource.Name}] no uniform flat-array variable matched strict binding");
            }
        }

        return result;
    }

    private static List<MetadataBufferCandidate> BuildMetadataBufferCandidates(ShaderSymbolData metadata, List<string> summary)
    {
        var result = new List<MetadataBufferCandidate>();
        foreach (ResourceBinding resource in metadata.Resources.Where(r => r.RegisterType == 'b'))
        {
            ConstantBuffer? constantBuffer = metadata.ConstantBuffers.FirstOrDefault(cb => string.Equals(cb.Name, resource.Name, StringComparison.Ordinal));
            if (constantBuffer == null)
            {
                summary.Add($"[{resource.Name}] no USC constant buffer metadata found");
                continue;
            }

            result.Add(new MetadataBufferCandidate
            {
                Resource = resource,
                ConstantBuffer = constantBuffer
            });
        }

        return result;
    }

    private static bool TryPlanRewrite(
        SpirvModule module,
        TypeInfo types,
        ConstantMaps constants,
        FlatUniformBufferInfo actualBuffer,
        List<MetadataBufferCandidate> metadataBuffers,
        HashSet<string> assignedMetadataNames,
        List<string> summary,
        out BufferRewritePlan? plan)
    {
        plan = null;
        List<MetadataBufferCandidate> orderedCandidates = BuildOrderedMetadataCandidates(actualBuffer, metadataBuffers, assignedMetadataNames);
        List<string> failures = new();

        foreach (MetadataBufferCandidate metadataCandidate in orderedCandidates)
        {
            FlatUniformBufferInfo candidateBuffer = actualBuffer.WithMetadata(metadataCandidate.Resource, metadataCandidate.ConstantBuffer);
            if (!TryCreateRewritePlan(module, types, constants, candidateBuffer, out plan, out string failure))
            {
                failures.Add($"[{metadataCandidate.Resource.Name}] {failure}");
                continue;
            }

            if (!string.Equals(metadataCandidate.Resource.Name, actualBuffer.Metadata.Name, StringComparison.Ordinal))
            {
                summary.Add($"[{actualBuffer.Metadata.Name}] remapped binding {actualBuffer.Metadata.Set}:{actualBuffer.Metadata.Binding} -> {metadataCandidate.Resource.Name}");
            }

            return true;
        }

        foreach (string failure in failures)
        {
            summary.Add(failure);
        }

        return false;
    }

    private static List<MetadataBufferCandidate> BuildOrderedMetadataCandidates(
        FlatUniformBufferInfo actualBuffer,
        List<MetadataBufferCandidate> metadataBuffers,
        HashSet<string> assignedMetadataNames)
    {
        return metadataBuffers
            .Where(candidate => !assignedMetadataNames.Contains(candidate.Resource.Name) || string.Equals(candidate.Resource.Name, actualBuffer.Metadata.Name, StringComparison.Ordinal))
            .OrderByDescending(candidate => candidate.Resource.Set == actualBuffer.Metadata.Set && candidate.Resource.Binding == actualBuffer.Metadata.Binding)
            .ThenByDescending(candidate => string.Equals(candidate.Resource.Name, actualBuffer.Metadata.Name, StringComparison.Ordinal))
            .ThenBy(candidate => candidate.Resource.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static bool TryCreateRewritePlan(
        SpirvModule module,
        TypeInfo types,
        ConstantMaps constants,
        FlatUniformBufferInfo candidateBuffer,
        out BufferRewritePlan? plan,
        out string failure)
    {
        plan = null;
            StructuredBufferLayout? layout = BuildStructuredLayout(candidateBuffer, module, constants);
        if (layout == null)
        {
            failure = "layout build failed";
            return false;
        }

        if (!IsStrictFlatUniformArray(candidateBuffer, layout))
        {
            failure = $"strict flat array check failed: stride={candidateBuffer.ArrayStride}, arrayLength={candidateBuffer.ArrayLength}, requiredRegisters={layout.RequiredRegisterCount}";
            return false;
        }

        var memberTypeIds = new List<uint>(layout.Members.Count);
        foreach (StructuredMemberLayout member in layout.Members)
        {
            uint typeId = ResolveMemberTypeId(module, types, member);
            if (typeId == 0)
            {
                failure = "member type resolution failed";
                return false;
            }

            member.ResolvedTypeId = typeId;
            memberTypeIds.Add(typeId);
        }

        if (!CanRewriteAllAccessChains(module, candidateBuffer, layout, constants, out string? validationFailure))
        {
            failure = $"rewrite validation failed: {validationFailure}";
            return false;
        }

        plan = new BufferRewritePlan
        {
            Info = candidateBuffer,
            Layout = layout,
            NewStructTypeId = module.AllocateId(),
            NewPointerTypeId = module.AllocateId(),
            MemberTypeIds = memberTypeIds
        };
        failure = string.Empty;
        return true;
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
            if ((instruction.OpCode != SpvOpCode.OpAccessChain && instruction.OpCode != SpvOpCode.OpInBoundsAccessChain) || instruction.Words.Length < 5)
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

            if (TranslateFlatAccess(layout, accessPath, constants) == null)
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
        if (!analysis.TryGetVectorShape(vectorTypeId, out uint componentTypeId, out uint rowCount))
        {
            return;
        }

        if (componentTypeId != info.FloatTypeId)
        {
            return;
        }

        info.MatrixTypeIds[(checked((int)rowCount), checked((int)columnCount))] = typeId;
    }

    private static bool IsStrictFlatUniformArray(FlatUniformBufferInfo flatBuffer, StructuredBufferLayout layout)
    {
        if (flatBuffer.ArrayStride != 16)
        {
            return false;
        }

        if (flatBuffer.ConstantBuffer.Partial)
        {
            // Unity/USC partial constant buffers only serialize the members that were actually
            // referenced by the shader. Large unmapped gaps are expected and must stay unmapped;
            // forcing the reconstructed struct to cover the full flat array recreates m0[N]-style
            // output and destroys the exact symbol recovery we want.
            return layout.MaxUsedRegisterCount > 0 && layout.MaxUsedRegisterCount <= flatBuffer.ArrayLength;
        }

        return layout.RequiredRegisterCount == flatBuffer.ArrayLength;
    }

    private static StructuredBufferLayout? BuildStructuredLayout(FlatUniformBufferInfo flatBuffer, SpirvModule module, ConstantMaps constants)
    {
        if (flatBuffer.ConstantBuffer.CBParams.Count == 0 && flatBuffer.ConstantBuffer.StructParams.Count == 0)
        {
            return null;
        }

        var layout = new StructuredBufferLayout();
        var rawMembers = new List<StructuredMemberLayout>();
        int maxAvailableByteOffset = flatBuffer.ArrayLength * 16;

        if (TryCreateSyntheticRepeatedStruct(flatBuffer, module, constants, out StructuredMemberLayout? repeatedStructMember))
        {
            rawMembers.Add(repeatedStructMember);
        }
        else
        {
        foreach (ConstantBufferParameter metadataParameter in flatBuffer.ConstantBuffer.CBParams.OrderBy(p => p.Index))
        {
            MemberLogicalType? logicalType = TryCreateLogicalTypeFromUscLayout(metadataParameter);
            if (logicalType == null)
            {
                return null;
            }

            // Unity partial constant buffers only expose the slots that survived codegen for this
            // concrete shader. USC may still carry later members from the original logical buffer;
            // keep the known in-range members and drop anything beyond the actual flat array length
            // instead of rejecting the whole reconstruction and falling back to m0[N].
            if (flatBuffer.ConstantBuffer.Partial && metadataParameter.Index >= maxAvailableByteOffset)
            {
                continue;
            }

            int registerCount = GetRequiredRegisterCount(metadataParameter.Index, logicalType);
            var member = new StructuredMemberLayout
            {
                Metadata = metadataParameter,
                LogicalType = logicalType,
                RegisterCount = registerCount,
                RegisterOffset = metadataParameter.Index / 16
            };

            rawMembers.Add(member);
        }

        foreach (StructParameter structParameter in flatBuffer.ConstantBuffer.StructParams.OrderBy(s => s.Index))
        {
            if (structParameter.CBParams.Count == 0)
            {
                continue;
            }

            if (flatBuffer.ConstantBuffer.Partial && structParameter.Index >= maxAvailableByteOffset)
            {
                continue;
            }

            var structMember = new StructuredMemberLayout
            {
                Metadata = new ConstantBufferParameter
                {
                    ParamName = structParameter.Name,
                    Index = structParameter.Index,
                    ParamType = ShaderParamType.Float,
                    Rows = 1,
                    Columns = 1,
                    IsMatrix = false,
                    ArraySize = Math.Max(structParameter.ArraySize, 1)
                },
                LogicalType = new MemberLogicalType
                {
                    Kind = LogicalTypeKind.Struct,
                    ScalarKind = ScalarKind.Float,
                    Rows = 1,
                    Columns = 1,
                    ArrayLength = Math.Max(structParameter.ArraySize, 1),
                    DeclaredByteSize = Math.Max(structParameter.Size, 16),
                    UscIndex = structParameter.Index,
                    IsMatrix = false
                },
                RegisterOffset = structParameter.Index / 16,
                RegisterCount = Math.Max(1, ((Math.Max(structParameter.Size, 16) * Math.Max(structParameter.ArraySize, 1)) + 15) / 16),
                StructName = structParameter.Name,
                ParentBufferName = flatBuffer.ConstantBuffer.Name
            };

            foreach (ConstantBufferParameter childParameter in structParameter.CBParams.OrderBy(p => p.Index))
            {
                if (flatBuffer.ConstantBuffer.Partial && childParameter.Index >= maxAvailableByteOffset)
                {
                    continue;
                }

                MemberLogicalType? childLogicalType = TryCreateLogicalTypeFromUscLayout(childParameter);
                if (childLogicalType == null)
                {
                    return null;
                }

                structMember.Children.Add(new StructuredMemberLayout
                {
                    Metadata = childParameter,
                    LogicalType = childLogicalType,
                    RegisterOffset = childParameter.Index / 16,
                    RegisterCount = GetRequiredRegisterCount(childParameter.Index, childLogicalType),
                    RelativeOffset = childParameter.Index - structParameter.Index
                });
            }

            rawMembers.Add(structMember);
        }
        }

        rawMembers.Sort((left, right) => left.Metadata.Index.CompareTo(right.Metadata.Index));

        if (rawMembers.Count == 0)
        {
            return null;
        }

        int maxUsedByteOffset = 0;
        foreach (StructuredMemberLayout member in rawMembers)
        {
            layout.Members.Add(member);
            maxUsedByteOffset = Math.Max(maxUsedByteOffset, member.Metadata.Index + GetMemberSpanBytes(member));
        }

        layout.RequiredRegisterCount = flatBuffer.ConstantBuffer.Partial
            ? Math.Max(1, (maxUsedByteOffset + 15) / 16)
            : flatBuffer.ArrayLength;
        layout.MaxUsedRegisterCount = Math.Max(1, (maxUsedByteOffset + 15) / 16);
        return layout;
    }

    private static bool TryCreateSyntheticRepeatedStruct(
        FlatUniformBufferInfo flatBuffer,
        SpirvModule module,
        ConstantMaps constants,
        out StructuredMemberLayout? repeatedStructMember)
    {
        repeatedStructMember = null;

        if (flatBuffer.ConstantBuffer.StructParams.Count != 0 || flatBuffer.ConstantBuffer.CBParams.Count == 0)
        {
            return false;
        }

        if (!TryAnalyzeRepeatedRecordPattern(module, flatBuffer, constants, out RepeatedRecordPattern? pattern))
        {
            return false;
        }

        int strideBytes = pattern.RegisterStride * 16;
        if (flatBuffer.ConstantBuffer.CBParams.Any(parameter => parameter.Index < 0 || parameter.Index >= strideBytes))
        {
            return false;
        }

        int arrayLength = Math.Max(1, (flatBuffer.ArrayLength + pattern.RegisterStride - 1) / pattern.RegisterStride);
        var structMember = new StructuredMemberLayout
        {
            Metadata = new ConstantBufferParameter
            {
                ParamName = flatBuffer.ConstantBuffer.Name,
                Index = 0,
                ParamType = ShaderParamType.Float,
                Rows = 1,
                Columns = 1,
                IsMatrix = false,
                ArraySize = arrayLength
            },
            LogicalType = new MemberLogicalType
            {
                Kind = LogicalTypeKind.Struct,
                ScalarKind = ScalarKind.Float,
                Rows = 1,
                Columns = 1,
                ArrayLength = arrayLength,
                DeclaredByteSize = strideBytes,
                UscIndex = 0,
                IsMatrix = false
            },
            RegisterOffset = 0,
            RegisterCount = pattern.RegisterStride * arrayLength,
            StructName = $"{flatBuffer.ConstantBuffer.Name}_Record",
            ParentBufferName = flatBuffer.ConstantBuffer.Name
        };

        foreach (int registerOffset in Enumerable.Range(0, pattern.RegisterStride))
        {
            string aliasName = ResolveRegisterAliasName(flatBuffer.ConstantBuffer, registerOffset);
            structMember.Children.Add(new StructuredMemberLayout
            {
                Metadata = new ConstantBufferParameter
                {
                    ParamName = aliasName,
                    ParamType = ShaderParamType.Float,
                    Rows = 4,
                    Columns = 1,
                    IsMatrix = false,
                    ArraySize = 1,
                    Index = registerOffset * 16
                },
                LogicalType = new MemberLogicalType
                {
                    Kind = LogicalTypeKind.Vector,
                    ScalarKind = ScalarKind.Float,
                    Rows = 4,
                    Columns = 1,
                    ArrayLength = 1,
                    DeclaredByteSize = 16,
                    UscIndex = registerOffset * 16,
                    IsMatrix = false
                },
                RegisterOffset = registerOffset,
                RegisterCount = 1,
                RelativeOffset = registerOffset * 16
            });
        }

        if (structMember.Children.Count == 0)
        {
            return false;
        }

        structMember.Children.Sort((left, right) => left.RelativeOffset.CompareTo(right.RelativeOffset));
        repeatedStructMember = structMember;
        return true;
    }

    private static bool TryAnalyzeRepeatedRecordPattern(
        SpirvModule module,
        FlatUniformBufferInfo flatBuffer,
        ConstantMaps constants,
        out RepeatedRecordPattern? pattern)
    {
        pattern = null;
        var strideCounts = new Dictionary<int, int>();
        var accessedRegistersByStride = new Dictionary<int, HashSet<int>>();

        foreach (SpirvInstruction instruction in module.Instructions)
        {
            if ((instruction.OpCode != SpvOpCode.OpAccessChain && instruction.OpCode != SpvOpCode.OpInBoundsAccessChain) || instruction.Words.Length < 5)
            {
                continue;
            }

            if (instruction[3] != flatBuffer.VariableId || !TryParseFlatAccessChain(instruction, constants, out FlatAccessPath accessPath))
            {
                continue;
            }

            if (accessPath.Slot.DynamicIndexId != 0 && accessPath.Slot.DynamicIndexStride > 1)
            {
                if (!strideCounts.ContainsKey(accessPath.Slot.DynamicIndexStride))
                {
                    strideCounts[accessPath.Slot.DynamicIndexStride] = 0;
                    accessedRegistersByStride[accessPath.Slot.DynamicIndexStride] = new HashSet<int>();
                }

                strideCounts[accessPath.Slot.DynamicIndexStride]++;
                accessedRegistersByStride[accessPath.Slot.DynamicIndexStride].Add(accessPath.Slot.ConstantRegisterOffset);
            }
        }

        if (strideCounts.Count == 0)
        {
            return false;
        }

        int dominantStride = strideCounts
            .OrderByDescending(entry => entry.Value)
            .ThenByDescending(entry => entry.Key)
            .First()
            .Key;

        HashSet<int> accessedRegisters = accessedRegistersByStride[dominantStride];
        foreach (SpirvInstruction instruction in module.Instructions)
        {
            if ((instruction.OpCode != SpvOpCode.OpAccessChain && instruction.OpCode != SpvOpCode.OpInBoundsAccessChain) || instruction.Words.Length < 5)
            {
                continue;
            }

            if (instruction[3] != flatBuffer.VariableId || !TryParseFlatAccessChain(instruction, constants, out FlatAccessPath accessPath))
            {
                continue;
            }

            if (accessPath.Slot.DynamicIndexId == 0)
            {
                accessedRegisters.Add(Mod(accessPath.Slot.ConstantRegisterOffset, dominantStride));
            }
        }

        pattern = new RepeatedRecordPattern
        {
            RegisterStride = dominantStride,
            AccessedRegisters = accessedRegisters
        };
        return true;
    }

    private static string ResolveRegisterAliasName(ConstantBuffer constantBuffer, int registerOffset)
    {
        ConstantBufferParameter? alias = constantBuffer.CBParams
            .Where(parameter => parameter.Index / 16 == registerOffset && !string.IsNullOrWhiteSpace(parameter.ParamName))
            .OrderBy(parameter => parameter.Index % 16)
            .ThenBy(parameter => parameter.Index)
            .FirstOrDefault();

        if (alias != null)
        {
            string leafName = alias.ParamName.Split('.').Last();
            if (!string.IsNullOrWhiteSpace(leafName))
            {
                return leafName;
            }
        }

        return $"slot_{registerOffset}";
    }

    private static int Mod(int value, int modulus)
    {
        int remainder = value % modulus;
        return remainder < 0 ? remainder + modulus : remainder;
    }

    private static int GetMemberSpanBytes(StructuredMemberLayout member)
    {
        if (member.LogicalType.Kind == LogicalTypeKind.Struct)
        {
            // For Unity partial constant buffers, struct arrays represent a repeated per-instance
            // stride. The metadata does not guarantee that the backing flat array preserves the
            // fully serialized tail of every array element, so span calculations must be based on
            // one logical struct stride rather than the entire repeated array extent.
            return Math.Max(member.LogicalType.DeclaredByteSize, 16);
        }

        if (member.LogicalType.Kind == LogicalTypeKind.Matrix)
        {
            return member.LogicalType.Columns * 16 * Math.Max(member.LogicalType.ArrayLength, 1);
        }

        return member.LogicalType.DeclaredByteSize;
    }

    private static MemberLogicalType? TryCreateLogicalTypeFromUscLayout(ConstantBufferParameter metadataParameter)
    {
        if (metadataParameter.Rows <= 0 || metadataParameter.Columns <= 0)
        {
            return null;
        }

        ScalarKind? scalarKind = TryResolveScalarKind(metadataParameter.ParamType);
        if (scalarKind == null)
        {
            return null;
        }

        int declaredByteSize = GetDeclaredByteSize(metadataParameter);

        return new MemberLogicalType
        {
            Kind = metadataParameter.IsMatrix
                ? LogicalTypeKind.Matrix
                : metadataParameter.Rows == 1 ? LogicalTypeKind.Scalar : LogicalTypeKind.Vector,
            ScalarKind = scalarKind.Value,
            Rows = metadataParameter.Rows,
            Columns = metadataParameter.Columns,
            ArrayLength = Math.Max(metadataParameter.ArraySize, 1),
            DeclaredByteSize = declaredByteSize,
            UscIndex = metadataParameter.Index,
            IsMatrix = metadataParameter.IsMatrix
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

    private static int GetDeclaredByteSize(ConstantBufferParameter metadataParameter)
    {
        int elementCount = metadataParameter.Rows * metadataParameter.Columns * Math.Max(metadataParameter.ArraySize, 1);
        return metadataParameter.IsMatrix
            ? metadataParameter.Columns * 16 * Math.Max(metadataParameter.ArraySize, 1)
            : elementCount * 4;
    }

    private static int GetRequiredRegisterCount(int byteOffset, MemberLogicalType type)
    {
        if (type.Kind == LogicalTypeKind.Matrix)
        {
            return Math.Max(1, type.DeclaredByteSize / 16);
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

        if (logicalType.Kind == LogicalTypeKind.Struct && logicalType.ArrayLength > 1)
        {
            return EnsureArrayType(module, types, baseTypeId, logicalType.ArrayLength, Math.Max(logicalType.DeclaredByteSize, 16));
        }

        if ((logicalType.Kind == LogicalTypeKind.Scalar || logicalType.Kind == LogicalTypeKind.Vector) && logicalType.ArrayLength > 1)
        {
            int arrayStride = logicalType.Kind == LogicalTypeKind.Vector && logicalType.Rows == 4
                ? 16
                : logicalType.DeclaredByteSize / logicalType.ArrayLength;
            return EnsureArrayType(module, types, baseTypeId, logicalType.ArrayLength, arrayStride);
        }

        return baseTypeId;
    }

    private static uint EnsureArrayType(SpirvModule module, TypeInfo types, uint elementTypeId, int arrayLength, int arrayStride)
    {
        uint lengthConstantId = FindOrCreateUIntConstant(module, types, checked((uint)arrayLength));

        foreach (SpirvInstruction instruction in module.Instructions)
        {
            if (instruction.OpCode == SpvOpCode.OpTypeArray && instruction.Words.Length >= 4 &&
                instruction[2] == elementTypeId && instruction[3] == lengthConstantId)
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
            if (instruction.OpCode == SpvOpCode.OpConstant && instruction.Words.Length >= 4 &&
                instruction[1] == uintTypeId && instruction[3] == value)
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

    private static uint EnsureStructType(SpirvModule module, TypeInfo types, StructuredMemberLayout member)
    {
        if (string.IsNullOrWhiteSpace(member.StructName) || member.Children.Count == 0)
        {
            return 0;
        }

        var childTypeIds = new List<uint>(member.Children.Count);
        foreach (StructuredMemberLayout child in member.Children)
        {
            uint childTypeId = ResolveMemberTypeId(module, types, child);
            if (childTypeId == 0)
            {
                return 0;
            }

            child.ResolvedTypeId = childTypeId;
            childTypeIds.Add(childTypeId);
        }

        uint structTypeId = module.AllocateId();
        int decorationInsertIndex = module.FindFirstTypeInstructionIndex();
        while (decorationInsertIndex > 0 &&
               (module.Instructions[decorationInsertIndex - 1].OpCode == SpvOpCode.OpDecorate ||
                module.Instructions[decorationInsertIndex - 1].OpCode == SpvOpCode.OpMemberDecorate))
        {
            decorationInsertIndex--;
        }

        var decorations = new List<SpirvInstruction>();
        for (int i = 0; i < member.Children.Count; i++)
        {
            StructuredMemberLayout child = member.Children[i];
            decorations.Add(new SpirvInstruction
            {
                OpCode = SpvOpCode.OpMemberDecorate,
                Words =
                [
                    SpvOpCode.MakeInstructionWord(SpvOpCode.OpMemberDecorate, 5),
                    structTypeId,
                    (uint)i,
                    SpvOpCode.DecorationOffset,
                    (uint)child.RelativeOffset
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
                        (uint)i,
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
                        (uint)i,
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
            Words = new[] { SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypeStruct, (ushort)(2 + childTypeIds.Count)), structTypeId }
                .Concat(childTypeIds)
                .ToArray()
        });

        module.InsertDebugName(structTypeId, member.StructName);
        for (int i = 0; i < member.Children.Count; i++)
        {
            string leafName = member.Children[i].Metadata.ParamName.Split('.').Last();
            string fullName = string.IsNullOrWhiteSpace(member.ParentBufferName)
                ? $"{member.StructName}.{leafName}"
                : $"{member.ParentBufferName}.{member.StructName}.{leafName}";
            module.InsertDebugMemberName(structTypeId, (uint)i, fullName);
        }

        return structTypeId;
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
        int insertIndex = module.FindTypeSectionEndIndex();
        module.Instructions.Insert(insertIndex, new SpirvInstruction
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
        int insertIndex = module.FindTypeSectionEndIndex();
        module.Instructions.Insert(insertIndex, new SpirvInstruction
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
        int insertIndex = module.FindTypeSectionEndIndex();
        module.Instructions.Insert(insertIndex, new SpirvInstruction
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

        Dictionary<int, uint> typeMap = scalarKind switch
        {
            ScalarKind.Float => types.FloatVectorTypeIds,
            ScalarKind.Int => types.IntVectorTypeIds,
            ScalarKind.UInt => types.UIntVectorTypeIds,
            _ => throw new InvalidOperationException("Unsupported vector scalar kind.")
        };

        if (typeMap.TryGetValue(componentCount, out uint existingTypeId) && existingTypeId != 0)
        {
            return existingTypeId;
        }

        uint componentTypeId = EnsureScalarType(module, types, scalarKind);
        uint resultId = module.AllocateId();
        int insertIndex = module.FindTypeSectionEndIndex();
        module.Instructions.Insert(insertIndex, new SpirvInstruction
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

        typeMap[componentCount] = resultId;
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
        int insertIndex = module.FindTypeSectionEndIndex();
        module.Instructions.Insert(insertIndex, new SpirvInstruction
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

        for (int i = 0; i < rewrite.Layout.Members.Count; i++)
        {
            StructuredMemberLayout member = rewrite.Layout.Members[i];
            decorations.Add(new SpirvInstruction
            {
                OpCode = SpvOpCode.OpMemberDecorate,
                Words =
                [
                    SpvOpCode.MakeInstructionWord(SpvOpCode.OpMemberDecorate, 5),
                    rewrite.NewStructTypeId,
                    (uint)i,
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
                        (uint)i,
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
                        (uint)i,
                        SpvOpCode.DecorationMatrixStride,
                        16
                    ]
                });
            }
        }

        module.Instructions.InsertRange(decorationInsertIndex, decorations);

        int structInsertIndex = module.FindTypeSectionEndIndex();
        module.Instructions.Insert(structInsertIndex, new SpirvInstruction
        {
            OpCode = SpvOpCode.OpTypeStruct,
            Words = new[] { SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypeStruct, (ushort)(2 + rewrite.MemberTypeIds.Count)), rewrite.NewStructTypeId }
                .Concat(rewrite.MemberTypeIds)
                .ToArray()
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

        for (int i = 0; i < rewrite.Layout.Members.Count; i++)
        {
            string memberName = rewrite.Layout.Members[i].Metadata.ParamName;
            if (string.IsNullOrWhiteSpace(memberName))
            {
                continue;
            }

            module.InsertDebugMemberName(rewrite.NewStructTypeId, (uint)i, memberName);
        }
    }

    private static void RewriteVariablesAndAccessChains(SpirvModule module, List<BufferRewritePlan> rewrites, ConstantMaps constants)
    {
        var rewriteByVariableId = rewrites.ToDictionary(r => r.Info.VariableId);
        var uniformPointerTypes = new Dictionary<uint, uint>();
        var rewrittenAccessChains = new Dictionary<uint, RewrittenAccessChainInfo>();
        foreach (BufferRewritePlan rewrite in rewrites)
        {
            foreach (uint memberTypeId in rewrite.MemberTypeIds)
            {
                if (!uniformPointerTypes.ContainsKey(memberTypeId))
                {
                    uniformPointerTypes[memberTypeId] = FindOrCreateUniformPointerType(module, memberTypeId);
                }
            }
        }

        foreach (SpirvInstruction instruction in module.Instructions)
        {
            if (instruction.OpCode == SpvOpCode.OpVariable && instruction.Words.Length >= 4)
            {
                uint resultId = instruction[2];
                if (rewriteByVariableId.TryGetValue(resultId, out BufferRewritePlan? rewrite))
                {
                    instruction[1] = rewrite.NewPointerTypeId;
                }

                continue;
            }

            if ((instruction.OpCode != SpvOpCode.OpAccessChain && instruction.OpCode != SpvOpCode.OpInBoundsAccessChain) || instruction.Words.Length < 5)
            {
                continue;
            }

            uint baseId = instruction[3];
            if (!rewriteByVariableId.TryGetValue(baseId, out BufferRewritePlan? plan))
            {
                continue;
            }

            if (!TryParseFlatAccessChain(instruction, constants, out FlatAccessPath accessPath))
            {
                continue;
            }

            StructuredAccessTranslation? translated = TranslateFlatAccess(plan.Layout, accessPath, constants);
            if (translated == null || !uniformPointerTypes.TryGetValue(translated.MemberTypeId, out uint pointerTypeId))
            {
                continue;
            }

            var newWords = new List<uint>
            {
                SpvOpCode.MakeInstructionWord(instruction.OpCode, (ushort)(4 + translated.Indices.Count)),
                pointerTypeId,
                instruction[2],
                instruction[3]
            };
            newWords.AddRange(translated.Indices);
            instruction.Words = newWords.ToArray();
            rewrittenAccessChains[instruction[2]] = new RewrittenAccessChainInfo
            {
                AccessChainResultId = instruction[2],
                BaseVariableId = instruction[3],
                InstructionOpCode = instruction.OpCode,
                Plan = plan,
                OriginalAccessPath = new FlatAccessPath
                {
                    Slot = new SlotExpression
                    {
                        ConstantRegisterOffset = accessPath.Slot.ConstantRegisterOffset,
                        DynamicIndexId = accessPath.Slot.DynamicIndexId,
                        DynamicIndexStride = accessPath.Slot.DynamicIndexStride
                    },
                    ExtraIndices = accessPath.ExtraIndices.ToList()
                },
                Translation = translated
            };
        }

        RewriteLoadsAndCompositeExtracts(module, rewrittenAccessChains, constants, uniformPointerTypes);
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
        var totalUseCounts = CountResultUses(module);

        for (int i = 0; i < module.Instructions.Count; i++)
        {
            SpirvInstruction instruction = module.Instructions[i];
            if (instruction.OpCode == SpvOpCode.OpLoad && instruction.Words.Length >= 4 && rewrittenAccessChains.TryGetValue(instruction[3], out RewrittenAccessChainInfo? accessInfo))
            {
                loadInfos[instruction[2]] = new RewrittenLoadInfo
                {
                    InstructionIndex = i,
                    ResultId = instruction[2],
                    AccessChain = accessInfo,
                    OriginalResultTypeId = instruction[1],
                    HasCompositeExtractUsers = false
                };
                continue;
            }

            if (instruction.OpCode == SpvOpCode.OpCompositeExtract && instruction.Words.Length >= 5 && loadInfos.TryGetValue(instruction[3], out RewrittenLoadInfo? loadInfo))
            {
                loadInfo.HasCompositeExtractUsers = true;
                if (!compositeExtractUseCounts.ContainsKey(loadInfo.ResultId))
                {
                    compositeExtractUseCounts[loadInfo.ResultId] = 0;
                }

                compositeExtractUseCounts[loadInfo.ResultId]++;

                List<int> extractIndices = instruction.Words.Skip(4).Select(v => checked((int)v)).ToList();
                FlatAccessPath directAccessPath = new()
                {
                    Slot = new SlotExpression
                    {
                        ConstantRegisterOffset = loadInfo.AccessChain.OriginalAccessPath.Slot.ConstantRegisterOffset,
                        DynamicIndexId = loadInfo.AccessChain.OriginalAccessPath.Slot.DynamicIndexId,
                        DynamicIndexStride = loadInfo.AccessChain.OriginalAccessPath.Slot.DynamicIndexStride
                    },
                    ExtraIndices = loadInfo.AccessChain.OriginalAccessPath.ExtraIndices.Concat(extractIndices).ToList()
                };

                StructuredAccessTranslation? translated = TranslateFlatAccess(loadInfo.AccessChain.Plan.Layout, directAccessPath, constants);
                if (translated == null || !uniformPointerTypes.TryGetValue(translated.MemberTypeId, out uint pointerTypeId))
                {
                    continue;
                }

                uint pointerResultId = module.AllocateId();
                var pointerInstruction = new SpirvInstruction
                {
                    OpCode = loadInfo.AccessChain.InstructionOpCode,
                    Words = new[]
                    {
                        SpvOpCode.MakeInstructionWord(loadInfo.AccessChain.InstructionOpCode, (ushort)(4 + translated.Indices.Count)),
                        pointerTypeId,
                        pointerResultId,
                        loadInfo.AccessChain.BaseVariableId
                    }.Concat(translated.Indices).ToArray()
                };

                var loadInstruction = new SpirvInstruction
                {
                    OpCode = SpvOpCode.OpLoad,
                    Words =
                    [
                        SpvOpCode.MakeInstructionWord(SpvOpCode.OpLoad, 4),
                        translated.MemberTypeId,
                        instruction[2],
                        pointerResultId
                    ]
                };

                module.Instructions.Insert(i, pointerInstruction);
                module.Instructions.Insert(i + 1, loadInstruction);
                i += 2;
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

            int compositeExtractUsers = compositeExtractUseCounts.TryGetValue(loadInfo.ResultId, out int count) ? count : 0;
            int totalUses = totalUseCounts.TryGetValue(loadInfo.ResultId, out int uses) ? uses : 0;
            if (compositeExtractUsers == totalUses)
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
                if (!uses.ContainsKey(operand))
                {
                    uses[operand] = 0;
                }

                uses[operand]++;
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
        for (int i = slotOperandIndex + 1; i < instruction.Words.Length; i++)
        {
            if (!constants.IdToValue.TryGetValue(instruction.Words[i], out uint value))
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

        foreach (StructuredMemberLayout member in layout.Members)
        {
            if (absoluteRegister < member.RegisterOffset || absoluteRegister >= member.RegisterOffset + member.RegisterCount)
            {
                continue;
            }

            if (member.LogicalType.Kind == LogicalTypeKind.Struct)
            {
                int structRegisterCount = Math.Max(1, (Math.Max(member.LogicalType.DeclaredByteSize, 16) + 15) / 16);
                int structArrayIndex = member.LogicalType.ArrayLength > 1 ? (absoluteRegister - member.RegisterOffset) / structRegisterCount : -1;
                int structArrayRegisterOffset = member.LogicalType.ArrayLength > 1 ? structArrayIndex * structRegisterCount : 0;

                for (int childIndex = 0; childIndex < member.Children.Count; childIndex++)
                {
                    StructuredMemberLayout child = member.Children[childIndex];
                    int childAbsoluteRegisterOffset = member.RegisterOffset + structArrayRegisterOffset + (child.RelativeOffset / 16);
                    if (absoluteRegister < childAbsoluteRegisterOffset || absoluteRegister >= childAbsoluteRegisterOffset + child.RegisterCount)
                    {
                        continue;
                    }

                    List<int>? childLogicalIndices = TranslateMemberAccess(child, absoluteRegister - structArrayRegisterOffset, componentIndex, accessPath.ExtraIndices);
                    if (childLogicalIndices == null)
                    {
                        continue;
                    }

                    if (!TryGetConstantId(constants, (uint)layout.Members.IndexOf(member), out uint parentIndexConstId) ||
                        !TryGetConstantId(constants, (uint)childIndex, out uint childIndexConstId))
                    {
                        return null;
                    }

                    var nestedIndices = new List<uint> { parentIndexConstId };
                    if (member.LogicalType.ArrayLength > 1)
                    {
                        if (structArrayIndex < 0 || structArrayIndex >= member.LogicalType.ArrayLength || !TryGetConstantId(constants, (uint)structArrayIndex, out uint structArrayIndexConstId))
                        {
                            return null;
                        }

                        nestedIndices.Add(structArrayIndexConstId);
                    }

                    nestedIndices.Add(childIndexConstId);
                    foreach (int logicalIndex in childLogicalIndices)
                    {
                        if (!TryGetConstantId(constants, (uint)logicalIndex, out uint logicalIndexConstId))
                        {
                            return null;
                        }

                        nestedIndices.Add(logicalIndexConstId);
                    }

                    return new StructuredAccessTranslation
                    {
                        Indices = nestedIndices,
                        MemberTypeId = child.ResolvedTypeId
                    };
                }

                continue;
            }

            if (!IsMemberByteMatch(member, absoluteByteOffset, accessPath.ExtraIndices))
            {
                continue;
            }

            List<int>? logicalIndices = TranslateMemberAccess(member, absoluteRegister, componentIndex, accessPath.ExtraIndices);
            if (logicalIndices == null)
            {
                continue;
            }

            if (!TryGetConstantId(constants, (uint)layout.Members.IndexOf(member), out uint memberIndexConstId))
            {
                return null;
            }

            var indices = new List<uint> { memberIndexConstId };
            foreach (int logicalIndex in logicalIndices)
            {
                if (!TryGetConstantId(constants, (uint)logicalIndex, out uint logicalIndexConstId))
                {
                    return null;
                }

                indices.Add(logicalIndexConstId);
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
        if (member.LogicalType.Kind == LogicalTypeKind.Struct)
        {
            return false;
        }

        int memberStart = member.Metadata.Index;
        int memberSize = Math.Max(member.LogicalType.DeclaredByteSize, 4);
        int memberEnd = memberStart + memberSize;

        if (member.LogicalType.Kind == LogicalTypeKind.Matrix)
        {
            return absoluteByteOffset >= memberStart && absoluteByteOffset < memberEnd;
        }

        if (extraIndices.Count == 0)
        {
            return absoluteByteOffset == memberStart;
        }

        return absoluteByteOffset >= memberStart && absoluteByteOffset < memberEnd;
    }

    private static List<int>? TranslateMemberAccess(StructuredMemberLayout member, int absoluteRegister, int componentIndex, List<int> extraIndices)
    {
        MemberLogicalType logicalType = member.LogicalType;
        int localRegister = absoluteRegister - member.RegisterOffset;
        List<int> trailingIndices = extraIndices.Count > 1 ? extraIndices.Skip(1).ToList() : [];
        int memberComponentOffset = (member.Metadata.Index % 16) / 4;

        if (logicalType.Kind == LogicalTypeKind.Matrix)
        {
            if (localRegister < 0 || localRegister >= logicalType.Columns)
            {
                return null;
            }

            if (extraIndices.Count > 0)
            {
                if (componentIndex < 0 || componentIndex >= logicalType.Rows)
                {
                    return null;
                }

                if (trailingIndices.Count > 0)
                {
                    return null;
                }

                return [localRegister, componentIndex];
            }

            return [localRegister];
        }

        if (member.RegisterCount == 1)
        {
            if (extraIndices.Count == 0)
            {
                return memberComponentOffset == 0 ? [] : null;
            }

            if (logicalType.Kind == LogicalTypeKind.Vector)
            {
                int relativeComponentIndex = componentIndex - memberComponentOffset;
                if (relativeComponentIndex < 0 || relativeComponentIndex >= logicalType.Rows || trailingIndices.Count > 0)
                {
                    return null;
                }

                return [relativeComponentIndex];
            }

            if (logicalType.Kind == LogicalTypeKind.Scalar)
            {
                if (componentIndex != memberComponentOffset || trailingIndices.Count > 0)
                {
                    return null;
                }

                return [];
            }

            return null;
        }

        if (localRegister < 0 || localRegister >= member.RegisterCount)
        {
            return null;
        }

        if (trailingIndices.Count > 0)
        {
            return null;
        }

        return extraIndices.Count > 0 ? [localRegister, componentIndex] : [localRegister];
    }

    private static StructuredAccessTranslation? TranslateDynamicFlatAccess(StructuredBufferLayout layout, FlatAccessPath accessPath, ConstantMaps constants, int componentIndex)
    {
        foreach (StructuredMemberLayout member in layout.Members)
        {
            if (member.LogicalType.Kind != LogicalTypeKind.Struct || member.LogicalType.ArrayLength <= 1)
            {
                continue;
            }

            int structRegisterCount = Math.Max(1, (Math.Max(member.LogicalType.DeclaredByteSize, 16) + 15) / 16);
            if (accessPath.Slot.DynamicIndexStride != structRegisterCount)
            {
                continue;
            }

            int registerWithinStruct = accessPath.Slot.ConstantRegisterOffset;
            if (registerWithinStruct < 0 || registerWithinStruct >= structRegisterCount)
            {
                continue;
            }

            for (int childIndex = 0; childIndex < member.Children.Count; childIndex++)
            {
                StructuredMemberLayout child = member.Children[childIndex];
                int childRegisterOffset = child.RelativeOffset / 16;
                if (registerWithinStruct < childRegisterOffset || registerWithinStruct >= childRegisterOffset + child.RegisterCount)
                {
                    continue;
                }

                int absoluteRegister = member.RegisterOffset + registerWithinStruct;
                List<int>? childLogicalIndices = TranslateMemberAccess(child, absoluteRegister, componentIndex, accessPath.ExtraIndices);
                if (childLogicalIndices == null)
                {
                    continue;
                }

                if (!TryGetConstantId(constants, (uint)layout.Members.IndexOf(member), out uint parentIndexConstId) ||
                    !TryGetConstantId(constants, (uint)childIndex, out uint childIndexConstId))
                {
                    return null;
                }

                var indices = new List<uint>
                {
                    parentIndexConstId,
                    accessPath.Slot.DynamicIndexId,
                    childIndexConstId
                };

                foreach (int logicalIndex in childLogicalIndices)
                {
                    if (!TryGetConstantId(constants, (uint)logicalIndex, out uint logicalIndexConstId))
                    {
                        return null;
                    }

                    indices.Add(logicalIndexConstId);
                }

                return new StructuredAccessTranslation
                {
                    Indices = indices,
                    MemberTypeId = child.ResolvedTypeId
                };
            }
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
        int insertIndex = module.FindTypeSectionEndIndex();
        module.Instructions.Insert(insertIndex, new SpirvInstruction
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
        if (constants.ValueToId.TryGetValue(value, out constantId))
        {
            return true;
        }

        constantId = 0;
        return false;
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
        public string? StructName { get; set; }
        public string? ParentBufferName { get; set; }
        public List<StructuredMemberLayout> Children { get; } = new();
        public int RelativeOffset { get; set; }
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

    private sealed class RepeatedRecordPattern
    {
        public int RegisterStride { get; set; }
        public HashSet<int> AccessedRegisters { get; set; } = new();
    }

    private sealed class FlatUniformBufferInfo
    {
        public int ActualSet { get; set; }
        public int ActualBinding { get; set; }
        public uint VariableId { get; set; }
        public uint PointerTypeId { get; set; }
        public uint StructTypeId { get; set; }
        public uint ArrayTypeId { get; set; }
        public uint ElementTypeId { get; set; }
        public int ArrayLength { get; set; }
        public int ArrayStride { get; set; }
        public ResourceBinding Metadata { get; set; } = null!;
        public ConstantBuffer ConstantBuffer { get; set; } = null!;

        public FlatUniformBufferInfo WithMetadata(ResourceBinding metadata, ConstantBuffer constantBuffer)
        {
            return new FlatUniformBufferInfo
            {
                ActualSet = ActualSet,
                ActualBinding = ActualBinding,
                VariableId = VariableId,
                PointerTypeId = PointerTypeId,
                StructTypeId = StructTypeId,
                ArrayTypeId = ArrayTypeId,
                ElementTypeId = ElementTypeId,
                ArrayLength = ArrayLength,
                ArrayStride = ArrayStride,
                Metadata = metadata,
                ConstantBuffer = constantBuffer
            };
        }
    }

    private sealed class MetadataBufferCandidate
    {
        public ResourceBinding Resource { get; set; } = null!;
        public ConstantBuffer ConstantBuffer { get; set; } = null!;
    }
}
