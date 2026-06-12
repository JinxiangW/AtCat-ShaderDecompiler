namespace Ruri.ShaderTools.Spirv;

// Normalises dxil-spirv's legacy-DXBC (dxbc-spirv) constant-buffer representation into the
// float4 form the rest of the pipeline already understands.
//
// Why this exists: dxbc-spirv lowers a D3D constant buffer to a single SCALAR float array
// member — `OpTypeStruct { float _m0[4N] }` with `ArrayStride 4` and `GL_EXT_scalar_block_layout`.
// spirv-cross's HLSL backend cannot express that ("cbuffer member 0 (_m0) cannot be expressed
// with either HLSL packing layout or packoffset"), so emission falls back to GLSL and the
// metadata symbol patcher — tuned to the float4 cbuffer shape produced by the DXIL path — fails
// to match. The DXIL path instead produces `float4 _m0[N]` (ArrayStride 16), which spirv-cross
// renders as HLSL and the patcher names correctly.
//
// This pass rewrites every Uniform (cbuffer) struct member of the scalar form into the float4
// form, and rewrites every access chain that indexes it: a scalar float index `j` selects
// float4 register `j >> 2`, component `j & 3`. Constant indices fold to constants; dynamic
// indices get an inserted `OpShiftRightLogical`/`OpBitwiseAnd` pair (spirv-cross folds the
// `(x << 2) >> 2` round-trips back out). The byte layout of the underlying buffer is unchanged
// — only the SPIR-V type/index representation moves from scalar-granular to vec4-granular.
//
// No-op (returns the input unchanged) when there are no scalar Uniform cbuffer arrays, so it is
// safe to run on every shader regardless of front-end (DXIL output is already float4).
internal static class ScalarCbufferVectorizer
{
    public static byte[] Vectorize(byte[] spirv)
    {
        SpirvModule module;
        try { module = SpirvModule.Parse(spirv); }
        catch { return spirv; }

        // --- Scan: types, constants, decorations, variables ---
        var constUintValue = new Dictionary<uint, uint>();          // OpConstant id -> value
        var floatScalarTypes = new HashSet<uint>();                 // OpTypeFloat width 32
        var arrayElem = new Dictionary<uint, uint>();               // OpTypeArray id -> element type id
        var arrayStride = new Dictionary<uint, uint>();             // type id -> ArrayStride
        var blockStructs = new HashSet<uint>();                     // struct ids with Decoration Block
        var pointerTarget = new Dictionary<uint, (uint Storage, uint Target)>();
        var structInsts = new Dictionary<uint, SpirvInstruction>(); // struct id -> OpTypeStruct
        uint uintTypeId = 0;

        foreach (SpirvInstruction i in module.Instructions)
        {
            switch (i.OpCode)
            {
                case SpvOpCode.OpTypeFloat when i.Words.Length >= 3 && i[2] == 32:
                    floatScalarTypes.Add(i[1]); break;
                case SpvOpCode.OpTypeInt when i.Words.Length >= 4 && i[2] == 32 && i[3] == 0:
                    uintTypeId = i[1]; break;
                case SpvOpCode.OpConstant when i.Words.Length >= 4:
                    constUintValue[i[2]] = i[3]; break;
                case SpvOpCode.OpTypeArray when i.Words.Length >= 4:
                    arrayElem[i[1]] = i[2]; break;
                case SpvOpCode.OpTypeStruct:
                    structInsts[i[1]] = i; break;
                case SpvOpCode.OpTypePointer when i.Words.Length >= 4:
                    pointerTarget[i[1]] = (i[2], i[3]); break;
                case SpvOpCode.OpDecorate when i.Words.Length >= 3 && i[2] == SpvOpCode.DecorationBlock:
                    blockStructs.Add(i[1]); break;
                case SpvOpCode.OpDecorate when i.Words.Length >= 4 && i[2] == SpvOpCode.DecorationArrayStride:
                    arrayStride[i[1]] = i[3]; break;
            }
        }

        // Uniform-storage structs whose members include a scalar-float array (stride 4).
        var scalarMembers = new Dictionary<(uint Struct, int Member), uint>();   // -> scalar array typeId
        var varToStruct = new Dictionary<uint, uint>();                          // cbuffer var id -> struct id

        foreach (SpirvInstruction v in module.Instructions)
        {
            if (v.OpCode != SpvOpCode.OpVariable || v.Words.Length < 4) continue;
            if (v[3] != SpvOpCode.StorageClassUniform) continue;     // cbuffer only, never SSBO
            if (!pointerTarget.TryGetValue(v[1], out var pt)) continue;
            uint structId = pt.Target;
            if (!blockStructs.Contains(structId) || !structInsts.TryGetValue(structId, out SpirvInstruction? sInst)) continue;

            bool isTarget = false;
            for (int mi = 0; mi < sInst.Words.Length - 2; mi++)
            {
                uint memberType = sInst[2 + mi];
                if (arrayElem.TryGetValue(memberType, out uint elem) && floatScalarTypes.Contains(elem)
                    && arrayStride.TryGetValue(memberType, out uint stride) && stride == 4)
                {
                    scalarMembers[(structId, mi)] = memberType;
                    isTarget = true;
                }
            }
            if (isTarget) varToStruct[v[2]] = structId;
        }

        if (scalarMembers.Count == 0) return spirv;                  // not the DXBC-direct scalar form — no-op

        // --- Phase A: vectorise the matched members in-place: float[4N] -> float4[N] ---
        uint floatTypeId = floatScalarTypes.First();
        uint vec4TypeId = EnsureVec4(module, floatTypeId);
        var vectorisedArray = new Dictionary<uint, uint>();          // scalar array id -> float4 array id
        foreach (var ((structId, member), scalarArrayId) in scalarMembers)
        {
            if (!vectorisedArray.TryGetValue(scalarArrayId, out uint vec4ArrayId))
            {
                int vec4Len = (ArrayLength(module, scalarArrayId, constUintValue) + 3) / 4;
                vec4ArrayId = CreateArray(module, vec4TypeId, vec4Len, 16);
                vectorisedArray[scalarArrayId] = vec4ArrayId;
            }
            structInsts[structId][2 + member] = vec4ArrayId;         // repoint struct member -> float4 array
        }

        // --- Phase B: collect access chains to split (snapshot — no mutation here) ---
        // shape we split: [op, ptrType, result, base, memberIdx(const), floatIdx]  (exactly 2 indices,
        // ending at a scalar float). Record (instruction, floatIdx, isConst, jValue).
        var plans = new List<(SpirvInstruction Inst, uint FloatIdx, bool IsConst, uint J)>();
        foreach (SpirvInstruction inst in module.Instructions)
        {
            bool isAccess = inst.OpCode is SpvOpCode.OpAccessChain or SpvOpCode.OpInBoundsAccessChain;
            if (!isAccess || inst.Words.Length != 6 || !varToStruct.TryGetValue(inst[3], out uint structId)) continue;
            if (!constUintValue.TryGetValue(inst[4], out uint memberIndex)) continue;
            if (!scalarMembers.ContainsKey((structId, (int)memberIndex))) continue;

            uint floatIdx = inst[5];
            bool isConst = constUintValue.TryGetValue(floatIdx, out uint j);
            plans.Add((inst, floatIdx, isConst, isConst ? j : 0));
        }
        if (plans.Count == 0) return module.ToBytes();               // types fixed, nothing indexes them

        // --- Phase C: create every constant the splits need (mutates type section, no live foreach) ---
        if (uintTypeId == 0) uintTypeId = EnsureUintType(module);
        bool anyDynamic = plans.Exists(static p => !p.IsConst);
        uint const2 = anyDynamic ? FindOrCreateUint(module, uintTypeId, 2) : 0;
        uint const3 = anyDynamic ? FindOrCreateUint(module, uintTypeId, 3) : 0;
        foreach (var p in plans)
        {
            if (!p.IsConst) continue;
            FindOrCreateUint(module, uintTypeId, p.J >> 2);
            FindOrCreateUint(module, uintTypeId, p.J & 3);
        }

        // --- Phase D: apply. Const splits rewrite the index operands in place; dynamic splits also
        // need an OpShiftRightLogical / OpBitwiseAnd pair inserted just before the access chain. ---
        var dynamicExtra = new Dictionary<SpirvInstruction, (SpirvInstruction Shr, SpirvInstruction And)>();
        foreach (var p in plans)
        {
            if (p.IsConst)
            {
                uint kId = FindOrCreateUint(module, uintTypeId, p.J >> 2);
                uint cId = FindOrCreateUint(module, uintTypeId, p.J & 3);
                p.Inst.Words = [p.Inst.Words[0], p.Inst[1], p.Inst[2], p.Inst[3], p.Inst[4], kId, cId];
            }
            else
            {
                uint kId = module.AllocateId();
                uint cId = module.AllocateId();
                var shr = new SpirvInstruction
                {
                    OpCode = SpvOpCode.OpShiftRightLogical,
                    Words = [SpvOpCode.MakeInstructionWord(SpvOpCode.OpShiftRightLogical, 5), uintTypeId, kId, p.FloatIdx, const2],
                };
                var and = new SpirvInstruction
                {
                    OpCode = SpvOpCode.OpBitwiseAnd,
                    Words = [SpvOpCode.MakeInstructionWord(SpvOpCode.OpBitwiseAnd, 5), uintTypeId, cId, p.FloatIdx, const3],
                };
                p.Inst.Words = [p.Inst.Words[0], p.Inst[1], p.Inst[2], p.Inst[3], p.Inst[4], kId, cId];
                dynamicExtra[p.Inst] = (shr, and);
            }
        }

        if (dynamicExtra.Count > 0)
        {
            var rebuilt = new List<SpirvInstruction>(module.Instructions.Count + dynamicExtra.Count * 2);
            foreach (SpirvInstruction inst in module.Instructions)
            {
                if (dynamicExtra.TryGetValue(inst, out var ex)) { rebuilt.Add(ex.Shr); rebuilt.Add(ex.And); }
                rebuilt.Add(inst);
            }
            module.Instructions.Clear();
            module.Instructions.AddRange(rebuilt);
        }

        return module.ToBytes();
    }

