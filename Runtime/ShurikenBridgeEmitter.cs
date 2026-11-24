using System;
using UnityEngine;

namespace ShurikenBridge
{
    /// <summary>
    /// Drop-in bridge component that hijacks Unity's built-in ParticleSystem (Shuriken) Inspector UI
    /// and executes 100% of simulation, analytical plane bounce collision, external force fields,
    /// frustum culling, and LOD routing on the GPU via ShurikenBridgeManager.
    /// - Keeps Unity's native ParticleSystem Inspector UI & Scene View gizmos 100% intact.
    /// - Pauses CPU Shuriken simulation (0.000 ms CPU / 0 native Draw Calls).
    /// - Automatically falls back to native ParticleSystem on platforms without Compute Shader support.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(ParticleSystem))]
    [AddComponentMenu("ShurikenBridge/Shuriken Bridge Emitter")]
    public sealed class ShurikenBridgeEmitter : MonoBehaviour
    {
        [Header("GPU Hijack & Material Routing")]
        [Tooltip("When enabled, pauses CPU Shuriken and renders 100% on the GPU via ShurikenBridgeManager.")]
        [SerializeField] private bool _hijackNativeParticleSystem = true;

        [Tooltip("Optional material override. Leave empty to use ParticleSystemRenderer.sharedMaterial (supports URP/Particles/*, URP/Lit, Shader Graph, and Custom HLSL shaders).")]
        [SerializeField] private Material _gpuParticleMaterial;

        [Tooltip("Override the inferred render shape (otherwise inferred automatically from ParticleSystemRenderer.renderMode & mesh).")]
        [SerializeField] private bool _overrideRenderShape;
        [SerializeField] private ShurikenRenderShape _customRenderShape = ShurikenRenderShape.Billboard;

        [Tooltip("Stochastically thins sub-pixel distant particles at LOD 1/2 to eliminate far overdraw.")]
        [SerializeField] private bool _enableDistanceThinning = true;

        [Tooltip("Automatically falls back to native CPU Shuriken when unsupported modules (Trails, SubEmitters, Lights, Trigger, Mesh Surface Shape, or RenderMode.None) are enabled.")]
        [SerializeField] private bool _autoFallbackUnsupportedModules = true;

        [Tooltip("Automatically polls runtime changes to emission.enabled, rateOverTime, startColor, and simulationSpeed without requiring manual MarkDirty() calls.")]
        [SerializeField] private bool _autoSyncRuntimeProperties = true;

        private static readonly Vector4[] s_ScratchCollisionPlanes = new Vector4[ShurikenBridgeManager.MaxCollisionPlanesPerEmitter];

        private ParticleSystem _particleSystem;
        private ParticleSystemRenderer _particleRenderer;
        private int _registeredSlotIndex = -1;
        private int _assignedBucketIndex = -1;
        private int _assignedLutProfileIndex;
        private uint _activeGpuParticleCount;
        private int _desiredGpuParticleCount;
        private float _emitterStartTime;
        private bool _isPlayingOnGpu = true;
        private bool _isDirty = true;
        private bool _pendingValidateRebake;
        private bool _nativeRendererSuppressed;
        private bool _hasAssignedCollisionPlaneTransforms;
        private bool _isAutoFallback;
        private string _fallbackReason;

        // Cached runtime lifecycle & property polling snapshot
        private bool _cachedIsLooping = true;
        private float _cachedDuration = 5f;
        private float _cachedStartDelay;
        private float _cachedStartLifetimeMax = 3f;
        private float _cachedSimulationSpeed = 1f;
        private bool _lastEmissionEnabled = true;
        private float _lastRateOverTimeMax = -1f;
        private Color _lastStartColor;
        private int _lastGameObjectLayer = -1;

        public int RegisteredSlotIndex
        {
            get => _registeredSlotIndex;
            internal set => _registeredSlotIndex = value;
        }

        public int AssignedBucketIndex => _assignedBucketIndex;
        public int AssignedLutProfileIndex => _assignedLutProfileIndex;
        public uint ActiveGpuParticleCount => _activeGpuParticleCount;
        public int DesiredGpuParticleCount => _desiredGpuParticleCount;
        public bool IsPlayingOnGpu => _isPlayingOnGpu;
        public bool IsAutoFallback => _isAutoFallback;
        public string FallbackReason => _fallbackReason;
        public bool AutoFallbackUnsupportedModules
        {
            get => _autoFallbackUnsupportedModules;
            set
            {
                if (_autoFallbackUnsupportedModules != value)
                {
                    _autoFallbackUnsupportedModules = value;
                    SetHijackEnabled(_hijackNativeParticleSystem);
                }
            }
        }
        public bool IsHijackActive => _hijackNativeParticleSystem && !_isAutoFallback && ShurikenBridgeManager.IsPlatformHardwareSupported;
        public ParticleSystem NativeParticleSystem => _particleSystem;
        public ParticleSystemRenderer NativeParticleRenderer => _particleRenderer;

        /// <summary>
        /// Equivalent to ParticleSystem.IsAlive(). Returns true while GPU particles are actively emitting or alive.
        /// Safe for object pooling systems checking when a one-shot VFX has finished.
        /// </summary>
        public bool IsAlive()
        {
            if (!IsHijackActive)
            {
                return _particleSystem != null && _particleSystem.IsAlive(true);
            }

            if (!_isPlayingOnGpu)
            {
                return false;
            }

            if (_cachedIsLooping)
            {
                return true;
            }

            float simSpeed = Mathf.Max(0.0001f, _cachedSimulationSpeed);
            float totalLife = (_cachedStartDelay + _cachedDuration + _cachedStartLifetimeMax) / simSpeed;
            float elapsed = (Application.isPlaying ? Time.time : 0f) - _emitterStartTime;
            return elapsed < totalLife;
        }

