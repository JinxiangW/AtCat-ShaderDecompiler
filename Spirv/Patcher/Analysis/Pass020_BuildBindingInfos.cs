namespace Ruri.ShaderTools.Spirv.Patcher.Analysis;

// Pass 020 (analysis): turn the per-id maps from Pass010 into one `SpirvBindingInfo` per
// (set, binding) pair. The descriptor-type classifier walks variable → pointer → pointee
// to decide between UniformBuffer / StorageBuffer / Sampler / SampledImage / Unknown.
//
// dxil-spirv quirk: texture resources land as plain `OpTypeImage` rather than the
// `OpTypeSampledImage` shape direct dxc -spirv emits. We treat both as SampledImage so
// downstream symbol restoration doesn't leave DXIL routes stuck on machine names like
// `_8` / `_14`.
//
// Output is sorted by (Set, Binding) so the consumer's iteration order is deterministic
// across SPIR-V revisions.
internal static class Pass020_BuildBindingInfos
{
    public static void DoPass(BindingAnalysisState state)
    {
        var result = new List<SpirvBindingInfo>();

        foreach (KeyValuePair<uint, (int? Set, int? Binding)> kvp in state.IdToSetBinding)
        {
            if (!kvp.Value.Set.HasValue || !kvp.Value.Binding.HasValue)
            {
                continue;
            }

            var info = new SpirvBindingInfo
            {
                Id = kvp.Key,
                Set = kvp.Value.Set.Value,
                Binding = kvp.Value.Binding.Value,
            };

            if (state.IdToName.TryGetValue(kvp.Key, out string? currentName))
            {
                info.CurrentName = currentName;
            }

            ClassifyDescriptorType(state, info);
            result.Add(info);
        }

        state.Bindings = result.OrderBy(x => x.Set).ThenBy(x => x.Binding).ToList();
    }

    private static void ClassifyDescriptorType(BindingAnalysisState state, SpirvBindingInfo info)
    {
        if (!state.VariableTypeMap.TryGetValue(info.Id, out uint pointerTypeId))
        {
            return;
        }

        if (!state.TypePointerMap.TryGetValue(pointerTypeId, out (uint StorageClass, uint PointedType) ptrInfo))
        {
            return;
        }

        uint pointedType = ptrInfo.PointedType;

        if (state.StructTypeIds.Contains(pointedType))
        {
            // StorageClass 2 = Uniform (cbuffer); anything else with a struct backing is
            // a storage buffer (SSBO / UAV-bound structured buffer).
            info.DescriptorType = ptrInfo.StorageClass == 2 ? "UniformBuffer" : "StorageBuffer";
            info.StructTypeId = pointedType;
            info.StructMemberCount = state.StructMemberCounts.TryGetValue(pointedType, out int count) ? count : 0;
            if (state.StructMemberOffsets.TryGetValue(pointedType, out Dictionary<int, uint>? offsets))
            {
                info.MemberOffsets = offsets;
            }
            // Capture every existing OpMemberName decoration on this struct
            // type so the symbol patcher can choose between (a) overriding
            // with seed-CB names where available and (b) preserving cook
            // names (after sanitisation + dedup) for slots the seed didn't
            // cover. Without this snapshot the patcher silently lets the
            // cook's pre-baked decorations win at any slot we don't override,
            // which is the source of HLSL duplicate-member compile errors.
            foreach (var kv in state.MemberNames)
            {
                if (kv.Key.StructTypeId == pointedType)
                {
                    info.CurrentMemberNames[kv.Key.MemberIndex] = kv.Value;
                }
            }
            return;
        }

        if (state.SamplerTypeIds.Contains(pointedType))
        {
            info.DescriptorType = "Sampler";
            return;
        }

        // dxil-spirv quirk: textures may land as OpTypeImage (no OpTypeSampledImage
        // wrapper). Treat both as SampledImage so symbol restoration still kicks in.
        if (state.SampledImageTypeIds.Contains(pointedType) || state.ImageTypeIds.Contains(pointedType))
        {
            info.DescriptorType = "SampledImage";
            return;
        }

        info.DescriptorType = "Unknown";
    }
}
