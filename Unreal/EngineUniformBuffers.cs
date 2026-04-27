using System.Collections.Generic;

namespace Ruri.ShaderTools.Unreal;

// Static look-up of resource-typed members per engine-defined uniform
// buffer. Each entry maps (UB name, ResourceIndex within the UB,
// register class) -> the flat shader-side name that the D3D
// translator produces (after RemoveUniformBuffersFromSource flattens
// `View.X` to `View_X`).
//
// Indexing rule (source-truth from
// `Engine/Source/Developer/ShaderCompilerCommon/Private/ShaderCompilerCommon.cpp:BuildResourceTableMapping`):
// the compiler walks every resource-typed member of a uniform buffer
// in C++ source declaration order, increments **one** running counter
// across all register classes, and stamps that counter into
// `FResourceTableEntry.ResourceIndex`. So UB-internal index N is the
// N-th resource-typed member in source order, regardless of whether
// it's a texture, sampler, SRV, or UAV.
//
// Runtime serialization rule
// (`Engine/Source/Runtime/RenderCore/Public/ShaderCore.h::FBaseShaderResourceTable`):
// the `'TextureMap'` array exists only on the compiler side; in cooked
// SRTs textures and SRVs are merged into `ShaderResourceViewMap`. So
// every UBMT_TEXTURE / UBMT_RDG_TEXTURE / UBMT_SRV / UBMT_RDG_*_SRV
// member tagged here uses `UeSrtRegisterType.ShaderResourceView`.
//
// Member orders below are quoted from UE 5.1.1
// (`E:\UnrealEngine-5.1.1-release`); each line keeps the original
// `(line N)` annotation for spot-checking.
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
    private static void AddView(Dictionary<EngineKey, string> map)
    {
        AddMembers(map, "View", new (UeSrtRegisterType, string)[]
        {
            (UeSrtRegisterType.Sampler, "MaterialTextureBilinearWrapedSampler"),       // line 840
            (UeSrtRegisterType.Sampler, "MaterialTextureBilinearClampedSampler"),      // line 841
            (UeSrtRegisterType.ShaderResourceView, "VolumetricLightmapIndirectionTexture"),     // line 843
            (UeSrtRegisterType.ShaderResourceView, "VolumetricLightmapBrickAmbientVector"),     // line 844
            (UeSrtRegisterType.ShaderResourceView, "VolumetricLightmapBrickSHCoefficients0"),   // line 845
            (UeSrtRegisterType.ShaderResourceView, "VolumetricLightmapBrickSHCoefficients1"),   // line 846
            (UeSrtRegisterType.ShaderResourceView, "VolumetricLightmapBrickSHCoefficients2"),   // line 847
            (UeSrtRegisterType.ShaderResourceView, "VolumetricLightmapBrickSHCoefficients3"),   // line 848
            (UeSrtRegisterType.ShaderResourceView, "VolumetricLightmapBrickSHCoefficients4"),   // line 849
            (UeSrtRegisterType.ShaderResourceView, "VolumetricLightmapBrickSHCoefficients5"),   // line 850
            (UeSrtRegisterType.ShaderResourceView, "SkyBentNormalBrickTexture"),                 // line 851
            (UeSrtRegisterType.ShaderResourceView, "DirectionalLightShadowingBrickTexture"),     // line 852
            (UeSrtRegisterType.Sampler, "VolumetricLightmapBrickAmbientVectorSampler"),         // line 854
            (UeSrtRegisterType.Sampler, "VolumetricLightmapTextureSampler0"),                   // line 855
            (UeSrtRegisterType.Sampler, "VolumetricLightmapTextureSampler1"),                   // line 856
            (UeSrtRegisterType.Sampler, "VolumetricLightmapTextureSampler2"),                   // line 857
            (UeSrtRegisterType.Sampler, "VolumetricLightmapTextureSampler3"),                   // line 858
            (UeSrtRegisterType.Sampler, "VolumetricLightmapTextureSampler4"),                   // line 859
            (UeSrtRegisterType.Sampler, "VolumetricLightmapTextureSampler5"),                   // line 860
            (UeSrtRegisterType.Sampler, "SkyBentNormalTextureSampler"),                         // line 861
            (UeSrtRegisterType.Sampler, "DirectionalLightShadowingTextureSampler"),             // line 862
            (UeSrtRegisterType.ShaderResourceView, "GlobalDistanceFieldPageAtlasTexture"),       // line 864
            (UeSrtRegisterType.ShaderResourceView, "GlobalDistanceFieldCoverageAtlasTexture"),   // line 865
            (UeSrtRegisterType.ShaderResourceView, "GlobalDistanceFieldPageTableTexture"),       // line 866
            (UeSrtRegisterType.ShaderResourceView, "GlobalDistanceFieldMipTexture"),             // line 867
            (UeSrtRegisterType.ShaderResourceView, "AtmosphereTransmittanceTexture"),            // line 869
            (UeSrtRegisterType.Sampler, "AtmosphereTransmittanceTextureSampler"),               // line 870
            (UeSrtRegisterType.ShaderResourceView, "AtmosphereIrradianceTexture"),               // line 871
            (UeSrtRegisterType.Sampler, "AtmosphereIrradianceTextureSampler"),                  // line 872
            (UeSrtRegisterType.ShaderResourceView, "AtmosphereInscatterTexture"),                // line 873
            (UeSrtRegisterType.Sampler, "AtmosphereInscatterTextureSampler"),                   // line 874
            (UeSrtRegisterType.ShaderResourceView, "PerlinNoiseGradientTexture"),                // line 875
            (UeSrtRegisterType.Sampler, "PerlinNoiseGradientTextureSampler"),                   // line 876
            (UeSrtRegisterType.ShaderResourceView, "PerlinNoise3DTexture"),                      // line 877
            (UeSrtRegisterType.Sampler, "PerlinNoise3DTextureSampler"),                         // line 878
            (UeSrtRegisterType.ShaderResourceView, "SobolSamplingTexture"),                      // line 879
            (UeSrtRegisterType.Sampler, "SharedPointWrappedSampler"),                           // line 880
            (UeSrtRegisterType.Sampler, "SharedPointClampedSampler"),                           // line 881
            (UeSrtRegisterType.Sampler, "SharedBilinearWrappedSampler"),                        // line 882
            (UeSrtRegisterType.Sampler, "SharedBilinearClampedSampler"),                        // line 883
            (UeSrtRegisterType.Sampler, "SharedBilinearAnisoClampedSampler"),                   // line 884
            (UeSrtRegisterType.Sampler, "SharedTrilinearWrappedSampler"),                       // line 885
            (UeSrtRegisterType.Sampler, "SharedTrilinearClampedSampler"),                       // line 886
            (UeSrtRegisterType.ShaderResourceView, "PreIntegratedBRDF"),                         // line 887
            (UeSrtRegisterType.Sampler, "PreIntegratedBRDFSampler"),                            // line 888
            (UeSrtRegisterType.ShaderResourceView, "PrimitiveSceneData"),                        // line 889
            (UeSrtRegisterType.ShaderResourceView, "InstanceSceneData"),                         // line 890
            (UeSrtRegisterType.ShaderResourceView, "InstancePayloadData"),                       // line 891
            (UeSrtRegisterType.ShaderResourceView, "LightmapSceneData"),                         // line 892
            (UeSrtRegisterType.ShaderResourceView, "SkyIrradianceEnvironmentMap"),               // line 893
            (UeSrtRegisterType.ShaderResourceView, "TransmittanceLutTexture"),                   // line 895
            (UeSrtRegisterType.Sampler, "TransmittanceLutTextureSampler"),                      // line 896
            (UeSrtRegisterType.ShaderResourceView, "SkyViewLutTexture"),                         // line 897
            (UeSrtRegisterType.Sampler, "SkyViewLutTextureSampler"),                            // line 898
            (UeSrtRegisterType.ShaderResourceView, "DistantSkyLightLutTexture"),                 // line 899
            (UeSrtRegisterType.Sampler, "DistantSkyLightLutTextureSampler"),                    // line 900
            (UeSrtRegisterType.ShaderResourceView, "CameraAerialPerspectiveVolume"),             // line 901
            (UeSrtRegisterType.Sampler, "CameraAerialPerspectiveVolumeSampler"),                // line 902
            (UeSrtRegisterType.ShaderResourceView, "HairScatteringLUTTexture"),                  // line 904
            (UeSrtRegisterType.Sampler, "HairScatteringLUTSampler"),                            // line 905
            (UeSrtRegisterType.ShaderResourceView, "LTCMatTexture"),                             // line 907
            (UeSrtRegisterType.Sampler, "LTCMatSampler"),                                       // line 908
            (UeSrtRegisterType.ShaderResourceView, "LTCAmpTexture"),                             // line 909
            (UeSrtRegisterType.Sampler, "LTCAmpSampler"),                                       // line 910
            (UeSrtRegisterType.ShaderResourceView, "ShadingEnergyGGXSpecTexture"),               // line 914
            (UeSrtRegisterType.ShaderResourceView, "ShadingEnergyGGXGlassTexture"),              // line 915
            (UeSrtRegisterType.ShaderResourceView, "ShadingEnergyClothSpecTexture"),             // line 916
            (UeSrtRegisterType.ShaderResourceView, "ShadingEnergyDiffuseTexture"),               // line 917
            (UeSrtRegisterType.Sampler, "ShadingEnergySampler"),                                // line 918
            (UeSrtRegisterType.ShaderResourceView, "SSProfilesTexture"),                         // line 920
            (UeSrtRegisterType.Sampler, "SSProfilesSampler"),                                   // line 921
            (UeSrtRegisterType.Sampler, "SSProfilesTransmissionSampler"),                       // line 922
            (UeSrtRegisterType.ShaderResourceView, "SSProfilesPreIntegratedTexture"),            // line 923
            (UeSrtRegisterType.Sampler, "SSProfilesPreIntegratedSampler"),                      // line 924
            (UeSrtRegisterType.ShaderResourceView, "WaterIndirection"),                          // line 926
            (UeSrtRegisterType.ShaderResourceView, "WaterData"),                                 // line 927
            (UeSrtRegisterType.ShaderResourceView, "RectLightAtlasTexture"),                     // line 931
            (UeSrtRegisterType.Sampler, "RectLightAtlasSampler"),                               // line 932
            (UeSrtRegisterType.Sampler, "LandscapeWeightmapSampler"),                           // line 934
            (UeSrtRegisterType.ShaderResourceView, "LandscapeIndirection"),                      // line 935
            (UeSrtRegisterType.ShaderResourceView, "LandscapePerComponentData"),                 // line 936
            (UeSrtRegisterType.UnorderedAccessView, "VTFeedbackBuffer"),                        // line 938
            (UeSrtRegisterType.ShaderResourceView, "EditorVisualizeLevelInstanceIds"),           // line 939
            (UeSrtRegisterType.ShaderResourceView, "EditorSelectedHitProxyIds"),                 // line 940
            (UeSrtRegisterType.ShaderResourceView, "PhysicsFieldClipmapBuffer"),                 // line 942
        });
    }

    // BasePassRendering.h FOpaqueBasePassUniformParameters, lines 78-91.
    // Nested FSharedBasePassUniformParameters / FStrataBasePassUniformParameters
    // / FDBufferParameters references not expanded here; their resource
    // members fall back to the placeholder name from the symbolizer.
    private static void AddOpaqueBasePass(Dictionary<EngineKey, string> map)
    {
        AddMembers(map, "OpaqueBasePass", new (UeSrtRegisterType, string)[]
        {
            (UeSrtRegisterType.ShaderResourceView, "ForwardScreenSpaceShadowMaskTexture"),
            (UeSrtRegisterType.ShaderResourceView, "IndirectOcclusionTexture"),
            (UeSrtRegisterType.ShaderResourceView, "ResolvedSceneDepthTexture"),
            (UeSrtRegisterType.ShaderResourceView, "PreIntegratedGFTexture"),
            (UeSrtRegisterType.Sampler, "PreIntegratedGFSampler"),
            (UeSrtRegisterType.ShaderResourceView, "EyeAdaptationTexture"),
        });
    }

    // SceneTexturesConfig.h FSceneTextureUniformParameters, lines 15-35.
    private static void AddSceneTextures(Dictionary<EngineKey, string> map)
    {
        AddMembers(map, "SceneTextures", new (UeSrtRegisterType, string)[]
        {
            (UeSrtRegisterType.ShaderResourceView, "SceneColorTexture"),
            (UeSrtRegisterType.ShaderResourceView, "SceneDepthTexture"),
            (UeSrtRegisterType.ShaderResourceView, "GBufferATexture"),
            (UeSrtRegisterType.ShaderResourceView, "GBufferBTexture"),
            (UeSrtRegisterType.ShaderResourceView, "GBufferCTexture"),
            (UeSrtRegisterType.ShaderResourceView, "GBufferDTexture"),
            (UeSrtRegisterType.ShaderResourceView, "GBufferETexture"),
            (UeSrtRegisterType.ShaderResourceView, "GBufferFTexture"),
            (UeSrtRegisterType.ShaderResourceView, "GBufferVelocityTexture"),
            (UeSrtRegisterType.ShaderResourceView, "ScreenSpaceAOTexture"),
            (UeSrtRegisterType.ShaderResourceView, "CustomDepthTexture"),
            (UeSrtRegisterType.ShaderResourceView, "CustomStencilTexture"),
            (UeSrtRegisterType.Sampler, "PointClampSampler"),
        });
    }

    // LumenSceneData.h FLumenCardScene, lines 50-60.
    private static void AddLumenCardScene(Dictionary<EngineKey, string> map)
    {
        AddMembers(map, "LumenCardScene", new (UeSrtRegisterType, string)[]
        {
            (UeSrtRegisterType.ShaderResourceView, "CardData"),
            (UeSrtRegisterType.ShaderResourceView, "CardPageData"),
            (UeSrtRegisterType.ShaderResourceView, "MeshCardsData"),
            (UeSrtRegisterType.ShaderResourceView, "HeightfieldData"),
            (UeSrtRegisterType.ShaderResourceView, "PageTableBuffer"),
            (UeSrtRegisterType.ShaderResourceView, "SceneInstanceIndexToMeshCardsIndexBuffer"),
            (UeSrtRegisterType.ShaderResourceView, "AlbedoAtlas"),
            (UeSrtRegisterType.ShaderResourceView, "OpacityAtlas"),
            (UeSrtRegisterType.ShaderResourceView, "NormalAtlas"),
            (UeSrtRegisterType.ShaderResourceView, "EmissiveAtlas"),
            (UeSrtRegisterType.ShaderResourceView, "DepthAtlas"),
        });
    }

    // VirtualShadowMapArray.h FVirtualShadowMapUniformParameters, lines 141-145.
    private static void AddVirtualShadowMap(Dictionary<EngineKey, string> map)
    {
        AddMembers(map, "VirtualShadowMap", new (UeSrtRegisterType, string)[]
        {
            (UeSrtRegisterType.ShaderResourceView, "ProjectionData"),
            (UeSrtRegisterType.ShaderResourceView, "PageTable"),
            (UeSrtRegisterType.ShaderResourceView, "PageFlags"),
            (UeSrtRegisterType.ShaderResourceView, "PageRectBounds"),
            (UeSrtRegisterType.ShaderResourceView, "PhysicalPagePool"),
        });
    }

    // VolumetricCloudRendering.cpp FRenderVolumetricCloudGlobalParameters,
    // lines 537-558. Nested SHADER_PARAMETER_STRUCT_INCLUDEs not expanded.
    private static void AddRenderVolumetricCloudParameters(Dictionary<EngineKey, string> map)
    {
        AddMembers(map, "RenderVolumetricCloudParameters", new (UeSrtRegisterType, string)[]
        {
            (UeSrtRegisterType.ShaderResourceView, "SceneDepthTexture"),
            (UeSrtRegisterType.ShaderResourceView, "CloudShadowTexture0"),
            (UeSrtRegisterType.ShaderResourceView, "CloudShadowTexture1"),
            (UeSrtRegisterType.Sampler, "CloudBilinearTextureSampler"),
            (UeSrtRegisterType.ShaderResourceView, "HZBTexture"),
            (UeSrtRegisterType.Sampler, "HZBSampler"),
        });
    }

    private static void AddMembers(
        Dictionary<EngineKey, string> map,
        string uniformBufferName,
        (UeSrtRegisterType type, string name)[] members)
    {
        for (int i = 0; i < members.Length; i++)
        {
            var (type, name) = members[i];
            map[new EngineKey(uniformBufferName, i, type)] = $"{uniformBufferName}_{name}";
        }
    }

    private readonly record struct EngineKey(string UniformBufferName, int ResourceIndex, UeSrtRegisterType RegisterType);
}
