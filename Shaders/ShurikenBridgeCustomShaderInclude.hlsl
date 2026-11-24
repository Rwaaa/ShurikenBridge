#ifndef SHURIKEN_BRIDGE_CUSTOM_SHADER_INCLUDE_HLSL
#define SHURIKEN_BRIDGE_CUSTOM_SHADER_INCLUDE_HLSL

// ============================================================================
// ShurikenBridge — Universal Custom HLSL Shader Include
// Usage in any custom .shader file:
//   #pragma multi_compile_instancing
//   #pragma instancing_options procedural:ParticleInstancingSetup
//   #include "Packages/com.shurikenbridge.core/Shaders/ShurikenBridgeCustomShaderInclude.hlsl"
//   (or relative/Assets path if installed inside Assets/ShurikenBridge/)
// ============================================================================

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ParticlesInstancing.hlsl"

half4 GetShurikenBridgeColor(half4 vertexColor = half4(1.0h, 1.0h, 1.0h, 1.0h))
{
#if defined(UNITY_PARTICLE_INSTANCING_ENABLED)
    UNITY_PARTICLE_INSTANCE_DATA data = unity_ParticleInstanceData[unity_InstanceID];
    uint rgba = data.color;
    half4 unpacked = half4(
        (rgba & 255u) * (1.0h / 255.0h),
        ((rgba >> 8u) & 255u) * (1.0h / 255.0h),
        ((rgba >> 16u) & 255u) * (1.0h / 255.0h),
        ((rgba >> 24u) & 255u) * (1.0h / 255.0h));
    return vertexColor * unpacked;
#else
    return vertexColor;
#endif
}

float2 GetShurikenBridgeFlipbookUV(float2 inputUv)
{
#if defined(UNITY_PARTICLE_INSTANCING_ENABLED)
    if (unity_ParticleUVShiftData.x != 0.0)
    {
        UNITY_PARTICLE_INSTANCE_DATA data = unity_ParticleInstanceData[unity_InstanceID];
        float numTilesX = max(1.0, unity_ParticleUVShiftData.y);
        float2 animScale = unity_ParticleUVShiftData.zw;
        float index0 = floor(data.animFrame);
        float vIdx0 = floor(index0 / numTilesX);
        float uIdx0 = floor(index0 - vIdx0 * numTilesX);
        float2 offset0 = float2(uIdx0 * animScale.x, (1.0 - animScale.y) - vIdx0 * animScale.y);
        return inputUv * animScale + offset0;
    }
#endif
    return inputUv;
}

#endif // SHURIKEN_BRIDGE_CUSTOM_SHADER_INCLUDE_HLSL
