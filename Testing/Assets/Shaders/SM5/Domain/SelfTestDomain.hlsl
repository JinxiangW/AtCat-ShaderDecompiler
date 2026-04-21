cbuffer DomainGlobals : register(b0)
{
    float4x4 DomainObjectToClip;
    float4 DomainEyePosition_HeightScale;
    float4 DomainUvScale_Bias;
};

Texture2D DomainHeightTexture : register(t1);
SamplerState DomainLinearSampler : register(s1);

float3 SafeNormalize(float3 v)
{
    return normalize(v + 1e-4.xxx);
}

struct HullControlPoint
{
    float3 Position : POSITION;
    float3 Normal : NORMAL;
    float2 TexCoord : TEXCOORD0;
};

struct HullConstants
{
    float Edges[3] : SV_TessFactor;
    float Inside : SV_InsideTessFactor;
};

struct DomainOutput
{
    float4 Position : SV_Position;
    float3 Normal : TEXCOORD0;
    float2 TexCoord : TEXCOORD1;
    float3 WorldPosition : TEXCOORD2;
};

[domain("tri")]
DomainOutput DSMain(HullConstants constants, const OutputPatch<HullControlPoint, 3> patch, float3 bary : SV_DomainLocation)
{
    DomainOutput output;
    float3 position = patch[0].Position * bary.x + patch[1].Position * bary.y + patch[2].Position * bary.z;
    float3 normal = SafeNormalize(patch[0].Normal * bary.x + patch[1].Normal * bary.y + patch[2].Normal * bary.z);
    float2 uv = patch[0].TexCoord * bary.x + patch[1].TexCoord * bary.y + patch[2].TexCoord * bary.z;
    float2 scaledUv = uv * DomainUvScale_Bias.xy + DomainUvScale_Bias.zw;
    float height = DomainHeightTexture.SampleLevel(DomainLinearSampler, scaledUv, 0.0).x * DomainEyePosition_HeightScale.w;
    float3 displaced = position + normal * height + constants.Inside * 0.001.xxx;
    output.Position = mul(float4(displaced, 1.0), DomainObjectToClip);
    output.Normal = normal;
    output.TexCoord = scaledUv;
    output.WorldPosition = displaced;
    return output;
}
