namespace Ruri.ShaderDecompiler.Testing.Assets.Shaders;

internal static class SelfTestAssets
{
    public const string ComplexPbrShader = """
cbuffer VertexFrameData : register(b0)
{
    float4x4 VertexWorldViewProjection;
    float4x4 VertexPrevWorldViewProjection;
    float4 VertexCameraWorldPos_Time;
    float4 VertexMorphWeights;
};

cbuffer VertexObjectData : register(b1)
{
    float4 VertexLocalBoundsMin;
    float4 VertexLocalBoundsMax;
    float4 VertexObjectTint_Roughness;
    float4 VertexObjectParams;
};

cbuffer HullData : register(b2)
{
    float4 HullTessellationFactors;
    float4 HullWindDirection_EdgeBias;
};

cbuffer DomainData : register(b3)
{
    float4x4 DomainWorldViewProjection;
    float4 DomainViewPosition_HeightScale;
    float4 DomainUvScale_Bias;
};

cbuffer GeometryData : register(b4)
{
    float4 GeometryShellParams;
    float4 GeometryDebugTint;
};

cbuffer PixelViewData : register(b5)
{
    float4 PixelCameraPosition_Exposure;
    float4 PixelLightDirection_Intensity;
    float4 PixelLightColor_RoughnessBias;
    float4 PixelFogColor_Density;
};

cbuffer MaterialData : register(b6)
{
    float4 BaseColorFactor;
    float4 SurfaceParams;
    float4 EmissiveColor_AlphaCutoff;
    float4 ClearCoat_Wetness;
};

cbuffer ComputeParams : register(b7)
{
    uint4 ComputeOutputExtent;
    uint4 ComputeClusterStride;
    uint4 ComputeActiveLightCount;
    float4 ComputeWeights;
};

Texture2D WindNoiseTexture : register(t16);
Texture2D HeightTexture : register(t17);
Texture2D AlbedoTexture : register(t18);
Texture2D NormalTexture : register(t19);
Texture2D MaterialTexture : register(t20);
Texture2D EmissiveTexture : register(t21);
TextureCube ReflectionProbe : register(t22);
StructuredBuffer<float4> ClusterLightData : register(t23);
ByteAddressBuffer ClusterIndices : register(t24);

SamplerState VertexLinearSampler : register(s32);
SamplerState DomainLinearSampler : register(s33);
SamplerState PixelLinearSampler : register(s34);

RWTexture2D<float4> DebugOutput : register(u48);
RWStructuredBuffer<float4> ReductionBuffer : register(u49);
RWByteAddressBuffer CounterBuffer : register(u50);

struct VSInput
{
    float3 Position : POSITION;
    float3 Normal : NORMAL;
    float4 Tangent : TANGENT;
    float2 TexCoord : TEXCOORD0;
};

struct VSOutput
{
    float4 ClipPosition : SV_Position;
    float3 WorldNormal : TEXCOORD0;
    float3 WorldTangent : TEXCOORD1;
    float3 WorldBitangent : TEXCOORD2;
    float2 TexCoord : TEXCOORD3;
    float3 ViewDir : TEXCOORD4;
    float2 MotionVector : TEXCOORD5;
};

struct HullControlPoint
{
    float3 Position : POSITION;
    float3 Normal : NORMAL;
    float2 TexCoord : TEXCOORD0;
    float3 ViewDir : TEXCOORD1;
};

struct PatchConstants
{
    float Edges[3] : SV_TessFactor;
    float Inside : SV_InsideTessFactor;
};

struct DomainOutput
{
    float4 ClipPosition : SV_Position;
    float3 WorldNormal : TEXCOORD0;
    float3 WorldTangent : TEXCOORD1;
    float3 WorldBitangent : TEXCOORD2;
    float2 TexCoord : TEXCOORD3;
    float3 ViewDir : TEXCOORD4;
    float3 WorldPosition : TEXCOORD5;
};

struct GeometryOutput
{
    float4 ClipPosition : SV_Position;
    float3 WorldNormal : TEXCOORD0;
    float3 WorldTangent : TEXCOORD1;
    float3 WorldBitangent : TEXCOORD2;
    float2 TexCoord : TEXCOORD3;
    float3 ViewDir : TEXCOORD4;
    float3 WorldPosition : TEXCOORD5;
    float4 DebugColor : TEXCOORD6;
};

float3 SRGBToLinear(float3 c)
{
    return pow(abs(c), 2.2);
}

float3 FresnelSchlick(float cosTheta, float3 f0)
{
    return f0 + (1.0 - f0) * pow(saturate(1.0 - cosTheta), 5.0);
}

float DistributionGGX(float3 n, float3 h, float roughness)
{
    float a = roughness * roughness;
    float a2 = a * a;
    float nh = saturate(dot(n, h));
    float nh2 = nh * nh;
    float denom = nh2 * (a2 - 1.0) + 1.0;
    return a2 / max(3.14159265 * denom * denom, 1e-4);
}

float GeometrySchlickGGX(float ndv, float roughness)
{
    float r = roughness + 1.0;
    float k = (r * r) * 0.125;
    return ndv / lerp(ndv, 1.0, k);
}

float GeometrySmith(float3 n, float3 v, float3 l, float roughness)
{
    return GeometrySchlickGGX(saturate(dot(n, v)), roughness) * GeometrySchlickGGX(saturate(dot(n, l)), roughness);
}

VSOutput VSMain(VSInput input)
{
    VSOutput output;
    float windSample = WindNoiseTexture.SampleLevel(VertexLinearSampler, input.TexCoord * VertexObjectParams.xy + VertexCameraWorldPos_Time.ww, 0.0).r;
    float morphAmount = dot(VertexMorphWeights, float4(input.Tangent.xyz, 1.0));
    float3 objectCenter = 0.5 * (VertexLocalBoundsMin.xyz + VertexLocalBoundsMax.xyz);
    float3 centeredPosition = input.Position - objectCenter;
    float3 worldPos = input.Position
        + input.Normal * (windSample * VertexObjectParams.z + morphAmount * 0.05)
        + centeredPosition * VertexObjectTint_Roughness.w * 0.01
        + VertexObjectTint_Roughness.xyz * 0.01;
    float4 currentClip = mul(float4(worldPos, 1.0), VertexWorldViewProjection);
    float4 previousClip = mul(float4(worldPos, 1.0), VertexPrevWorldViewProjection);
    output.ClipPosition = currentClip;
    output.WorldNormal = normalize(input.Normal + VertexObjectTint_Roughness.xyz * 0.05);
    output.WorldTangent = normalize(input.Tangent.xyz);
    output.WorldBitangent = normalize(cross(output.WorldNormal, output.WorldTangent) * input.Tangent.w);
    output.TexCoord = input.TexCoord;
    output.ViewDir = VertexCameraWorldPos_Time.xyz - worldPos;
    output.MotionVector = currentClip.xy - previousClip.xy;
    return output;
}

[domain("tri")]
[partitioning("integer")]
[outputtopology("triangle_cw")]
[outputcontrolpoints(3)]
[patchconstantfunc("HSConstants")]
HullControlPoint HSMain(InputPatch<HullControlPoint, 3> patch, uint pointId : SV_OutputControlPointID)
{
    HullControlPoint output = patch[pointId];
    float windPush = dot(output.Normal, normalize(HullWindDirection_EdgeBias.xyz)) * HullWindDirection_EdgeBias.w;
    output.Position += output.Normal * windPush;
    output.ViewDir += HullWindDirection_EdgeBias.xyz * HullTessellationFactors.w;
    return output;
}

PatchConstants HSConstants(InputPatch<HullControlPoint, 3> patch)
{
    PatchConstants constants;
    float normalWeight = abs(dot(normalize(patch[0].Normal + patch[1].Normal + patch[2].Normal), normalize(HullWindDirection_EdgeBias.xyz)));
    constants.Edges[0] = max(1.0, HullTessellationFactors.x + normalWeight * HullWindDirection_EdgeBias.w);
    constants.Edges[1] = max(1.0, HullTessellationFactors.y + normalWeight * HullWindDirection_EdgeBias.w);
    constants.Edges[2] = max(1.0, HullTessellationFactors.z + normalWeight * HullWindDirection_EdgeBias.w);
    constants.Inside = max(1.0, HullTessellationFactors.w + normalWeight);
    return constants;
}

[domain("tri")]
DomainOutput DSMain(PatchConstants constants, const OutputPatch<HullControlPoint, 3> patch, float3 bary : SV_DomainLocation)
{
    DomainOutput output;
    float3 position = patch[0].Position * bary.x + patch[1].Position * bary.y + patch[2].Position * bary.z;
    float3 normal = normalize(patch[0].Normal * bary.x + patch[1].Normal * bary.y + patch[2].Normal * bary.z);
    float2 texCoord = patch[0].TexCoord * bary.x + patch[1].TexCoord * bary.y + patch[2].TexCoord * bary.z;
    float3 viewDir = patch[0].ViewDir * bary.x + patch[1].ViewDir * bary.y + patch[2].ViewDir * bary.z;
    float2 scaledUv = texCoord * DomainUvScale_Bias.xy + DomainUvScale_Bias.zw;
    float displacement = HeightTexture.SampleLevel(DomainLinearSampler, scaledUv, 0.0).r * DomainViewPosition_HeightScale.w;
    float3 displacedPosition = position + normal * displacement;
    output.ClipPosition = mul(float4(displacedPosition, 1.0), DomainWorldViewProjection);
    output.WorldNormal = normal;
    output.WorldTangent = normalize(float3(1.0, 0.0, 0.0) + normal.yzx * 0.25);
    output.WorldBitangent = normalize(cross(output.WorldNormal, output.WorldTangent));
    output.TexCoord = scaledUv + constants.Inside * 0.001;
    output.ViewDir = DomainViewPosition_HeightScale.xyz - displacedPosition + viewDir * 0.1;
    output.WorldPosition = displacedPosition;
    return output;
}

[maxvertexcount(3)]
void GSMain(triangle DomainOutput inputVertices[3], inout TriangleStream<GeometryOutput> stream)
{
    float3 faceNormal = normalize(cross(inputVertices[1].WorldPosition - inputVertices[0].WorldPosition, inputVertices[2].WorldPosition - inputVertices[0].WorldPosition));
    for (int i = 0; i < 3; i++)
    {
        GeometryOutput output;
        float shellOffset = GeometryShellParams.x + GeometryShellParams.y * (i + 1);
        float3 adjustedPosition = inputVertices[i].WorldPosition + faceNormal * shellOffset;
        output.ClipPosition = inputVertices[i].ClipPosition + float4(faceNormal * shellOffset, 0.0);
        output.WorldNormal = normalize(inputVertices[i].WorldNormal + faceNormal * GeometryShellParams.z + GeometryDebugTint.xyz * 0.05);
        output.WorldTangent = inputVertices[i].WorldTangent;
        output.WorldBitangent = inputVertices[i].WorldBitangent;
        output.TexCoord = inputVertices[i].TexCoord + GeometryShellParams.ww * 0.01;
        output.ViewDir = inputVertices[i].ViewDir + adjustedPosition * GeometryShellParams.w * 0.01;
        output.WorldPosition = adjustedPosition;
        output.DebugColor = GeometryDebugTint;
        stream.Append(output);
    }
    stream.RestartStrip();
}

float4 PSMain(GeometryOutput input) : SV_Target0
{
    float4 albedoSample = AlbedoTexture.Sample(PixelLinearSampler, input.TexCoord);
    float4 materialSample = MaterialTexture.Sample(PixelLinearSampler, input.TexCoord);
    float3 emissiveSample = EmissiveTexture.Sample(PixelLinearSampler, input.TexCoord).rgb;
    float3 tangentNormal = NormalTexture.Sample(PixelLinearSampler, input.TexCoord).xyz * 2.0 - 1.0;

    float3x3 tbn = float3x3(normalize(input.WorldTangent), normalize(input.WorldBitangent), normalize(input.WorldNormal));
    float3 n = normalize(mul(tangentNormal * float3(SurfaceParams.z, SurfaceParams.z, ClearCoat_Wetness.x + 1.0), tbn));
    float3 v = normalize(PixelCameraPosition_Exposure.xyz - input.WorldPosition + input.ViewDir * 0.25);
    float3 l = normalize(-PixelLightDirection_Intensity.xyz);
    float3 h = normalize(v + l);

    float metallic = saturate(SurfaceParams.x * materialSample.b);
    float roughness = saturate(max(0.04, SurfaceParams.y * materialSample.g + PixelLightColor_RoughnessBias.w + ClearCoat_Wetness.y * 0.1));
    float ao = lerp(1.0, materialSample.r, SurfaceParams.w);
    float clearCoat = saturate(ClearCoat_Wetness.x * materialSample.a);
    float wetness = saturate(ClearCoat_Wetness.y + input.DebugColor.w * 0.1);

    float3 baseColor = SRGBToLinear(albedoSample.rgb) * BaseColorFactor.rgb * input.DebugColor.rgb;
    float3 f0 = lerp(float3(0.04, 0.04, 0.04), baseColor, metallic);

    float ndf = DistributionGGX(n, h, roughness);
    float g = GeometrySmith(n, v, l, roughness);
    float3 f = FresnelSchlick(saturate(dot(h, v)), lerp(f0, 1.0.xxx, clearCoat));

    float3 numerator = ndf * g * f;
    float denominator = max(4.0 * saturate(dot(n, v)) * saturate(dot(n, l)), 1e-4);
    float3 specular = numerator / denominator;

    float3 kd = (1.0 - f) * (1.0 - metallic);
    float ndl = saturate(dot(n, l));
    float3 diffuse = kd * baseColor * (1.0 / 3.14159265);
    float3 direct = (diffuse + specular) * PixelLightColor_RoughnessBias.xyz * PixelLightDirection_Intensity.w * ndl;

    float3 reflected = reflect(-v, n);
    float3 ibl = ReflectionProbe.SampleLevel(PixelLinearSampler, reflected, roughness * 6.0).rgb * lerp(f, 1.0.xxx, wetness * 0.2);
    float3 emissive = SRGBToLinear(emissiveSample) * EmissiveColor_AlphaCutoff.xyz * (SurfaceParams.z + clearCoat);

    float alpha = albedoSample.a * BaseColorFactor.a;
    clip(alpha - EmissiveColor_AlphaCutoff.w);

    float3 finalColor = (direct + ibl + emissive) * ao;
    finalColor = lerp(finalColor, PixelFogColor_Density.xyz, saturate(PixelFogColor_Density.w * length(input.ViewDir) * 0.01));
    finalColor = 1.0 - exp(-finalColor * PixelCameraPosition_Exposure.w);
    return float4(finalColor, alpha);
}

[numthreads(8, 8, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    if (dispatchThreadId.x >= ComputeOutputExtent.x || dispatchThreadId.y >= ComputeOutputExtent.y)
    {
        return;
    }

    uint flatIndex = dispatchThreadId.y * ComputeOutputExtent.x + dispatchThreadId.x;
    uint lightCount = max(ComputeActiveLightCount.x, 1u);
    uint lightIndexOffset = (flatIndex % lightCount) * ComputeClusterStride.x;
    uint encodedLightIndex = ClusterIndices.Load(lightIndexOffset * 4);
    uint clampedLightIndex = encodedLightIndex % lightCount;
    float4 lightData = ClusterLightData[clampedLightIndex];
    float luminance = dot(lightData.rgb, ComputeWeights.xyz) + ComputeWeights.w;
    DebugOutput[dispatchThreadId.xy] = float4(lightData.rgb * luminance, 1.0);
    ReductionBuffer[flatIndex] = float4(lightData.rgb, luminance);
    CounterBuffer.Store(flatIndex * 4, asuint(luminance));
}
""";
}
