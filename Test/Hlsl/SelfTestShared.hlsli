#ifndef RURI_SELFTEST_SHARED_HLSLI
#define RURI_SELFTEST_SHARED_HLSLI

#define SELFTEST_TYPE_COVERAGE_MEMBERS(Prefix) \
    float Prefix##Scalar : packoffset(c0.x); \
    float2 Prefix##Float2 : packoffset(c1.x); \
    float3 Prefix##Float3 : packoffset(c2.x); \
    bool Prefix##Bool : packoffset(c4.x); \
    int Prefix##Int : packoffset(c5.x); \
    int2 Prefix##Int2 : packoffset(c6.x); \
    int3 Prefix##Int3 : packoffset(c7.x); \
    int4 Prefix##Int4 : packoffset(c8); \
    uint Prefix##UInt : packoffset(c9.x); \
    uint2 Prefix##UInt2 : packoffset(c10.x); \
    uint3 Prefix##UInt3 : packoffset(c11.x); \
    uint4 Prefix##UInt4 : packoffset(c12); \
    row_major float2x2 Prefix##Matrix2 : packoffset(c13); \
    row_major float3x3 Prefix##Matrix3 : packoffset(c15);

struct CoverageLocalData
{
    float2 Uv;
    int2 SignedPair;
    uint4 Mask;
    float3 Basis[2];
};

#define SELFTEST_TEMPLATE_FIELDS(Prefix) \
    bool Prefix##Bool1; \
    bool2 Prefix##Bool2; \
    bool3 Prefix##Bool3; \
    bool4 Prefix##Bool4; \
    float Prefix##Float1; \
    float2 Prefix##Float2; \
    float3 Prefix##Float3; \
    float4 Prefix##Float4; \
    int Prefix##Int1; \
    int2 Prefix##Int2; \
    int3 Prefix##Int3; \
    int4 Prefix##Int4; \
    uint Prefix##UInt1; \
    uint2 Prefix##UInt2; \
    uint3 Prefix##UInt3; \
    uint4 Prefix##UInt4; \
    float2x2 Prefix##Float2x2; \
    float2x3 Prefix##Float2x3; \
    float2x4 Prefix##Float2x4; \
    float3x2 Prefix##Float3x2; \
    float3x3 Prefix##Float3x3; \
    float3x4 Prefix##Float3x4; \
    float4x2 Prefix##Float4x2; \
    float4x3 Prefix##Float4x3; \
    float4x4 Prefix##Float4x4;

struct SelfTestTemplateData
{
    SELFTEST_TEMPLATE_FIELDS(Template)
};

struct SelfTestTemplateAccumulator
{
    float Value;
    float4 Color;
};

float SelfTestBoolToFloat(bool value)
{
    return value ? 1.0f : 0.0f;
}

float SelfTestBoolToFloat2(bool2 value)
{
    return (value.x ? 1.0f : 0.0f) + (value.y ? 1.0f : 0.0f);
}

float SelfTestBoolToFloat3(bool3 value)
{
    return (value.x ? 1.0f : 0.0f) + (value.y ? 1.0f : 0.0f) + (value.z ? 1.0f : 0.0f);
}

float SelfTestBoolToFloat4(bool4 value)
{
    return (value.x ? 1.0f : 0.0f) + (value.y ? 1.0f : 0.0f) + (value.z ? 1.0f : 0.0f) + (value.w ? 1.0f : 0.0f);
}

float SelfTestTemplateEval(SelfTestTemplateData value)
{
    float sum = 0.0f;
    sum += SelfTestBoolToFloat(value.TemplateBool1);
    sum += SelfTestBoolToFloat2(value.TemplateBool2);
    sum += SelfTestBoolToFloat3(value.TemplateBool3);
    sum += SelfTestBoolToFloat4(value.TemplateBool4);
    sum += value.TemplateFloat1;
    sum += dot(value.TemplateFloat2, 1.0.xx);
    sum += dot(value.TemplateFloat3, 1.0.xxx);
    sum += dot(value.TemplateFloat4, 1.0.xxxx);
    sum += value.TemplateInt1;
    sum += value.TemplateInt2.x + value.TemplateInt2.y;
    sum += value.TemplateInt3.x + value.TemplateInt3.y + value.TemplateInt3.z;
    sum += value.TemplateInt4.x + value.TemplateInt4.y + value.TemplateInt4.z + value.TemplateInt4.w;
    sum += value.TemplateUInt1;
    sum += value.TemplateUInt2.x + value.TemplateUInt2.y;
    sum += value.TemplateUInt3.x + value.TemplateUInt3.y + value.TemplateUInt3.z;
    sum += value.TemplateUInt4.x + value.TemplateUInt4.y + value.TemplateUInt4.z + value.TemplateUInt4.w;
    sum += value.TemplateFloat2x2[0][0] + value.TemplateFloat2x2[1][1];
    sum += value.TemplateFloat2x3[0][0] + value.TemplateFloat2x3[1][2];
    sum += value.TemplateFloat2x4[0][0] + value.TemplateFloat2x4[1][3];
    sum += value.TemplateFloat3x2[0][0] + value.TemplateFloat3x2[2][1];
    sum += value.TemplateFloat3x3[0][0] + value.TemplateFloat3x3[2][2];
    sum += value.TemplateFloat3x4[0][0] + value.TemplateFloat3x4[2][3];
    sum += value.TemplateFloat4x2[0][0] + value.TemplateFloat4x2[3][1];
    sum += value.TemplateFloat4x3[0][0] + value.TemplateFloat4x3[3][2];
    sum += value.TemplateFloat4x4[0][0] + value.TemplateFloat4x4[3][3];
    return sum;
}

float4 ExpandPackedColor(uint packed)
{
    uint4 bytes = uint4((packed >> 0) & 255u, (packed >> 8) & 255u, (packed >> 16) & 255u, (packed >> 24) & 255u);
    return float4(bytes) / 255.0;
}

float3 SafeNormalize(float3 v)
{
    return normalize(v + 1e-4.xxx);
}

float2 Rotate90(float2 value)
{
    return float2(-value.y, value.x);
}

#endif
