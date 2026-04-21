namespace Ruri.ShaderDecompiler.Spirv;

internal sealed class StructuredCBufferRewriter
{
    public bool LastRewriteApplied { get; private set; }

    public byte[] Rewrite(byte[] spirv, ShaderSymbolData metadata)
    {
        LastRewriteApplied = false;
        var module = SpirvModule.Parse(spirv);
        var flatBuffers = BuildFlatUniformBufferMap(module, metadata);
        if (flatBuffers.Count == 0)
        {
            return spirv;
        }

        var constants = BuildConstantMaps(module);
        var types = AnalyzeTypes(module);
        var rewrites = new List<BufferRewritePlan>();

        foreach (var flatBuffer in flatBuffers.Values)
        {
            if (flatBuffer.Metadata.Members == null || flatBuffer.Metadata.Members.Count == 0)
            {
                continue;
            }

            int registerCount = flatBuffer.Metadata.Members.Sum(GetRegisterSpan);
            if (registerCount != flatBuffer.ArrayLength)
            {
                continue;
            }

            uint float2TypeId = EnsureFloatVectorType(module, types, 2);
            uint float3TypeId = EnsureFloatVectorType(module, types, 3);
            uint float4TypeId = EnsureFloatVectorType(module, types, 4);
            uint intTypeId = EnsureIntType(module, types);
            uint int2TypeId = EnsureIntVectorType(module, types, intTypeId, 2);
            uint int3TypeId = EnsureIntVectorType(module, types, intTypeId, 3);
            uint int4TypeId = EnsureIntVectorType(module, types, intTypeId, 4);
            uint uintTypeId = EnsureUIntType(module, types);
            uint uint2TypeId = EnsureUIntVectorType(module, types, uintTypeId, 2);
            uint uint3TypeId = EnsureUIntVectorType(module, types, uintTypeId, 3);
            uint uint4TypeId = EnsureUIntVectorType(module, types, uintTypeId, 4);
            uint mat2TypeId = EnsureMatrixType(module, types, float2TypeId, 2);
            uint mat3TypeId = EnsureMatrixType(module, types, float3TypeId, 3);
            uint mat4TypeId = EnsureMatrixType(module, types, float4TypeId, 4);
            var memberTypeIds = new List<uint>();
            foreach (var member in flatBuffer.Metadata.Members.OrderBy(m => m.ByteOffset))
            {
                uint typeId = ResolveMemberTypeId(member, types, float2TypeId, float3TypeId, float4TypeId, intTypeId, int2TypeId, int3TypeId, int4TypeId, mat2TypeId, mat3TypeId, mat4TypeId, uint2TypeId, uint3TypeId, uint4TypeId);
                if (typeId == 0)
                {
                    memberTypeIds.Clear();
                    break;
                }

                memberTypeIds.Add(typeId);
            }

            if (memberTypeIds.Count == 0)
            {
                continue;
            }

            uint newStructTypeId = module.AllocateId();
            uint newPointerTypeId = module.AllocateId();

            rewrites.Add(new BufferRewritePlan
            {
                Info = flatBuffer,
                NewStructTypeId = newStructTypeId,
                NewPointerTypeId = newPointerTypeId,
                MemberTypeIds = memberTypeIds
            });

            InsertStructuredType(module, flatBuffer, newStructTypeId, newPointerTypeId, memberTypeIds);
            InsertStructuredNames(module, flatBuffer, newStructTypeId);
        }

        if (rewrites.Count == 0)
        {
            return spirv;
        }

        RewriteVariablesAndAccessChains(module, rewrites, constants);
        LastRewriteApplied = true;
        return module.ToBytes();
    }

