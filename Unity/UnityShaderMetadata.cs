namespace Ruri.ShaderTools;

public sealed class UnityShaderMetadata
{
    public uint m_ObjectHideFlags { get; set; }
    public UnityPPtr m_CorrespondingSourceObject { get; set; } = new();
    public UnityPPtr m_PrefabInstance { get; set; } = new();
    public UnityPPtr m_PrefabAsset { get; set; } = new();
    public string m_Name { get; set; } = string.Empty;
    public UnitySerializedShader m_ParsedForm { get; set; } = new();
    public string m_CustomEditorName { get; set; } = string.Empty;
    public string m_FallbackName { get; set; } = string.Empty;
    public List<UnitySerializedShaderDependency> m_Dependencies { get; set; } = new();
    public List<UnitySerializedCustomEditorForRenderPipeline> m_CustomEditorForRenderPipelines { get; set; } = new();
    public List<int> platforms { get; set; } = new();
    public List<List<uint>> offsets { get; set; } = new();
    public List<List<uint>> compressedLengths { get; set; } = new();
    public List<List<uint>> decompressedLengths { get; set; } = new();
    public byte[] compressedBlob { get; set; } = [];
}

public sealed class UnityPPtr
{
    public int m_FileID { get; set; }
    public long m_PathID { get; set; }
}

public sealed class UnitySerializedShaderDependency
{
    public string from { get; set; } = string.Empty;
    public string to { get; set; } = string.Empty;
}

public sealed class UnitySerializedCustomEditorForRenderPipeline
{
    public string customEditorName { get; set; } = string.Empty;
    public string renderPipelineType { get; set; } = string.Empty;
}

public sealed class UnitySerializedShader
{
    public string m_Name { get; set; } = string.Empty;
    public UnitySerializedProperties m_PropInfo { get; set; } = new();
    public List<UnitySerializedSubShader> m_SubShaders { get; set; } = new();
    public string m_FallbackName { get; set; } = string.Empty;
    public List<string> m_KeywordNames { get; set; } = new();
}

public sealed class UnitySerializedProperties
{
    public List<UnitySerializedProperty> m_Props { get; set; } = new();
}

public sealed class UnitySerializedProperty
{
    public string m_Name { get; set; } = string.Empty;
    public string m_Description { get; set; } = string.Empty;
    public List<string> m_Attributes { get; set; } = new();
    public int m_Type { get; set; }
    public uint m_Flags { get; set; }
    public float[] m_DefValue { get; set; } = new float[4];
    public UnitySerializedTextureProperty m_DefTexture { get; set; } = new();
}

public sealed class UnitySerializedTextureProperty
{
    public string m_DefaultName { get; set; } = string.Empty;
    public int m_TexDim { get; set; }
}

public sealed class UnitySerializedSubShader
{
    public List<UnitySerializedPass> m_Passes { get; set; } = new();
    public UnitySerializedTagMap m_Tags { get; set; } = new();
    public int m_LOD { get; set; }
}

public sealed class UnitySerializedPass
{
    public List<UnitySerializedNameIndex> m_NameIndices { get; set; } = new();
    public int m_Type { get; set; }
    public UnitySerializedShaderState m_State { get; set; } = new();
    public uint m_ProgramMask { get; set; }
    public string m_UseName { get; set; } = string.Empty;
    public string m_Name { get; set; } = string.Empty;
    public string m_TextureName { get; set; } = string.Empty;
    public bool m_HasInstancingVariant { get; set; }
    public bool m_HasProceduralInstancingVariant { get; set; }
    public List<UnityProgramData> Programs { get; set; } = new();
}

public sealed class UnitySerializedNameIndex
{
    public string first { get; set; } = string.Empty;
    public int second { get; set; }
}