        /// <summary>
        /// Evaluates whether a ParticleSystem uses modules that cannot be replicated on the stateless GPU simulator.
        /// </summary>
        public static bool CheckUnsupportedModules(ParticleSystem ps, ParticleSystemRenderer psr, out string reason)
        {
            if (ps == null)
            {
                reason = "Missing ParticleSystem component.";
                return true;
            }

            if (psr != null && psr.renderMode == ParticleSystemRenderMode.None)
            {
                reason = "RenderMode.None (helper emitter without visual geometry).";
                return true;
            }

            if (ps.trails.enabled)
            {
                reason = "Trails module is enabled (ribbon/trail geometry requires native CPU Shuriken).";
                return true;
            }

            if (ps.subEmitters.enabled && ps.subEmitters.subEmittersCount > 0)
            {
                reason = "SubEmitters module is enabled.";
                return true;
            }

            if (ps.trigger.enabled)
            {
                reason = "Trigger module is enabled.";
                return true;
            }

            if (ps.lights.enabled)
            {
                reason = "Lights module is enabled.";
                return true;
            }

            if (ps.shape.enabled)
            {
                ParticleSystemShapeType st = ps.shape.shapeType;
                if (st == ParticleSystemShapeType.Mesh ||
                    st == ParticleSystemShapeType.MeshRenderer ||
                    st == ParticleSystemShapeType.SkinnedMeshRenderer)
                {
                    reason = $"ShapeType.{st} emits from arbitrary mesh surface.";
                    return true;
                }
            }

            reason = null;
            return false;
        }

        private void EvaluateAutoFallbackState()
        {
            CacheComponentsIfNeeded();
            if (_autoFallbackUnsupportedModules && CheckUnsupportedModules(_particleSystem, _particleRenderer, out string reason))
            {
                _isAutoFallback = true;
                _fallbackReason = reason;
            }
            else
            {
                _isAutoFallback = false;
                _fallbackReason = null;
            }
        }

        /// <summary>
        /// Starts or restarts GPU particle emission (equivalent to ParticleSystem.Play()).
        /// </summary>
        public void Play(bool restart = true)
        {
            if (restart || !_isPlayingOnGpu)
            {
                _emitterStartTime = Application.isPlaying ? Time.time : 0f;
            }
            _isPlayingOnGpu = true;
            if (!IsHijackActive && _particleSystem != null)
            {
                _particleSystem.Play(true);
            }
            MarkDirty();
        }