    private static Dictionary<(int Set, int Binding), FlatUniformBufferInfo> BuildFlatUniformBufferMap(SpirvModule module, ShaderSymbolData metadata)
    {
        var pointerTypes = new Dictionary<uint, (uint StorageClass, uint TypeId)>();
        var variablePointerTypes = new Dictionary<uint, uint>();
        var structMembers = new Dictionary<uint, uint[]>();
        var arrayTypes = new Dictionary<uint, (uint ElementTypeId, uint LengthId)>();
        var constants = new Dictionary<uint, uint>();
        var setBindingById = new Dictionary<uint, (int? Set, int? Binding)>();

        foreach (var instruction in module.Instructions)
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
                            if (!setBindingById.ContainsKey(targetId))
                                setBindingById[targetId] = (set, null);
                            else
                                setBindingById[targetId] = (set, setBindingById[targetId].Binding);
                        }
                        else if (decoration == SpvOpCode.DecorationBinding)
                        {
                            int binding = (int)instruction[3];
                            if (!setBindingById.ContainsKey(targetId))
                                setBindingById[targetId] = (null, binding);
                            else
                                setBindingById[targetId] = (setBindingById[targetId].Set, binding);
                        }

                        break;
                    }
                case SpvOpCode.OpTypePointer when instruction.Words.Length >= 4:
                    pointerTypes[instruction[1]] = (instruction[2], instruction[3]);
                    break;
                case SpvOpCode.OpVariable when instruction.Words.Length >= 4:
                    variablePointerTypes[instruction[2]] = instruction[1];
                    break;
                case SpvOpCode.OpTypeStruct when instruction.Words.Length >= 3:
                    structMembers[instruction[1]] = instruction.Words.Skip(2).ToArray();
                    break;
                case SpvOpCode.OpTypeArray when instruction.Words.Length >= 4:
                    arrayTypes[instruction[1]] = (instruction[2], instruction[3]);
                    break;
                case SpvOpCode.OpConstant when instruction.Words.Length >= 4:
                    constants[instruction[2]] = instruction[3];
                    break;
            }
        }

        var result = new Dictionary<(int Set, int Binding), FlatUniformBufferInfo>();
        foreach (var resource in metadata.Resources.Where(r => r.Type == ShaderResourceType.ConstantBuffer))
        {
            var variableId = setBindingById.FirstOrDefault(kvp => kvp.Value.Set == resource.Set && kvp.Value.Binding == resource.Binding).Key;
            if (variableId == 0 || !variablePointerTypes.TryGetValue(variableId, out uint pointerTypeId))
            {
                continue;
            }

            if (!pointerTypes.TryGetValue(pointerTypeId, out var pointerInfo) || pointerInfo.StorageClass != SpvOpCode.StorageClassUniform)
            {
                continue;
            }

            if (!structMembers.TryGetValue(pointerInfo.TypeId, out uint[]? members) || members.Length != 1)
            {
                continue;
            }

            uint arrayTypeId = members[0];
            if (!arrayTypes.TryGetValue(arrayTypeId, out var arrayInfo) || !constants.TryGetValue(arrayInfo.LengthId, out uint arrayLength))
            {
                continue;
            }

            result[(resource.Set, resource.Binding)] = new FlatUniformBufferInfo
            {
                VariableId = variableId,
                PointerTypeId = pointerTypeId,
                StructTypeId = pointerInfo.TypeId,
                ArrayTypeId = arrayTypeId,
                ElementTypeId = arrayInfo.ElementTypeId,
                ArrayLength = (int)arrayLength,
                Metadata = resource
            };
        }

        if (result.Count == 0)
        {
            foreach (var resource in metadata.Resources.Where(r => r.Type == ShaderResourceType.ConstantBuffer))
            {
                foreach (var candidate in variablePointerTypes)
                {
                    if (!pointerTypes.TryGetValue(candidate.Value, out var pointerInfo) || pointerInfo.StorageClass != SpvOpCode.StorageClassUniform)
                    {
                        continue;
                    }

                    if (!structMembers.TryGetValue(pointerInfo.TypeId, out uint[]? members) || members.Length != 1)
                    {
                        continue;
                    }

                    uint arrayTypeId = members[0];
                    if (!arrayTypes.TryGetValue(arrayTypeId, out var arrayInfo) || !constants.TryGetValue(arrayInfo.LengthId, out uint arrayLength))
                    {
                        continue;
                    }

                    int registerCount = resource.Members?.Sum(GetRegisterSpan) ?? 0;
                    if (registerCount == (int)arrayLength)
                    {
                        result[(resource.Set, resource.Binding)] = new FlatUniformBufferInfo
                        {
                            VariableId = candidate.Key,
                            PointerTypeId = candidate.Value,
                            StructTypeId = pointerInfo.TypeId,
                            ArrayTypeId = arrayTypeId,
                            ElementTypeId = arrayInfo.ElementTypeId,
                            ArrayLength = (int)arrayLength,
                            Metadata = resource
                        };
                        break;
                    }
                }
            }
        }

        return result;
    }

    private static ConstantMaps BuildConstantMaps(SpirvModule module)
    {
        var result = new ConstantMaps();
        foreach (var instruction in module.Instructions)
        {
            if (instruction.OpCode == SpvOpCode.OpConstant && instruction.Words.Length >= 4)
            {
                result.IdToValue[instruction[2]] = instruction[3];
                result.ValueToId[instruction[3]] = instruction[2];
            }
        }

        return result;
    }

    private static TypeInfo AnalyzeTypes(SpirvModule module)
    {
        var info = new TypeInfo();
        foreach (var instruction in module.Instructions)
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
                case SpvOpCode.OpTypeVector when instruction.Words.Length >= 4 && instruction[2] == info.FloatTypeId && instruction[3] == 2:
                    info.Float2TypeId = instruction[1];
                    break;
                case SpvOpCode.OpTypeVector when instruction.Words.Length >= 4 && instruction[2] == info.FloatTypeId && instruction[3] == 3:
                    info.Float3TypeId = instruction[1];
                    break;
                case SpvOpCode.OpTypeVector when instruction.Words.Length >= 4 && instruction[2] == info.FloatTypeId && instruction[3] == 4:
                    info.Float4TypeId = instruction[1];
                    break;
                case SpvOpCode.OpTypeVector when instruction.Words.Length >= 4 && instruction[2] == info.IntTypeId && instruction[3] == 2:
                    info.Int2TypeId = instruction[1];
                    break;
                case SpvOpCode.OpTypeVector when instruction.Words.Length >= 4 && instruction[2] == info.IntTypeId && instruction[3] == 3:
                    info.Int3TypeId = instruction[1];
                    break;
                case SpvOpCode.OpTypeVector when instruction.Words.Length >= 4 && instruction[2] == info.IntTypeId && instruction[3] == 4:
                    info.Int4TypeId = instruction[1];
                    break;
                case SpvOpCode.OpTypeVector when instruction.Words.Length >= 4 && instruction[2] == info.UIntTypeId && instruction[3] == 2:
                    info.UInt2TypeId = instruction[1];
                    break;
                case SpvOpCode.OpTypeVector when instruction.Words.Length >= 4 && instruction[2] == info.UIntTypeId && instruction[3] == 3:
                    info.UInt3TypeId = instruction[1];
                    break;
                case SpvOpCode.OpTypeVector when instruction.Words.Length >= 4 && instruction[2] == info.UIntTypeId && instruction[3] == 4:
                    info.UInt4TypeId = instruction[1];
                    break;
                case SpvOpCode.OpTypeMatrix when instruction.Words.Length >= 4 && instruction[2] == info.Float2TypeId && instruction[3] == 2:
                    info.Float2x2TypeId = instruction[1];
                    break;
                case SpvOpCode.OpTypeMatrix when instruction.Words.Length >= 4 && instruction[2] == info.Float3TypeId && instruction[3] == 3:
                    info.Float3x3TypeId = instruction[1];
                    break;
                case SpvOpCode.OpTypeMatrix when instruction.Words.Length >= 4 && instruction[2] == info.Float4TypeId && instruction[3] == 4:
                    info.Float4x4TypeId = instruction[1];
                    break;
            }
        }

        return info;
    }

    private static uint EnsureFloatVectorType(SpirvModule module, TypeInfo types, uint componentCount)
    {
        if (componentCount == 2 && types.Float2TypeId != 0) return types.Float2TypeId;
        if (componentCount == 3 && types.Float3TypeId != 0) return types.Float3TypeId;
        if (componentCount == 4 && types.Float4TypeId != 0) return types.Float4TypeId;

        uint resultId = module.AllocateId();
        int insertIndex = module.FindTypeSectionEndIndex();
        module.Instructions.Insert(insertIndex, new SpirvInstruction
        {
            OpCode = SpvOpCode.OpTypeVector,
            Words = new uint[]
            {
                SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypeVector, 4),
                resultId,
                types.FloatTypeId,
                componentCount
            }
        });

        if (componentCount == 2) types.Float2TypeId = resultId;
        else if (componentCount == 3) types.Float3TypeId = resultId;
        else if (componentCount == 4) types.Float4TypeId = resultId;
        return resultId;
    }

    private static uint EnsureMatrixType(SpirvModule module, TypeInfo types, uint vectorTypeId, uint columnCount)
    {
        if (columnCount == 2 && types.Float2x2TypeId != 0) return types.Float2x2TypeId;
        if (columnCount == 3 && types.Float3x3TypeId != 0) return types.Float3x3TypeId;
        if (columnCount == 4 && types.Float4x4TypeId != 0) return types.Float4x4TypeId;

        uint resultId = module.AllocateId();
        int insertIndex = module.FindTypeSectionEndIndex();
        module.Instructions.Insert(insertIndex, new SpirvInstruction
        {
            OpCode = SpvOpCode.OpTypeMatrix,
            Words = new uint[]
            {
                SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypeMatrix, 4),
                resultId,
                vectorTypeId,
                columnCount
            }
        });

        if (columnCount == 2) types.Float2x2TypeId = resultId;
        else if (columnCount == 3) types.Float3x3TypeId = resultId;
        else if (columnCount == 4) types.Float4x4TypeId = resultId;
        return resultId;
    }

    private static uint EnsureIntType(SpirvModule module, TypeInfo types)
    {
        if (types.IntTypeId != 0) return types.IntTypeId;

        uint resultId = module.AllocateId();
        int insertIndex = module.FindTypeSectionEndIndex();
        module.Instructions.Insert(insertIndex, new SpirvInstruction
        {
            OpCode = SpvOpCode.OpTypeInt,
            Words = new uint[]
            {
                SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypeInt, 4),
                resultId,
                32,
                1
            }
        });

        types.IntTypeId = resultId;
        return resultId;
    }

    private static uint EnsureIntVectorType(SpirvModule module, TypeInfo types, uint intTypeId, uint componentCount)
    {
        if (componentCount == 2 && types.Int2TypeId != 0) return types.Int2TypeId;
        if (componentCount == 3 && types.Int3TypeId != 0) return types.Int3TypeId;
        if (componentCount == 4 && types.Int4TypeId != 0) return types.Int4TypeId;

        uint resultId = module.AllocateId();
        int insertIndex = module.FindTypeSectionEndIndex();
        module.Instructions.Insert(insertIndex, new SpirvInstruction
        {
            OpCode = SpvOpCode.OpTypeVector,
            Words = new uint[]
            {
                SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypeVector, 4),
                resultId,
                intTypeId,
                componentCount
            }
        });

        if (componentCount == 2) types.Int2TypeId = resultId;
        else if (componentCount == 3) types.Int3TypeId = resultId;
        else if (componentCount == 4) types.Int4TypeId = resultId;
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
            Words = new uint[]
            {
                SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypeInt, 4),
                resultId,
                32,
                0
            }
        });

        types.UIntTypeId = resultId;
        return resultId;
    }

    private static uint EnsureUIntVectorType(SpirvModule module, TypeInfo types, uint uintTypeId, uint componentCount)
    {
        if (componentCount == 2 && types.UInt2TypeId != 0)
        {
            return types.UInt2TypeId;
        }

        if (componentCount == 4 && types.UInt4TypeId != 0)
        {
            return types.UInt4TypeId;
        }

        uint resultId = module.AllocateId();
        int insertIndex = module.FindTypeSectionEndIndex();
        module.Instructions.Insert(insertIndex, new SpirvInstruction
        {
            OpCode = SpvOpCode.OpTypeVector,
            Words = new uint[]
            {
                SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypeVector, 4),
                resultId,
                uintTypeId,
                componentCount
            }
        });

        if (componentCount == 2)
        {
            types.UInt2TypeId = resultId;
        }
        else if (componentCount == 4)
        {
            types.UInt4TypeId = resultId;
        }

        return resultId;
    }

    private static uint ResolveMemberTypeId(StructMember member, TypeInfo types, uint float2TypeId, uint float3TypeId, uint float4TypeId, uint intTypeId, uint int2TypeId, uint int3TypeId, uint int4TypeId, uint mat2TypeId, uint mat3TypeId, uint mat4TypeId, uint uint2TypeId, uint uint3TypeId, uint uint4TypeId)
    {
        return member.TypeName switch
        {
            "float" => types.FloatTypeId,
            "float2" => float2TypeId,
            "float3" => float3TypeId,
            "float4" => float4TypeId,
            "float2x2" => mat2TypeId,
            "float3x3" => mat3TypeId,
            "float4x4" => mat4TypeId,
            "int" => intTypeId,
            "int2" => int2TypeId,
            "int3" => int3TypeId,
            "int4" => int4TypeId,
            "uint" => types.UIntTypeId,
            "uint2" => uint2TypeId,
            "uint3" => uint3TypeId,
            "uint4" => uint4TypeId,
            _ => 0
        };
    }

    private static void InsertStructuredType(
        SpirvModule module,
        FlatUniformBufferInfo flatBuffer,
        uint newStructTypeId,
        uint newPointerTypeId,
        List<uint> memberTypeIds)
    {
        int typeInsertIndex = module.FindFirstTypeInstructionIndex();
        int decorationInsertIndex = typeInsertIndex;

        while (decorationInsertIndex > 0 && (module.Instructions[decorationInsertIndex - 1].OpCode == SpvOpCode.OpDecorate || module.Instructions[decorationInsertIndex - 1].OpCode == SpvOpCode.OpMemberDecorate))
        {
            decorationInsertIndex--;
        }

        var decorations = new List<SpirvInstruction>
        {
            new()
            {
                OpCode = SpvOpCode.OpDecorate,
                Words = new uint[]
                {
                    SpvOpCode.MakeInstructionWord(SpvOpCode.OpDecorate, 3),
                    newStructTypeId,
                    SpvOpCode.DecorationBlock
                }
            }
        };

        for (int i = 0; i < flatBuffer.Metadata.Members!.Count; i++)
        {
            var member = flatBuffer.Metadata.Members[i];
            decorations.Add(new SpirvInstruction
            {
                OpCode = SpvOpCode.OpMemberDecorate,
                Words = new uint[]
                {
                    SpvOpCode.MakeInstructionWord(SpvOpCode.OpMemberDecorate, 5),
                    newStructTypeId,
                    (uint)i,
                    SpvOpCode.DecorationOffset,
                    (uint)member.ByteOffset
                }
            });

            if (member.TypeName is "float2x2" or "float3x3" or "float4x4")
            {
                decorations.Add(new SpirvInstruction
                {
                    OpCode = SpvOpCode.OpMemberDecorate,
                    Words = new uint[]
                    {
                        SpvOpCode.MakeInstructionWord(SpvOpCode.OpMemberDecorate, 4),
                        newStructTypeId,
                        (uint)i,
                        SpvOpCode.DecorationRowMajor
                    }
                });
                decorations.Add(new SpirvInstruction
                {
                    OpCode = SpvOpCode.OpMemberDecorate,
                    Words = new uint[]
                    {
                        SpvOpCode.MakeInstructionWord(SpvOpCode.OpMemberDecorate, 5),
                        newStructTypeId,
                        (uint)i,
                        SpvOpCode.DecorationMatrixStride,
                        16
                    }
                });
            }
        }

        module.Instructions.InsertRange(decorationInsertIndex, decorations);

        int structInsertIndex = module.FindTypeSectionEndIndex();
        module.Instructions.Insert(structInsertIndex, new SpirvInstruction
        {
            OpCode = SpvOpCode.OpTypeStruct,
            Words = new[] { SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypeStruct, (ushort)(2 + memberTypeIds.Count)), newStructTypeId }
                .Concat(memberTypeIds)
                .ToArray()
        });

        module.Instructions.Insert(module.FindTypeSectionEndIndex(), new SpirvInstruction
        {
            OpCode = SpvOpCode.OpTypePointer,
            Words = new uint[]
            {
                SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypePointer, 4),
                newPointerTypeId,
                SpvOpCode.StorageClassUniform,
                newStructTypeId
            }
        });
    }

    private static void InsertStructuredNames(SpirvModule module, FlatUniformBufferInfo flatBuffer, uint newStructTypeId)
    {
        // IMPORTANT:
        // spirv-cross HLSL naming is sensitive to type/variable collisions for uniform blocks.
        // For clean HLSL we want:
        // - block/type name == metadata name (e.g. ViewData)
        // - variable name != block/type name
        // If both use the same debug name, spirv-cross generates `ViewData_1_*` member names.
        // If the type is unnamed, spirv-cross synthesizes `type_ViewData`.
        // Therefore we name the struct type with the real cbuffer name and keep the variable on
        // a private, non-user-facing name.
        module.InsertDebugName(newStructTypeId, flatBuffer.Metadata.Name);
        module.InsertDebugName(flatBuffer.VariableId, $"__ruri_{flatBuffer.Metadata.Name}_var");

        for (int i = 0; i < flatBuffer.Metadata.Members!.Count; i++)
        {
            string memberName = flatBuffer.Metadata.Members[i].Name;
            if (string.IsNullOrWhiteSpace(memberName))
            {
                continue;
            }

            module.InsertDebugMemberName(newStructTypeId, (uint)i, memberName);
        }
    }

    private static void RewriteVariablesAndAccessChains(
        SpirvModule module,
        List<BufferRewritePlan> rewrites,
        ConstantMaps constants)
    {
        var rewriteByVariableId = rewrites.ToDictionary(r => r.Info.VariableId);
        var uniformPointerTypes = new Dictionary<uint, uint>();
        foreach (var rewrite in rewrites)
        {
            foreach (uint memberTypeId in rewrite.MemberTypeIds)
            {
                if (!uniformPointerTypes.ContainsKey(memberTypeId))
                {
                    uniformPointerTypes[memberTypeId] = FindOrCreateUniformPointerType(module, memberTypeId);
                }
            }
        }

        foreach (var instruction in module.Instructions)
        {
            if (instruction.OpCode == SpvOpCode.OpVariable && instruction.Words.Length >= 4)
            {
                uint resultId = instruction[2];
                if (rewriteByVariableId.TryGetValue(resultId, out var rewrite))
                {
                    instruction[1] = rewrite.NewPointerTypeId;
                }
            }
            else if ((instruction.OpCode == SpvOpCode.OpAccessChain || instruction.OpCode == SpvOpCode.OpInBoundsAccessChain) && instruction.Words.Length >= 5)
            {
                uint baseId = instruction[3];
                if (!rewriteByVariableId.TryGetValue(baseId, out var rewrite))
                {
                    continue;
                }

                uint slot;
                if (instruction.Words.Length >= 6)
                {
                    uint memberIndexConstId = instruction[4];
                    uint slotConstId = instruction[5];
                    if (!constants.IdToValue.TryGetValue(memberIndexConstId, out uint memberIndexValue) || memberIndexValue != 0)
                    {
                        continue;
                    }

                    if (!constants.IdToValue.TryGetValue(slotConstId, out slot))
                    {
                        continue;
                    }
                }
                else
                {
                    uint slotConstId = instruction[4];
                    if (!constants.IdToValue.TryGetValue(slotConstId, out slot))
                    {
                        continue;
                    }
                }

                var translated = TranslateFlatSlotToStructuredIndices(rewrite.Info.Metadata.Members!, (int)slot, constants, rewrite.MemberTypeIds);
                if (translated == null)
                {
                    continue;
                }

                if (!uniformPointerTypes.TryGetValue(translated.MemberTypeId, out uint pointerTypeId))
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
            }
        }
    }

    private static StructuredAccessTranslation? TranslateFlatSlotToStructuredIndices(List<StructMember> members, int slot, ConstantMaps constants, List<uint> memberTypeIds)
    {
        int runningSlot = 0;
        var orderedMembers = members.OrderBy(m => m.ByteOffset).ToList();
        for (int orderedIndex = 0; orderedIndex < orderedMembers.Count; orderedIndex++)
        {
            var member = orderedMembers[orderedIndex];
            int span = GetRegisterSpan(member);
            if (slot < runningSlot || slot >= runningSlot + span)
            {
                runningSlot += span;
                continue;
            }

            int memberIndex = members.IndexOf(member);
            var result = new List<uint>();

            if (!TryGetConstantId(constants, (uint)memberIndex, out uint memberIndexConstId))
            {
                return null;
            }

            result.Add(memberIndexConstId);
            if (span > 1)
            {
                uint localSlot = (uint)(slot - runningSlot);
                if (!TryGetConstantId(constants, localSlot, out uint localSlotConstId))
                {
                    return null;
                }

                result.Add(localSlotConstId);
            }

            return new StructuredAccessTranslation
            {
                Indices = result,
                MemberTypeId = memberTypeIds[orderedIndex]
            };
        }

        return null;
    }

    private static uint FindOrCreateUniformPointerType(SpirvModule module, uint memberTypeId)
    {
        foreach (var instruction in module.Instructions)
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
            Words = new uint[]
            {
                SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypePointer, 4),
                resultId,
                SpvOpCode.StorageClassUniform,
                memberTypeId
            }
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

    private static int GetRegisterSpan(StructMember member)
    {
        return Math.Max(1, (member.ByteSize + 15) / 16);
    }

    private sealed class TypeInfo
    {
        public uint FloatTypeId { get; set; }
        public uint Float2TypeId { get; set; }
        public uint Float3TypeId { get; set; }
        public uint Float4TypeId { get; set; }
        public uint Float2x2TypeId { get; set; }
        public uint Float3x3TypeId { get; set; }
        public uint Float4x4TypeId { get; set; }
        public uint IntTypeId { get; set; }
        public uint Int2TypeId { get; set; }
        public uint Int3TypeId { get; set; }
        public uint Int4TypeId { get; set; }
        public uint UIntTypeId { get; set; }
        public uint UInt2TypeId { get; set; }
        public uint UInt3TypeId { get; set; }
        public uint UInt4TypeId { get; set; }
    }

    private sealed class BufferRewritePlan
    {
        public FlatUniformBufferInfo Info { get; set; } = null!;
        public uint NewStructTypeId { get; set; }
        public uint NewPointerTypeId { get; set; }
        public List<uint> MemberTypeIds { get; set; } = new();
    }

    private sealed class StructuredAccessTranslation
    {
        public List<uint> Indices { get; set; } = new();
        public uint MemberTypeId { get; set; }
    }

    private sealed class ConstantMaps
    {
        public Dictionary<uint, uint> IdToValue { get; } = new();
        public Dictionary<uint, uint> ValueToId { get; } = new();
    }

    private sealed class FlatUniformBufferInfo
    {
        public uint VariableId { get; set; }
        public uint PointerTypeId { get; set; }
        public uint StructTypeId { get; set; }
        public uint ArrayTypeId { get; set; }
        public uint ElementTypeId { get; set; }
        public int ArrayLength { get; set; }
        public ResourceBinding Metadata { get; set; } = null!;
    }
}
