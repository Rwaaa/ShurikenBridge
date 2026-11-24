using System;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace ShurikenBridge.Samples
{
    /// <summary>
    /// Standalone 3-Phase A/B Benchmark comparing 2,048 built-in CPU Shuriken ParticleSystems (131,072 particles)
    /// against ShurikenBridge across 4 distinct Materials (URP/Lit, URP/Particles/Unlit, ShurikenBridge/AdditiveBillboard, URP/Particles/Lit)
    /// with live GPU Plane Bounce Collision, Color/Size by Speed, Multi-Burst, and ParticleSystemForceField External Forces!
    /// Drop this component onto any GameObject in a Unity 2022.3 LTS+ or Unity 6 URP scene and press Play.
    /// </summary>
    [AddComponentMenu("ShurikenBridge/Samples/Factory 2048-Emitter Benchmark")]
    public sealed class ShurikenBridgeBenchmarkSample : MonoBehaviour
    {
        private enum Phase
        {
            Phase1_NativeWarmup = 0,
            Phase1_NativeSample = 1,
            Phase2_GpuWideWarmup = 2,
            Phase2_GpuWideSample = 3,
            Phase3_GpuFocusedWarmup = 4,
            Phase3_GpuFocusedSample = 5,
            Done = 6
        }

        private struct Metrics
        {
            public double DeltaSum;
            public long MainThreadNsSum;
            public long DrawCallsSum;
            public long BatchesSum;
            public long SetPassSum;
            public uint VisibleParticles;
            public uint Lod0;
            public uint Lod1;
            public uint Lod2;
            public ulong TrueTriangles;
            public int Frames;
        }

        [Header("Benchmark Configuration")]
        [Range(128, 4096)] public int EmitterCount = 2048;
        [Range(16, 256)] public int ParticlesPerEmitter = 64;
        [Range(10, 90)] public int WarmupFrames = 25;
        [Range(30, 180)] public int SampleFrames = 90;
        public bool EnableFloorBounceCollision = true;
        public bool EnableGlobalWindForceField = true;

        private ShurikenBridgeManager _manager;
        private Transform _root;
        private GameObject _forceFieldGo;
        private GameObject _floorPlaneGo;
        private readonly List<ShurikenBridgeEmitter> _emitters = new List<ShurikenBridgeEmitter>(2048);

        private Material _matSmokeUrpLit;
        private Material _matSparkUrpParticleUnlit;
        private Material _matFlameDirectBillboard;
        private Material _matDebrisUrpParticleLit;
        private Mesh _sphereMesh;
        private Mesh _cubeMesh;
        private Camera _cam;

        private ProfilerRecorder _recMain;
        private ProfilerRecorder _recDraws;
        private ProfilerRecorder _recBatches;
        private ProfilerRecorder _recSetPass;

        private Phase _phase;
        private int _frameCounter;
        private double _phaseSeconds;
        private Metrics _m1;
        private Metrics _m2;
        private Metrics _m3;

        private void Start()
        {
            _cam = Camera.main != null ? Camera.main : FindFirstObjectByType<Camera>();
            _recMain = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 1);
            _recDraws = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count", 1);
            _recBatches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count", 1);
            _recSetPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count", 1);

            SetupManagerAndScene();
            BeginPhase1();
        }

        private void OnDestroy()
        {
            if (_recMain.Valid) _recMain.Dispose();
            if (_recDraws.Valid) _recDraws.Dispose();
            if (_recBatches.Valid) _recBatches.Dispose();
            if (_recSetPass.Valid) _recSetPass.Dispose();

            if (_root != null) Destroy(_root.gameObject);
            if (_forceFieldGo != null) Destroy(_forceFieldGo);
            if (_floorPlaneGo != null) Destroy(_floorPlaneGo);
            if (_matSmokeUrpLit != null) Destroy(_matSmokeUrpLit);
            if (_matSparkUrpParticleUnlit != null) Destroy(_matSparkUrpParticleUnlit);
            if (_matFlameDirectBillboard != null) Destroy(_matFlameDirectBillboard);
            if (_matDebrisUrpParticleLit != null) Destroy(_matDebrisUrpParticleLit);
        }

        private void SetupManagerAndScene()
        {
            _manager = ShurikenBridgeManager.Instance;
            if (_manager == null)
            {
                GameObject mgrGo = new GameObject("ShurikenBridgeManager");
                _manager = mgrGo.AddComponent<ShurikenBridgeManager>();
            }

            int maxPerCmd = Mathf.Clamp((EmitterCount * ParticlesPerEmitter) / 3, 16384, 65536);
            _manager.ConfigureCapacities(null, EmitterCount + 64, ParticlesPerEmitter, maxPerCmd, 26f, 56f, _cam);

            if (EnableGlobalWindForceField)
            {
                _forceFieldGo = new GameObject("ShurikenBridge_GlobalWindForceField");
                _forceFieldGo.transform.position = Vector3.zero;
                ParticleSystemForceField ff = _forceFieldGo.AddComponent<ParticleSystemForceField>();
                ff.endRange = 200f;
                ff.directionX = new ParticleSystem.MinMaxCurve(0.85f);
                ff.rotationSpeed = new ParticleSystem.MinMaxCurve(0.15f);
                _manager.RefreshForceFields();
            }

            if (EnableFloorBounceCollision)
            {
                _floorPlaneGo = new GameObject("ShurikenBridge_CollisionFloorPlane");
                _floorPlaneGo.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            }

            Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
            Shader urpPartUnlit = Shader.Find("Universal Render Pipeline/Particles/Unlit") ?? urpLit;
            Shader urpPartLit = Shader.Find("Universal Render Pipeline/Particles/Lit") ?? urpLit;
            Shader sbAdd = Shader.Find("ShurikenBridge/AdditiveBillboard") ?? urpPartUnlit;

            _matSmokeUrpLit = new Material(urpLit) { name = "SB_Sample_0_Smoke_URPLit" };
            _matSmokeUrpLit.SetColor("_BaseColor", new Color(0.75f, 0.78f, 0.84f, 1f));

            _matSparkUrpParticleUnlit = new Material(urpPartUnlit) { name = "SB_Sample_1_Spark_URPParticlesUnlit" };
            _matSparkUrpParticleUnlit.SetColor("_BaseColor", new Color(1f, 0.78f, 0.28f, 1f));
            _matSparkUrpParticleUnlit.SetFloat("_Surface", 1f);
            _matSparkUrpParticleUnlit.SetFloat("_Blend", 2f);
            _matSparkUrpParticleUnlit.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            _matSparkUrpParticleUnlit.SetFloat("_DstBlend", (float)BlendMode.One);
            _matSparkUrpParticleUnlit.SetFloat("_ZWrite", 0f);
            _matSparkUrpParticleUnlit.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            _matSparkUrpParticleUnlit.renderQueue = (int)RenderQueue.Transparent;

            _matFlameDirectBillboard = new Material(sbAdd) { name = "SB_Sample_2_Flame_ShurikenBridgeDirect" };
            _matFlameDirectBillboard.SetColor("_BaseColor", new Color(1f, 0.45f, 0.14f, 1f));
            _matFlameDirectBillboard.SetFloat("_HdrIntensity", 2.5f);

            _matDebrisUrpParticleLit = new Material(urpPartLit) { name = "SB_Sample_3_Debris_URPParticlesLit" };
            _matDebrisUrpParticleLit.SetColor("_BaseColor", new Color(0.88f, 0.52f, 0.24f, 1f));

            _sphereMesh = Resources.GetBuiltinResource<Mesh>("New-Sphere.fbx");
            if (_sphereMesh == null) _sphereMesh = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");

            _cubeMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");

            _root = new GameObject($"ShurikenBridgeSampleEmitters_{EmitterCount}").transform;
            int cols = 64;
            float sx = 2.0f, sz = 2.2f;
            float startX = -(cols - 1) * sx * 0.5f;
            float startZ = -34f;

            for (int i = 0; i < EmitterCount; i++)
            {
                int col = i % cols;
                int row = i / cols;
                int type = i & 3;

                GameObject go = new GameObject($"SB_Emitter_{i:D4}");
                go.transform.SetParent(_root, false);
                // Standard Unity ParticleSystem orientation: rotate -90 deg on X so local +Z points straight UP (+Y)
                go.transform.SetPositionAndRotation(
                    new Vector3(startX + col * sx, 0.6f, startZ + row * sz),
                    Quaternion.Euler(-90f, 0f, 0f));

                ParticleSystem ps = go.AddComponent<ParticleSystem>();
                ParticleSystemRenderer psr = go.GetComponent<ParticleSystemRenderer>();
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

                ParticleSystem.MainModule main = ps.main;
                main.playOnAwake = false;
                main.loop = true;
                main.prewarm = true;
                main.maxParticles = ParticlesPerEmitter;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                ps.useAutoRandomSeed = false;
                ps.randomSeed = (uint)(i * 9781 + 13);

                ParticleSystem.EmissionModule em = ps.emission;
                em.enabled = true;

                ParticleSystem.ShapeModule sh = ps.shape;
                sh.enabled = true;

                Material overrideMat = null;
                ShurikenRenderShape renderShape;

                if (type == 0)
                {
                    main.startLifetime = new ParticleSystem.MinMaxCurve(1.8f, 2.6f);
                    main.startSpeed = new ParticleSystem.MinMaxCurve(1.4f, 2.6f);
                    main.startSize = new ParticleSystem.MinMaxCurve(0.28f, 0.45f);
                    main.gravityModifier = -0.08f;
                    em.rateOverTime = ParticlesPerEmitter / 2f;
                    sh.shapeType = ParticleSystemShapeType.Cone;
                    sh.radius = 0.25f;
                    sh.angle = 16f;
                    ParticleSystem.ExternalForcesModule ext = ps.externalForces;
                    ext.enabled = EnableGlobalWindForceField;
                    ext.multiplier = 0.35f;
                    psr.renderMode = ParticleSystemRenderMode.Mesh;
                    psr.mesh = _sphereMesh;
                    psr.enableGPUInstancing = true;
                    psr.sharedMaterial = _matSmokeUrpLit;
                    renderShape = ShurikenRenderShape.MeshSphere;
                }
                else if (type == 1)
                {
                    main.startLifetime = new ParticleSystem.MinMaxCurve(0.8f, 1.3f);
                    main.startSpeed = new ParticleSystem.MinMaxCurve(4.5f, 8.5f);
                    main.startSize = new ParticleSystem.MinMaxCurve(0.12f, 0.22f);
                    main.gravityModifier = 1.35f;
                    em.rateOverTime = ParticlesPerEmitter / 1.0f;
                    sh.shapeType = ParticleSystemShapeType.Hemisphere;
                    sh.radius = 0.18f;
                    ParticleSystem.CollisionModule colMod = ps.collision;
                    colMod.enabled = EnableFloorBounceCollision;
                    colMod.type = ParticleSystemCollisionType.Planes;
                    if (_floorPlaneGo != null) colMod.AddPlane(_floorPlaneGo.transform);
                    colMod.bounce = 0.65f;
                    colMod.dampen = 0.15f;
                    psr.renderMode = ParticleSystemRenderMode.Stretch;
                    psr.velocityScale = 0.15f;
                    psr.lengthScale = 2.2f;
                    psr.sharedMaterial = _matSparkUrpParticleUnlit;
                    renderShape = ShurikenRenderShape.StretchedBillboard;
                }
                else if (type == 2)
                {
                    main.startLifetime = new ParticleSystem.MinMaxCurve(0.9f, 1.5f);
                    main.startSpeed = new ParticleSystem.MinMaxCurve(1.8f, 3.4f);
                    main.startSize = new ParticleSystem.MinMaxCurve(0.32f, 0.55f);
                    main.gravityModifier = -0.22f;
                    em.rateOverTime = ParticlesPerEmitter / 1.15f;
                    sh.shapeType = ParticleSystemShapeType.Donut;
                    sh.radius = 0.32f;
                    sh.donutRadius = 0.12f;
                    psr.renderMode = ParticleSystemRenderMode.Billboard;
                    psr.sharedMaterial = _matSparkUrpParticleUnlit;
                    overrideMat = _matFlameDirectBillboard;
                    renderShape = ShurikenRenderShape.Billboard;
                }
                else
                {
                    main.startLifetime = new ParticleSystem.MinMaxCurve(0.95f, 1.65f);
                    main.startSpeed = new ParticleSystem.MinMaxCurve(3.2f, 6.0f);
                    main.startSize = new ParticleSystem.MinMaxCurve(0.18f, 0.32f);
                    main.gravityModifier = 1.65f;
                    em.rateOverTime = ParticlesPerEmitter / 1.2f;
                    sh.shapeType = ParticleSystemShapeType.Box;
                    sh.scale = new Vector3(0.45f, 0.2f, 0.45f);
                    ParticleSystem.CollisionModule colMod = ps.collision;
                    colMod.enabled = EnableFloorBounceCollision;
                    colMod.type = ParticleSystemCollisionType.Planes;
                    if (_floorPlaneGo != null) colMod.AddPlane(_floorPlaneGo.transform);
                    colMod.bounce = 0.55f;
                    colMod.dampen = 0.22f;
                    psr.renderMode = ParticleSystemRenderMode.Mesh;
                    psr.mesh = _cubeMesh;
                    psr.enableGPUInstancing = true;
                    psr.sharedMaterial = _matDebrisUrpParticleLit;
                    renderShape = ShurikenRenderShape.MeshCube;
                }

                ShurikenBridgeEmitter emitter = go.AddComponent<ShurikenBridgeEmitter>();
                emitter.ConfigureBridge(overrideMat, renderShape, enableDistanceThinning: true, hijackEnabled: false);
                _emitters.Add(emitter);
            }
        }

        private void BeginPhase1()
        {
            _phase = Phase.Phase1_NativeWarmup;
            _frameCounter = 0;
            _phaseSeconds = 0.0;
            SetCamera(new Vector3(0f, 38f, -68f), new Vector3(28f, 0f, 0f));
            SetHijackAll(false);
        }

        private void SetHijackAll(bool hijack)
        {
            _manager.GpuRenderingActive = hijack;
            for (int i = 0; i < _emitters.Count; i++)
            {
                _emitters[i].SetHijackEnabled(hijack);
            }
            if (hijack) _manager.SyncDirtyEmittersAndBuffers();
        }

        private void SetCamera(Vector3 pos, Vector3 euler)
        {
            if (_cam != null) _cam.transform.SetPositionAndRotation(pos, Quaternion.Euler(euler));
        }

        private void Update()
        {
            if (_phase == Phase.Done) return;

            _frameCounter++;
            _phaseSeconds += Time.unscaledDeltaTime;

            switch (_phase)
            {
                case Phase.Phase1_NativeWarmup:
                    if (_frameCounter >= WarmupFrames || (_frameCounter >= 5 && _phaseSeconds >= 1.5))
                        Advance(Phase.Phase1_NativeSample);
                    break;
                case Phase.Phase1_NativeSample:
                    Sample(ref _m1);
                    if (_frameCounter >= SampleFrames || (_frameCounter >= 15 && _phaseSeconds >= 3.5))
                    {
                        SetHijackAll(true);
                        Advance(Phase.Phase2_GpuWideWarmup);
                    }
                    break;
                case Phase.Phase2_GpuWideWarmup:
                    if (_frameCounter >= WarmupFrames || (_frameCounter >= 10 && _phaseSeconds >= 4.0))
                        Advance(Phase.Phase2_GpuWideSample);
                    break;
                case Phase.Phase2_GpuWideSample:
                    Sample(ref _m2);
                    if (_frameCounter >= SampleFrames || (_frameCounter >= 15 && _phaseSeconds >= 3.5))
                    {
                        _manager.ReadbackVisibleParticleStats(out _m2.VisibleParticles, out _m2.Lod0, out _m2.Lod1, out _m2.Lod2, out _m2.TrueTriangles);
                        SetCamera(new Vector3(-24f, 6.2f, -24f), new Vector3(13f, -42f, 0f));
                        Advance(Phase.Phase3_GpuFocusedWarmup);
                    }
                    break;
                case Phase.Phase3_GpuFocusedWarmup:
                    if (_frameCounter >= WarmupFrames || (_frameCounter >= 5 && _phaseSeconds >= 1.5))
                        Advance(Phase.Phase3_GpuFocusedSample);
                    break;
                case Phase.Phase3_GpuFocusedSample:
                    Sample(ref _m3);
                    if (_frameCounter >= SampleFrames || (_frameCounter >= 15 && _phaseSeconds >= 3.5))
                    {
                        _manager.ReadbackVisibleParticleStats(out _m3.VisibleParticles, out _m3.Lod0, out _m3.Lod1, out _m3.Lod2, out _m3.TrueTriangles);
                        LogReport();
                        SetCamera(new Vector3(0f, 38f, -68f), new Vector3(28f, 0f, 0f));
                        Advance(Phase.Done);
                    }
                    break;
            }
        }

        private void Advance(Phase next)
        {
            _phase = next;
            _frameCounter = 0;
            _phaseSeconds = 0.0;
        }

        private void Sample(ref Metrics m)
        {
            float dt = Time.unscaledDeltaTime;
            if (dt > 1.0f) return;
            m.DeltaSum += dt;
            if (_recMain.Valid) m.MainThreadNsSum += _recMain.LastValue;
            if (_recDraws.Valid) m.DrawCallsSum += _recDraws.LastValue;
            if (_recBatches.Valid) m.BatchesSum += _recBatches.LastValue;
            if (_recSetPass.Valid) m.SetPassSum += _recSetPass.LastValue;
            m.Frames++;
        }

        private void LogReport()
        {
            int n1 = Mathf.Max(1, _m1.Frames), n2 = Mathf.Max(1, _m2.Frames), n3 = Mathf.Max(1, _m3.Frames);
            double fps1 = 1000.0 / Math.Max(0.001, (_m1.DeltaSum / n1) * 1000.0);
            double fps2 = 1000.0 / Math.Max(0.001, (_m2.DeltaSum / n2) * 1000.0);
            double fps3 = 1000.0 / Math.Max(0.001, (_m3.DeltaSum / n3) * 1000.0);
            Debug.Log(
                $"[ShurikenBridge Standalone Sample Benchmark | Emitters: {EmitterCount:N0} | Max Particles: {EmitterCount * ParticlesPerEmitter:N0}]\n" +
                $"Phase 1 (Built-in Shuriken): {fps1:F1} FPS | MainThread: {(_m1.MainThreadNsSum / (double)n1) * 1e-6:F3} ms | DrawCalls: {_m1.DrawCallsSum / (double)n1:F1} | SetPass: {_m1.SetPassSum / (double)n1:F1}\n" +
                $"Phase 2 (ShurikenBridge Wide): {fps2:F1} FPS | MainThread: {(_m2.MainThreadNsSum / (double)n2) * 1e-6:F3} ms | DrawCalls: {_m2.DrawCallsSum / (double)n2:F1} | SetPass: {_m2.SetPassSum / (double)n2:F1} | Visible: {_m2.VisibleParticles:N0}\n" +
                $"Phase 3 (ShurikenBridge Focused): {fps3:F1} FPS | MainThread: {(_m3.MainThreadNsSum / (double)n3) * 1e-6:F3} ms | DrawCalls: {_m3.DrawCallsSum / (double)n3:F1} | SetPass: {_m3.SetPassSum / (double)n3:F1} | Visible: {_m3.VisibleParticles:N0}");
        }
    }
}
