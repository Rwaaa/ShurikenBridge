#ifndef SHURIKEN_BRIDGE_SHADERGRAPH_NODE_HLSL
#define SHURIKEN_BRIDGE_SHADERGRAPH_NODE_HLSL

// ============================================================================
// ShurikenBridge — Custom Function Node for Unity Shader Graph
// In Shader Graph:
//   1. Add a Custom Function Node (Type: File, Source: ShurikenBridgeShaderGraphNode.hlsl, Name: ShurikenBridgeParticleData)
//   2. Inputs: PositionOS (Vector3), NormalOS (Vector3), VertexColor (Vector4)
//   3. Outputs: OutPositionOS (Vector3), OutNormalOS (Vector3), OutParticleColor (Vector4)
// ============================================================================

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ParticlesInstancing.hlsl"

void ShurikenBridgeParticleData_float(
    float3 PositionOS,
    float3 NormalOS,
    float4 VertexColor,
    out float3 OutPositionOS,
    out float3 OutNormalOS,
    out float4 OutParticleColor)
{
    OutPositionOS = PositionOS;
    OutNormalOS = NormalOS;
#if defined(UNITY_PARTICLE_INSTANCING_ENABLED)
    UNITY_PARTICLE_INSTANCE_DATA data = unity_ParticleInstanceData[unity_InstanceID];
    uint rgba = data.color;
    float4 unpacked = float4(
        (rgba & 255u) * (1.0 / 255.0),
        ((rgba >> 8u) & 255u) * (1.0 / 255.0),
        ((rgba >> 16u) & 255u) * (1.0 / 255.0),
        ((rgba >> 24u) & 255u) * (1.0 / 255.0));
    OutParticleColor = VertexColor * unpacked;
#else
    OutParticleColor = VertexColor;
#endif
}

void ShurikenBridgeParticleData_half(
    half3 PositionOS,
    half3 NormalOS,
    half4 VertexColor,
    out half3 OutPositionOS,
    out half3 OutNormalOS,
    out half4 OutParticleColor)
{
    OutPositionOS = PositionOS;
    OutNormalOS = NormalOS;
#if defined(UNITY_PARTICLE_INSTANCING_ENABLED)
    UNITY_PARTICLE_INSTANCE_DATA data = unity_ParticleInstanceData[unity_InstanceID];
    uint rgba = data.color;
    half4 unpacked = half4(
        (rgba & 255u) * (1.0h / 255.0h),
        ((rgba >> 8u) & 255u) * (1.0h / 255.0h),
        ((rgba >> 16u) & 255u) * (1.0h / 255.0h),
        ((rgba >> 24u) & 255u) * (1.0h / 255.0h));
    OutParticleColor = VertexColor * unpacked;
#else
    OutParticleColor = VertexColor;
#endif
}

#endif // SHURIKEN_BRIDGE_SHADERGRAPH_NODE_HLSL
