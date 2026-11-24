#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ShurikenBridge.Editor
{
    [CustomEditor(typeof(ShurikenBridgeEmitter))]
    [CanEditMultipleObjects]
    public sealed class ShurikenBridgeEmitterEditor : UnityEditor.Editor
    {
        private struct ShaderPatchCacheEntry
        {
            public long LastWriteTicksUtc;
            public bool NeedsPatch;
        }

        private static readonly Dictionary<int, ShaderPatchCacheEntry> s_ShaderPatchCheckCache =
            new Dictionary<int, ShaderPatchCacheEntry>(16);
        public override void OnInspectorGUI()
        {
            ShurikenBridgeEmitter emitter = (ShurikenBridgeEmitter)target;

            EditorGUILayout.Space(4f);
            DrawTelemetryAndModulesBox(emitter);
            EditorGUILayout.Space(6f);

            EditorGUI.BeginChangeCheck();
            DrawDefaultInspector();
            if (EditorGUI.EndChangeCheck())
            {
                foreach (UnityEngine.Object obj in targets)
                {
                    if (obj is ShurikenBridgeEmitter e)
                    {
                        e.MarkDirty(forceRebakeLut: true);
                    }
                }
            }

            EditorGUILayout.Space(8f);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Sync & Rebake LUT", GUILayout.Height(26f)))
            {
                foreach (UnityEngine.Object obj in targets)
                {
                    if (obj is ShurikenBridgeEmitter e)
                    {
                        e.MarkDirty(forceRebakeLut: true);
                        e.SyncToManagerIfDirty();
                    }
                }
                if (ShurikenBridgeManager.Instance != null)
                {
                    ShurikenBridgeManager.Instance.RefreshForceFields();
                    ShurikenBridgeManager.Instance.SyncBucketMaterialsFromSource();
                }
                SceneView.RepaintAll();
            }

            if (GUILayout.Button(emitter.IsPlayingOnGpu ? "Stop GPU" : "Play / Restart GPU", GUILayout.Height(26f)))
            {
                foreach (UnityEngine.Object obj in targets)
                {
                    if (obj is ShurikenBridgeEmitter e)
                    {
                        if (e.IsPlayingOnGpu) e.Stop();
                        else e.Play(restart: true);
                    }
                }
                SceneView.RepaintAll();
            }

            if (GUILayout.Button("Convert All Scene", GUILayout.Height(26f)))
            {
                ConvertAllSceneParticleSystemsToShurikenBridge();
            }
            if (GUILayout.Button("Revert Scene", GUILayout.Height(26f)))
            {
                RevertAllSceneToNativeShuriken();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4f);
            ShurikenSceneViewDebugMode currentSceneMode = ShurikenBridgeManager.PlayModeSceneViewDebugMode;
            ShurikenSceneViewDebugMode nextSceneMode = (ShurikenSceneViewDebugMode)EditorGUILayout.EnumPopup(
                new GUIContent(
                    "Play Mode Scene View (Global)",
                    "Editor-only Play Mode Scene View debug mode (automatically forced Off in all Player Builds).\n" +
                    "- Off: Max FPS (Scene View skipped)\n" +
                    "- GpuParticles: High FPS GPU preview + click picker\n" +
                    "- NativeShuriken: 100% native CPU Shuriken in Scene View while Game View renders GPU ShurikenBridge"),
                currentSceneMode);
            if (nextSceneMode != currentSceneMode)
            {
                ShurikenBridgeManager.PlayModeSceneViewDebugMode = nextSceneMode;
            }

            if (nextSceneMode == ShurikenSceneViewDebugMode.NativeShuriken)
            {
                EditorGUILayout.HelpBox(
                    "NativeShuriken Scene View Debug is ACTIVE: Scene View renders 100% native CPU Shuriken (native click/box-select/gizmos) while Game View renders GPU ShurikenBridge. Note: CPU simulation runs in Editor while this mode is active; forced Off in Player Builds.",
                    MessageType.Info);
            }

            if (emitter.NativeParticleSystem != null &&
                emitter.NativeParticleSystem.collision.enabled &&
                emitter.NativeParticleSystem.collision.type == ParticleSystemCollisionType.World)
            {
                EditorGUILayout.Space(4f);
                EditorGUILayout.HelpBox(
                    "Collision Type is set to 'World'. ShurikenBridge runs Analytical Plane Collision (zero PhysX CPU cost): switch Collision Type to 'Planes' to assign up to 6 custom walls/floors, or leave as-is to use the automatic y = 0 ground bounce plane.",
                    MessageType.None);
            }

            if (ShurikenBridgeManager.Instance != null &&
                emitter.IsHijackActive &&
                emitter.DesiredGpuParticleCount > ShurikenBridgeManager.Instance.ParticlesPerEmitter)
            {
                int currentCap = ShurikenBridgeManager.Instance.ParticlesPerEmitter;
                int neededCap = Mathf.Clamp(Mathf.NextPowerOfTwo(emitter.DesiredGpuParticleCount), 8, 1024);
                EditorGUILayout.Space(4f);
                EditorGUILayout.HelpBox(
                    $"This emitter requests ~{emitter.DesiredGpuParticleCount} concurrent particles, which is currently clamped to the Manager's per-emitter slot cap ({currentCap}).",
                    MessageType.Info);
                if (GUILayout.Button($"Expand Manager Per-Emitter Cap to {neededCap}", GUILayout.Height(22f)))
                {
                    ShurikenBridgeManager.Instance.RequestParticlesPerEmitterBudget(neededCap, forceExpand: true);
                    emitter.MarkDirty();
                    emitter.SyncToManagerIfDirty();
                    SceneView.RepaintAll();
                }
            }

            DrawCustomShaderAutoPatcherIfNeeded(emitter);
        }

        private static void DrawTelemetryAndModulesBox(ShurikenBridgeEmitter emitter)
        {
            ShurikenBridgeManager manager = ShurikenBridgeManager.Instance;
            bool isHijacked = emitter.IsHijackActive;

            Material activeMat = emitter.GpuParticleMaterial != null
                ? emitter.GpuParticleMaterial
                : (emitter.NativeParticleRenderer != null ? emitter.NativeParticleRenderer.sharedMaterial : null);

            ShurikenShaderCompatibilityMode compatMode = ShurikenBridgeManager.ClassifyMaterialCompatibility(
                activeMat,
                out Shader bridgedShader);

            string shaderDesc;
            if (activeMat == null || activeMat.shader == null)
            {
                shaderDesc = "Default ShurikenBridge Fallback Shader";
            }
            else if (compatMode == ShurikenShaderCompatibilityMode.DirectMegaMesh)
            {
                shaderDesc = $"Direct Mega-Mesh ({activeMat.shader.name}) — 0-Copy Single MDI";
            }
            else if (compatMode == ShurikenShaderCompatibilityMode.AutoBridgedStandardShader)
            {
                string targetName = bridgedShader != null ? bridgedShader.name : "URP/Particles/*";
                shaderDesc = $"Auto-Bridged ({activeMat.shader.name} -> {targetName})";
            }
            else
            {
                shaderDesc = $"Native unity_ParticleInstanceData ({activeMat.shader.name})";
            }

            string activeFeatures = BuildActiveGpuModulesSummary(emitter.NativeParticleSystem);

            MessageType msgType = isHijacked ? MessageType.Info : MessageType.Warning;
            string statusLine;
            if (emitter.IsAutoFallback)
            {
                statusLine = $"ShurikenBridge AUTO-FALLBACK to Native CPU Shuriken:\nReason: {emitter.FallbackReason}\n(Uncheck 'Auto Fallback Unsupported Modules' below to force GPU approximation)";
            }
            else if (isHijacked)
            {
                statusLine = "ShurikenBridge ACTIVE — CPU Shuriken Paused (0.000 ms CPU / 0 Native DrawCalls)";
            }
            else
            {
                statusLine = "ShurikenBridge BYPASSED — Running on Native Built-in CPU ParticleSystem";
            }

            string bucketInfo = manager != null
                ? $"Shader Mode: {shaderDesc}\n" +
                  $"Slot: #{emitter.RegisteredSlotIndex} | Bucket: #{emitter.AssignedBucketIndex} ({emitter.ResolveRenderShape()}) | LUT Profile: #{emitter.AssignedLutProfileIndex}\n" +
                  $"Active GPU Modules: {activeFeatures}\n" +
                  $"Global Batch: {manager.RegisteredEmitterCount} Emitters | {manager.ActiveMaterialBucketCount} Buckets | {manager.ActiveForceFieldCount} ForceFields"
                : $"Shader Mode: {shaderDesc}\nActive GPU Modules: {activeFeatures}\nShurikenBridgeManager not active in scene yet.";

            EditorGUILayout.HelpBox($"{statusLine}\n{bucketInfo}", msgType);
        }

        private static string BuildActiveGpuModulesSummary(ParticleSystem ps)
        {
            if (ps == null) return "None";
            StringBuilder sb = new StringBuilder(128);
            sb.Append("Main");
            if (ps.main.prewarm) sb.Append("(Prewarm)");
            if (ps.main.simulationSpace == ParticleSystemSimulationSpace.Local) sb.Append("(Local)");
            if (ps.emission.burstCount > 0) sb.Append(", Burst");
            if (ps.colorBySpeed.enabled || ps.sizeBySpeed.enabled || ps.rotationBySpeed.enabled) sb.Append(", BySpeed");
            if (ps.noise.enabled) sb.Append(", Noise");
            if (ps.collision.enabled) sb.Append($", Collision({Mathf.Max(1, ps.collision.planeCount)}P)");
            if (ps.externalForces.enabled) sb.Append(", ExternalForces");
            return sb.ToString();
        }

        private static void DrawCustomShaderAutoPatcherIfNeeded(ShurikenBridgeEmitter emitter)
        {
            Material activeMat = emitter.GpuParticleMaterial != null
                ? emitter.GpuParticleMaterial
                : (emitter.NativeParticleRenderer != null ? emitter.NativeParticleRenderer.sharedMaterial : null);

            if (activeMat == null || activeMat.shader == null)
            {
                return;
            }

            string assetPath = AssetDatabase.GetAssetPath(activeMat.shader);
            if (string.IsNullOrEmpty(assetPath) ||
                !assetPath.EndsWith(".shader", StringComparison.OrdinalIgnoreCase) ||
                !assetPath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                return;
            }

            if (activeMat.shader.name.StartsWith("ShurikenBridge/", StringComparison.Ordinal))
            {
                return;
            }

            string fullPath = Path.GetFullPath(assetPath);
            if (!File.Exists(fullPath))
            {
                return;
            }

            int shaderId = activeMat.shader.GetInstanceID();
            long writeTicksUtc = File.GetLastWriteTimeUtc(fullPath).Ticks;
            bool needsPatch;

            if (s_ShaderPatchCheckCache.TryGetValue(shaderId, out ShaderPatchCacheEntry cached) &&
                cached.LastWriteTicksUtc == writeTicksUtc)
            {
                needsPatch = cached.NeedsPatch;
            }
            else
            {
                string source = File.ReadAllText(fullPath);
                needsPatch = source.IndexOf("ParticleInstancingSetup", StringComparison.Ordinal) < 0 &&
                             source.IndexOf("_BucketBaseCommand", StringComparison.Ordinal) < 0;
                s_ShaderPatchCheckCache[shaderId] = new ShaderPatchCacheEntry
                {
                    LastWriteTicksUtc = writeTicksUtc,
                    NeedsPatch = needsPatch
                };
            }

            if (!needsPatch)
            {
                return;
            }

            EditorGUILayout.Space(4f);
            EditorGUILayout.HelpBox(
                $"Custom Shader '{activeMat.shader.name}' ({assetPath}) does not contain '#pragma instancing_options procedural:ParticleInstancingSetup' yet.",
                MessageType.Warning);

            if (GUILayout.Button($"1-Click Auto-Patch '{activeMat.shader.name}' for ShurikenBridge", GUILayout.Height(24f)))
            {
                AutoPatchCustomShaderFile(fullPath, assetPath);
                s_ShaderPatchCheckCache.Remove(shaderId);
            }
        }

        private static void AutoPatchCustomShaderFile(string fullPath, string assetPath)
        {
            string source = File.ReadAllText(fullPath);
            int pragmaVertexIdx = source.IndexOf("#pragma vertex", StringComparison.Ordinal);
            if (pragmaVertexIdx < 0)
            {
                Debug.LogWarning($"[ShurikenBridge] Could not locate '#pragma vertex' in {assetPath}.");
                return;
            }

            int lineEnd = source.IndexOf('\n', pragmaVertexIdx);
            if (lineEnd < 0) lineEnd = source.Length;

            string includePath = File.Exists(Path.GetFullPath("Assets/ShurikenBridge/Shaders/ShurikenBridgeCustomShaderInclude.hlsl"))
                ? "Assets/ShurikenBridge/Shaders/ShurikenBridgeCustomShaderInclude.hlsl"
                : "Packages/com.shurikenbridge.core/Shaders/ShurikenBridgeCustomShaderInclude.hlsl";

            string injection =
                "\n            #pragma multi_compile_instancing\n" +
                "            #pragma instancing_options procedural:ParticleInstancingSetup\n" +
                $"            #include \"{includePath}\"";

            string patched = source.Insert(lineEnd, injection);
            File.WriteAllText(fullPath, patched);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            Debug.Log($"[ShurikenBridge] Successfully injected GPU Particle Instancing pragmas into '{assetPath}'.");
        }

        [MenuItem("Tools/ShurikenBridge/Convert All Scene ParticleSystems to ShurikenBridge", false, 20)]
        public static void ConvertAllSceneParticleSystemsToShurikenBridge()
        {
            ParticleSystem[] allSystems = UnityEngine.Object.FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None);

            // Identify any ParticleSystem referenced as a SubEmitter target so we preserve its native parent-child trigger link
            HashSet<ParticleSystem> subEmitterTargets = new HashSet<ParticleSystem>();
            for (int i = 0; i < allSystems.Length; i++)
            {
                ParticleSystem ps = allSystems[i];
                if (ps == null || !ps.subEmitters.enabled) continue;
                int subCount = ps.subEmitters.subEmittersCount;
                for (int s = 0; s < subCount; s++)
                {
                    ParticleSystem targetSub = ps.subEmitters.GetSubEmitterSystem(s);
                    if (targetSub != null)
                    {
                        subEmitterTargets.Add(targetSub);
                    }
                }
            }

            int addedCount = 0;
            int skippedUnsupportedCount = 0;

            for (int i = 0; i < allSystems.Length; i++)
            {
                ParticleSystem ps = allSystems[i];
                if (ps == null)
                {
                    continue;
                }

                ParticleSystemRenderer psr = ps.GetComponent<ParticleSystemRenderer>();
                if (subEmitterTargets.Contains(ps) || ShurikenBridgeEmitter.CheckUnsupportedModules(ps, psr, out _))
                {
                    skippedUnsupportedCount++;
                    continue;
                }

                if (!ps.TryGetComponent<ShurikenBridgeEmitter>(out ShurikenBridgeEmitter emitter))
                {
                    emitter = Undo.AddComponent<ShurikenBridgeEmitter>(ps.gameObject);
                    addedCount++;
                }

                emitter.SetHijackEnabled(true);
                emitter.MarkDirty(forceRebakeLut: true);
            }

            SceneView.RepaintAll();
            Debug.Log($"[ShurikenBridge] Converted scene ParticleSystems: {addedCount} newly attached, {skippedUnsupportedCount} skipped (Trails/SubEmitters/MeshShape left on native CPU Shuriken for 100% visual fidelity).");
        }

        [MenuItem("Tools/ShurikenBridge/Revert All Scene ShurikenBridge Emitters to Native", false, 21)]
        public static void RevertAllSceneToNativeShuriken()
        {
            ShurikenBridgeEmitter[] emitters = UnityEngine.Object.FindObjectsByType<ShurikenBridgeEmitter>(FindObjectsSortMode.None);
            int removedCount = 0;

            for (int i = 0; i < emitters.Length; i++)
            {
                ShurikenBridgeEmitter emitter = emitters[i];
                if (emitter == null) continue;

                emitter.SetHijackEnabled(false);
                Undo.DestroyObjectImmediate(emitter);
                removedCount++;
            }

            SceneView.RepaintAll();
            Debug.Log($"[ShurikenBridge] Reverted {removedCount} ShurikenBridgeEmitters back to pure native Unity ParticleSystems.");
        }
    }
}
#endif
