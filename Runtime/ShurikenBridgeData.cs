using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace ShurikenBridge
{
    public enum ShurikenRenderShape : int
    {
        Billboard = 0,
        StretchedBillboard = 1,
        MeshSphere = 2,
        MeshCube = 3
    }

    public enum ShurikenEmitterShape : uint
    {
        Cone = 0u,
        Sphere = 1u,
        Hemisphere = 2u,
        Box = 3u,
        Circle = 4u,
        Edge = 5u,
        Donut = 6u
    }

    /// <summary>
    /// Global Editor-only Play Mode Scene View debugging mode.
    /// Automatically forced to Off before every Player Build and compile-time disabled outside UNITY_EDITOR.
    /// </summary>
    public enum ShurikenSceneViewDebugMode : int
    {
        /// <summary>Default / Build mode: Scene View skips Play Mode particle rendering for maximum performance.</summary>
        Off = 0,
        /// <summary>Renders actual ShurikenBridge GPU particles in Scene View with individual click picking (high FPS).</summary>
        GpuParticles = 1,
        /// <summary>Runs 100% native CPU Shuriken in Scene View (native click/marquee/gizmos) while Game View renders GPU ShurikenBridge.</summary>
        NativeShuriken = 2
    }

    /// <summary>
    /// 256-Byte (16 x float4) strictly 16-byte aligned GPU Emitter Descriptor.
    /// Matches ShurikenBridgeSimulation.compute 1:1 across Metal, DX11, DX12, Vulkan, and OpenGL ES 3.1.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GpuEmitterDescriptor
    {
        // Row 0 (16B)
        public Vector3 WorldPosition;
        public uint MaterialBucketIndex;

        // Row 1 (16B)
        public Vector4 WorldRotationQuat;

        // Row 2 (16B)
        public Vector3 WorldScale;
        public uint ActiveParticleCount;

        // Row 3 (16B)
        public float Duration;
        public float StartDelay;
        public float StartLifetimeMin;
        public float StartLifetimeMax;

        // Row 4 (16B)
        public float StartSpeedMin;
        public float StartSpeedMax;
        public float SimulationSpeed;
        public float GravityModifier;

        // Row 5 (16B)
        public Vector3 StartSizeMin3D;
        public float StartSizeMaxRatio;

        // Row 6 (16B)
        public Vector3 StartRotationMin3D;
        public float StartRotationMaxZ;

        // Row 7 (16B)
        public uint Flags0;
        public uint Flags1;
        public uint RandomSeed;
        public float EmitterStartTime;

        // Row 8 (16B)
        public float ShapeRadius;
        public float ShapeAngleRad;
        public float ShapeRadiusThickness;
        public float ShapeDonutRadius;

        // Row 9 (16B)
        public Vector3 ShapeBoxScale;
        public float DragDamping;

        // Row 10 (16B)
        public Vector3 ShapeLocalOffset;
        public float NoiseStrength;

        // Row 11 (16B)
        public Vector3 LinearVelocityAndForce;
        public float RadialAndOrbitalSwirl;

        // Row 12 (16B)
        public uint StartColorMinPacked;
        public uint StartColorMaxPacked;
        public float NoiseFrequency;
        public float ExternalForceMultiplier;

        // Row 13 (16B)
        public float BurstTime;
        public float BurstRepeatInterval;
        public float BurstCycleCount;
        public float SpeedRangeMin;

        // Row 14 (16B)
        public float SpeedRangeMax;
        public float CollisionBounce;
        public float CollisionDampen;
        public float CollisionLifetimeLoss;

        // Row 15 (16B)
        public Vector4 PrimaryCollisionPlane;

        public static uint PackColorRGBA8(Color c)
        {
            Color linear = c.linear;
            uint r = (uint)Mathf.Clamp(Mathf.RoundToInt(linear.r * 255f), 0, 255);
            uint g = (uint)Mathf.Clamp(Mathf.RoundToInt(linear.g * 255f), 0, 255);
            uint b = (uint)Mathf.Clamp(Mathf.RoundToInt(linear.b * 255f), 0, 255);
            uint a = (uint)Mathf.Clamp(Mathf.RoundToInt(linear.a * 255f), 0, 255);
            return r | (g << 8) | (b << 16) | (a << 24);
        }

        public static uint PackFlags0(
            bool looping,
            bool useLocalSpace,
            bool prewarm,
            bool noiseEnabled,
            bool velocityOrForceEnabled,
            bool distanceThinningEnabled,
            bool collapseLodToCommand0,
            bool collisionEnabled,
            bool externalForcesEnabled,
            bool colorBySpeedEnabled,
            bool sizeBySpeedEnabled,
            bool rotationBySpeedEnabled,
            bool startSize3D,
            bool startRotation3D,
            ShurikenEmitterShape shape,
            int lutProfileIndex)
        {
            uint f = 0u;
            if (looping) f |= 1u << 0;
            if (useLocalSpace) f |= 1u << 1;
            if (prewarm) f |= 1u << 2;
            if (noiseEnabled) f |= 1u << 3;
            if (velocityOrForceEnabled) f |= 1u << 4;
            if (distanceThinningEnabled) f |= 1u << 5;
            if (collapseLodToCommand0) f |= 1u << 6;
            if (collisionEnabled) f |= 1u << 7;
            if (externalForcesEnabled) f |= 1u << 8;
            if (colorBySpeedEnabled) f |= 1u << 9;
            if (sizeBySpeedEnabled) f |= 1u << 10;
            if (rotationBySpeedEnabled) f |= 1u << 11;
            if (startSize3D) f |= 1u << 12;
            if (startRotation3D) f |= 1u << 13;
            f |= ((uint)shape & 0x0Fu) << 16;
            f |= ((uint)Mathf.Clamp(lutProfileIndex, 0, 127) & 0x7Fu) << 20;
            return f;
        }

        public static uint PackFlags1(int flipbookTilesX, int flipbookTilesY, int burstParticleCount, int collisionPlaneCount)
        {
            uint f = 0u;
            f |= (uint)Mathf.Clamp(flipbookTilesX, 1, 64) & 0xFFu;
            f |= ((uint)Mathf.Clamp(flipbookTilesY, 1, 64) & 0xFFu) << 8;
            f |= ((uint)Mathf.Clamp(burstParticleCount, 0, 255) & 0xFFu) << 16;
            f |= ((uint)Mathf.Clamp(collisionPlaneCount, 0, 6) & 0xFFu) << 24;
            return f;
        }
    }

    /// <summary>
    /// 64-Byte (4 x float4) strictly 16-byte aligned Visible Particle Instance.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GpuVisibleParticle
    {
        public Vector4 PositionAndSizeX;
        public Vector4 VelocityAndRotZ;
        public Vector4 ColorAndAlphaAge;
        public Vector4 ScaleYZAndRotXY;
    }

    /// <summary>
    /// 64-Byte (4 x float4) External Force Field Descriptor mapped from Unity's ParticleSystemForceField.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GpuForceFieldDescriptor
    {
        public Vector3 Position;
        public float EndRange;

        public Vector4 RotationQuat;

        public Vector3 DirectionForce;
        public float GravityStrength;

        public float RotationSpeed;
        public float DragStrength;
        public float StartRange;
        public float EnabledFlag;
    }

    /// <summary>
    /// 56-Byte struct matching Unity's official DefaultParticleInstanceData (unity_ParticleInstanceData).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GpuUnityParticleInstanceData
    {
        public Vector4 TransformRow0;
        public Vector4 TransformRow1;
        public Vector4 TransformRow2;
        public uint ColorRGBA8;
        public float AnimFrame;
    }

    public struct ShurikenSubMeshSlice
    {
        public ShurikenRenderShape Shape;
        public int LodLevel;
        public uint IndexCount;
        public uint StartIndex;
        public uint BaseVertex;
        public uint TrianglesPerInstance;
    }

    /// <summary>
    /// Procedural unified Mega-Mesh Builder containing 4 RenderShapes x 3 LODs (12 sub-geometries)
    /// with baked TEXCOORD1.x = LodLevel(0..2), baseVertex = 0, and pure white vertex colors.
    /// </summary>
    public static class ShurikenMegaMeshBuilder
    {
        public const int ShapeCount = 4;
        public const int LodCount = 3;
        public const int TotalSliceCount = ShapeCount * LodCount;

        public static Mesh BuildParticleMegaMesh(out ShurikenSubMeshSlice[] outSlices)
        {
            outSlices = new ShurikenSubMeshSlice[TotalSliceCount];

            List<Vector3> allVertices = new List<Vector3>(2048);
            List<Vector3> allNormals = new List<Vector3>(2048);
            List<Color32> allColors = new List<Color32>(2048);
            List<Vector2> allUvs0 = new List<Vector2>(2048);
            List<Vector2> allUvs1 = new List<Vector2>(2048);
            List<int> allIndices = new List<int>(8192);

            for (int shapeIdx = 0; shapeIdx < ShapeCount; shapeIdx++)
            {
                ShurikenRenderShape shape = (ShurikenRenderShape)shapeIdx;
                for (int lod = 0; lod < LodCount; lod++)
                {
                    int sliceIdx = shapeIdx * LodCount + lod;
                    int vertexOffset = allVertices.Count;
                    int startIndex = allIndices.Count;
                    Vector2 lodMetaUv = new Vector2(lod, shapeIdx);

                    AppendShapeLodGeometry(
                        shape,
                        lod,
                        lodMetaUv,
                        vertexOffset,
                        allVertices,
                        allNormals,
                        allUvs0,
                        allUvs1,
                        allIndices);

                    int indexCount = allIndices.Count - startIndex;
                    outSlices[sliceIdx] = new ShurikenSubMeshSlice
                    {
                        Shape = shape,
                        LodLevel = lod,
                        IndexCount = (uint)indexCount,
                        StartIndex = (uint)startIndex,
                        BaseVertex = 0u,
                        TrianglesPerInstance = (uint)(indexCount / 3)
                    };
                }
            }

            Color32 white32 = new Color32(255, 255, 255, 255);
            for (int i = 0; i < allVertices.Count; i++)
            {
                allColors.Add(white32);
            }

            Mesh megaMesh = new Mesh
            {
                name = "ShurikenBridge_CombinedMegaMesh_4Shapes_3LODs",
                indexFormat = IndexFormat.UInt32
            };

            megaMesh.SetVertices(allVertices);
            megaMesh.SetNormals(allNormals);
            megaMesh.SetColors(allColors);
            megaMesh.SetUVs(0, allUvs0);
            megaMesh.SetUVs(1, allUvs1);
            megaMesh.SetTriangles(allIndices, 0, true);
            megaMesh.bounds = new Bounds(Vector3.zero, new Vector3(4000f, 1000f, 4000f));

            return megaMesh;
        }

        private static void AppendShapeLodGeometry(
            ShurikenRenderShape shape,
            int lod,
            Vector2 lodMetaUv,
            int vertexOffset,
            List<Vector3> verts,
            List<Vector3> norms,
            List<Vector2> uvs0,
            List<Vector2> uvs1,
            List<int> indices)
        {
            switch (shape)
            {
                case ShurikenRenderShape.Billboard:
                case ShurikenRenderShape.StretchedBillboard:
                    if (lod < 2)
                        AppendQuad(lodMetaUv, vertexOffset, verts, norms, uvs0, uvs1, indices);
                    else
                        AppendBoundingTriangle(lodMetaUv, vertexOffset, verts, norms, uvs0, uvs1, indices);
                    break;

                case ShurikenRenderShape.MeshSphere:
                    if (lod == 0)
                        AppendUvSphere(16, 10, 0.5f, lodMetaUv, vertexOffset, verts, norms, uvs0, uvs1, indices);
                    else if (lod == 1)
                        AppendUvSphere(8, 6, 0.5f, lodMetaUv, vertexOffset, verts, norms, uvs0, uvs1, indices);
                    else
                        AppendSubdividedBox(1, Vector3.one * 0.85f, 0f, lodMetaUv, verts, norms, uvs0, uvs1, indices);
                    break;

                case ShurikenRenderShape.MeshCube:
                default:
                    if (lod == 0)
                        AppendSubdividedBox(3, Vector3.one * 0.9f, 0.22f, lodMetaUv, verts, norms, uvs0, uvs1, indices);
                    else if (lod == 1)
                        AppendSubdividedBox(2, Vector3.one * 0.9f, 0.12f, lodMetaUv, verts, norms, uvs0, uvs1, indices);
                    else
                        AppendSubdividedBox(1, Vector3.one * 0.9f, 0f, lodMetaUv, verts, norms, uvs0, uvs1, indices);
                    break;
            }
        }

        private static void AppendQuad(
            Vector2 lodMetaUv,
            int vOff,
            List<Vector3> verts,
            List<Vector3> norms,
            List<Vector2> uvs0,
            List<Vector2> uvs1,
            List<int> indices)
        {
            verts.Add(new Vector3(-0.5f, -0.5f, 0f));
            verts.Add(new Vector3(0.5f, -0.5f, 0f));
            verts.Add(new Vector3(0.5f, 0.5f, 0f));
            verts.Add(new Vector3(-0.5f, 0.5f, 0f));

            for (int i = 0; i < 4; i++)
            {
                norms.Add(Vector3.back);
                uvs1.Add(lodMetaUv);
            }

            uvs0.Add(new Vector2(0f, 0f));
            uvs0.Add(new Vector2(1f, 0f));
            uvs0.Add(new Vector2(1f, 1f));
            uvs0.Add(new Vector2(0f, 1f));

            indices.Add(vOff + 0);
            indices.Add(vOff + 2);
            indices.Add(vOff + 1);

            indices.Add(vOff + 0);
            indices.Add(vOff + 3);
            indices.Add(vOff + 2);
        }

        private static void AppendBoundingTriangle(
            Vector2 lodMetaUv,
            int vOff,
            List<Vector3> verts,
            List<Vector3> norms,
            List<Vector2> uvs0,
            List<Vector2> uvs1,
            List<int> indices)
        {
            const float xExt = 0.8660254f;
            verts.Add(new Vector3(-xExt, -0.5f, 0f));
            verts.Add(new Vector3(xExt, -0.5f, 0f));
            verts.Add(new Vector3(0.0f, 1.0f, 0f));

            for (int i = 0; i < 3; i++)
            {
                norms.Add(Vector3.back);
                uvs1.Add(lodMetaUv);
            }

            uvs0.Add(new Vector2(0.5f - xExt, 0.0f));
            uvs0.Add(new Vector2(0.5f + xExt, 0.0f));
            uvs0.Add(new Vector2(0.5f, 1.5f));

            indices.Add(vOff + 0);
            indices.Add(vOff + 2);
            indices.Add(vOff + 1);
        }

        private static void AppendUvSphere(
            int lonSegments,
            int latSegments,
            float radius,
            Vector2 lodMetaUv,
            int vertexOffset,
            List<Vector3> verts,
            List<Vector3> norms,
            List<Vector2> uvs0,
            List<Vector2> uvs1,
            List<int> indices)
        {
            for (int lat = 0; lat <= latSegments; lat++)
            {
                float v = (float)lat / latSegments;
                float theta = v * Mathf.PI;
                float sinTheta = Mathf.Sin(theta);
                float cosTheta = Mathf.Cos(theta);

                for (int lon = 0; lon <= lonSegments; lon++)
                {
                    float u = (float)lon / lonSegments;
                    float phi = u * 2.0f * Mathf.PI;
                    float sinPhi = Mathf.Sin(phi);
                    float cosPhi = Mathf.Cos(phi);

                    Vector3 unitDir = new Vector3(cosPhi * sinTheta, cosTheta, sinPhi * sinTheta);
                    verts.Add(unitDir * radius);
                    norms.Add(unitDir);
                    uvs0.Add(new Vector2(u, v));
                    uvs1.Add(lodMetaUv);
                }
            }

            int stride = lonSegments + 1;
            for (int lat = 0; lat < latSegments; lat++)
            {
                for (int lon = 0; lon < lonSegments; lon++)
                {
                    int current = vertexOffset + lat * stride + lon;
                    int next = current + stride;

                    if (lat != 0)
                    {
                        indices.Add(current);
                        indices.Add(current + 1);
                        indices.Add(next);
                    }

                    if (lat != latSegments - 1)
                    {
                        indices.Add(current + 1);
                        indices.Add(next + 1);
                        indices.Add(next);
                    }
                }
            }
        }

        private static void AppendSubdividedBox(
            int subdivisions,
            Vector3 size,
            float spherifyFactor,
            Vector2 lodMetaUv,
            List<Vector3> verts,
            List<Vector3> norms,
            List<Vector2> uvs0,
            List<Vector2> uvs1,
            List<int> indices)
        {
            Vector3 half = size * 0.5f;
            AppendBoxFace(subdivisions, new Vector3(0, 0, half.z), new Vector3(half.x, 0, 0), new Vector3(0, half.y, 0), Vector3.forward, spherifyFactor, lodMetaUv, verts, norms, uvs0, uvs1, indices);
            AppendBoxFace(subdivisions, new Vector3(0, 0, -half.z), new Vector3(-half.x, 0, 0), new Vector3(0, half.y, 0), Vector3.back, spherifyFactor, lodMetaUv, verts, norms, uvs0, uvs1, indices);
            AppendBoxFace(subdivisions, new Vector3(half.x, 0, 0), new Vector3(0, 0, -half.z), new Vector3(0, half.y, 0), Vector3.right, spherifyFactor, lodMetaUv, verts, norms, uvs0, uvs1, indices);
            AppendBoxFace(subdivisions, new Vector3(-half.x, 0, 0), new Vector3(0, 0, half.z), new Vector3(0, half.y, 0), Vector3.left, spherifyFactor, lodMetaUv, verts, norms, uvs0, uvs1, indices);
            AppendBoxFace(subdivisions, new Vector3(0, half.y, 0), new Vector3(half.x, 0, 0), new Vector3(0, 0, -half.z), Vector3.up, spherifyFactor, lodMetaUv, verts, norms, uvs0, uvs1, indices);
            AppendBoxFace(subdivisions, new Vector3(0, -half.y, 0), new Vector3(half.x, 0, 0), new Vector3(0, 0, half.z), Vector3.down, spherifyFactor, lodMetaUv, verts, norms, uvs0, uvs1, indices);
        }

        private static void AppendBoxFace(
            int subdiv,
            Vector3 center,
            Vector3 rightAxis,
            Vector3 upAxis,
            Vector3 faceNormal,
            float spherify,
            Vector2 lodMetaUv,
            List<Vector3> verts,
            List<Vector3> norms,
            List<Vector2> uvs0,
            List<Vector2> uvs1,
            List<int> indices)
        {
            int startVert = verts.Count;
            for (int y = 0; y <= subdiv; y++)
            {
                float v = y / (float)subdiv;
                float ty = v * 2.0f - 1.0f;
                for (int x = 0; x <= subdiv; x++)
                {
                    float u = x / (float)subdiv;
                    float tx = u * 2.0f - 1.0f;
                    Vector3 pos = center + rightAxis * tx + upAxis * ty;
                    Vector3 n = faceNormal;

                    if (spherify > 0.001f)
                    {
                        Vector3 sphereDir = pos.normalized;
                        pos = Vector3.Lerp(pos, sphereDir * pos.magnitude * 0.85f, spherify);
                        n = Vector3.Lerp(faceNormal, sphereDir, spherify * 0.7f).normalized;
                    }

                    verts.Add(pos);
                    norms.Add(n);
                    uvs0.Add(new Vector2(u, v));
                    uvs1.Add(lodMetaUv);
                }
            }

            int stride = subdiv + 1;
            for (int y = 0; y < subdiv; y++)
            {
                for (int x = 0; x < subdiv; x++)
                {
                    int i0 = startVert + y * stride + x;
                    int i1 = i0 + 1;
                    int i2 = i0 + stride;
                    int i3 = i2 + 1;

                    indices.Add(i0);
                    indices.Add(i2);
                    indices.Add(i1);

                    indices.Add(i1);
                    indices.Add(i2);
                    indices.Add(i3);
                }
            }
        }
    }
}
