#include "../../Common/SelfTestShared.hlsli"

cbuffer PixelViewData : register(b0)
{
    float4 PixelCameraPosition_Exposure;
    float4 PixelLightDirection_Intensity;
    float4 PixelLightColor_RoughnessBias;
    float4 PixelFogColor_Density;
};

cbuffer PixelMaterialData : register(b1)
{
    SELFTEST_TYPE_COVERAGE_MEMBERS(Pixel)
    struct PixelSurfaceStruct
    {
        float4 LayerWeights;
        float3 DetailNormalScale;
        float RoughnessBias;
    };
    PixelSurfaceStruct PixelSurface : packoffset(c18);
    float4 BaseColorFactor : packoffset(c20);
    float4 SurfaceParams : packoffset(c21);
    float4 EmissiveColor_AlphaCutoff : packoffset(c22);
    float4 ClearCoat_Wetness : packoffset(c23);
};

Texture2D AlbedoTexture : register(t2);
Texture2D NormalTexture : register(t3);
Texture2DArray LayeredTexture : register(t4);
Texture3D VolumeTexture : register(t5);
TextureCube ReflectionProbe : register(t6);
SamplerState PixelLinearSampler : register(s2);

struct GeometryOutput
{
    float4 Position : SV_Position;
    float3 Normal : TEXCOORD0;
    float2 TexCoord : TEXCOORD1;
    float4 Color : TEXCOORD2;
};

