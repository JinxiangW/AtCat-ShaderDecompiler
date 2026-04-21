#include "../../Common/SelfTestShared.hlsli"

cbuffer HullGlobals : register(b0)
{
    float4 HullFactors;
    float4 HullDirection_Bias;
};

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

[domain("tri")]
[partitioning("integer")]
[outputtopology("triangle_cw")]
[outputcontrolpoints(3)]
[patchconstantfunc("HSConstants")]
HullControlPoint HSMain(InputPatch<HullControlPoint, 3> patch, uint pointId : SV_OutputControlPointID)
{
    HullControlPoint output = patch[pointId];
    output.Position += output.Normal * dot(output.Normal, normalize(HullDirection_Bias.xyz)) * HullDirection_Bias.w;
    return output;
}

HullConstants HSConstants(InputPatch<HullControlPoint, 3> patch)
{
    HullConstants constants;
    float weight = abs(dot(SafeNormalize(patch[0].Normal + patch[1].Normal + patch[2].Normal), SafeNormalize(HullDirection_Bias.xyz)));
    constants.Edges[0] = max(1.0, HullFactors.x + weight);
    constants.Edges[1] = max(1.0, HullFactors.y + weight);
    constants.Edges[2] = max(1.0, HullFactors.z + weight);
    constants.Inside = max(1.0, HullFactors.w + HullDirection_Bias.w + weight);
    return constants;
}
