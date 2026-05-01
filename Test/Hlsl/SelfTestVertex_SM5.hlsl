#include "SelfTestShared.hlsli"

cbuffer VertexMatrices : register(b0)
{
    float4x4 VertexObjectToClip;
    float4x4 VertexPrevObjectToClip;
};

cbuffer VertexScalars : register(b1)
{
    float4 VertexColorScale_Time : packoffset(c0);
    float4 VertexMorphWeights : packoffset(c1);
};

cbuffer VertexMaterial : register(b2)
{
    float4 VertexBoundsMin;
    float4 VertexBoundsMax;
    float4 VertexTint_Roughness;
    float4 VertexUvScale_Bias;
};

Texture2D VertexNoiseTexture : register(t0);
SamplerState VertexLinearSampler : register(s0);

struct VertexAuxData
{
    bool UseTint;
    int Selector;
    uint PackedMask;
    float2 Weights;
};

struct VSInput
{
    float3 Position : POSITION;
    float3 Normal : NORMAL;
    float4 Tangent : TANGENT;
    float2 TexCoord : TEXCOORD0;
    uint PackedColor : COLOR0;
};

struct VSOutput
{
    float4 Position : SV_Position;
    float3 Normal : TEXCOORD0;
    float4 Color : TEXCOORD1;
    float2 TexCoord : TEXCOORD2;
    float2 MotionVector : TEXCOORD3;
};

VSOutput VSMain(VSInput input)
{
    VSOutput output;
    CoverageLocalData localData;
    VertexAuxData auxData;
    localData.Uv = input.TexCoord * VertexUvScale_Bias.xy + VertexUvScale_Bias.zw;
    localData.SignedPair = int2((int)input.PackedColor, (int)(input.PackedColor >> 16));
    localData.Mask = uint4(input.PackedColor, input.PackedColor ^ 0x55AA55AAu, input.PackedColor | 3u, input.PackedColor & 255u);
    localData.Basis[0] = input.Normal;
    localData.Basis[1] = input.Tangent.xyz;

    auxData.UseTint = (localData.Mask.x & 1u) != 0u;
    auxData.Selector = localData.SignedPair.x & 3;
    auxData.PackedMask = localData.Mask.y;
    auxData.Weights = float2(VertexMorphWeights.x, VertexMorphWeights.y);

    float4 packedColor = ExpandPackedColor(input.PackedColor);
    float noise = VertexNoiseTexture.SampleLevel(VertexLinearSampler, localData.Uv, 0.0).x;
    float3 centered = input.Position - 0.5 * (VertexBoundsMin.xyz + VertexBoundsMax.xyz);
    float2 rotatedUv = Rotate90(localData.Uv);
    float4 tangentWeights = float4(input.Tangent.xyz, 1.0);
    float3 offset = input.Normal * (noise + dot(VertexMorphWeights, tangentWeights) * 0.05);
    float3 worldPos = input.Position + offset + centered * VertexTint_Roughness.www * 0.01;
    float4 clipPos = mul(float4(worldPos, 1.0), VertexObjectToClip);
    float4 prevClip = mul(float4(worldPos, 1.0), VertexPrevObjectToClip);
    float3 tintDelta = auxData.UseTint ? VertexTint_Roughness.xyz : 0.0.xxx;
    float scalarAdjust = (auxData.Selector == 0) ? auxData.Weights.x : auxData.Weights.y;
    scalarAdjust += ((auxData.PackedMask & 8u) != 0u) ? 0.125f : 0.0f;
    [unroll]
    for (int basisIndex = 0; basisIndex < 2; basisIndex++)
    {
        scalarAdjust += localData.Basis[basisIndex].x * 0.001f;
    }

    output.Position = clipPos;
    output.Normal = SafeNormalize(input.Normal + tintDelta * 0.05 + scalarAdjust.xxx * 0.001f);
    output.Color = lerp(packedColor, packedColor * VertexColorScale_Time, saturate(noise + VertexColorScale_Time.w));
    output.TexCoord = localData.Uv + rotatedUv * 0.01f * scalarAdjust;
    output.MotionVector = clipPos.xy - prevClip.xy;
    return output;
}
