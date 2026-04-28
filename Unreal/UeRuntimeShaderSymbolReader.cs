using System;
using System.Text.RegularExpressions;

namespace Ruri.ShaderTools.Unreal;

internal static class UeRuntimeShaderSymbolReader
{
    private static readonly Regex GeneratedUniformBufferNamePattern = new("^CB\\d+UBO$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ShaderSymbolData Read(UnrealShaderParser.UnrealMetadata? metadata, UeMaterialUniformBufferLayout? materialLayout = null)
    {
        ShaderSymbolData symbols = new();
        if (metadata?.UniformBufferNames == null)
        {
            return symbols;
        }

        for (int i = 0; i < metadata.UniformBufferNames.Count; i++)
        {
            string name = metadata.UniformBufferNames[i];
            if (!IsCanonicalUniformBufferName(name))
            {
                continue;
            }

            symbols.ConstantBufferBindings.Add(new BufferBinding
            {
                Name = name,
                NameIndex = -1,
                Index = i,
                Set = 0,
                ArraySize = 0,
            });
        }

        UeShaderResourceTableSymbolizer.EnrichSymbolData(symbols, metadata, materialLayout);

        return symbols;
    }

    private static bool IsCanonicalUniformBufferName(string? name)
    {
        return !string.IsNullOrWhiteSpace(name) && !GeneratedUniformBufferNamePattern.IsMatch(name);
    }
}