        /// <summary>
        /// Stops GPU particle emission (equivalent to ParticleSystem.Stop()).
        /// </summary>
        public void Stop()
        {
            _isPlayingOnGpu = false;
            if (!IsHijackActive && _particleSystem != null)
            {
                _particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
            MarkDirty();
        }

        /// <summary>
        /// Immediately restarts one-shot or looping GPU particle emission from t = 0.
        /// </summary>
        public void Restart()
        {
            Play(restart: true);
        }

        public Material GpuParticleMaterial
        {
            get => _gpuParticleMaterial;
            set
            {
                _gpuParticleMaterial = value;
                MarkDirty();
            }
        }

        public void ConfigureBridge(
            Material gpuMaterial,
            ShurikenRenderShape renderShape,
            bool enableDistanceThinning,
            bool hijackEnabled)
        {
            _gpuParticleMaterial = gpuMaterial;
            _overrideRenderShape = true;
            _customRenderShape = renderShape;
            _enableDistanceThinning = enableDistanceThinning;
            SetHijackEnabled(hijackEnabled);
            MarkDirty();
        }

        public void SetHijackEnabled(bool hijackEnabled)
        {
            _hijackNativeParticleSystem = hijackEnabled;
            EvaluateAutoFallbackState();

            if (!isActiveAndEnabled)
            {
                return;
            }

            if (IsHijackActive)
            {
                SuppressNativeCpuShuriken();
                EnsureManagerAndRegister();
                MarkDirty();
            }
            else
            {
                if (ShurikenBridgeManager.CurrentInstance != null)
                {
                    ShurikenBridgeManager.CurrentInstance.UnregisterEmitter(this);
                }
                RestoreNativeCpuShuriken();
            }
        }

        private void Awake()
        {
            EvaluateAutoFallbackState();
            _emitterStartTime = Application.isPlaying ? Time.time : 0f;
            if (Application.isPlaying && _particleSystem != null)
            {
                _isPlayingOnGpu = _particleSystem.main.playOnAwake || _particleSystem.isPlaying;
            }
            else
            {
                _isPlayingOnGpu = true;
            }
        }

        private void OnEnable()
        {
            if (!gameObject.scene.IsValid())
            {
                return;
            }

            EvaluateAutoFallbackState();
            _nativeRendererSuppressed = false;
            _emitterStartTime = Application.isPlaying ? Time.time : 0f;
            if (Application.isPlaying && _particleSystem != null)
            {
                _isPlayingOnGpu = _particleSystem.main.playOnAwake || _particleSystem.isPlaying;
            }
            else
            {
                _isPlayingOnGpu = true;
            }

            if (IsHijackActive)
            {
                SuppressNativeCpuShuriken(interceptExternalPlayCall: false);
                EnsureManagerAndRegister();
                MarkDirty();
            }
            else
            {
                RestoreNativeCpuShuriken();
            }
        }

        private void OnDisable()
        {
            if (ShurikenBridgeManager.CurrentInstance != null)
            {
                ShurikenBridgeManager.CurrentInstance.UnregisterEmitter(this);
            }
            if (_particleRenderer != null)
            {
                _particleRenderer.forceRenderingOff = false;
            }
        }

        private void OnValidate()
        {
            if (!gameObject.scene.IsValid())
            {
                return;
            }

            // Defer manager creation and renderer state mutations out of OnValidate to avoid Unity SendMessage/CheckConsistency warnings
            _isDirty = true;
            _pendingValidateRebake = true;
        }

        public void MarkDirty(bool forceRebakeLut = false)
        {
            _isDirty = true;
            if (forceRebakeLut && _particleSystem != null && ShurikenBridgeManager.Instance != null && ShurikenBridgeManager.Instance.LutBaker != null)
            {
                _assignedLutProfileIndex = ShurikenBridgeManager.Instance.LutBaker.GetOrBakeProfileRow(_particleSystem, true);
            }
        }

        private void CacheComponentsIfNeeded()
        {
            if (_particleSystem == null)
            {
                _particleSystem = GetComponent<ParticleSystem>();
            }
            if (_particleRenderer == null)
            {
                _particleRenderer = GetComponent<ParticleSystemRenderer>();
            }
        }

        private void EnsureManagerAndRegister()
        {
            if (!gameObject.scene.IsValid())
            {
                return;
            }

            ShurikenBridgeManager manager = ShurikenBridgeManager.Instance;
            if (manager == null)
            {
                GameObject mgrGo = new GameObject("ShurikenBridgeManager (Auto-Created)");
                if (!Application.isPlaying)
                {
                    mgrGo.hideFlags = HideFlags.DontSave;
                }
                manager = mgrGo.AddComponent<ShurikenBridgeManager>();
            }
            manager.RegisterEmitter(this);
        }

        private void SuppressNativeCpuShuriken(bool interceptExternalPlayCall = true)
        {
#if UNITY_EDITOR
            if (ShurikenBridgeManager.IsNativeSceneViewDebugActive)
            {
                SyncNativeSceneViewMirrorState();
                return;
            }
#endif
            if (_particleSystem != null)
            {
                bool isPlayingNative = _particleSystem.isPlaying;
                if (isPlayingNative || (!_nativeRendererSuppressed && _particleSystem.particleCount > 0))
                {
                    // If user gameplay code called ParticleSystem.Play() in Play Mode, seamlessly trigger GPU playback!
                    if (interceptExternalPlayCall && Application.isPlaying && isPlayingNative)
                    {
                        _isPlayingOnGpu = true;
                        _emitterStartTime = Time.time;
                        _isDirty = true;
                    }
                    _particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                }
            }
            // Use non-serialized Renderer.forceRenderingOff instead of Renderer.enabled so scene/prefab files are never dirtied in Edit Mode
            if (!_nativeRendererSuppressed && _particleRenderer != null)
            {
                if (!_particleRenderer.forceRenderingOff)
                {
                    _particleRenderer.forceRenderingOff = true;
                }
                _nativeRendererSuppressed = true;
            }
        }

#if UNITY_EDITOR
        /// <summary>
        /// Editor-only: mirrors playback into the native CPU ParticleSystem while NativeShuriken Scene View debug mode is active,
        /// or re-suppresses native CPU Shuriken immediately when switching back to Off / GpuParticles.
        /// </summary>
        internal void ApplySceneViewDebugMode(bool nativeSceneViewActive)
        {
            CacheComponentsIfNeeded();
            if (!IsHijackActive)
            {
                return;
            }

            if (nativeSceneViewActive)
            {
                _nativeRendererSuppressed = false;
                SyncNativeSceneViewMirrorState();
            }
            else
            {
                _nativeRendererSuppressed = false;
                if (_particleRenderer != null)
                {
                    _particleRenderer.forceRenderingOff = false;
                }
                SuppressNativeCpuShuriken(interceptExternalPlayCall: false);
            }
        }

        internal void SetNativeRendererForceRenderingOffForEditor(bool forceOff)
        {
            if (_particleRenderer != null && _particleRenderer.forceRenderingOff != forceOff)
            {
                _particleRenderer.forceRenderingOff = forceOff;
            }
        }

        private void SyncNativeSceneViewMirrorState()
        {
            if (_particleRenderer != null)
            {
                if (_particleRenderer.forceRenderingOff)
                {
                    _particleRenderer.forceRenderingOff = false;
                }
            }

            if (_particleSystem != null)
            {
                if (_isPlayingOnGpu)
                {
                    if (!_particleSystem.isPlaying)
                    {
                        _particleSystem.Play(true);
                    }
                }
                else
                {
                    if (_particleSystem.isPlaying || _particleSystem.particleCount > 0)
                    {
                        _particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    }
                }
            }
        }
#endif

        private void RestoreNativeCpuShuriken()
        {
            _nativeRendererSuppressed = false;
            if (_particleRenderer != null)
            {
                _particleRenderer.forceRenderingOff = false;
            }
            if (_particleSystem != null && Application.isPlaying && !_particleSystem.isPlaying)
            {
                _particleSystem.Play(true);
            }
        }

        internal void OnManagerReleased()
        {
            _registeredSlotIndex = -1;
            _assignedBucketIndex = -1;
            RestoreNativeCpuShuriken();
        }

        internal void OnManagerReenabled()
        {
            if (!isActiveAndEnabled || !gameObject.scene.IsValid())
            {
                return;
            }
            EvaluateAutoFallbackState();
            if (IsHijackActive)
            {
                _nativeRendererSuppressed = false;
                SuppressNativeCpuShuriken(interceptExternalPlayCall: false);
                EnsureManagerAndRegister();
                MarkDirty(forceRebakeLut: true);
            }
        }

        public ShurikenRenderShape ResolveRenderShape()
        {
            if (_overrideRenderShape)
            {
                return _customRenderShape;
            }

            if (_particleRenderer == null)
            {
                return ShurikenRenderShape.Billboard;
            }

            switch (_particleRenderer.renderMode)
            {
                case ParticleSystemRenderMode.Stretch:
                    return ShurikenRenderShape.StretchedBillboard;
                case ParticleSystemRenderMode.Mesh:
                    Mesh m = _particleRenderer.mesh;
                    if (m != null && m.name.IndexOf("Cube", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return ShurikenRenderShape.MeshCube;
                    }
                    return ShurikenRenderShape.MeshSphere;
                default:
                    return ShurikenRenderShape.Billboard;
            }
        }

        public void SyncToManagerIfDirty()
        {
            if (_pendingValidateRebake)
            {
                _pendingValidateRebake = false;
                EvaluateAutoFallbackState();
                if (!IsHijackActive)
                {
                    if (ShurikenBridgeManager.CurrentInstance != null && _registeredSlotIndex >= 0)
                    {
                        ShurikenBridgeManager.CurrentInstance.UnregisterEmitter(this);
                    }
                    RestoreNativeCpuShuriken();
                    return;
                }
                MarkDirty(forceRebakeLut: true);
            }

            if (!IsHijackActive || _registeredSlotIndex < 0)
            {
                return;
            }

            SuppressNativeCpuShuriken();

            // 1. Evaluate One-Shot (Non-Looping) completion & StopAction (Disable / Destroy / Callback)
            if (Application.isPlaying && _isPlayingOnGpu && !_cachedIsLooping && !IsAlive())
            {
                _isPlayingOnGpu = false;
                _isDirty = true;

                if (_particleSystem != null)
                {
                    ParticleSystemStopAction stopAction = _particleSystem.main.stopAction;
                    if (stopAction == ParticleSystemStopAction.Callback)
                    {
                        SendMessage("OnParticleSystemStopped", SendMessageOptions.DontRequireReceiver);
                    }
                    else if (stopAction == ParticleSystemStopAction.Disable)
                    {
                        gameObject.SetActive(false);
                        return;
                    }
                    else if (stopAction == ParticleSystemStopAction.Destroy)
                    {
                        Destroy(gameObject);
                    }
                }
            }

            // 2. Optional automatic polling of runtime emission/color/layer changes
            if (Application.isPlaying && _autoSyncRuntimeProperties && !_isDirty && _particleSystem != null)
            {
                ParticleSystem.MainModule main = _particleSystem.main;
                ParticleSystem.EmissionModule em = _particleSystem.emission;
                bool emEnabled = em.enabled;
                float rateMax = emEnabled ? em.rateOverTime.constantMax : 0f;
                Color startCol = main.startColor.color;
                float simSpd = main.simulationSpeed;
                int layer = gameObject.layer;

                if (emEnabled != _lastEmissionEnabled ||
                    Mathf.Abs(rateMax - _lastRateOverTimeMax) > 0.001f ||
                    startCol != _lastStartColor ||
                    Mathf.Abs(simSpd - _cachedSimulationSpeed) > 0.001f ||
                    layer != _lastGameObjectLayer)
                {
                    _isDirty = true;
                }
            }

            bool collisionPlaneMoved = (_hasAssignedCollisionPlaneTransforms || !Application.isPlaying) && CheckIfCollisionPlanesMoved();
            if (!_isDirty && !transform.hasChanged && !collisionPlaneMoved)
            {
                return;
            }

            transform.hasChanged = false;
            _isDirty = false;

            ShurikenBridgeManager manager = ShurikenBridgeManager.Instance;
            if (manager == null)
            {
                return;
            }

            CacheComponentsIfNeeded();
            if (_particleSystem == null)
            {
                return;
            }

            ShurikenRenderShape renderShape = ResolveRenderShape();
            Material targetMat = _gpuParticleMaterial != null
                ? _gpuParticleMaterial
                : (_particleRenderer != null ? _particleRenderer.sharedMaterial : null);

            if (targetMat == null)
            {
                targetMat = manager.GetFallbackMaterial(renderShape);
            }

            Mesh customMesh = null;
            float stretchMult = 0.28f;
            float maxStretch = 5.0f;

            if (_particleRenderer != null)
            {
                if (_particleRenderer.renderMode == ParticleSystemRenderMode.Stretch)
                {
                    stretchMult = Mathf.Max(0.05f, _particleRenderer.velocityScale + _particleRenderer.lengthScale * 0.1f);
                    maxStretch = Mathf.Max(1.2f, _particleRenderer.lengthScale * 2.2f);
                }
                else if (_particleRenderer.renderMode == ParticleSystemRenderMode.Mesh && !_overrideRenderShape)
                {
                    Mesh assignedMesh = _particleRenderer.mesh;
                    if (assignedMesh != null &&
                        !assignedMesh.name.Equals("Sphere", StringComparison.OrdinalIgnoreCase) &&
                        !assignedMesh.name.Equals("Cube", StringComparison.OrdinalIgnoreCase))
                    {
                        customMesh = assignedMesh;
                    }
                }
            }

            ParticleSystem.TextureSheetAnimationModule sheetMod = _particleSystem.textureSheetAnimation;
            int tilesX = (sheetMod.enabled && sheetMod.numTilesX > 0) ? sheetMod.numTilesX : 1;
            int tilesY = (sheetMod.enabled && sheetMod.numTilesY > 0) ? sheetMod.numTilesY : 1;
            int currentLayer = gameObject.layer;

            _assignedBucketIndex = manager.GetOrCreateMaterialBucket(
                targetMat,
                renderShape,
                customMesh,
                stretchMult,
                maxStretch,
                tilesX,
                tilesY,
                currentLayer);
            _assignedLutProfileIndex = manager.LutBaker != null
                ? manager.LutBaker.GetOrBakeProfileRow(_particleSystem, false)
                : 0;

            bool collapseLodToCommand0 = manager.IsBucketCollapsedToSingleCommand(_assignedBucketIndex);

            GpuEmitterDescriptor desc = ExtractDescriptorFromParticleSystem(
                _particleSystem,
                transform,
                (uint)_assignedBucketIndex,
                _assignedLutProfileIndex,
                manager,
                _enableDistanceThinning,
                collapseLodToCommand0,
                _isPlayingOnGpu,
                _emitterStartTime,
                out int extractedPlaneCount,
                out bool hasTransformPlanes,
                out int desiredParticleCount);

            _desiredGpuParticleCount = desiredParticleCount;

            // Update cached lifecycle & runtime property snapshot
            ParticleSystem.MainModule mSnap = _particleSystem.main;
            ParticleSystem.EmissionModule eSnap = _particleSystem.emission;
            _cachedIsLooping = mSnap.loop;
            _cachedDuration = desc.Duration;
            _cachedStartDelay = desc.StartDelay;
            _cachedStartLifetimeMax = desc.StartLifetimeMax;
            _cachedSimulationSpeed = desc.SimulationSpeed;
            _lastEmissionEnabled = eSnap.enabled;
            _lastRateOverTimeMax = _lastEmissionEnabled ? eSnap.rateOverTime.constantMax : 0f;
            _lastStartColor = mSnap.startColor.color;
            _lastGameObjectLayer = currentLayer;

            _hasAssignedCollisionPlaneTransforms = hasTransformPlanes;
            _activeGpuParticleCount = desc.ActiveParticleCount;
            manager.UpdateEmitterDescriptorAtSlot(_registeredSlotIndex, in desc);

            if (extractedPlaneCount > 1)
            {
                manager.UpdateCollisionPlanesAtSlot(_registeredSlotIndex, s_ScratchCollisionPlanes, extractedPlaneCount);
            }
        }

        private bool CheckIfCollisionPlanesMoved()
        {
            if (_particleSystem == null)
            {
                return false;
            }

            ParticleSystem.CollisionModule col = _particleSystem.collision;
            if (!col.enabled || col.planeCount <= 0)
            {
                return false;
            }

            bool moved = false;
            int count = Mathf.Min(col.planeCount, ShurikenBridgeManager.MaxCollisionPlanesPerEmitter);
            for (int p = 0; p < count; p++)
            {
                Transform pt = col.GetPlane(p);
                if (pt != null && pt.hasChanged)
                {
                    pt.hasChanged = false;
                    moved = true;
                }
            }
            return moved;
        }

        private static GpuEmitterDescriptor ExtractDescriptorFromParticleSystem(
            ParticleSystem ps,
            Transform tr,
            uint bucketIndex,
            int lutProfileIndex,
            ShurikenBridgeManager manager,
            bool enableDistanceThinning,
            bool collapseLodToCommand0,
            bool isPlayingOnGpu,
            float emitterStartTime,
            out int outCollisionPlaneCount,
            out bool outHasTransformPlanes,
            out int outDesiredTotalParticles)
        {
            ParticleSystem.MainModule main = ps.main;
            ParticleSystem.EmissionModule emission = ps.emission;
            ParticleSystem.ShapeModule shape = ps.shape;
            ParticleSystem.VelocityOverLifetimeModule velMod = ps.velocityOverLifetime;
            ParticleSystem.ForceOverLifetimeModule forceMod = ps.forceOverLifetime;
            ParticleSystem.LimitVelocityOverLifetimeModule limitVelMod = ps.limitVelocityOverLifetime;
            ParticleSystem.NoiseModule noiseMod = ps.noise;
            ParticleSystem.TextureSheetAnimationModule sheetMod = ps.textureSheetAnimation;
            ParticleSystem.CollisionModule colMod = ps.collision;
            ParticleSystem.ExternalForcesModule extMod = ps.externalForces;
            ParticleSystem.ColorBySpeedModule colSpeedMod = ps.colorBySpeed;
            ParticleSystem.SizeBySpeedModule sizeSpeedMod = ps.sizeBySpeed;
            ParticleSystem.RotationBySpeedModule rotSpeedMod = ps.rotationBySpeed;

            ExtractMinMaxFloat(main.startLifetime, 0.1f, 3.0f, out float lifeMin, out float lifeMax);
            ExtractMinMaxFloat(main.startSpeed, 0.0f, 5.0f, out float speedMin, out float speedMax);
            ExtractMinMaxFloat(main.gravityModifier, 0.0f, 0.0f, out float gravMin, out float gravMax);
            ExtractMinMaxFloat(main.startDelay, 0.0f, 0.0f, out float delayMin, out _);
            ExtractMinMaxColor(main.startColor, out Color colMin, out Color colMax);

            // 1. StartSize & StartSize3D
            Vector3 startSizeMin3D;
            float startSizeMaxRatio;
            if (main.startSize3D)
            {
                ExtractMinMaxFloat(main.startSizeX, 0.05f, 1f, out float sxMin, out float sxMax);
                ExtractMinMaxFloat(main.startSizeY, 0.05f, 1f, out float syMin, out float syMax);
                ExtractMinMaxFloat(main.startSizeZ, 0.05f, 1f, out float szMin, out float szMax);
                startSizeMin3D = new Vector3(Mathf.Max(0.01f, sxMin), Mathf.Max(0.01f, syMin), Mathf.Max(0.01f, szMin));
                float avgMin = (startSizeMin3D.x + startSizeMin3D.y + startSizeMin3D.z) / 3f;
                float avgMax = (Mathf.Max(0.01f, sxMax) + Mathf.Max(0.01f, syMax) + Mathf.Max(0.01f, szMax)) / 3f;
                startSizeMaxRatio = avgMax / Mathf.Max(0.001f, avgMin);
            }
            else
            {
                ExtractMinMaxFloat(main.startSize, 0.05f, 1.0f, out float sMin, out float sMax);
                sMin = Mathf.Max(0.01f, sMin);
                sMax = Mathf.Max(sMin, sMax);
                startSizeMin3D = new Vector3(sMin, sMin, sMin);
                startSizeMaxRatio = sMax / sMin;
            }

            // 2. StartRotation & StartRotation3D
            Vector3 startRotMin3D;
            float startRotMaxZ;
            if (main.startRotation3D)
            {
                ExtractMinMaxFloat(main.startRotationX, 0f, 0f, out float rxMin, out _);
                ExtractMinMaxFloat(main.startRotationY, 0f, 0f, out float ryMin, out _);
                ExtractMinMaxFloat(main.startRotationZ, 0f, 0f, out float rzMin, out float rzMax);
                startRotMin3D = new Vector3(rxMin, ryMin, rzMin);
                startRotMaxZ = rzMax;
            }
            else
            {
                ExtractMinMaxFloat(main.startRotation, 0f, 0f, out float rzMin, out float rzMax);
                startRotMin3D = new Vector3(0f, 0f, rzMin);
                startRotMaxZ = rzMax;
            }

            // 3. Emission RateOverTime + Synchronized Multi-Burst
            float avgLife = Mathf.Max(0.1f, (lifeMin + lifeMax) * 0.5f);
            float rate = emission.enabled ? EvaluateMaxFloat(emission.rateOverTime, 16f) : 0f;
            int rateParticles = Mathf.CeilToInt(rate * avgLife);

            int totalBurstParticles = 0;
            float burstTime = 0f;
            float burstRepeatInterval = 0f;
            float burstCycleCount = 1f;

            if (emission.enabled && emission.burstCount > 0)
            {
                for (int b = 0; b < emission.burstCount; b++)
                {
                    ParticleSystem.Burst burst = emission.GetBurst(b);
                    int bCount = Mathf.RoundToInt(EvaluateMaxFloat(burst.count, 0f));
                    totalBurstParticles += Mathf.Max(0, bCount);
                    if (b == 0)
                    {
                        burstTime = Mathf.Max(0f, burst.time);
                        burstRepeatInterval = Mathf.Max(0f, burst.repeatInterval);
                        burstCycleCount = burst.cycleCount;
                    }
                }
            }

            int desiredTotal = Mathf.Min(rateParticles + totalBurstParticles, main.maxParticles);
            outDesiredTotalParticles = desiredTotal;
            if (isPlayingOnGpu && manager != null && desiredTotal > manager.ParticlesPerEmitter)
            {
                manager.RequestParticlesPerEmitterBudget(desiredTotal);
            }
            int maxParticlesPerEmitterSlot = manager != null ? manager.ParticlesPerEmitter : ShurikenBridgeManager.DefaultParticlesPerEmitter;
            int clampedTotal = isPlayingOnGpu
                ? Mathf.Clamp(desiredTotal, 1, maxParticlesPerEmitterSlot)
                : 0;
            int clampedBurst = Mathf.Min(totalBurstParticles, clampedTotal);

            // 4. Emission Shape (Cone, Sphere, Hemisphere, Box, Circle, Edge, Donut + RadiusThickness + Local Offset/Rotation)
            ShurikenEmitterShape gpuShape = ShurikenEmitterShape.Cone;
            float shapeRadius = 0.5f;
            float shapeAngleRad = 25f * Mathf.Deg2Rad;
            float shapeRadiusThickness = 1.0f;
            float shapeDonutRadius = 0.2f;
            Vector3 shapeBoxScale = Vector3.one;
            Vector3 shapeLocalOffset = Vector3.zero;
            Quaternion composedRotation = tr.rotation;

            if (shape.enabled)
            {
                shapeRadius = Mathf.Max(0.01f, shape.radius);
                shapeAngleRad = Mathf.Clamp(shape.angle * Mathf.Deg2Rad, 0f, 1.45f);
                shapeRadiusThickness = Mathf.Clamp01(shape.radiusThickness);
                shapeDonutRadius = Mathf.Max(0.01f, shape.donutRadius);
                shapeBoxScale = shape.scale;
                shapeLocalOffset = shape.position;
                if (shape.rotation != Vector3.zero)
                {
                    composedRotation = tr.rotation * Quaternion.Euler(shape.rotation);
                }

                switch (shape.shapeType)
                {
                    case ParticleSystemShapeType.Sphere:
                        gpuShape = ShurikenEmitterShape.Sphere;
                        break;
                    case ParticleSystemShapeType.Hemisphere:
                        gpuShape = ShurikenEmitterShape.Hemisphere;
                        break;
                    case ParticleSystemShapeType.Box:
                    case ParticleSystemShapeType.BoxShell:
                    case ParticleSystemShapeType.BoxEdge:
                        gpuShape = ShurikenEmitterShape.Box;
                        break;
                    case ParticleSystemShapeType.Circle:
                        gpuShape = ShurikenEmitterShape.Circle;
                        break;
                    case ParticleSystemShapeType.SingleSidedEdge:
                        gpuShape = ShurikenEmitterShape.Edge;
                        break;
                    case ParticleSystemShapeType.Donut:
                        gpuShape = ShurikenEmitterShape.Donut;
                        break;
                    default:
                        gpuShape = ShurikenEmitterShape.Cone;
                        break;
                }
            }

            // 5. Velocity / Force / LimitVelocity / Noise
            Vector3 linearVelForce = Vector3.zero;
            float orbitalSwirl = 0f;
            if (velMod.enabled)
            {
                linearVelForce.x += EvaluateMaxFloat(velMod.x, 0f);
                linearVelForce.y += EvaluateMaxFloat(velMod.y, 0f);
                linearVelForce.z += EvaluateMaxFloat(velMod.z, 0f);
                orbitalSwirl += EvaluateMaxFloat(velMod.orbitalY, 0f);
            }
            if (forceMod.enabled)
            {
                linearVelForce.x += EvaluateMaxFloat(forceMod.x, 0f) * 0.5f;
                linearVelForce.y += EvaluateMaxFloat(forceMod.y, 0f) * 0.5f;
                linearVelForce.z += EvaluateMaxFloat(forceMod.z, 0f) * 0.5f;
            }

            float dragDamping = limitVelMod.enabled ? Mathf.Max(0f, limitVelMod.dampen * 2.5f) : 0f;
            float noiseStrength = noiseMod.enabled ? EvaluateMaxFloat(noiseMod.strength, 0f) : 0f;
            float noiseFreq = noiseMod.enabled ? Mathf.Max(0.05f, noiseMod.frequency) : 0.5f;

            // 6. By-Speed Range (ColorBySpeed / SizeBySpeed / RotationBySpeed)
            Vector2 speedRange = new Vector2(0f, 10f);
            if (colSpeedMod.enabled) speedRange = colSpeedMod.range;
            else if (sizeSpeedMod.enabled) speedRange = sizeSpeedMod.range;
            else if (rotSpeedMod.enabled) speedRange = rotSpeedMod.range;

            // 7. Analytical Plane Collision (Planes mode up to 6 planes, or default y=0 floor plane)
            bool collisionEnabled = colMod.enabled;
            float colBounce = collisionEnabled ? Mathf.Max(0f, EvaluateMaxFloat(colMod.bounce, 0.5f)) : 0f;
            float colDampen = collisionEnabled ? Mathf.Clamp01(EvaluateMaxFloat(colMod.dampen, 0.1f)) : 0f;
            float colLifeLoss = collisionEnabled ? Mathf.Clamp01(EvaluateMaxFloat(colMod.lifetimeLoss, 0f)) : 0f;
            Vector4 primaryPlane = new Vector4(0f, 1f, 0f, 0f); // Default horizontal floor y = 0
            outCollisionPlaneCount = 0;
            outHasTransformPlanes = false;

            if (collisionEnabled)
            {
                int foundPlanes = 0;
                int pLimit = Mathf.Min(colMod.planeCount, ShurikenBridgeManager.MaxCollisionPlanesPerEmitter);
                for (int p = 0; p < pLimit; p++)
                {
                    Transform planeTr = colMod.GetPlane(p);
                    if (planeTr == null)
                    {
                        continue;
                    }
                    Vector3 n = planeTr.up.normalized;
                    float d = -Vector3.Dot(n, planeTr.position);
                    s_ScratchCollisionPlanes[foundPlanes++] = new Vector4(n.x, n.y, n.z, d);
                }

                if (foundPlanes > 0)
                {
                    primaryPlane = s_ScratchCollisionPlanes[0];
                    outCollisionPlaneCount = foundPlanes;
                    outHasTransformPlanes = true;
                }
                else if (colMod.type == ParticleSystemCollisionType.World)
                {
                    // World mode fallback: horizontal ground plane y = 0 (zero PhysX CPU cost)
                    outCollisionPlaneCount = 1;
                }
                else
                {
                    // Planes mode with 0 assigned planes: match Native Shuriken 1:1 (no collision until a plane is assigned)
                    collisionEnabled = false;
                    outCollisionPlaneCount = 0;
                }
            }

            // 8. External Forces
            bool extEnabled = extMod.enabled;
            float extMultiplier = extEnabled ? EvaluateMaxFloat(extMod.multiplierCurve, extMod.multiplier) : 0f;

            int tilesX = (sheetMod.enabled && sheetMod.numTilesX > 0) ? sheetMod.numTilesX : 1;
            int tilesY = (sheetMod.enabled && sheetMod.numTilesY > 0) ? sheetMod.numTilesY : 1;

            bool useLocalSpace = main.simulationSpace == ParticleSystemSimulationSpace.Local;
            uint randomSeed = ps.randomSeed != 0u ? ps.randomSeed : (uint)(Mathf.Abs(ps.GetInstanceID()) + 1);

            uint flags0 = GpuEmitterDescriptor.PackFlags0(
                main.loop,
                useLocalSpace,
                main.prewarm,
                noiseMod.enabled,
                velMod.enabled || forceMod.enabled,
                enableDistanceThinning,
                collapseLodToCommand0,
                collisionEnabled,
                extEnabled,
                colSpeedMod.enabled,
                sizeSpeedMod.enabled,
                rotSpeedMod.enabled,
                main.startSize3D,
                main.startRotation3D,
                gpuShape,
                lutProfileIndex);

            uint flags1 = GpuEmitterDescriptor.PackFlags1(tilesX, tilesY, clampedBurst, outCollisionPlaneCount);

            // 9. Respect ParticleSystemScalingMode (Hierarchy vs Local vs Shape)
            Vector3 shapeScale;
            Vector3 sizeScale;
            switch (main.scalingMode)
            {
                case ParticleSystemScalingMode.Local:
                    shapeScale = tr.localScale;
                    sizeScale = new Vector3(Mathf.Abs(tr.localScale.x), Mathf.Abs(tr.localScale.y), Mathf.Abs(tr.localScale.z));
                    break;
                case ParticleSystemScalingMode.Shape:
                    shapeScale = tr.lossyScale;
                    sizeScale = Vector3.one;
                    break;
                default: // ParticleSystemScalingMode.Hierarchy
                    shapeScale = tr.lossyScale;
                    sizeScale = new Vector3(Mathf.Abs(tr.lossyScale.x), Mathf.Abs(tr.lossyScale.y), Mathf.Abs(tr.lossyScale.z));
                    break;
            }
            Vector3 scaledStartSizeMin3D = Vector3.Scale(startSizeMin3D, sizeScale);

            return new GpuEmitterDescriptor
            {
                WorldPosition = tr.position,
                MaterialBucketIndex = bucketIndex,
                WorldRotationQuat = new Vector4(composedRotation.x, composedRotation.y, composedRotation.z, composedRotation.w),
                WorldScale = shapeScale,
                ActiveParticleCount = (uint)clampedTotal,
                Duration = Mathf.Max(0.1f, main.duration),
                StartDelay = Mathf.Max(0f, delayMin),
                StartLifetimeMin = Mathf.Max(0.05f, lifeMin),
                StartLifetimeMax = Mathf.Max(0.05f, lifeMax),
                StartSpeedMin = speedMin,
                StartSpeedMax = speedMax,
                SimulationSpeed = Mathf.Max(0f, main.simulationSpeed),
                GravityModifier = (gravMin + gravMax) * 0.5f,
                StartSizeMin3D = scaledStartSizeMin3D,
                StartSizeMaxRatio = startSizeMaxRatio,
                StartRotationMin3D = startRotMin3D,
                StartRotationMaxZ = startRotMaxZ,
                Flags0 = flags0,
                Flags1 = flags1,
                RandomSeed = randomSeed,
                EmitterStartTime = emitterStartTime,
                ShapeRadius = shapeRadius,
                ShapeAngleRad = shapeAngleRad,
                ShapeRadiusThickness = shapeRadiusThickness,
                ShapeDonutRadius = shapeDonutRadius,
                ShapeBoxScale = shapeBoxScale,
                DragDamping = dragDamping,
                ShapeLocalOffset = shapeLocalOffset,
                NoiseStrength = noiseStrength,
                LinearVelocityAndForce = linearVelForce,
                RadialAndOrbitalSwirl = orbitalSwirl,
                StartColorMinPacked = GpuEmitterDescriptor.PackColorRGBA8(colMin),
                StartColorMaxPacked = GpuEmitterDescriptor.PackColorRGBA8(colMax),
                NoiseFrequency = noiseFreq,
                ExternalForceMultiplier = extMultiplier,
                BurstTime = burstTime,
                BurstRepeatInterval = burstRepeatInterval,
                BurstCycleCount = burstCycleCount,
                SpeedRangeMin = speedRange.x,
                SpeedRangeMax = Mathf.Max(speedRange.x + 0.01f, speedRange.y),
                CollisionBounce = colBounce,
                CollisionDampen = colDampen,
                CollisionLifetimeLoss = colLifeLoss,
                PrimaryCollisionPlane = primaryPlane
            };
        }

        private static void ExtractMinMaxFloat(
            in ParticleSystem.MinMaxCurve curve,
            float defaultMin,
            float defaultMax,
            out float outMin,
            out float outMax)
        {
            switch (curve.mode)
            {
                case ParticleSystemCurveMode.Constant:
                    outMin = curve.constant;
                    outMax = curve.constant;
                    break;
                case ParticleSystemCurveMode.TwoConstants:
                    outMin = curve.constantMin;
                    outMax = curve.constantMax;
                    break;
                case ParticleSystemCurveMode.Curve:
                    float val = curve.curve != null ? curve.curve.Evaluate(0f) * curve.curveMultiplier : defaultMax;
                    outMin = val;
                    outMax = val;
                    break;
                case ParticleSystemCurveMode.TwoCurves:
                    outMin = curve.curveMin != null ? curve.curveMin.Evaluate(0f) * curve.curveMultiplier : defaultMin;
                    outMax = curve.curveMax != null ? curve.curveMax.Evaluate(0f) * curve.curveMultiplier : defaultMax;
                    break;
                default:
                    outMin = defaultMin;
                    outMax = defaultMax;
                    break;
            }
        }

        private static float EvaluateMaxFloat(in ParticleSystem.MinMaxCurve curve, float fallback)
        {
            ExtractMinMaxFloat(in curve, fallback, fallback, out _, out float maxVal);
            return maxVal;
        }

        private static void ExtractMinMaxColor(
            in ParticleSystem.MinMaxGradient grad,
            out Color outMin,
            out Color outMax)
        {
            switch (grad.mode)
            {
                case ParticleSystemGradientMode.Color:
                    outMin = grad.color;
                    outMax = grad.color;
                    break;
                case ParticleSystemGradientMode.TwoColors:
                    outMin = grad.colorMin;
                    outMax = grad.colorMax;
                    break;
                case ParticleSystemGradientMode.Gradient:
                    Color c = grad.gradient != null ? grad.gradient.Evaluate(0f) : Color.white;
                    outMin = c;
                    outMax = c;
                    break;
                case ParticleSystemGradientMode.TwoGradients:
                    outMin = grad.gradientMin != null ? grad.gradientMin.Evaluate(0f) : Color.white;
                    outMax = grad.gradientMax != null ? grad.gradientMax.Evaluate(0f) : Color.white;
                    break;
                default:
                    outMin = Color.white;
                    outMax = Color.white;
                    break;
            }
        }
    }
}
