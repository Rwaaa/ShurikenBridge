Shader "ShurikenBridge/LitMesh"
{
    Properties
    {
        _BaseColor ("Base Color Tint", Color) = (1, 1, 1, 1)
        _EmissionColor ("Emission Color", Color) = (0, 0, 0, 0)
        _AmbientStrength ("Ambient Strength", Range(0.0, 1.0)) = 0.42
        _RimIntensity ("Volumetric Rim Intensity", Range(0.0, 2.0)) = 0.45
        _BucketBaseCommand ("Bucket Base Command Offset", Int) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry+10"
        }
        LOD 100

        Pass
        {
            Name "ShurikenBridgeLitMeshForward"
            Tags { "LightMode" = "UniversalForward" }

            ZWrite On
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex ShurikenBridgeLitMeshVert
            #pragma fragment ShurikenBridgeLitMeshFrag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct GpuVisibleParticle
            {
                float4 PositionAndSizeX;    // xyz: World Position, w: Scale X
                float4 VelocityAndRotZ;     // xyz: World Velocity, w: Rotation Z (radians)
                float4 ColorAndAlphaAge;    // rgb: Linear HDR Color, a: floor(normAge * 255) + alpha
                float4 ScaleYZAndRotXY;     // x: Scale Y, y: Scale Z, z: Rotation X, w: Rotation Y
            };

            StructuredBuffer<GpuVisibleParticle> _VisibleParticlesBuffer;
            uint _MaxInstancesPerCommand;

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half4 _EmissionColor;
                half _AmbientStrength;
                half _RimIntensity;
                uint _BucketBaseCommand;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                float2 uv2        : TEXCOORD1; // x = LodLevel (0..2)
                uint instanceID   : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half3 color       : COLOR0;
            };

            float3x3 BuildEulerRotation3x3(float3 eulerRad)
            {
                float sx, cx, sy, cy, sz, cz;
                sincos(eulerRad.x, sx, cx);
                sincos(eulerRad.y, sy, cy);
                sincos(eulerRad.z, sz, cz);

                float3 col0 = float3(cy * cz + sy * sx * sz, cx * sz, -sy * cz + cy * sx * sz);
                float3 col1 = float3(-cy * sz + sy * sx * cz, cx * cz, sy * sz + cy * sx * cz);
                float3 col2 = float3(sy * cx, -sx, cy * cx);
                return float3x3(col0, col1, col2);
            }

            Varyings ShurikenBridgeLitMeshVert(Attributes input)
            {
                Varyings output;

                uint lodLevel = (uint)(input.uv2.x + 0.5);
                uint cmdIndex = _BucketBaseCommand + lodLevel;
                uint globalSlot = cmdIndex * _MaxInstancesPerCommand + input.instanceID;
                GpuVisibleParticle part = _VisibleParticlesBuffer[globalSlot];

                float3 scale3D = float3(part.PositionAndSizeX.w, part.ScaleYZAndRotXY.x, part.ScaleYZAndRotXY.y);
                float3 eulerRot = float3(part.ScaleYZAndRotXY.z, part.ScaleYZAndRotXY.w, part.VelocityAndRotZ.w);
                float3x3 rotMat = BuildEulerRotation3x3(eulerRot);

                float3 scaledLocal = input.positionOS.xyz * scale3D;
                float3 rotatedPos = rotMat[0] * scaledLocal.x + rotMat[1] * scaledLocal.y + rotMat[2] * scaledLocal.z;
                float3 rotatedNormalRaw = rotMat[0] * input.normalOS.x + rotMat[1] * input.normalOS.y + rotMat[2] * input.normalOS.z;
                float normalLen = length(rotatedNormalRaw);
                float3 rotatedNormal = (normalLen > 0.0001) ? (rotatedNormalRaw / normalLen) : float3(0.0, 1.0, 0.0);

                float3 worldPos = rotatedPos + part.PositionAndSizeX.xyz;
                output.positionCS = TransformWorldToHClip(worldPos);

                Light mainLight = GetMainLight();
                half ndotl = saturate(dot((half3)rotatedNormal, (half3)mainLight.direction) * 0.65h + 0.35h);
                half3 lighting = mainLight.color * ndotl + half3(_AmbientStrength, _AmbientStrength, _AmbientStrength);

                float3 toCam = _WorldSpaceCameraPos.xyz - worldPos;
                float distToCam = length(toCam);
                float3 viewDirWS = (distToCam > 0.001) ? (toCam / distToCam) : float3(0.0, 0.0, 1.0);
                half rim = saturate(1.0h - saturate(dot((half3)rotatedNormal, (half3)viewDirWS)));
                half3 rimGlow = rim * rim * _RimIntensity * mainLight.color;

                half alpha = (half)frac(part.ColorAndAlphaAge.a);
                half normAge = (half)(floor(part.ColorAndAlphaAge.a) * (1.0 / 255.0));

                half heatBoost = (1.0h - smoothstep(0.0h, 0.55h, normAge));
                half3 baseRgb = (half3)part.ColorAndAlphaAge.rgb * _BaseColor.rgb * alpha;

                output.color = baseRgb * (lighting + rimGlow) + _EmissionColor.rgb * heatBoost;
                return output;
            }

            half4 ShurikenBridgeLitMeshFrag(Varyings input) : SV_Target
            {
                return half4(input.color, 1.0h);
            }
            ENDHLSL
        }
    }

    // =========================================================================
    // SubShader 2: Built-in Render Pipeline (BiRP) Lit Mesh Fallback
    // =========================================================================
    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry+10"
        }
        LOD 100

        Pass
        {
            Name "ShurikenBridgeBuiltInLitMesh"
            Tags { "LightMode" = "ForwardBase" }

            ZWrite On
            ZTest LEqual
            Cull Back

            CGPROGRAM
            #pragma target 4.5
            #pragma vertex ShurikenBridgeBiRPLitMeshVert
            #pragma fragment ShurikenBridgeBiRPLitMeshFrag

            #include "UnityCG.cginc"
            #include "Lighting.cginc"

            struct GpuVisibleParticle
            {
                float4 PositionAndSizeX;
                float4 VelocityAndRotZ;
                float4 ColorAndAlphaAge;
                float4 ScaleYZAndRotXY;
            };

            StructuredBuffer<GpuVisibleParticle> _VisibleParticlesBuffer;
            uint _MaxInstancesPerCommand;

            half4 _BaseColor;
            half4 _EmissionColor;
            half _AmbientStrength;
            half _RimIntensity;
            uint _BucketBaseCommand;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                float2 uv2        : TEXCOORD1;
                uint instanceID   : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half3 color       : COLOR0;
            };

            float3x3 BuildEulerRotation3x3BiRP(float3 eulerRad)
            {
                float sx, cx, sy, cy, sz, cz;
                sincos(eulerRad.x, sx, cx);
                sincos(eulerRad.y, sy, cy);
                sincos(eulerRad.z, sz, cz);

                float3 col0 = float3(cy * cz + sy * sx * sz, cx * sz, -sy * cz + cy * sx * sz);
                float3 col1 = float3(-cy * sz + sy * sx * cz, cx * cz, sy * sz + cy * sx * cz);
                float3 col2 = float3(sy * cx, -sx, cy * cx);
                return float3x3(col0, col1, col2);
            }

            Varyings ShurikenBridgeBiRPLitMeshVert(Attributes input)
            {
                Varyings output;

                uint lodLevel = (uint)(input.uv2.x + 0.5);
                uint cmdIndex = _BucketBaseCommand + lodLevel;
                uint globalSlot = cmdIndex * _MaxInstancesPerCommand + input.instanceID;
                GpuVisibleParticle part = _VisibleParticlesBuffer[globalSlot];

                float3 scale3D = float3(part.PositionAndSizeX.w, part.ScaleYZAndRotXY.x, part.ScaleYZAndRotXY.y);
                float3 eulerRot = float3(part.ScaleYZAndRotXY.z, part.ScaleYZAndRotXY.w, part.VelocityAndRotZ.w);
                float3x3 rotMat = BuildEulerRotation3x3BiRP(eulerRot);

                float3 scaledLocal = input.positionOS.xyz * scale3D;
                float3 rotatedPos = rotMat[0] * scaledLocal.x + rotMat[1] * scaledLocal.y + rotMat[2] * scaledLocal.z;
                float3 rotatedNormalRaw = rotMat[0] * input.normalOS.x + rotMat[1] * input.normalOS.y + rotMat[2] * input.normalOS.z;
                float normalLen = length(rotatedNormalRaw);
                float3 rotatedNormal = (normalLen > 0.0001) ? (rotatedNormalRaw / normalLen) : float3(0.0, 1.0, 0.0);

                float3 worldPos = rotatedPos + part.PositionAndSizeX.xyz;
                output.positionCS = mul(UNITY_MATRIX_VP, float4(worldPos, 1.0));

                half3 lightDir = normalize(_WorldSpaceLightPos0.xyz);
                half3 lightCol = _LightColor0.rgb;
                half ndotl = saturate(dot((half3)rotatedNormal, lightDir) * 0.65h + 0.35h);
                half3 lighting = lightCol * ndotl + half3(_AmbientStrength, _AmbientStrength, _AmbientStrength);

                float3 toCam = _WorldSpaceCameraPos.xyz - worldPos;
                float distToCam = length(toCam);
                float3 viewDirWS = (distToCam > 0.001) ? (toCam / distToCam) : float3(0.0, 0.0, 1.0);
                half rim = saturate(1.0h - saturate(dot((half3)rotatedNormal, (half3)viewDirWS)));
                half3 rimGlow = rim * rim * _RimIntensity * lightCol;

                half alpha = (half)frac(part.ColorAndAlphaAge.a);
                half normAge = (half)(floor(part.ColorAndAlphaAge.a) * (1.0 / 255.0));

                half heatBoost = (1.0h - smoothstep(0.0h, 0.55h, normAge));
                half3 baseRgb = (half3)part.ColorAndAlphaAge.rgb * _BaseColor.rgb * alpha;

                output.color = baseRgb * (lighting + rimGlow) + _EmissionColor.rgb * heatBoost;
                return output;
            }

            half4 ShurikenBridgeBiRPLitMeshFrag(Varyings input) : SV_Target
            {
                return half4(input.color, 1.0h);
            }
            ENDCG
        }
    }
}
