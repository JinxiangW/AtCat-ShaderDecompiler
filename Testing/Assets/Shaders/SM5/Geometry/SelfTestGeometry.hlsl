#include "../../Common/SelfTestShared.hlsli"

cbuffer GeometryGlobals : register(b0)
{
    float4 GeometryShellParams;
    float4 GeometryDebugTint;
};

struct DomainOutput
{
    float4 Position : SV_Position;
    float3 Normal : TEXCOORD0;
    float2 TexCoord : TEXCOORD1;
    float3 WorldPosition : TEXCOORD2;
};

struct GeometryOutput
{
    float4 Position : SV_Position;
    float3 Normal : TEXCOORD0;
    float2 TexCoord : TEXCOORD1;
    float4 Color : TEXCOORD2;
};

[maxvertexcount(3)]
void GSMain(triangle DomainOutput inputVertices[3], inout TriangleStream<GeometryOutput> stream)
{
    float3 faceNormal = SafeNormalize(cross(inputVertices[1].WorldPosition - inputVertices[0].WorldPosition, inputVertices[2].WorldPosition - inputVertices[0].WorldPosition));
    for (int i = 0; i < 3; i++)
    {
        GeometryOutput output;
        float shell = GeometryShellParams.x + GeometryShellParams.y * (i + 1);
        output.Position = inputVertices[i].Position + float4(faceNormal * shell, 0.0);
        output.Normal = SafeNormalize(inputVertices[i].Normal + faceNormal * GeometryShellParams.z);
        output.TexCoord = inputVertices[i].TexCoord + GeometryShellParams.ww * 0.01;
        output.Color = GeometryDebugTint;
        stream.Append(output);
    }
    stream.RestartStrip();
}
