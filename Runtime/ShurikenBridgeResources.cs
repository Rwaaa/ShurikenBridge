using UnityEngine;

namespace ShurikenBridge
{
    /// <summary>
    /// Serialized shader and compute shader registry stored in Runtime/Resources/ShurikenBridgeResources.asset.
    /// Ensures Unity includes ShurikenBridgeSimulation.compute and fallback/bridging URP shaders in all
    /// Standalone, Mobile (Android/iOS), and Console Player Builds without shader stripping.
    /// </summary>
    public sealed class ShurikenBridgeResources : ScriptableObject
    {
        public const string ResourceLoadPath = "ShurikenBridgeResources";

        public ComputeShader SimulationCompute;
        public Shader DirectLitMeshShader;
        public Shader DirectAdditiveBillboardShader;
        public Shader UrpParticlesLitShader;
        public Shader UrpParticlesSimpleLitShader;
        public Shader UrpParticlesUnlitShader;
        public Shader BuiltInParticlesStandardSurfaceShader;
        public Shader BuiltInParticlesStandardUnlitShader;

        [Header("Editor Debugging (Forced OFF in Player Builds)")]
        [Tooltip("Editor-only Play Mode Scene View debugging mode (Off / GpuParticles / NativeShuriken). Automatically forced to Off before every Player Build.")]
        public ShurikenSceneViewDebugMode PlayModeSceneViewDebugMode = ShurikenSceneViewDebugMode.Off;

        public bool HasAllReferences =>
            SimulationCompute != null &&
            DirectLitMeshShader != null &&
            DirectAdditiveBillboardShader != null &&
            UrpParticlesLitShader != null &&
            UrpParticlesSimpleLitShader != null &&
            UrpParticlesUnlitShader != null;

        private int PopulatedReferenceCount
        {
            get
            {
                int c = 0;
                if (SimulationCompute != null) c++;
                if (DirectLitMeshShader != null) c++;
                if (DirectAdditiveBillboardShader != null) c++;
                if (UrpParticlesLitShader != null) c++;
                if (UrpParticlesSimpleLitShader != null) c++;
                if (UrpParticlesUnlitShader != null) c++;
                if (BuiltInParticlesStandardSurfaceShader != null) c++;
                if (BuiltInParticlesStandardUnlitShader != null) c++;
                return c;
            }
        }

        private static ShurikenBridgeResources s_Cached;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticCache()
        {
            s_Cached = null;
        }

        public static void InvalidateCache()
        {
            s_Cached = null;
        }

        public static ShurikenBridgeResources Load()
        {
            if (s_Cached == null)
            {
                ShurikenBridgeResources[] all = Resources.LoadAll<ShurikenBridgeResources>(ResourceLoadPath);
                if (all != null && all.Length > 0)
                {
                    ShurikenBridgeResources best = all[0];
                    int bestScore = best != null ? best.PopulatedReferenceCount : -1;
                    for (int i = 1; i < all.Length; i++)
                    {
                        if (all[i] != null && all[i].PopulatedReferenceCount > bestScore)
                        {
                            best = all[i];
                            bestScore = all[i].PopulatedReferenceCount;
                        }
                    }
                    s_Cached = best;
                }
                else
                {
                    s_Cached = Resources.Load<ShurikenBridgeResources>(ResourceLoadPath);
                }
            }
            return s_Cached;
        }
    }
}