public sealed class UnitySerializedShaderState
{
    public string m_Name { get; set; } = string.Empty;
    public int gpuProgramID { get; set; }
    public UnitySerializedShaderRTBlendState rtBlend0 { get; set; } = new();
    public UnitySerializedShaderRTBlendState rtBlend1 { get; set; } = new();
    public UnitySerializedShaderRTBlendState rtBlend2 { get; set; } = new();
    public UnitySerializedShaderRTBlendState rtBlend3 { get; set; } = new();
    public UnitySerializedShaderRTBlendState rtBlend4 { get; set; } = new();
    public UnitySerializedShaderRTBlendState rtBlend5 { get; set; } = new();
    public UnitySerializedShaderRTBlendState rtBlend6 { get; set; } = new();
    public UnitySerializedShaderRTBlendState rtBlend7 { get; set; } = new();
    public bool rtSeparateBlend { get; set; }
    public UnitySerializedStencilOp stencilOp { get; set; } = new();
    public UnitySerializedStencilOp stencilOpFront { get; set; } = new();
    public UnitySerializedStencilOp stencilOpBack { get; set; } = new();
    public UnitySerializedShaderFloatValue stencilRef { get; set; } = new();
    public UnitySerializedShaderFloatValue stencilReadMask { get; set; } = new();
    public UnitySerializedShaderFloatValue stencilWriteMask { get; set; } = new();
    public float fogMode { get; set; }
    public UnitySerializedShaderVectorValue fogColor { get; set; } = new();
    public UnitySerializedShaderFloatValue fogDensity { get; set; } = new();
    public UnitySerializedShaderFloatValue fogStart { get; set; } = new();
    public UnitySerializedShaderFloatValue fogEnd { get; set; } = new();
    public UnitySerializedShaderFloatValue alphaToMask { get; set; } = new();
    public UnitySerializedShaderFloatValue zClip { get; set; } = new();
    public UnitySerializedShaderFloatValue zTest { get; set; } = new();
    public UnitySerializedShaderFloatValue zWrite { get; set; } = new();
    public UnitySerializedShaderFloatValue culling { get; set; } = new();
    public UnitySerializedShaderFloatValue offsetFactor { get; set; } = new();
    public UnitySerializedShaderFloatValue offsetUnits { get; set; } = new();
    public bool lighting { get; set; }
    public int m_LOD { get; set; }
    public UnitySerializedTagMap m_Tags { get; set; } = new();
}

public sealed class UnitySerializedShaderRTBlendState
{
    public UnitySerializedShaderFloatValue srcBlend { get; set; } = new();
    public UnitySerializedShaderFloatValue destBlend { get; set; } = new();
    public UnitySerializedShaderFloatValue srcBlendAlpha { get; set; } = new();
    public UnitySerializedShaderFloatValue destBlendAlpha { get; set; } = new();
    public UnitySerializedShaderFloatValue blendOp { get; set; } = new();
    public UnitySerializedShaderFloatValue blendOpAlpha { get; set; } = new();
    public UnitySerializedShaderFloatValue colMask { get; set; } = new();
}

public sealed class UnitySerializedStencilOp
{
    public UnitySerializedShaderFloatValue pass { get; set; } = new();
    public UnitySerializedShaderFloatValue fail { get; set; } = new();
    public UnitySerializedShaderFloatValue zFail { get; set; } = new();
    public UnitySerializedShaderFloatValue comp { get; set; } = new();
}

public sealed class UnitySerializedShaderVectorValue
{
    public UnitySerializedShaderFloatValue x { get; set; } = new();
    public UnitySerializedShaderFloatValue y { get; set; } = new();
    public UnitySerializedShaderFloatValue z { get; set; } = new();
    public UnitySerializedShaderFloatValue w { get; set; } = new();
}

public sealed class UnitySerializedShaderFloatValue
{
    public float val { get; set; }
    public string name { get; set; } = string.Empty;
}

public sealed class UnitySerializedTagMap
{
    public List<UnityTagMapEntry> tags { get; set; } = new();
}

public sealed class UnityTagMapEntry
{
    public string first { get; set; } = string.Empty;
    public string second { get; set; } = string.Empty;
}

public sealed class UnityProgramData
{
    public string Stage { get; set; } = string.Empty;
    public uint BlobIndex { get; set; }
    public uint? ParameterBlobIndex { get; set; }
    public List<ushort> KeywordIndices { get; set; } = new();
    public bool Success { get; set; }
    public string SourceLanguage { get; set; } = "hlsl";
    public string SourceFileExtension { get; set; } = ".hlsl";
    public string? SourceCode { get; set; }
    public string? ErrorMessage { get; set; }
}
