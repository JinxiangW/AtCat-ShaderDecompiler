cbuffer PixelViewData : register(b0)
{
    float4 PixelCameraPosition_Exposure;
    float4 PixelLightDirection_Intensity;
    float4 PixelLightColor_RoughnessBias;
    float4 PixelFogColor_Density;
};

cbuffer PixelMaterialData : register(b1)
{
    float PixelScalar : packoffset(c0.x);
    float2 PixelFloat2 : packoffset(c1.x);
    float3 PixelFloat3 : packoffset(c2.x);
    float4 BaseColorFactor : packoffset(c3);
    int PixelInt : packoffset(c4.x);
    int2 PixelInt2 : packoffset(c5.x);
    int3 PixelInt3 : packoffset(c6.x);
    int4 PixelInt4 : packoffset(c7);
    uint PixelUInt : packoffset(c8.x);
    uint2 PixelUInt2 : packoffset(c9.x);
    uint3 PixelUInt3 : packoffset(c10.x);
    uint4 PixelUInt4 : packoffset(c11);
    float4 SurfaceParams : packoffset(c12);
    float4 EmissiveColor_AlphaCutoff : packoffset(c13);
    float4 ClearCoat_Wetness : packoffset(c14);
    row_major float2x2 PixelMatrix2 : packoffset(c15);
    row_major float3x3 PixelMatrix3 : packoffset(c17);
};

Texture2D AlbedoTexture : register(t2);
Texture2D NormalTexture : register(t3);
Texture2DArray LayeredTexture : register(t4);
Texture3D VolumeTexture : register(t5);
TextureCube ReflectionProbe : register(t6);
SamplerState PixelLinearSampler : register(s2);

float3 SafeNormalize(float3 v)
{
    return normalize(v + 1e-4.xxx);
}

struct PixelLocalData
{
    float3 Accumulated;
    int Mode;
    uint Flags;
    bool AlphaTest;
};

struct GeometryOutput
{
    float4 Position : SV_Position;
    float3 Normal : TEXCOORD0;
    float2 TexCoord : TEXCOORD1;
    float4 Color : TEXCOORD2;
};

float4 PSMain(GeometryOutput input) : SV_Target0
{
    PixelLocalData localData;
    float4 albedo = AlbedoTexture.Sample(PixelLinearSampler, input.TexCoord);
    float3 normalMap = NormalTexture.Sample(PixelLinearSampler, input.TexCoord).xyz * 2.0 - 1.0;
    float4 layered = LayeredTexture.Sample(PixelLinearSampler, float3(input.TexCoord, 0.0));
    float4 volume = VolumeTexture.Sample(PixelLinearSampler, float3(input.TexCoord, 0.5));
    float3 reflection = ReflectionProbe.SampleLevel(PixelLinearSampler, reflect(-normalize(PixelLightDirection_Intensity.xyz), SafeNormalize(input.Normal)), 0.0).rgb;

    float3 n = SafeNormalize(input.Normal + normalMap * SurfaceParams.zzz);
    n = SafeNormalize(mul(PixelMatrix3, n + PixelFloat3 * 0.001f));
    float3 l = SafeNormalize(-PixelLightDirection_Intensity.xyz);
    float ndl = saturate(dot(n, l));
    localData.Mode = (int)round(SurfaceParams.w * 3.0);
    localData.Flags = asuint(ClearCoat_Wetness.x);
    localData.AlphaTest = (localData.Flags & 1u) != 0u;
    float2 shiftedUv = mul(PixelMatrix2, input.TexCoord + PixelFloat2 * 0.01f);
    float2 offsets[2] = { float2(-0.125, 0.125), float2(0.125, -0.125) };
    float edge = AlbedoTexture.Sample(PixelLinearSampler, shiftedUv + offsets[localData.Mode & 1]).a;
    float3 combined = albedo.rgb * BaseColorFactor.rgb;
    combined += layered.rgb * PixelLightColor_RoughnessBias.xyz * ndl;
    combined += volume.rgb * ClearCoat_Wetness.yyy;
    combined += reflection * EmissiveColor_AlphaCutoff.xxx;

    localData.Accumulated = 0.0.xxx;
    float3 taps[3] =
    {
        albedo.rgb,
        layered.rgb,
        volume.rgb
    };

    [unroll]
    for (int tapIndex = 0; tapIndex < 3; tapIndex++)
    {
        localData.Accumulated += taps[tapIndex] * (0.125f * (tapIndex + 1));
    }

    switch (localData.Mode)
    {
        case 0:
            combined += localData.Accumulated * 0.25f;
            break;
        case 1:
            combined += abs(localData.Accumulated) * 0.125f;
            break;
        default:
            combined += sqrt(saturate(localData.Accumulated)) * 0.0625f;
            break;
    }

    int2 pixelCoord = int2(input.Position.xy);
    uint2 pixelCoordU = uint2(max(pixelCoord, 0));
    float2 parity = float2(pixelCoordU & 1u);
    float scalarMix = PixelScalar + PixelUInt * 0.0001f + PixelInt * 0.0001f;
    scalarMix += (PixelInt2.x + PixelInt3.y + PixelInt4.z) * 0.0001f;
    scalarMix += (PixelUInt2.x + PixelUInt3.y + PixelUInt4.z) * 0.0001f;
    combined += parity.xxy * 0.005f;
    combined += scalarMix.xxx * 0.01f;
    combined = lerp(combined, PixelFogColor_Density.xyz, saturate(PixelFogColor_Density.w * length(input.Position.xy) * 0.01));
    combined = 1.0 - exp(-combined * PixelCameraPosition_Exposure.w);
    if (localData.AlphaTest)
    {
        clip(edge * albedo.a * BaseColorFactor.a - EmissiveColor_AlphaCutoff.w);
    }
    return float4(combined * input.Color.rgb, albedo.a * BaseColorFactor.a);
}