float4 PSMain(GeometryOutput input) : SV_Target0
{
    SelfTestTemplateData templateData;
    SelfTestTemplateAccumulator templateAccumulator;
    float4 albedo = AlbedoTexture.Sample(PixelLinearSampler, input.TexCoord);
    float3 normalMap = NormalTexture.Sample(PixelLinearSampler, input.TexCoord).xyz * 2.0 - 1.0;
    float4 layered = LayeredTexture.Sample(PixelLinearSampler, float3(input.TexCoord, 0.0));
    float4 volume = VolumeTexture.Sample(PixelLinearSampler, float3(input.TexCoord, 0.5));
    float3 reflection = ReflectionProbe.SampleLevel(PixelLinearSampler, reflect(-normalize(PixelLightDirection_Intensity.xyz), SafeNormalize(input.Normal)), 0.0).rgb;

    float3 n = SafeNormalize(input.Normal + normalMap * SurfaceParams.zzz);
    n = SafeNormalize(mul(PixelMatrix3, n + PixelFloat3 * 0.001f));
    float3 l = SafeNormalize(-PixelLightDirection_Intensity.xyz);
    float ndl = saturate(dot(n, l));
    int mode = (int)round(SurfaceParams.w * 3.0);
    uint flags = asuint(ClearCoat_Wetness.x);
    bool alphaTest = PixelBool || ((flags & 1u) != 0u);
    float2 shiftedUv = mul(PixelMatrix2, input.TexCoord + PixelFloat2 * 0.01f);

    templateData.TemplateBool1 = PixelBool;
    templateData.TemplateBool2 = bool2(PixelBool, (PixelUInt & 1u) != 0u);
    templateData.TemplateBool3 = bool3(PixelBool, (PixelUInt2.x & 1u) != 0u, (PixelUInt3.y & 1u) != 0u);
    templateData.TemplateBool4 = bool4(templateData.TemplateBool3, (PixelUInt4.z & 1u) != 0u);
    templateData.TemplateFloat1 = PixelScalar;
    templateData.TemplateFloat2 = PixelFloat2;
    templateData.TemplateFloat3 = PixelFloat3;
    templateData.TemplateFloat4 = BaseColorFactor;
    templateData.TemplateInt1 = PixelInt;
    templateData.TemplateInt2 = PixelInt2;
    templateData.TemplateInt3 = PixelInt3;
    templateData.TemplateInt4 = PixelInt4;
    templateData.TemplateUInt1 = PixelUInt;
    templateData.TemplateUInt2 = PixelUInt2;
    templateData.TemplateUInt3 = PixelUInt3;
    templateData.TemplateUInt4 = PixelUInt4;
    templateData.TemplateFloat2x2 = PixelMatrix2;
    templateData.TemplateFloat2x3 = float2x3(PixelMatrix2[0].xyx, PixelMatrix2[1].xyx);
    templateData.TemplateFloat2x4 = float2x4(float4(PixelMatrix2[0], 0.0f, 1.0f), float4(PixelMatrix2[1], shiftedUv.x, shiftedUv.y));
    templateData.TemplateFloat3x2 = float3x2(float2(PixelMatrix2[0].x, PixelMatrix2[0].y), float2(PixelMatrix2[1].x, PixelMatrix2[1].y), shiftedUv);
    templateData.TemplateFloat3x3 = PixelMatrix3;
    templateData.TemplateFloat3x4 = float3x4(float4(PixelMatrix3[0], 0.0f), float4(PixelMatrix3[1], 0.0f), float4(PixelMatrix3[2], 1.0f));
    templateData.TemplateFloat4x2 = float4x2(float2(PixelMatrix2[0].x, PixelMatrix2[0].y), float2(PixelMatrix2[1].x, PixelMatrix2[1].y), PixelFloat2, shiftedUv);
    templateData.TemplateFloat4x3 = float4x3(PixelMatrix3[0], PixelMatrix3[1], PixelMatrix3[2], n);
    templateData.TemplateFloat4x4 = float4x4(float4(PixelMatrix3[0], 0.0f), float4(PixelMatrix3[1], 0.0f), float4(PixelMatrix3[2], 0.0f), float4(0.0f, 0.0f, 0.0f, 1.0f));
    templateAccumulator.Value = SelfTestTemplateEval(templateData);
    templateAccumulator.Color = float4(templateAccumulator.Value.xxx, 1.0f);
    float2 offsets[2] = { float2(-0.125, 0.125), float2(0.125, -0.125) };
    float edge = AlbedoTexture.Sample(PixelLinearSampler, shiftedUv + offsets[mode & 1]).a;
    float3 combined = albedo.rgb * BaseColorFactor.rgb;
    combined += layered.rgb * (PixelLightColor_RoughnessBias.xyz + PixelSurface.LayerWeights.xyz) * ndl;
    combined += volume.rgb * ClearCoat_Wetness.yyy;
    combined += reflection * EmissiveColor_AlphaCutoff.xxx;

    float3 accumulated = 0.0.xxx;
    float3 taps[3] =
    {
        albedo.rgb,
        layered.rgb,
        volume.rgb
    };

    [unroll]
    for (int tapIndex = 0; tapIndex < 3; tapIndex++)
    {
        accumulated += taps[tapIndex] * (0.125f * (tapIndex + 1));
    }

    switch (mode)
    {
        case 0:
            combined += accumulated * 0.25f;
            break;
        case 1:
            combined += abs(accumulated) * 0.125f;
            break;
        default:
            combined += sqrt(saturate(accumulated)) * 0.0625f;
            break;
    }

    int2 pixelCoord = int2(input.Position.xy);
    uint2 pixelCoordU = uint2(max(pixelCoord, 0));
    float2 parity = float2(pixelCoordU & 1u);
    float scalarMix = PixelScalar + PixelUInt * 0.0001f + PixelInt * 0.0001f;
    scalarMix += (PixelInt2.x + PixelInt3.y + PixelInt4.z) * 0.0001f;
    scalarMix += (PixelUInt2.x + PixelUInt3.y + PixelUInt4.z) * 0.0001f;
    scalarMix += PixelSurface.RoughnessBias + dot(PixelSurface.DetailNormalScale, 0.01.xxx);
    scalarMix += templateAccumulator.Value * 0.000001f;
    combined += parity.xxy * 0.005f;
    combined += scalarMix.xxx * 0.01f;
    combined = lerp(combined, PixelFogColor_Density.xyz, saturate(PixelFogColor_Density.w * length(input.Position.xy) * 0.01));
    combined = 1.0 - exp(-combined * PixelCameraPosition_Exposure.w);
    if (alphaTest)
    {
        clip(edge * albedo.a * BaseColorFactor.a - EmissiveColor_AlphaCutoff.w);
    }
    return float4(combined * input.Color.rgb, albedo.a * BaseColorFactor.a);
}