    // === helpers (self-contained: scan-then-create, reusing existing ids) ===

    private static int ArrayLength(SpirvModule module, uint arrayTypeId, Dictionary<uint, uint> constUintValue)
    {
        foreach (SpirvInstruction i in module.Instructions)
            if (i.OpCode == SpvOpCode.OpTypeArray && i.Words.Length >= 4 && i[1] == arrayTypeId)
                return constUintValue.TryGetValue(i[3], out uint len) ? (int)len : 0;
        return 0;
    }

    private static uint EnsureVec4(SpirvModule module, uint floatTypeId)
    {
        foreach (SpirvInstruction i in module.Instructions)
            if (i.OpCode == SpvOpCode.OpTypeVector && i.Words.Length >= 4 && i[2] == floatTypeId && i[3] == 4)
                return i[1];
        uint id = module.AllocateId();
        module.Instructions.Insert(module.FindTypeSectionEndIndex(), new SpirvInstruction
        {
            OpCode = SpvOpCode.OpTypeVector,
            Words = [SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypeVector, 4), id, floatTypeId, 4],
        });
        return id;
    }

    private static uint CreateArray(SpirvModule module, uint elementTypeId, int length, uint stride)
    {
        uint lenConst = FindOrCreateUint(module, EnsureUintType(module), checked((uint)length));
        foreach (SpirvInstruction i in module.Instructions)
            if (i.OpCode == SpvOpCode.OpTypeArray && i.Words.Length >= 4 && i[2] == elementTypeId && i[3] == lenConst)
                return i[1];

        uint id = module.AllocateId();
        // ArrayStride decoration lands ahead of the type block (SPIR-V layout rule).
        int decoIndex = module.FindFirstTypeInstructionIndex();
        while (decoIndex > 0 && (module.Instructions[decoIndex - 1].OpCode == SpvOpCode.OpDecorate
                                 || module.Instructions[decoIndex - 1].OpCode == SpvOpCode.OpMemberDecorate))
            decoIndex--;
        module.Instructions.Insert(decoIndex, new SpirvInstruction
        {
            OpCode = SpvOpCode.OpDecorate,
            Words = [SpvOpCode.MakeInstructionWord(SpvOpCode.OpDecorate, 4), id, SpvOpCode.DecorationArrayStride, stride],
        });
        module.Instructions.Insert(module.FindTypeSectionEndIndex(), new SpirvInstruction
        {
            OpCode = SpvOpCode.OpTypeArray,
            Words = [SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypeArray, 4), id, elementTypeId, lenConst],
        });
        return id;
    }

    private static uint EnsureUintType(SpirvModule module)
    {
        foreach (SpirvInstruction i in module.Instructions)
            if (i.OpCode == SpvOpCode.OpTypeInt && i.Words.Length >= 4 && i[2] == 32 && i[3] == 0)
                return i[1];
        uint id = module.AllocateId();
        module.Instructions.Insert(module.FindTypeSectionEndIndex(), new SpirvInstruction
        {
            OpCode = SpvOpCode.OpTypeInt,
            Words = [SpvOpCode.MakeInstructionWord(SpvOpCode.OpTypeInt, 4), id, 32, 0],
        });
        return id;
    }

    private static uint FindOrCreateUint(SpirvModule module, uint uintTypeId, uint value)
    {
        foreach (SpirvInstruction i in module.Instructions)
            if (i.OpCode == SpvOpCode.OpConstant && i.Words.Length >= 4 && i[1] == uintTypeId && i[3] == value)
                return i[2];
        uint id = module.AllocateId();
        module.Instructions.Insert(module.FindTypeSectionEndIndex(), new SpirvInstruction
        {
            OpCode = SpvOpCode.OpConstant,
            Words = [SpvOpCode.MakeInstructionWord(SpvOpCode.OpConstant, 4), uintTypeId, id, value],
        });
        return id;
    }
}
