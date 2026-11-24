Shader "ShurikenBridge/AdditiveBillboard"
{
    Properties
    {
        _BaseColor ("Base Color Tint", Color) = (1, 1, 1, 1)
        _HdrIntensity ("HDR Glow Intensity", Range(0.5, 8.0)) = 2.4
        _CoreSharpness ("Hot Core Sharpness", Range(1.0, 8.0)) = 3.2
        _StretchMultiplier ("Velocity Stretch Factor (0 = Billboard)", Range(0.0, 1.0)) = 0.0
        _MaxStretchRatio ("Max Velocity Stretch Ratio", Range(1.0, 10.0)) = 4.5
        _BucketBaseCommand ("Bucket Base Command Offset", Int) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
        }
        LOD 100

        Pass
        {
            Name "ShurikenBridgeAdditiveForward"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha One
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex ShurikenBridgeBillboardVert
            #pragma fragment ShurikenBridgeBillboardFrag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

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
                half _HdrIntensity;
                half _CoreSharpness;
                half _StretchMultiplier;
                half _MaxStretchRatio;
                uint _BucketBaseCommand;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                float2 uv2        : TEXCOORD1; // x = LodLevel (0..2)
                uint instanceID   : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                half4 color       : COLOR0;
            };

            Varyings ShurikenBridgeBillboardVert(Attributes input)
            {
                Varyings output;

                uint lodLevel = (uint)(input.uv2.x + 0.5);
                uint cmdIndex = _BucketBaseCommand + lodLevel;
                uint globalSlot = cmdIndex * _MaxInstancesPerCommand + input.instanceID;
                GpuVisibleParticle part = _VisibleParticlesBuffer[globalSlot];

                float3 centerWS = part.PositionAndSizeX.xyz;
                float2 sizeXY = float2(part.PositionAndSizeX.w, part.ScaleYZAndRotXY.x);
                float3 velWS = part.VelocityAndRotZ.xyz;
                float rotAngle = part.VelocityAndRotZ.w;

                float3 toPart = centerWS - _WorldSpaceCameraPos.xyz;
                float distToCam = length(toPart);
                float3 camForward = (distToCam > 0.001) ? (toPart / distToCam) : float3(0.0, 0.0, 1.0);
                float3 upRef = (abs(camForward.y) > 0.99) ? float3(1.0, 0.0, 0.0) : float3(0.0, 1.0, 0.0);
                float3 camRight = normalize(cross(upRef, camForward));
                float3 camUp = cross(camForward, camRight);

                float2 localXY = input.positionOS.xy;
                float3 worldOffset;

                if (_StretchMultiplier > 0.001h)
                {
                    float speed = length(velWS);
                    float3 dirWS = (speed > 0.01) ? (velWS / speed) : camUp;
                    float3 planeVel = dirWS - camForward * dot(dirWS, camForward);
                    float planeSpeed = length(planeVel);
                    float3 stretchUp = (planeSpeed > 0.001) ? (planeVel / planeSpeed) : camUp;
                    float3 stretchRight = normalize(cross(stretchUp, camForward));

                    float stretchFactor = clamp(1.0 + speed * (float)_StretchMultiplier, 1.0, (float)_MaxStretchRatio);
                    worldOffset = stretchRight * (localXY.x * sizeXY.x) + stretchUp * (localXY.y * sizeXY.y * stretchFactor);
                }
                else
                {
                    float s, c;
                    sincos(rotAngle, s, c);
                    float2 rotXY = float2(localXY.x * c - localXY.y * s, localXY.x * s + localXY.y * c);
                    worldOffset = camRight * (rotXY.x * sizeXY.x) + camUp * (rotXY.y * sizeXY.y);
                }

                float3 worldPos = centerWS + worldOffset;
                output.positionCS = TransformWorldToHClip(worldPos);
                output.uv = input.uv;

                half alpha = (half)frac(part.ColorAndAlphaAge.a) * _BaseColor.a;
                half3 rgb = (half3)part.ColorAndAlphaAge.rgb * _BaseColor.rgb * _HdrIntensity;

                output.color = half4(rgb, alpha);
                return output;
            }

            half4 ShurikenBridgeBillboardFrag(Varyings input) : SV_Target
            {
                float2 centeredUv = input.uv * 2.0 - 1.0;
                float distSq = dot(centeredUv, centeredUv);
                if (distSq >= 1.0)
                {
                    discard;
                }

                half radialMask = (half)(1.0 - distSq);
                half softFalloff = radialMask * radialMask;
                half hotCore = pow(radialMask, _CoreSharpness) * 0.65h;

                half3 finalRgb = input.color.rgb * softFalloff + hotCore * input.color.rgb;
                half finalAlpha = saturate(input.color.a * softFalloff);

                return half4(finalRgb, finalAlpha);
            }
            ENDHLSL
        }
    }

    // =========================================================================
    // SubShader 2: Built-in Render Pipeline (BiRP) Fallback
    // =========================================================================
    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
        }
        LOD 100

        Pass
        {
            Name "ShurikenBridgeBuiltInAdditive"

            Blend SrcAlpha One
            ZWrite Off
            ZTest LEqual
            Cull Off

            CGPROGRAM
            #pragma target 4.5
            #pragma vertex ShurikenBridgeBiRPBillboardVert
            #pragma fragment ShurikenBridgeBiRPBillboardFrag

            #include "UnityCG.cginc"

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
            half _HdrIntensity;
            half _CoreSharpness;
            half _StretchMultiplier;
            half _MaxStretchRatio;
            uint _BucketBaseCommand;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                float2 uv2        : TEXCOORD1;
                uint instanceID   : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                half4 color       : COLOR0;
            };

            Varyings ShurikenBridgeBiRPBillboardVert(Attributes input)
            {
                Varyings output;

                uint lodLevel = (uint)(input.uv2.x + 0.5);
                uint cmdIndex = _BucketBaseCommand + lodLevel;
                uint globalSlot = cmdIndex * _MaxInstancesPerCommand + input.instanceID;
                GpuVisibleParticle part = _VisibleParticlesBuffer[globalSlot];

                float3 centerWS = part.PositionAndSizeX.xyz;
                float2 sizeXY = float2(part.PositionAndSizeX.w, part.ScaleYZAndRotXY.x);
                float3 velWS = part.VelocityAndRotZ.xyz;
                float rotAngle = part.VelocityAndRotZ.w;

                float3 toPart = centerWS - _WorldSpaceCameraPos.xyz;
                float distToCam = length(toPart);
                float3 camForward = (distToCam > 0.001) ? (toPart / distToCam) : float3(0.0, 0.0, 1.0);
                float3 upRef = (abs(camForward.y) > 0.99) ? float3(1.0, 0.0, 0.0) : float3(0.0, 1.0, 0.0);
                float3 camRight = normalize(cross(upRef, camForward));
                float3 camUp = cross(camForward, camRight);

                float2 localXY = input.positionOS.xy;
                float3 worldOffset;

                if (_StretchMultiplier > 0.001h)
                {
                    float speed = length(velWS);
                    float3 dirWS = (speed > 0.01) ? (velWS / speed) : camUp;
                    float3 planeVel = dirWS - camForward * dot(dirWS, camForward);
                    float planeSpeed = length(planeVel);
                    float3 stretchUp = (planeSpeed > 0.001) ? (planeVel / planeSpeed) : camUp;
                    float3 stretchRight = normalize(cross(stretchUp, camForward));

                    float stretchFactor = clamp(1.0 + speed * (float)_StretchMultiplier, 1.0, (float)_MaxStretchRatio);
                    worldOffset = stretchRight * (localXY.x * sizeXY.x) + stretchUp * (localXY.y * sizeXY.y * stretchFactor);
                }
                else
                {
                    float s, c;
                    sincos(rotAngle, s, c);
                    float2 rotXY = float2(localXY.x * c - localXY.y * s, localXY.x * s + localXY.y * c);
                    worldOffset = camRight * (rotXY.x * sizeXY.x) + camUp * (rotXY.y * sizeXY.y);
                }

                float3 worldPos = centerWS + worldOffset;
                output.positionCS = mul(UNITY_MATRIX_VP, float4(worldPos, 1.0));
                output.uv = input.uv;

                half alpha = (half)frac(part.ColorAndAlphaAge.a) * _BaseColor.a;
                half3 rgb = (half3)part.ColorAndAlphaAge.rgb * _BaseColor.rgb * _HdrIntensity;

                output.color = half4(rgb, alpha);
                return output;
            }

            half4 ShurikenBridgeBiRPBillboardFrag(Varyings input) : SV_Target
            {
                float2 centeredUv = input.uv * 2.0 - 1.0;
                float distSq = dot(centeredUv, centeredUv);
                if (distSq >= 1.0)
                {
                    discard;
                }

                half radialMask = (half)(1.0 - distSq);
                half softFalloff = radialMask * radialMask;
                half hotCore = pow(radialMask, _CoreSharpness) * 0.65h;

                half3 finalRgb = input.color.rgb * softFalloff + hotCore * input.color.rgb;
                half finalAlpha = saturate(input.color.a * softFalloff);

                return half4(finalRgb, finalAlpha);
            }
            ENDCG
        }
    }
}
