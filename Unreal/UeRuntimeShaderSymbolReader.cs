namespace Ruri.ShaderTools.Unreal;

internal static class UeRuntimeShaderSymbolReader
{
    public static ShaderSymbolData Read(UnrealShaderParser.UnrealMetadata? metadata)
    {
        ShaderSymbolData symbols = new();
        if (metadata?.UniformBufferNames == null)
        {
            return symbols;
        }

        for (int i = 0; i < metadata.UniformBufferNames.Count; i++)
        {
            string name = metadata.UniformBufferNames[i];
            if (string.IsNullOrWhiteSpace(name))
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

        symbols.RefreshCompatibilityViews();
        return symbols;
    }
}
