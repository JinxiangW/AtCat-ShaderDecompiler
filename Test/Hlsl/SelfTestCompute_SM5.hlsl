#include "SelfTestShared.hlsli"

cbuffer ComputeParams : register(b0)
{
    uint4 ComputeOutputExtent : packoffset(c0);
    uint4 ComputeClusterStride : packoffset(c1);
    uint4 ComputeActiveLightCount : packoffset(c2);
    float4 ComputeWeights : packoffset(c3);
};

StructuredBuffer<float4> ClusterLightData : register(t7);
ByteAddressBuffer ClusterIndices : register(t8);
RWTexture2D<float4> DebugOutput : register(u0);
RWStructuredBuffer<float4> ReductionBuffer : register(u1);
RWByteAddressBuffer CounterBuffer : register(u2);

[numthreads(8, 8, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    if (dispatchThreadId.x >= ComputeOutputExtent.x || dispatchThreadId.y >= ComputeOutputExtent.y)
    {
        return;
    }

    uint flatIndex = dispatchThreadId.y * ComputeOutputExtent.x + dispatchThreadId.x;
    uint lightCount = max(ComputeActiveLightCount.x, 1u);
    uint stride = max(ComputeClusterStride.x, 1u);
    uint encodedLightIndex = ClusterIndices.Load((flatIndex % lightCount) * stride * 4u);
    uint clampedLightIndex = encodedLightIndex % lightCount;
    float4 lightData = ClusterLightData[clampedLightIndex];
    float luminance = dot(lightData.rgb, ComputeWeights.xyz) + ComputeWeights.w;
    uint2 tileCoord = dispatchThreadId.xy & 7u;
    uint packedFlags = (clampedLightIndex << 16) | (encodedLightIndex & 65535u);
    uint bitCount = countbits(packedFlags);
    int signedIndex = (int)(clampedLightIndex & 127u) - 64;

    float2 remappedTile = float2(tileCoord);
    float3 remappedColor = lightData.rgb;
    float adjust = (tileCoord.x + tileCoord.y) * 0.0001f;
    adjust += remappedTile.x * 0.0001f;
    if ((bitCount & 1u) != 0u)
    {
        adjust += signedIndex * 0.00001f;
    }

    DebugOutput[dispatchThreadId.xy] = float4(lightData.rgb * luminance, 1.0);
    ReductionBuffer[flatIndex] = float4(remappedColor + adjust.xxx, luminance + adjust);
    CounterBuffer.Store(flatIndex * 4u, asuint(luminance + adjust));
}
