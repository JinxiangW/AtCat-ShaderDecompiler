namespace Ruri.ShaderDecompiler.Testing.Assets.Shaders;

internal static class SelfTestAssets
{
    public const string CommonShader = """
cbuffer ViewData : register(b0)
{
    float4x4 ViewProjection;
    float4 CameraPosition_Exposure;
    float4 LightDirection_Intensity;
    float4 LightColor_RoughnessBias;
};

cbuffer MaterialParams : register(b1)
{
    float4 BaseColorFactor;
    float4 SurfaceParams;
    float4 EmissiveColor_AlphaCutoff;
};

Texture2D AlbedoTexture : register(t0);
Texture2D NormalTexture : register(t1);
Texture2D MaterialTexture : register(t2);
Texture2D EmissiveTexture : register(t3);
TextureCube ReflectionProbe : register(t4);
SamplerState LinearWrapSampler : register(s0);

struct VSInput
{
    float3 Position : POSITION;
    float3 Normal : NORMAL;
    float4 Tangent : TANGENT;
    float2 TexCoord : TEXCOORD0;
};

struct VSOutput
{
    float4 Position : SV_Position;
    float3 WorldNormal : TEXCOORD0;
    float3 WorldTangent : TEXCOORD1;
    float3 WorldBitangent : TEXCOORD2;
    float2 TexCoord : TEXCOORD3;
    float3 ViewDir : TEXCOORD4;
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
    float4 worldPos = float4(input.Position, 1.0);
    output.Position = mul(worldPos, ViewProjection);
    output.WorldNormal = normalize(input.Normal);
    output.WorldTangent = normalize(input.Tangent.xyz);
    output.WorldBitangent = normalize(cross(output.WorldNormal, output.WorldTangent) * input.Tangent.w);
    output.TexCoord = input.TexCoord;
    output.ViewDir = CameraPosition_Exposure.xyz - worldPos.xyz;
    return output;
}

float4 PSMain(VSOutput input) : SV_Target0
{
    float4 albedoSample = AlbedoTexture.Sample(LinearWrapSampler, input.TexCoord);
    float4 materialSample = MaterialTexture.Sample(LinearWrapSampler, input.TexCoord);
    float3 emissiveSample = EmissiveTexture.Sample(LinearWrapSampler, input.TexCoord).rgb;
    float3 tangentNormal = NormalTexture.Sample(LinearWrapSampler, input.TexCoord).xyz * 2.0 - 1.0;

    float3x3 tbn = float3x3(normalize(input.WorldTangent), normalize(input.WorldBitangent), normalize(input.WorldNormal));
    float3 n = normalize(mul(tangentNormal * float3(SurfaceParams.z, SurfaceParams.z, 1.0), tbn));
    float3 v = normalize(input.ViewDir);
    float3 l = normalize(-LightDirection_Intensity.xyz);
    float3 h = normalize(v + l);

    float metallic = saturate(SurfaceParams.x * materialSample.b);
    float roughness = saturate(max(0.04, SurfaceParams.y * materialSample.g + LightColor_RoughnessBias.w));
    float ao = lerp(1.0, materialSample.r, SurfaceParams.w);

    float3 baseColor = SRGBToLinear(albedoSample.rgb) * BaseColorFactor.rgb;
    float3 f0 = lerp(float3(0.04, 0.04, 0.04), baseColor, metallic);

    float ndf = DistributionGGX(n, h, roughness);
    float g = GeometrySmith(n, v, l, roughness);
    float3 f = FresnelSchlick(saturate(dot(h, v)), f0);

    float3 numerator = ndf * g * f;
    float denominator = max(4.0 * saturate(dot(n, v)) * saturate(dot(n, l)), 1e-4);
    float3 specular = numerator / denominator;

    float3 kd = (1.0 - f) * (1.0 - metallic);
    float ndl = saturate(dot(n, l));
    float3 diffuse = kd * baseColor * (1.0 / 3.14159265);
    float3 direct = (diffuse + specular) * LightColor_RoughnessBias.xyz * LightDirection_Intensity.w * ndl;

    float3 reflected = reflect(-v, n);
    float3 ibl = ReflectionProbe.SampleLevel(LinearWrapSampler, reflected, roughness * 6.0).rgb * f;
    float3 emissive = SRGBToLinear(emissiveSample) * EmissiveColor_AlphaCutoff.xyz * SurfaceParams.z;

    float alpha = albedoSample.a * BaseColorFactor.a;
    clip(alpha - EmissiveColor_AlphaCutoff.w);

    float3 finalColor = (direct + ibl + emissive) * ao;
    finalColor = 1.0 - exp(-finalColor * CameraPosition_Exposure.w);
    return float4(finalColor, alpha);
}
""";
}
