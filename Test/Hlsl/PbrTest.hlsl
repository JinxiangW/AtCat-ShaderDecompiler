// Standalone PBR pixel shader resembling a Unity URP material pass.
// CBuffer / texture / sampler layout mirrors what AssetRipper exports
// for a real Unity shader so the metadata-driven symbol-injection
// pipeline can be exercised end-to-end. Compile with:
//   dxc.exe -T ps_5_0 -E main PbrTest.hlsl -Fo PbrTest.dxbc.bin
//   dxc.exe -T ps_6_0 -E main -spirv PbrTest.hlsl -Fo PbrTest.spv

cbuffer UnityPerFrame : register(b0)
{
    float4 _Time          : packoffset(c0);
    float3 _WorldSpaceCameraPos : packoffset(c1);
    float  _Exposure      : packoffset(c1.w);
};

cbuffer UnityPerDraw : register(b1)
{
    row_major float4x4 unity_ObjectToWorld : packoffset(c0);
    row_major float4x4 unity_WorldToObject : packoffset(c4);
};

cbuffer UnityPerMaterial : register(b2)
{
    float4 _MainTex_ST            : packoffset(c0);
    float4 _BumpMap_ST            : packoffset(c1);
    float4 _MetallicGlossMap_ST   : packoffset(c2);
    float4 _BaseColor             : packoffset(c3);
    float4 _EmissionColor         : packoffset(c4);
    float  _Metallic              : packoffset(c5);
    float  _Smoothness            : packoffset(c5.y);
    float  _OcclusionStrength     : packoffset(c5.z);
    float  _BumpScale             : packoffset(c5.w);
    float  _Cutoff                : packoffset(c6);
    float  _ClearCoat             : packoffset(c6.y);
    float  _ClearCoatRoughness    : packoffset(c6.z);
    float  _SpecularIntensity     : packoffset(c6.w);
    float3 _SubsurfaceColor       : packoffset(c7);
    float  _SubsurfaceRadius      : packoffset(c7.w);
};

Texture2D<float4> _MainTex          : register(t0);
Texture2D<float4> _BumpMap          : register(t1);
Texture2D<float4> _MetallicGlossMap : register(t2);
Texture2D<float4> _OcclusionMap     : register(t3);
Texture2D<float4> _EmissionMap      : register(t4);

SamplerState sampler_MainTex          : register(s0);
SamplerState sampler_BumpMap          : register(s1);
SamplerState sampler_MetallicGlossMap : register(s2);
SamplerState sampler_OcclusionMap     : register(s3);
SamplerState sampler_EmissionMap      : register(s4);

struct PsIn
{
    float4 PositionCS : SV_Position;
    float3 PositionWS : TEXCOORD0;
    float3 NormalWS   : TEXCOORD1;
    float3 TangentWS  : TEXCOORD2;
    float2 Uv         : TEXCOORD3;
};

static const float PI = 3.14159265359f;

float3 FresnelSchlick(float cosTheta, float3 F0)
{
    return F0 + (1.0f - F0) * pow(saturate(1.0f - cosTheta), 5.0f);
}

float DistributionGGX(float3 n, float3 h, float roughness)
{
    float a   = roughness * roughness;
    float a2  = a * a;
    float ndh = saturate(dot(n, h));
    float d   = ndh * ndh * (a2 - 1.0f) + 1.0f;
    return a2 / max(PI * d * d, 1e-5f);
}

float GeometrySchlickGGX(float ndv, float k)
{
    return ndv / (ndv * (1.0f - k) + k);
}

float GeometrySmith(float3 n, float3 v, float3 l, float roughness)
{
    float r   = roughness + 1.0f;
    float k   = (r * r) / 8.0f;
    float ndv = saturate(dot(n, v));
    float ndl = saturate(dot(n, l));
    return GeometrySchlickGGX(ndv, k) * GeometrySchlickGGX(ndl, k);
}

float3 SampleNormalTS(float2 uv, float scale)
{
    float3 n = _BumpMap.Sample(sampler_BumpMap, uv).xyz * 2.0f - 1.0f;
    n.xy *= scale;
    return normalize(n);
}

float3 TangentToWorld(float3 nTs, float3 nWs, float3 tWs)
{
    float3 b = normalize(cross(nWs, tWs));
    return normalize(nTs.x * tWs + nTs.y * b + nTs.z * nWs);
}

float4 main(PsIn input) : SV_Target
{
    float2 uvBase     = input.Uv * _MainTex_ST.xy + _MainTex_ST.zw;
    float2 uvNormal   = input.Uv * _BumpMap_ST.xy + _BumpMap_ST.zw;
    float2 uvMetallic = input.Uv * _MetallicGlossMap_ST.xy + _MetallicGlossMap_ST.zw;

    float4 baseColor  = _MainTex.Sample(sampler_MainTex, uvBase) * _BaseColor;
    clip(baseColor.a - _Cutoff);

    float3 normalTs = SampleNormalTS(uvNormal, _BumpScale);
    float3 normalWs = TangentToWorld(normalTs, normalize(input.NormalWS), normalize(input.TangentWS));

    float4 mr   = _MetallicGlossMap.Sample(sampler_MetallicGlossMap, uvMetallic);
    float metallic   = mr.r * _Metallic;
    float roughness  = saturate((1.0f - mr.a * _Smoothness));
    float occlusion  = lerp(1.0f, _OcclusionMap.Sample(sampler_OcclusionMap, uvBase).g, _OcclusionStrength);
    float3 emission  = _EmissionMap.Sample(sampler_EmissionMap, uvBase).rgb * _EmissionColor.rgb;

    float3 viewWs    = normalize(_WorldSpaceCameraPos - input.PositionWS);
    float3 lightWs   = normalize(float3(0.4f, 0.7f, 0.55f));
    float3 halfWs    = normalize(viewWs + lightWs);

    float3 F0   = lerp(0.04f.xxx, baseColor.rgb, metallic);
    float3 F    = FresnelSchlick(saturate(dot(halfWs, viewWs)), F0);
    float  D    = DistributionGGX(normalWs, halfWs, roughness);
    float  G    = GeometrySmith(normalWs, viewWs, lightWs, roughness);

    float ndl   = saturate(dot(normalWs, lightWs));
    float ndv   = saturate(dot(normalWs, viewWs));

    float3 specular = (D * G * F) / max(4.0f * ndl * ndv, 1e-4f) * _SpecularIntensity;
    float3 kd       = (1.0f - F) * (1.0f - metallic);
    float3 diffuse  = kd * baseColor.rgb / PI;

    float clearCoat = _ClearCoat * pow(saturate(1.0f - dot(viewWs, normalWs)), 1.0f + _ClearCoatRoughness * 4.0f);
    float3 sss      = _SubsurfaceColor * saturate(_SubsurfaceRadius - dot(normalWs, lightWs));

    float3 directLighting = (diffuse + specular) * ndl;
    float3 indirect       = baseColor.rgb * 0.05f * occlusion;
    float3 worldOffset    = mul((float3x3)unity_ObjectToWorld, normalWs);

    float3 final = directLighting + indirect + emission + clearCoat.xxx + sss;
    final *= _Exposure * (1.0f + 0.001f * dot(_Time.xyz, worldOffset));

    return float4(final, baseColor.a);
}
