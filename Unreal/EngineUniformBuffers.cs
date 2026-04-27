using System.Collections.Generic;

namespace Ruri.ShaderTools.Unreal;

// Static look-up of resource-typed members per engine-defined uniform
// buffer. Each entry maps (UB name, ResourceIndex within the UB,
// register class) -> the flat shader-side name that the D3D
// translator produces (after RemoveUniformBuffersFromSource flattens
// `View.X` to `View_X`).
//
// Member orderings are quoted from UE 5.1.1
// (E:\UnrealEngine-5.1.1-release). The compiler-side `Entry.ResourceIndex`
// is allocated **per register class** (textures, samplers, SRVs, UAVs
// each have their own running index in source order), so the lookup
// keys here are also per-register-class. For unknown UB members the
// fallback in UeShaderResourceTableSymbolizer still produces a
// readable placeholder `<UBName>_<RegisterClass><i>`.
internal static class EngineUniformBuffers
{
    private static readonly Dictionary<EngineKey, string> KnownNames = BuildKnownNames();

    public static string? Resolve(string uniformBufferName, int resourceIndex, UeSrtRegisterType registerType)
    {
        EngineKey key = new(uniformBufferName, resourceIndex, registerType);
        return KnownNames.TryGetValue(key, out string? name) ? name : null;
    }

    private static Dictionary<EngineKey, string> BuildKnownNames()
    {
        Dictionary<EngineKey, string> map = new();
        AddView(map);
        AddOpaqueBasePass(map);
        AddSceneTextures(map);
        AddLumenCardScene(map);
        AddVirtualShadowMap(map);
        AddRenderVolumetricCloudParameters(map);
        return map;
    }

    // SceneView.h FViewUniformShaderParameters, lines 840-942 (UE 5.1.1).
    // Resource indices are per register class.
    private static void AddView(Dictionary<EngineKey, string> map)
    {
        string[] textures =
        {
            "VolumetricLightmapIndirectionTexture",
            "VolumetricLightmapBrickAmbientVector",
            "VolumetricLightmapBrickSHCoefficients0",
            "VolumetricLightmapBrickSHCoefficients1",
            "VolumetricLightmapBrickSHCoefficients2",
            "VolumetricLightmapBrickSHCoefficients3",
            "VolumetricLightmapBrickSHCoefficients4",
            "VolumetricLightmapBrickSHCoefficients5",
            "SkyBentNormalBrickTexture",
            "DirectionalLightShadowingBrickTexture",
            "GlobalDistanceFieldPageAtlasTexture",
            "GlobalDistanceFieldCoverageAtlasTexture",
            "GlobalDistanceFieldPageTableTexture",
            "GlobalDistanceFieldMipTexture",
            "AtmosphereTransmittanceTexture",
            "AtmosphereIrradianceTexture",
            "AtmosphereInscatterTexture",
            "PerlinNoiseGradientTexture",
            "PerlinNoise3DTexture",
            "SobolSamplingTexture",
            "PreIntegratedBRDF",
            "TransmittanceLutTexture",
            "SkyViewLutTexture",
            "DistantSkyLightLutTexture",
            "CameraAerialPerspectiveVolume",
            "HairScatteringLUTTexture",
            "LTCMatTexture",
            "LTCAmpTexture",
            "ShadingEnergyGGXSpecTexture",
            "ShadingEnergyGGXGlassTexture",
            "ShadingEnergyClothSpecTexture",
            "ShadingEnergyDiffuseTexture",
            "SSProfilesTexture",
            "SSProfilesPreIntegratedTexture",
            "RectLightAtlasTexture",
        };
        AddRange(map, "View", UeSrtRegisterType.Texture, textures);
        AddRange(map, "View", UeSrtRegisterType.ShaderResourceView, textures);

        string[] samplers =
        {
            "MaterialTextureBilinearWrapedSampler",
            "MaterialTextureBilinearClampedSampler",
            "VolumetricLightmapBrickAmbientVectorSampler",
            "VolumetricLightmapTextureSampler0",
            "VolumetricLightmapTextureSampler1",
            "VolumetricLightmapTextureSampler2",
            "VolumetricLightmapTextureSampler3",
            "VolumetricLightmapTextureSampler4",
            "VolumetricLightmapTextureSampler5",
            "SkyBentNormalTextureSampler",
            "DirectionalLightShadowingTextureSampler",
            "AtmosphereTransmittanceTextureSampler",
            "AtmosphereIrradianceTextureSampler",
            "AtmosphereInscatterTextureSampler",
            "PerlinNoiseGradientTextureSampler",
            "PerlinNoise3DTextureSampler",
            "SharedPointWrappedSampler",
            "SharedPointClampedSampler",
            "SharedBilinearWrappedSampler",
            "SharedBilinearClampedSampler",
            "SharedBilinearAnisoClampedSampler",
            "SharedTrilinearWrappedSampler",
            "SharedTrilinearClampedSampler",
            "PreIntegratedBRDFSampler",
            "TransmittanceLutTextureSampler",
            "SkyViewLutTextureSampler",
            "DistantSkyLightLutTextureSampler",
            "CameraAerialPerspectiveVolumeSampler",
            "HairScatteringLUTSampler",
            "LTCMatSampler",
            "LTCAmpSampler",
            "ShadingEnergySampler",
            "SSProfilesSampler",
            "SSProfilesTransmissionSampler",
            "SSProfilesPreIntegratedSampler",
            "RectLightAtlasSampler",
            "LandscapeWeightmapSampler",
        };
        AddRange(map, "View", UeSrtRegisterType.Sampler, samplers);

        string[] srvs =
        {
            "PrimitiveSceneData",
            "InstanceSceneData",
            "InstancePayloadData",
            "LightmapSceneData",
            "SkyIrradianceEnvironmentMap",
            "WaterIndirection",
            "WaterData",
            "LandscapeIndirection",
            "LandscapePerComponentData",
            "EditorVisualizeLevelInstanceIds",
            "EditorSelectedHitProxyIds",
            "PhysicsFieldClipmapBuffer",
        };
        AddRange(map, "View", UeSrtRegisterType.ShaderResourceView, srvs, offset: textures.Length);

        AddRange(map, "View", UeSrtRegisterType.UnorderedAccessView, new[]
        {
            "VTFeedbackBuffer",
        });
    }

