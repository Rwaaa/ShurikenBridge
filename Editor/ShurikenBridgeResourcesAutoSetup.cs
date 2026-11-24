#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace ShurikenBridge.Editor
{
    /// <summary>
    /// Automatically creates and validates ShurikenBridgeResources.asset inside Runtime/Resources/
    /// on Editor load and prior to every Player Build so Compute Shaders and fallback/bridging URP shaders
    /// are never stripped in Standalone, Android, or iOS builds.
    /// </summary>
    [InitializeOnLoad]
    public sealed class ShurikenBridgeResourcesAutoSetup : IPreprocessBuildWithReport
    {
        private const string AssetsResourcesDir = "Assets/ShurikenBridge/Runtime/Resources";
        private const string AssetsResourceAssetPath = "Assets/ShurikenBridge/Runtime/Resources/ShurikenBridgeResources.asset";
        private const string ProjectFallbackResourcesDir = "Assets/Resources";
        private const string ProjectFallbackResourceAssetPath = "Assets/Resources/ShurikenBridgeResources.asset";

        private const string MenuPathSceneViewOff = "Tools/ShurikenBridge/Play Mode Scene View/Off (Default - Max FPS)";
        private const string MenuPathSceneViewGpu = "Tools/ShurikenBridge/Play Mode Scene View/GPU Particles (High FPS + Click Pick)";
        private const string MenuPathSceneViewNative = "Tools/ShurikenBridge/Play Mode Scene View/Native Shuriken (100% Native Scene View + A-B Compare)";

        public int callbackOrder => -100;

        static ShurikenBridgeResourcesAutoSetup()
        {
            EditorApplication.delayCall += EnsureResourcesAssetPopulated;
        }

        [MenuItem(MenuPathSceneViewOff, false, 50)]
        private static void SetSceneViewModeOff()
        {
            ShurikenBridgeManager.PlayModeSceneViewDebugMode = ShurikenSceneViewDebugMode.Off;
            Debug.Log("[ShurikenBridge] Play Mode Scene View Debug Mode: Off (Max FPS)");
        }

        [MenuItem(MenuPathSceneViewOff, true)]
        private static bool ValidateSceneViewModeOff()
        {
            Menu.SetChecked(MenuPathSceneViewOff, ShurikenBridgeManager.PlayModeSceneViewDebugMode == ShurikenSceneViewDebugMode.Off);
            return true;
        }

        [MenuItem(MenuPathSceneViewGpu, false, 51)]
        private static void SetSceneViewModeGpu()
        {
            ShurikenBridgeManager.PlayModeSceneViewDebugMode = ShurikenSceneViewDebugMode.GpuParticles;
            Debug.Log("[ShurikenBridge] Play Mode Scene View Debug Mode: GpuParticles (High FPS + Individual Click Pick)");
        }

        [MenuItem(MenuPathSceneViewGpu, true)]
        private static bool ValidateSceneViewModeGpu()
        {
            Menu.SetChecked(MenuPathSceneViewGpu, ShurikenBridgeManager.PlayModeSceneViewDebugMode == ShurikenSceneViewDebugMode.GpuParticles);
            return true;
        }

        [MenuItem(MenuPathSceneViewNative, false, 52)]
        private static void SetSceneViewModeNative()
        {
            ShurikenBridgeManager.PlayModeSceneViewDebugMode = ShurikenSceneViewDebugMode.NativeShuriken;
            Debug.Log("[ShurikenBridge] Play Mode Scene View Debug Mode: NativeShuriken (Scene View = 100% Native Shuriken, Game View = GPU ShurikenBridge)");
        }

        [MenuItem(MenuPathSceneViewNative, true)]
        private static bool ValidateSceneViewModeNative()
        {
            Menu.SetChecked(MenuPathSceneViewNative, ShurikenBridgeManager.PlayModeSceneViewDebugMode == ShurikenSceneViewDebugMode.NativeShuriken);
            return true;
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            EnsureResourcesAssetPopulated();

            // Force Play Mode Scene View debug mode to Off before every Player Build
            if (ShurikenBridgeManager.PlayModeSceneViewDebugMode != ShurikenSceneViewDebugMode.Off)
            {
                ShurikenBridgeManager.PlayModeSceneViewDebugMode = ShurikenSceneViewDebugMode.Off;
                Debug.Log("[ShurikenBridge] Forced PlayModeSceneViewDebugMode = Off for Player Build.");
            }

            ShurikenBridgeResources res = ShurikenBridgeResources.Load();
            if (res != null && res.PlayModeSceneViewDebugMode != ShurikenSceneViewDebugMode.Off)
            {
                res.PlayModeSceneViewDebugMode = ShurikenSceneViewDebugMode.Off;
                EditorUtility.SetDirty(res);
                AssetDatabase.SaveAssets();
                ShurikenBridgeResources.InvalidateCache();
            }
        }

        [MenuItem("Tools/ShurikenBridge/Verify Build Shader Resources")]
        public static void EnsureResourcesAssetPopulated()
        {
            bool livesInAssets = Directory.Exists(Path.GetFullPath("Assets/ShurikenBridge/Runtime"));

            string targetDir;
            string targetAssetPath;

            if (livesInAssets)
            {
                targetDir = AssetsResourcesDir;
                targetAssetPath = AssetsResourceAssetPath;
            }
            else
            {
                // Installed as a read-only UPM package: verify bundled Resources asset first.
                // Only create a project-local Assets/Resources/ fallback if any URP shader GUID is missing
                // (e.g., URP 14 vs URP 17 GUID differences across Unity 2022.3 LTS and Unity 6).
                ShurikenBridgeResources existing = ShurikenBridgeResources.Load();
                if (existing != null && existing.HasAllReferences)
                {
                    return;
                }
                targetDir = ProjectFallbackResourcesDir;
                targetAssetPath = ProjectFallbackResourceAssetPath;
            }

            if (!Directory.Exists(Path.GetFullPath(targetDir)))
            {
                Directory.CreateDirectory(Path.GetFullPath(targetDir));
            }

            ShurikenBridgeResources res = AssetDatabase.LoadAssetAtPath<ShurikenBridgeResources>(targetAssetPath);
            bool created = false;
            if (res == null)
            {
                res = ScriptableObject.CreateInstance<ShurikenBridgeResources>();
                AssetDatabase.CreateAsset(res, targetAssetPath);
                created = true;
            }

            bool dirty = created;

            if (res.SimulationCompute == null)
            {
                res.SimulationCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                    "Assets/ShurikenBridge/Shaders/ShurikenBridgeSimulation.compute");
                if (res.SimulationCompute == null)
                {
                    res.SimulationCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                        "Packages/com.shurikenbridge.core/Shaders/ShurikenBridgeSimulation.compute");
                }
                if (res.SimulationCompute != null) dirty = true;
            }

            if (res.DirectLitMeshShader == null)
            {
                res.DirectLitMeshShader = Shader.Find("ShurikenBridge/LitMesh");
                if (res.DirectLitMeshShader != null) dirty = true;
            }

            if (res.DirectAdditiveBillboardShader == null)
            {
                res.DirectAdditiveBillboardShader = Shader.Find("ShurikenBridge/AdditiveBillboard");
                if (res.DirectAdditiveBillboardShader != null) dirty = true;
            }

            if (res.UrpParticlesLitShader == null)
            {
                res.UrpParticlesLitShader = Shader.Find("Universal Render Pipeline/Particles/Lit");
                if (res.UrpParticlesLitShader != null) dirty = true;
            }

            if (res.UrpParticlesSimpleLitShader == null)
            {
                res.UrpParticlesSimpleLitShader = Shader.Find("Universal Render Pipeline/Particles/Simple Lit");
                if (res.UrpParticlesSimpleLitShader != null) dirty = true;
            }

            if (res.UrpParticlesUnlitShader == null)
            {
                res.UrpParticlesUnlitShader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
                if (res.UrpParticlesUnlitShader != null) dirty = true;
            }

            if (res.BuiltInParticlesStandardSurfaceShader == null)
            {
                res.BuiltInParticlesStandardSurfaceShader = Shader.Find("Particles/Standard Surface");
                if (res.BuiltInParticlesStandardSurfaceShader != null) dirty = true;
            }

            if (res.BuiltInParticlesStandardUnlitShader == null)
            {
                res.BuiltInParticlesStandardUnlitShader = Shader.Find("Particles/Standard Unlit");
                if (res.BuiltInParticlesStandardUnlitShader != null) dirty = true;
            }

            if (dirty)
            {
                EditorUtility.SetDirty(res);
                AssetDatabase.SaveAssets();
                ShurikenBridgeResources.InvalidateCache();
            }
        }
    }
}
#endif
