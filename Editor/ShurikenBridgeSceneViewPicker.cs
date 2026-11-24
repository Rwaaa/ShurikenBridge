#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ShurikenBridge.Editor
{
    /// <summary>
    /// Editor-only Scene View individual GPU particle picker & debug highlighter.
    /// Allows clicking ANY individual flying GPU particle in Scene View (in Edit Mode or during Play Mode
    /// when ShurikenBridgeManager.RenderInPlayModeSceneView is enabled) to select its owning
    /// ShurikenBridgeEmitter GameObject — just like Unity's native built-in Shuriken ParticleSystem!
    /// - Lives strictly inside ShurikenBridge.Editor.asmdef (0 bytes compiled into Player Builds).
    /// - Zero per-frame cost when idle; runs 2-stage Broad-Phase + Exact GPU-matched Narrow-Phase picking only on mouse click.
    /// - Never intercepts Transform Gizmo drags, marquee box selection, or Alt camera navigation.
    /// </summary>
    [InitializeOnLoad]
    public static class ShurikenBridgeSceneViewPicker
    {
        private struct PickedParticleDebugInfo
        {
            public bool Valid;
            public ShurikenBridgeEmitter Emitter;
            public int EmitterSlot;
            public uint LocalParticleIndex;
            public Vector3 WorldPos;
            public Vector3 WorldVel;
            public float Radius;
            public float Age;
            public float Life;
        }

        private static bool s_TrackingClick;
        private static Vector2 s_MouseDownPos;
        private static PickedParticleDebugInfo s_LastPicked;

        static ShurikenBridgeSceneViewPicker()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            SceneView.duringSceneGui += OnSceneGUI;
        }

        private static void OnSceneGUI(SceneView sceneView)
        {
            ShurikenBridgeManager manager = ShurikenBridgeManager.CurrentInstance;
            if (manager == null || !manager.IsSupportedAndReady || manager.RegisteredEmitterCount <= 0)
            {
                s_LastPicked.Valid = false;
                return;
            }

            if (Application.isPlaying &&
                ShurikenBridgeManager.PlayModeSceneViewDebugMode != ShurikenSceneViewDebugMode.GpuParticles)
            {
                s_LastPicked.Valid = false;
                return;
            }

            Event evt = Event.current;
            if (evt == null)
            {
                return;
            }

            if (evt.type == EventType.MouseDown && evt.button == 0)
            {
                // Ignore Alt camera orbit, active pan/zoom tools, and clicks directly on Transform Gizmo handles
                if (!evt.alt &&
                    Tools.viewTool == ViewTool.None &&
                    GUIUtility.hotControl == 0 &&
                    HandleUtility.nearestControl == 0)
                {
                    s_TrackingClick = true;
                    s_MouseDownPos = evt.mousePosition;
                }
                else
                {
                    s_TrackingClick = false;
                }
            }
            else if (evt.type == EventType.MouseUp && evt.button == 0)
            {
                // Note: Do NOT require GUIUtility.hotControl == 0 on MouseUp because Unity's built-in
                // RectSelection holds hotControl between MouseDown and MouseUp during every SceneView click.
                bool wasCleanClick = s_TrackingClick &&
                                     !evt.alt &&
                                     (evt.mousePosition - s_MouseDownPos).sqrMagnitude < 16f;
                s_TrackingClick = false;

                if (wasCleanClick)
                {
                    TryPickParticleInSceneView(manager, sceneView, evt);
                }
            }
            else if (evt.type == EventType.Repaint)
            {
                DrawPickedParticleDebugOverlay(manager, sceneView);
            }
        }

        private static void TryPickParticleInSceneView(ShurikenBridgeManager manager, SceneView sceneView, Event evt)
        {
            Ray ray = HandleUtility.GUIPointToWorldRay(evt.mousePosition);
            float globalTime = Application.isPlaying ? Time.time : (float)Time.realtimeSinceStartup;

            // Check if Unity's native SceneView picker already hit an opaque GameObject closer to the camera
            float nativeSolidHitDist = float.MaxValue;
            GameObject nativePicked = HandleUtility.PickGameObject(evt.mousePosition, false);
            if (nativePicked != null && nativePicked.GetComponent<ShurikenBridgeEmitter>() == null)
            {
                if (TryEstimateGameObjectRayDistance(nativePicked, ray, out float solidDist))
                {
                    nativeSolidHitDist = solidDist;
                }
            }

            IReadOnlyList<ShurikenBridgeEmitter> emitters = manager.EditorRegisteredEmitters;
            int emitterCount = emitters.Count;
            ShurikenBridgeCurveLutBaker lutBaker = manager.LutBaker;
            int forceFieldCount = manager.ActiveForceFieldCount;
            Camera sceneCam = sceneView != null ? sceneView.camera : null;

            bool foundHit = false;
            float bestRayT = nativeSolidHitDist;
            PickedParticleDebugInfo bestInfo = default;

            // Stage 1: Broad-Phase Ray vs Conservative Emitter Particle Cloud Bounding Sphere
            for (int e = 0; e < emitterCount; e++)
            {
                ShurikenBridgeEmitter emitter = emitters[e];
                if (emitter == null)
                {
                    continue;
                }

                if (!manager.EditorTryGetEmitterDescriptor(e, out GpuEmitterDescriptor desc) ||
                    desc.ActiveParticleCount == 0u)
                {
                    continue;
                }

                float cloudRadius = ComputeConservativeCloudRadius(in desc);
                Vector3 cloudCenter = ComputeConservativeCloudCenter(in desc);
                if (!RayIntersectsSphere(ray, cloudCenter, cloudRadius, bestRayT))
                {
                    continue;
                }

                // Stage 2: Narrow-Phase Exact GPU-Matched Analytical Particle Raycast
                uint activeParticles = desc.ActiveParticleCount;
                for (uint p = 0u; p < activeParticles; p++)
                {
                    if (!EvaluateParticleExact(
                            manager,
                            lutBaker,
                            forceFieldCount,
                            (uint)e,
                            p,
                            in desc,
                            globalTime,
                            out Vector3 worldPos,
                            out Vector3 worldVel,
                            out float particleRadius,
                            out float age,
                            out float life))
                    {
                        continue;
                    }

                    // Ensure even small/distant particles have a comfortable minimum screen-space pick radius
                    float minScreenRadius = sceneCam != null
                        ? Mathf.Max(0.06f, HandleUtility.GetHandleSize(worldPos) * 0.085f)
                        : 0.12f;
                    float pickRadius = Mathf.Max(particleRadius * 1.15f, minScreenRadius);

                    if (RaySphereHitDistance(ray, worldPos, pickRadius, out float hitT) && hitT < bestRayT)
                    {
                        bestRayT = hitT;
                        foundHit = true;
                        bestInfo = new PickedParticleDebugInfo
                        {
                            Valid = true,
                            Emitter = emitter,
                            EmitterSlot = e,
                            LocalParticleIndex = p,
                            WorldPos = worldPos,
                            WorldVel = worldVel,
                            Radius = particleRadius,
                            Age = age,
                            Life = life
                        };
                    }
                }
            }

            if (!foundHit || bestInfo.Emitter == null)
            {
                if (nativePicked == null)
                {
                    s_LastPicked.Valid = false;
                }
                return;
            }

            s_LastPicked = bestInfo;
            SelectEmitterGameObject(bestInfo.Emitter.gameObject, evt.shift || evt.control || evt.command);
            // Release RectSelection's hotControl and consume MouseUp so Unity does not immediately deselect on empty air
            GUIUtility.hotControl = 0;
            evt.Use();
            sceneView.Repaint();
        }

        private static void SelectEmitterGameObject(GameObject targetGo, bool additiveToggle)
        {
            if (!additiveToggle)
            {
                Selection.activeGameObject = targetGo;
                return;
            }

            UnityEngine.Object[] current = Selection.objects;
            List<UnityEngine.Object> list = new List<UnityEngine.Object>(current.Length + 1);
            bool alreadySelected = false;
            for (int i = 0; i < current.Length; i++)
            {
                if (current[i] == targetGo)
                {
                    alreadySelected = true;
                    continue;
                }
                list.Add(current[i]);
            }

            if (!alreadySelected)
            {
                list.Add(targetGo);
            }
            Selection.objects = list.ToArray();
        }

        private static void DrawPickedParticleDebugOverlay(ShurikenBridgeManager manager, SceneView sceneView)
        {
            if (!s_LastPicked.Valid || s_LastPicked.Emitter == null || Selection.activeGameObject != s_LastPicked.Emitter.gameObject)
            {
                s_LastPicked.Valid = false;
                return;
            }

            int slot = s_LastPicked.Emitter.RegisteredSlotIndex;
            if (!manager.EditorTryGetEmitterDescriptor(slot, out GpuEmitterDescriptor desc))
            {
                return;
            }

            float globalTime = Application.isPlaying ? Time.time : (float)Time.realtimeSinceStartup;
            Vector3 pPos = s_LastPicked.WorldPos;
            Vector3 pVel = s_LastPicked.WorldVel;
            float pRadius = s_LastPicked.Radius;
            float age = s_LastPicked.Age;
            float life = s_LastPicked.Life;

            if (EvaluateParticleExact(
                    manager,
                    manager.LutBaker,
                    manager.ActiveForceFieldCount,
                    (uint)slot,
                    s_LastPicked.LocalParticleIndex,
                    in desc,
                    globalTime,
                    out Vector3 livePos,
                    out Vector3 liveVel,
                    out float liveRadius,
                    out float liveAge,
                    out float liveLife))
            {
                pPos = livePos;
                pVel = liveVel;
                pRadius = liveRadius;
                age = liveAge;
                life = liveLife;
            }

            Camera cam = sceneView != null ? sceneView.camera : null;
            Vector3 normal = cam != null ? (cam.transform.position - pPos).normalized : Vector3.up;
            float ringRadius = Mathf.Max(pRadius * 1.15f, HandleUtility.GetHandleSize(pPos) * 0.12f);

            Color prevColor = Handles.color;
            Handles.color = new Color(0.2f, 0.95f, 1.0f, 0.95f);
            Handles.DrawWireDisc(pPos, normal, ringRadius);
            Handles.color = new Color(1.0f, 0.85f, 0.25f, 0.75f);
            Handles.DrawDottedLine(desc.WorldPosition, pPos, 3.0f);

            string label = $"{s_LastPicked.Emitter.name}\n" +
                           $"Slot #{slot} | Particle #{s_LastPicked.LocalParticleIndex} | Age {age:F2}/{life:F2}s | Speed {pVel.magnitude:F1} m/s";
            Handles.Label(pPos + Vector3.up * (ringRadius * 1.35f), label);
            Handles.color = prevColor;
        }

        private static bool TryEstimateGameObjectRayDistance(GameObject go, Ray ray, out float outDist)
        {
            outDist = float.MaxValue;
            if (go.TryGetComponent(out Collider col) && col.Raycast(ray, out RaycastHit hit, 10000f))
            {
                outDist = hit.distance;
                return true;
            }
            if (go.TryGetComponent(out Renderer rend) && rend.bounds.IntersectRay(ray, out float bDist) && bDist > 0f)
            {
                outDist = bDist;
                return true;
            }
            return false;
        }

        private static Vector3 ComputeConservativeCloudCenter(in GpuEmitterDescriptor desc)
        {
            float maxLife = Mathf.Max(0.1f, desc.StartLifetimeMax);
            float gravY = -9.81f * desc.GravityModifier;
            float midY = 0.25f * gravY * maxLife * maxLife;
            return desc.WorldPosition + new Vector3(0f, Mathf.Clamp(midY, -25f, 25f), 0f);
        }

        private static float ComputeConservativeCloudRadius(in GpuEmitterDescriptor desc)
        {
            float maxLife = Mathf.Max(0.1f, desc.StartLifetimeMax);
            float maxScale = Mathf.Max(Mathf.Abs(desc.WorldScale.x), Mathf.Max(Mathf.Abs(desc.WorldScale.y), Mathf.Abs(desc.WorldScale.z)));
            float shapeExtent = (Mathf.Max(desc.ShapeRadius + desc.ShapeDonutRadius, desc.ShapeBoxScale.magnitude * 0.5f) + desc.ShapeLocalOffset.magnitude) * Mathf.Max(0.1f, maxScale);
            float velTravel = (Mathf.Max(0f, desc.StartSpeedMax) + desc.LinearVelocityAndForce.magnitude) * maxLife * 1.35f;
            float gravTravel = 0.5f * Mathf.Abs(9.81f * desc.GravityModifier) * maxLife * maxLife;
            float noiseTravel = Mathf.Abs(desc.NoiseStrength) * maxLife * 1.5f;
            float extTravel = Mathf.Abs(desc.ExternalForceMultiplier) > 0.001f ? (4.0f * maxLife * maxLife) : 0f;
            float maxSize = Mathf.Max(desc.StartSizeMin3D.x, Mathf.Max(desc.StartSizeMin3D.y, desc.StartSizeMin3D.z)) * Mathf.Max(1f, desc.StartSizeMaxRatio);
            return shapeExtent + velTravel + gravTravel + noiseTravel + extTravel + maxSize + 2.0f;
        }

        private static bool RayIntersectsSphere(Ray ray, Vector3 center, float radius, float maxDist)
        {
            Vector3 oc = center - ray.origin;
            float proj = Vector3.Dot(oc, ray.direction);
            if (proj + radius < 0f || proj - radius > maxDist)
            {
                return false;
            }
            float distSq = Vector3.Dot(oc, oc) - proj * proj;
            return distSq <= radius * radius;
        }

        private static bool RaySphereHitDistance(Ray ray, Vector3 center, float radius, out float hitT)
        {
            hitT = float.MaxValue;
            Vector3 oc = ray.origin - center;
            float b = Vector3.Dot(oc, ray.direction);
            float c = Vector3.Dot(oc, oc) - radius * radius;
            float disc = b * b - c;
            if (disc < 0f)
            {
                return false;
            }
            float sqrtDisc = Mathf.Sqrt(disc);
            float t = -b - sqrtDisc;
            if (t < 0f)
            {
                t = -b + sqrtDisc;
                if (t < 0f)
                {
                    return false;
                }
            }
            hitT = t;
            return true;
        }

        // =========================================================================
        // Exact CPU mirror of ShurikenBridgeSimulation.compute (PcgHash + Physics)
        // Executed ONLY on MouseUp for the ~2..15 Broad-Phase candidate emitters.
        // =========================================================================

        private static uint PcgHash(uint input)
        {
            unchecked
            {
                uint state = input * 747796405u + 2891336453u;
                uint word = ((state >> (int)((state >> 28) + 4u)) ^ state) * 277803737u;
                return (word >> 22) ^ word;
            }
        }

        private static float Hash01(uint seed)
        {
            return (PcgHash(seed) & 0x00FFFFFFu) * (1.0f / 16777215.0f);
        }

        private static Vector3 HashUnitVector3(uint seed)
        {
            float z = Hash01(seed) * 2.0f - 1.0f;
            float a = Hash01(seed + 19937u) * 6.28318530718f;
            float r = Mathf.Sqrt(Mathf.Max(0.0f, 1.0f - z * z));
            return new Vector3(r * Mathf.Cos(a), z, r * Mathf.Sin(a));
        }

        private static Vector3 RotateByQuaternion(Vector3 v, Vector4 q)
        {
            Vector3 qxyz = new Vector3(q.x, q.y, q.z);
            Vector3 t = 2.0f * Vector3.Cross(qxyz, v);
            return v + q.w * t + Vector3.Cross(qxyz, t);
        }

        private static Vector3 SampleTurbulenceField(Vector3 p, float time, float freq)
        {
            Vector3 q = p * freq + new Vector3(time * 0.7f, time * 1.1f, time * 0.5f);
            float nx = Mathf.Sin(q.y * 1.7f + Mathf.Cos(q.z * 1.3f + time));
            float ny = Mathf.Cos(q.z * 1.5f + Mathf.Sin(q.x * 1.9f - time * 0.8f));
            float nz = Mathf.Sin(q.x * 1.3f - Mathf.Cos(q.y * 1.7f + time * 0.6f));
            return new Vector3(nx, ny, nz);
        }

        private static bool SolveAnalyticalPlaneHit(
            Vector3 p0,
            Vector3 v0,
            Vector3 acc,
            Vector4 plane,
            float radius,
            float maxT,
            out float outHitT)
        {
            outHitT = maxT + 1.0f;
            Vector3 n = new Vector3(plane.x, plane.y, plane.z);
            float s0 = Vector3.Dot(n, p0) + plane.w - radius;
            float vn = Vector3.Dot(n, v0);
            float an = Vector3.Dot(n, acc);

            if (s0 <= 0.0f && vn < -0.001f)
            {
                outHitT = 0.0005f;
                return true;
            }

            if (Mathf.Abs(an) < 0.0001f)
            {
                if (vn < -0.0001f && s0 > 0.0f)
                {
                    float tHit = -s0 / vn;
                    if (tHit > 0.0005f && tHit < maxT)
                    {
                        outHitT = tHit;
                        return true;
                    }
                }
                return false;
            }

            float halfA = 0.5f * an;
            float disc = vn * vn - 4.0f * halfA * s0;
            if (disc < 0.0f)
            {
                return false;
            }

            float sqrtD = Mathf.Sqrt(disc);
            float inv2A = 1.0f / an;
            float t1 = (-vn - sqrtD) * inv2A;
            float t2 = (-vn + sqrtD) * inv2A;

            float bestT = maxT + 1.0f;
            if (t1 > 0.0005f && t1 < bestT) bestT = t1;
            if (t2 > 0.0005f && t2 < bestT) bestT = t2;

            if (bestT < maxT)
            {
                outHitT = bestT;
                return true;
            }
            return false;
        }

        private static bool EvaluateParticleExact(
            ShurikenBridgeManager manager,
            ShurikenBridgeCurveLutBaker lutBaker,
            int forceFieldCount,
            uint emitterIdx,
            uint localParticleIdx,
            in GpuEmitterDescriptor emitter,
            float globalTime,
            out Vector3 outWorldPos,
            out Vector3 outWorldVel,
            out float outRadius,
            out float outAge,
            out float outLife)
        {
            outWorldPos = Vector3.zero;
            outWorldVel = Vector3.zero;
            outRadius = 0.1f;
            outAge = 0f;
            outLife = 1f;

            uint flags0 = emitter.Flags0;
            uint flags1 = emitter.Flags1;

            bool isLooping = (flags0 & (1u << 0)) != 0u;
            bool useLocalSpace = (flags0 & (1u << 1)) != 0u;
            bool isPrewarm = (flags0 & (1u << 2)) != 0u;
            bool noiseEnabled = (flags0 & (1u << 3)) != 0u;
            bool collisionEnabled = (flags0 & (1u << 7)) != 0u;
            bool externalForcesEnabled = (flags0 & (1u << 8)) != 0u;
            bool sizeBySpeedEnabled = (flags0 & (1u << 10)) != 0u;

            uint baseSeed;
            unchecked
            {
                baseSeed = emitter.RandomSeed ^ (emitterIdx * 747796405u + localParticleIdx * 2891336453u);
            }

            float life = Mathf.Max(0.05f, Mathf.Lerp(emitter.StartLifetimeMin, emitter.StartLifetimeMax, Hash01(baseSeed + 1u)));
            float simSpeed = Mathf.Max(0.0f, emitter.SimulationSpeed);
            float emitterTime = (globalTime - emitter.EmitterStartTime) * simSpeed - emitter.StartDelay;
            if (isPrewarm && isLooping)
            {
                emitterTime += Mathf.Max(emitter.Duration, life);
            }
            if (emitterTime < 0.0f)
            {
                return false;
            }

            uint burstCount = (flags1 >> 16) & 0xFFu;
            uint rateSlots = (emitter.ActiveParticleCount > burstCount) ? (emitter.ActiveParticleCount - burstCount) : 0u;
            bool isBurstParticle = (burstCount > 0u) && (localParticleIdx >= rateSlots);

            float age;
            uint cycleIdx;

            if (isBurstParticle)
            {
                float burstElapsed = emitterTime - emitter.BurstTime;
                if (burstElapsed < 0.0f) return false;
                float interval = (emitter.BurstRepeatInterval > 0.01f) ? emitter.BurstRepeatInterval : Mathf.Max(emitter.Duration, life);
                float cycleFloat = Mathf.Floor(burstElapsed / interval);
                cycleIdx = (uint)Mathf.Max(0.0f, cycleFloat);
                if (!isLooping && emitter.BurstCycleCount > 0.5f && cycleFloat >= emitter.BurstCycleCount) return false;
                age = burstElapsed - cycleFloat * interval;
                if (age >= life) return false;
            }
            else
            {
                float spawnPhase = (localParticleIdx / Mathf.Max(1.0f, rateSlots)) * life;
                float elapsed = emitterTime - spawnPhase;
                if (elapsed < 0.0f) return false;
                if (!isLooping && elapsed >= emitter.Duration + life) return false;
                float cycleFloat = Mathf.Floor(elapsed / life);
                cycleIdx = (uint)Mathf.Max(0.0f, cycleFloat);
                if (!isLooping && cycleIdx > 0u && (cycleFloat * life + spawnPhase) > emitter.Duration) return false;
                age = elapsed - cycleFloat * life;
            }

            uint cycleSeed;
            unchecked
            {
                cycleSeed = baseSeed ^ (cycleIdx * 1664525u + 1013904223u);
            }

            uint shapeType = (flags0 >> 16) & 0x0Fu;
            Vector3 localSpawnPos;
            Vector3 localEmitDir;

            float u1 = Hash01(cycleSeed + 3u);
            float u2 = Hash01(cycleSeed + 5u);
            float u3 = Hash01(cycleSeed + 7u);
            float thickness = Mathf.Clamp01(emitter.ShapeRadiusThickness);

            if (shapeType == 0u) // Cone (XY base, emits along +Z)
            {
                float angle = u1 * 6.28318530718f;
                float rNorm = Mathf.Lerp(1.0f, Mathf.Sqrt(u2), thickness);
                float r = rNorm * emitter.ShapeRadius;
                float s = Mathf.Sin(angle);
                float c = Mathf.Cos(angle);
                localSpawnPos = new Vector3(c * r, s * r, 0.0f);
                float tanCone = Mathf.Tan(Mathf.Clamp(emitter.ShapeAngleRad, 0.0f, 1.45f));
                localEmitDir = new Vector3(c * rNorm * tanCone, s * rNorm * tanCone, 1.0f).normalized;
            }
            else if (shapeType == 1u) // Sphere
            {
                Vector3 dir = HashUnitVector3(cycleSeed + 11u);
                localEmitDir = dir;
                float rNorm = Mathf.Lerp(1.0f, Mathf.Pow(u1, 0.3333333f), thickness);
                localSpawnPos = dir * (rNorm * emitter.ShapeRadius);
            }
            else if (shapeType == 2u) // Hemisphere (Dome along +Z)
            {
                Vector3 dir = HashUnitVector3(cycleSeed + 13u);
                dir.z = Mathf.Abs(dir.z);
                localEmitDir = dir.normalized;
                float rNorm = Mathf.Lerp(1.0f, Mathf.Pow(u1, 0.3333333f), thickness);
                localSpawnPos = localEmitDir * (rNorm * emitter.ShapeRadius);
            }
            else if (shapeType == 3u) // Box (Emits along +Z)
            {
                localSpawnPos = Vector3.Scale(new Vector3(u1 - 0.5f, u2 - 0.5f, u3 - 0.5f), emitter.ShapeBoxScale);
                localEmitDir = Vector3.forward;
            }
            else if (shapeType == 4u) // Circle (XY plane radial)
            {
                float angle = u1 * 6.28318530718f;
                float rNorm = Mathf.Lerp(1.0f, Mathf.Sqrt(u2), thickness);
                float r = rNorm * emitter.ShapeRadius;
                float s = Mathf.Sin(angle);
                float c = Mathf.Cos(angle);
                localSpawnPos = new Vector3(c * r, s * r, 0.0f);
                localEmitDir = new Vector3(c, s, 0.0f);
            }
            else if (shapeType == 5u) // Edge (X segment, emits +Y)
            {
                localSpawnPos = new Vector3((u1 * 2.0f - 1.0f) * emitter.ShapeRadius, 0.0f, 0.0f);
                localEmitDir = Vector3.up;
            }
            else // Donut (XY ring around Z axis, radial outward from tube)
            {
                float ringAngle = u1 * 6.28318530718f;
                float tubeAngle = u2 * 6.28318530718f;
                float tubeR = Mathf.Lerp(1.0f, Mathf.Sqrt(u3), thickness) * Mathf.Max(0.01f, emitter.ShapeDonutRadius);
                Vector3 ringDir = new Vector3(Mathf.Cos(ringAngle), Mathf.Sin(ringAngle), 0.0f);
                Vector3 tubeOffset = ringDir * (Mathf.Cos(tubeAngle) * tubeR) + new Vector3(0.0f, 0.0f, Mathf.Sin(tubeAngle) * tubeR);
                localSpawnPos = ringDir * emitter.ShapeRadius + tubeOffset;
                localEmitDir = tubeOffset.sqrMagnitude > 0.000001f ? tubeOffset.normalized : ringDir;
            }

            localSpawnPos += emitter.ShapeLocalOffset;

            Vector3 worldSpawnPos = emitter.WorldPosition + RotateByQuaternion(Vector3.Scale(localSpawnPos, emitter.WorldScale), emitter.WorldRotationQuat);
            Vector3 worldEmitDir = RotateByQuaternion(localEmitDir, emitter.WorldRotationQuat).normalized;

            int lutProfile = (int)((flags0 >> 20) & 0x7Fu);
            int lutBaseRow = lutProfile * 4;
            float normAge = Mathf.Clamp01(age / life);
            Color lutCurves = lutBaker != null ? lutBaker.EditorSampleLutRow(lutBaseRow + 1, normAge) : new Color(1f, 1f, 0f, 1f);

            float startSpeed = Mathf.Lerp(emitter.StartSpeedMin, emitter.StartSpeedMax, Hash01(cycleSeed + 17u));
            float effectiveSpeed = startSpeed * Mathf.Max(0.05f, lutCurves.g);
            float dragFactor = 1.0f / (1.0f + Mathf.Max(0.0f, emitter.DragDamping) * age);
            Vector3 initialVel = worldEmitDir * (effectiveSpeed * dragFactor);

            Vector3 linearVelForce = useLocalSpace
                ? RotateByQuaternion(emitter.LinearVelocityAndForce, emitter.WorldRotationQuat)
                : emitter.LinearVelocityAndForce;
            Vector3 gravityAcc = new Vector3(0.0f, -9.81f * emitter.GravityModifier, 0.0f);

            Vector3 startSizeVec = emitter.StartSizeMin3D * Mathf.Lerp(1.0f, Mathf.Max(0.01f, emitter.StartSizeMaxRatio), Hash01(cycleSeed + 23u));
            float approxRadius = Mathf.Max(startSizeVec.x, Mathf.Max(startSizeVec.y, startSizeVec.z)) * 0.45f * Mathf.Max(0.1f, lutCurves.r);

            Vector3 worldPos;
            Vector3 worldVel;

            if (collisionEnabled)
            {
                uint planeCount = (flags1 >> 24) & 0xFFu;
                Vector3 curPos = worldSpawnPos;
                Vector3 curVel = initialVel + linearVelForce;
                float remAge = age;
                float effectiveLife = life;

                for (int bounceIter = 0; bounceIter < 3; bounceIter++)
                {
                    float earliestT = remAge + 1.0f;
                    Vector4 hitPlane = new Vector4(0f, 1f, 0f, 0f);
                    bool foundHit = false;

                    if (planeCount <= 1u)
                    {
                        if (SolveAnalyticalPlaneHit(curPos, curVel, gravityAcc, emitter.PrimaryCollisionPlane, approxRadius, remAge, out float tHit))
                        {
                            earliestT = tHit;
                            hitPlane = emitter.PrimaryCollisionPlane;
                            foundHit = true;
                        }
                    }
                    else
                    {
                        int clampedPlanes = Mathf.Min((int)planeCount, ShurikenBridgeManager.MaxCollisionPlanesPerEmitter);
                        for (int plIdx = 0; plIdx < clampedPlanes; plIdx++)
                        {
                            Vector4 pl = manager.EditorGetCollisionPlane((int)emitterIdx, plIdx);
                            if (SolveAnalyticalPlaneHit(curPos, curVel, gravityAcc, pl, approxRadius, earliestT, out float tHit))
                            {
                                earliestT = tHit;
                                hitPlane = pl;
                                foundHit = true;
                            }
                        }
                    }

                    if (!foundHit)
                    {
                        break;
                    }

                    curPos = curPos + curVel * earliestT + 0.5f * gravityAcc * (earliestT * earliestT);
                    curVel = curVel + gravityAcc * earliestT;

                    Vector3 n = new Vector3(hitPlane.x, hitPlane.y, hitPlane.z);
                    Vector3 vNormal = Vector3.Dot(curVel, n) * n;
                    Vector3 vTangent = curVel - vNormal;
                    curVel = vTangent * Mathf.Clamp01(1.0f - emitter.CollisionDampen) - vNormal * Mathf.Max(0.0f, emitter.CollisionBounce);
                    curPos += n * 0.002f;
                    remAge -= earliestT;

                    effectiveLife -= life * Mathf.Clamp01(emitter.CollisionLifetimeLoss);
                    if (age >= effectiveLife)
                    {
                        return false;
                    }
                }

                worldPos = curPos + curVel * remAge + 0.5f * gravityAcc * (remAge * remAge);
                worldVel = curVel + gravityAcc * remAge;
                life = effectiveLife;
            }
            else
            {
                worldPos = worldSpawnPos + initialVel * age + linearVelForce * age + 0.5f * gravityAcc * (age * age);
                worldVel = initialVel + linearVelForce + gravityAcc * age;
            }

            if (Mathf.Abs(emitter.RadialAndOrbitalSwirl) > 0.001f)
            {
                Vector3 rel = worldPos - emitter.WorldPosition;
                float swirlAngle = emitter.RadialAndOrbitalSwirl * age;
                float s = Mathf.Sin(swirlAngle);
                float c = Mathf.Cos(swirlAngle);
                float rx = rel.x * c - rel.z * s;
                float rz = rel.x * s + rel.z * c;
                worldPos += new Vector3(rx - rel.x, 0.0f, rz - rel.z);
                worldVel += new Vector3(-rel.z, 0.0f, rel.x) * emitter.RadialAndOrbitalSwirl;
            }

            if (noiseEnabled && emitter.NoiseStrength > 0.001f)
            {
                Vector3 turb = SampleTurbulenceField(worldSpawnPos + worldPos * 0.35f, globalTime, Mathf.Max(0.1f, emitter.NoiseFrequency));
                float turbAmp = emitter.NoiseStrength * lutCurves.a;
                worldPos += turb * (turbAmp * age * 0.65f);
                worldVel += turb * turbAmp;
            }

            if (externalForcesEnabled && forceFieldCount > 0 && Mathf.Abs(emitter.ExternalForceMultiplier) > 0.001f)
            {
                float extMult = emitter.ExternalForceMultiplier;
                int ffLimit = Mathf.Min(forceFieldCount, ShurikenBridgeManager.MaxExternalForceFields);
                for (int f = 0; f < ffLimit; f++)
                {
                    if (!manager.EditorTryGetForceField(f, out GpuForceFieldDescriptor ff) || ff.EnabledFlag < 0.5f)
                    {
                        continue;
                    }
                    Vector3 rel = worldPos - ff.Position;
                    float distSq = rel.sqrMagnitude;
                    if (distSq <= ff.EndRange * ff.EndRange && distSq >= ff.StartRange * ff.StartRange)
                    {
                        float dist = Mathf.Sqrt(Mathf.Max(0.0001f, distSq));
                        float falloff = Mathf.Clamp01(1.0f - (dist - ff.StartRange) / Mathf.Max(0.001f, ff.EndRange - ff.StartRange));
                        Vector3 fieldForce = RotateByQuaternion(ff.DirectionForce, ff.RotationQuat);
                        if (Mathf.Abs(ff.GravityStrength) > 0.001f)
                        {
                            fieldForce += (-rel / dist) * ff.GravityStrength;
                        }
                        if (Mathf.Abs(ff.RotationSpeed) > 0.001f)
                        {
                            Vector3 fieldUp = RotateByQuaternion(Vector3.up, ff.RotationQuat);
                            fieldForce += Vector3.Cross(fieldUp, rel) * ff.RotationSpeed;
                        }
                        Vector3 appliedAcc = fieldForce * (falloff * extMult);
                        worldPos += appliedAcc * (0.5f * age * age);
                        worldVel += appliedAcc * age;
                    }
                }
            }

            float sizeSpeedMult = 1.0f;
            if (sizeBySpeedEnabled && lutBaker != null)
            {
                float speedT = Mathf.Clamp01((worldVel.magnitude - emitter.SpeedRangeMin) / Mathf.Max(0.001f, emitter.SpeedRangeMax - emitter.SpeedRangeMin));
                sizeSpeedMult = Mathf.Max(0.0f, lutBaker.EditorSampleLutRow(lutBaseRow + 3, speedT).r);
            }

            Vector3 currentSize3D = startSizeVec * Mathf.Max(0.0f, lutCurves.r) * sizeSpeedMult;
            float maxAxisSize = Mathf.Max(currentSize3D.x, Mathf.Max(currentSize3D.y, currentSize3D.z));
            if (maxAxisSize <= 0.0005f)
            {
                return false;
            }

            outWorldPos = worldPos;
            outWorldVel = worldVel;
            outRadius = maxAxisSize * 0.5f;
            outAge = age;
            outLife = life;
            return true;
        }
    }
}
#endif
