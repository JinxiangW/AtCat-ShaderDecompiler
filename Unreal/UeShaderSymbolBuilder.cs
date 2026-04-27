using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Ruri.ShaderTools.Unreal;

internal static class UeShaderSymbolBuilder
{
    public static ShaderSymbolData Build(UeShaderSymbolInputs inputs)
    {
        ShaderSymbolData metadata = new()
        {
            DebugName = inputs.MaterialPath
        };

        if (inputs.MaterialConstantBuffer != null)
        {
            metadata.ConstantBuffers.Add(inputs.MaterialConstantBuffer);
        }

        metadata.ConstantBuffers = metadata.ConstantBuffers
            .GroupBy(static buffer => buffer.Name, StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToList();
        metadata.RefreshCompatibilityViews();
        return metadata;
    }
}

internal static class UeShaderSymbolHeaderWriter
{
    public static string Build(UeShaderSymbolInputs inputs)
    {
        StringBuilder sb = new();
        sb.AppendLine("/*");
        sb.AppendLine(" * UE Shader Symbol Inputs");
        sb.AppendLine($" * Material: {inputs.MaterialPath}");
        if (!string.IsNullOrWhiteSpace(inputs.ShaderPlatform))
        {
            sb.AppendLine($" * ShaderPlatform: {inputs.ShaderPlatform}");
        }

        sb.AppendLine($" * Source: {(inputs.UsedLoadedMaterialResources ? "LoadedMaterialResources.UniformExpressionSet" : "Material Properties Fallback")}");
        sb.AppendLine($" * NumericParameters: {inputs.NumericParameterInfos.Count}");
        sb.AppendLine($" * MaterialConstantBuffer: {(inputs.MaterialConstantBuffer != null ? "present" : "absent")}");

        foreach (FMaterialParameterInfo parameterInfo in inputs.NumericParameterInfos
                     .GroupBy(static info => $"{info.Name}|{info.Association}|{info.Index}", StringComparer.Ordinal)
                     .Select(static group => group.First())
                     .Take(16))
        {
            sb.AppendLine($" * Parameter: Name={parameterInfo.Name}, Association={parameterInfo.Association}, Index={parameterInfo.Index}");
        }

        if (inputs.MaterialConstantBuffer != null)
        {
            foreach (ConstantBufferParameter parameter in inputs.MaterialConstantBuffer.CBParams.Take(32))
            {
                sb.AppendLine($" * MaterialCB: {parameter.ParamName} @ byte {parameter.ByteOffset} rows={parameter.Rows} cols={parameter.Columns}");
            }
        }

        sb.AppendLine(" */");
        sb.AppendLine();
        return sb.ToString();
    }
}

internal sealed class UeShaderSymbolInputs
{
    public string MaterialPath { get; set; } = string.Empty;
    public string? ShaderPlatform { get; set; }
    public bool UsedLoadedMaterialResources { get; set; }
    public ConstantBuffer? MaterialConstantBuffer { get; set; }
    public List<FMaterialParameterInfo> NumericParameterInfos { get; } = new();
    public UeMaterialUniformBufferLayout.MaterialResourceCounts? MaterialResourceCounts { get; set; }
}