    // BasePassRendering.h FOpaqueBasePassUniformParameters, lines 78-91.
    // Note: nested FSharedBasePassUniformParameters / FStrataBasePassUniformParameters
    // /  FDBufferParameters references are not expanded here; their
    // resource members will surface as placeholders until verified.
    private static void AddOpaqueBasePass(Dictionary<EngineKey, string> map)
    {
        string[] textures =
        {
            "ForwardScreenSpaceShadowMaskTexture",
            "IndirectOcclusionTexture",
            "ResolvedSceneDepthTexture",
            "PreIntegratedGFTexture",
            "EyeAdaptationTexture",
        };
        AddRange(map, "OpaqueBasePass", UeSrtRegisterType.Texture, textures);
        AddRange(map, "OpaqueBasePass", UeSrtRegisterType.ShaderResourceView, textures);

        AddRange(map, "OpaqueBasePass", UeSrtRegisterType.Sampler, new[]
        {
            "PreIntegratedGFSampler",
        });
    }

    // SceneTexturesConfig.h FSceneTextureUniformParameters, lines 15-35.
    private static void AddSceneTextures(Dictionary<EngineKey, string> map)
    {
        string[] textures =
        {
            "SceneColorTexture",
            "SceneDepthTexture",
            "GBufferATexture",
            "GBufferBTexture",
            "GBufferCTexture",
            "GBufferDTexture",
            "GBufferETexture",
            "GBufferFTexture",
            "GBufferVelocityTexture",
            "ScreenSpaceAOTexture",
            "CustomDepthTexture",
            "CustomStencilTexture",
        };
        AddRange(map, "SceneTextures", UeSrtRegisterType.Texture, textures);
        AddRange(map, "SceneTextures", UeSrtRegisterType.ShaderResourceView, textures);

        AddRange(map, "SceneTextures", UeSrtRegisterType.Sampler, new[]
        {
            "PointClampSampler",
        });
    }

    // LumenSceneData.h FLumenCardScene, lines 50-60.
    // Buffer SRVs come first in source order, then RDG textures.
    private static void AddLumenCardScene(Dictionary<EngineKey, string> map)
    {
        string[] srvs =
        {
            "CardData",
            "CardPageData",
            "MeshCardsData",
            "HeightfieldData",
            "PageTableBuffer",
            "SceneInstanceIndexToMeshCardsIndexBuffer",
            "AlbedoAtlas",
            "OpacityAtlas",
            "NormalAtlas",
            "EmissiveAtlas",
            "DepthAtlas",
        };
        AddRange(map, "LumenCardScene", UeSrtRegisterType.ShaderResourceView, srvs);
        AddRange(map, "LumenCardScene", UeSrtRegisterType.Texture, srvs);
    }

    // VirtualShadowMapArray.h FVirtualShadowMapUniformParameters, lines 141-145.
    private static void AddVirtualShadowMap(Dictionary<EngineKey, string> map)
    {
        string[] srvs =
        {
            "ProjectionData",
            "PageTable",
            "PageFlags",
            "PageRectBounds",
            "PhysicalPagePool",
        };
        AddRange(map, "VirtualShadowMap", UeSrtRegisterType.ShaderResourceView, srvs);
        AddRange(map, "VirtualShadowMap", UeSrtRegisterType.Texture, srvs);
    }

    // VolumetricCloudRendering.cpp FRenderVolumetricCloudGlobalParameters,
    // lines 537-558. Nested SHADER_PARAMETER_STRUCT_INCLUDEs not expanded.
    private static void AddRenderVolumetricCloudParameters(Dictionary<EngineKey, string> map)
    {
        string[] textures =
        {
            "SceneDepthTexture",
            "CloudShadowTexture0",
            "CloudShadowTexture1",
            "HZBTexture",
        };
        AddRange(map, "RenderVolumetricCloudParameters", UeSrtRegisterType.Texture, textures);
        AddRange(map, "RenderVolumetricCloudParameters", UeSrtRegisterType.ShaderResourceView, textures);

        AddRange(map, "RenderVolumetricCloudParameters", UeSrtRegisterType.Sampler, new[]
        {
            "CloudBilinearTextureSampler",
            "HZBSampler",
        });
    }

    private static void AddRange(
        Dictionary<EngineKey, string> map,
        string uniformBufferName,
        UeSrtRegisterType registerType,
        string[] memberNames,
        int offset = 0)
    {
        for (int i = 0; i < memberNames.Length; i++)
        {
            EngineKey key = new(uniformBufferName, offset + i, registerType);
            map[key] = $"{uniformBufferName}_{memberNames[i]}";
        }
    }

    private readonly record struct EngineKey(string UniformBufferName, int ResourceIndex, UeSrtRegisterType RegisterType);
}
