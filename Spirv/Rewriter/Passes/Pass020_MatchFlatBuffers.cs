using Ruri.ShaderTools.Spirv.Rewriter.Models;

namespace Ruri.ShaderTools.Spirv.Rewriter.Passes;

// Pass 020: match each metadata `ConstantBufferBinding` to a SPIR-V uniform variable.
//
// The match relies on (set, binding) decorations recorded in Pass010, plus a structural
// shape check: SPIR-V cbuffers from dxil-spirv land as
//   `%CBnUBO = OpTypeStruct %_arr_v4float_uint_N`
//   `%var = OpVariable %_ptr_Uniform_CBnUBO Uniform`
// — i.e. a single-member wrapper struct around a fixed-length float4 array. We only rewrite
// CBs that match this exact shape. Skip-with-summary on every other case (no metadata,
// missing decoration, wrong storage class, multi-member wrapper, runtime array, etc.) so
// the failure summary tells the user *why* a particular CB stayed flat.
internal static class Pass020_MatchFlatBuffers
{
    public static void DoPass(RewriterPipelineState state)
    {
        var result = new List<FlatUniformBufferInfo>();
        foreach (BufferBindingParameter resource in state.Metadata.BufferBindingParameters)
        {
            ConstantBufferParameter? constantBuffer = state.Metadata.GetConstantBufferByName(resource.Name);
            if (constantBuffer == null)
            {
                state.Summary.Add($"[{resource.Name}] no USC constant buffer metadata found");
                continue;
            }

            int resourceSet = state.Metadata.GetSetIdFor(resource.Index, ShaderResourceType.ConstantBuffer);
            uint? variableId = FindUniformVariableId(state.Analysis, resourceSet, resource.Index);
            if (variableId == 0 || variableId == null)
            {
                state.Summary.Add($"[{resource.Name}] no decorated id for set={resourceSet} binding={resource.Index}");
                continue;
            }

            if (!state.Analysis.VariablePointerTypes.TryGetValue(variableId.Value, out uint pointerTypeId))
            {
                state.Summary.Add($"[{resource.Name}] decorated id {variableId.Value} is not an OpVariable");
                continue;
            }

            if (!state.Analysis.PointerTypes.TryGetValue(pointerTypeId, out (uint StorageClass, uint TypeId) pointerInfo)
                || pointerInfo.StorageClass != SpvOpCode.StorageClassUniform)
            {
                state.Summary.Add($"[{resource.Name}] variable {variableId.Value} is not a uniform pointer");
                continue;
            }

            if (!state.Analysis.StructMembers.TryGetValue(pointerInfo.TypeId, out uint[]? wrapperMembers) || wrapperMembers.Length != 1)
            {
                state.Summary.Add($"[{resource.Name}] variable {variableId.Value} is not a single-member wrapper struct");
                continue;
            }

            uint arrayTypeId = wrapperMembers[0];
            if (!state.Analysis.ArrayTypes.TryGetValue(arrayTypeId, out (uint ElementTypeId, uint LengthId) arrayInfo)
                || !state.Analysis.Constants.TryGetValue(arrayInfo.LengthId, out uint arrayLength))
            {
                state.Summary.Add($"[{resource.Name}] wrapper member is not a fixed array type");
                continue;
            }

            int arrayStride = state.Analysis.ArrayStrides.TryGetValue(arrayTypeId, out uint strideValue)
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
                ConstantBuffer = constantBuffer,
            });
        }

        state.FlatBuffers = result;
    }

    private static uint? FindUniformVariableId(ModuleAnalysis analysis, int set, int binding)
    {
        return analysis.SetBindingById
            .Where(static entry => entry.Value.Set.HasValue && entry.Value.Binding.HasValue)
            .Where(entry => entry.Value.Set == set && entry.Value.Binding == binding)
            .Select(entry => entry.Key)
            .FirstOrDefault(id =>
                analysis.VariablePointerTypes.TryGetValue(id, out uint candidatePointerTypeId)
                && analysis.PointerTypes.TryGetValue(candidatePointerTypeId, out (uint StorageClass, uint TypeId) candidatePointerInfo)
                && candidatePointerInfo.StorageClass == SpvOpCode.StorageClassUniform);
    }
}
