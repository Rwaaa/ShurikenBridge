using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_6000_0_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
#endif
using UnityEngine.Rendering.Universal;

namespace ShurikenBridge
{
    public enum ShurikenShaderCompatibilityMode : int
    {
        DirectMegaMesh = 0,
        UnityParticleInstancing = 1,
        AutoBridgedStandardShader = 2
    }

    public sealed class ShurikenMaterialBucket
    {
        public int BucketIndex;
        public int BaseCommandIndex; // BucketIndex * 3
        public Material SourceMaterial;
        public ShurikenRenderShape RenderShape;
        public Mesh CustomMesh;
        public ShurikenShaderCompatibilityMode CompatibilityMode;
        public bool CollapseLodToSingleCommand;
        public int Layer;
        public int ActiveEmitterCount;
        public uint[] TrianglesPerLod = new uint[3];
        public Vector4 StretchAndFlipbookParams = new Vector4(0.25f, 4.5f, 1.0f, 0.0f);
        public Vector4 UvShiftData = new Vector4(0f, 1f, 1f, 1f);

        public Material RuntimeBucketMaterial;
        public RenderParams RenderParams;

        public readonly GraphicsBuffer[] LodUnityParticleBuffers = new GraphicsBuffer[3];
#if UNITY_6000_0_OR_NEWER
        public readonly BufferHandle[] LodRenderGraphHandles = new BufferHandle[3];
#endif
        public readonly MaterialPropertyBlock[] LodPropertyBlocks = new MaterialPropertyBlock[3];
        public readonly RenderParams[] LodRenderParams = new RenderParams[3];

        public int ActiveUnityLodBufferCount => CollapseLodToSingleCommand ? 1 : 3;

        public bool UsesUnityParticleInstancingProtocol =>
            CompatibilityMode != ShurikenShaderCompatibilityMode.DirectMegaMesh;
    }

    /// <summary>
    /// Central Zero-GC GPU Particle Orchestrator & Zero-Config URP L1203 (BeforeRenderingOpaques) Pass Injector.
    /// - Compatible with Unity 2022.3 LTS+ (URP 14.0+ ScriptableRenderPass.Execute) AND Unity 6+ (URP 17.0+ RenderGraph & Compatibility Mode).
    /// - Loads shaders via ShurikenBridgeResources (Resources.Load) for 100% Standalone / Mobile / Console Player Build safety.
    /// - Adaptive VRAM allocation (starts compact at 2k~4k instances/cmd = 6~12 MB and grows dynamically on demand).
    /// - Per-camera isolated rendering (Play Mode LateUpdate pre-SRP submission + Edit Mode live Scene View preview).
    /// </summary>
    [ExecuteAlways]
    [DefaultExecutionOrder(-500)]
    [AddComponentMenu("ShurikenBridge/Shuriken Bridge Manager")]
    public sealed class ShurikenBridgeManager : MonoBehaviour
    {
        public const int MaxMaterialBuckets = 16;
        public const int LodsPerBucket = 3;
        public const int MaxTotalCommands = MaxMaterialBuckets * LodsPerBucket; // 48 Commands
        public const int MaxCollisionPlanesPerEmitter = 6;
        public const int MaxExternalForceFields = 32;
        public const int HardMaxEmitters = 16384;
        public const int HardMaxInstancesPerCommand = 65536;
        public const int DefaultInitialMaxEmitters = 512;
        public const int DefaultParticlesPerEmitter = 64;
        public const int DefaultInitialInstancesPerCommandDesktop = 4096;
        public const int DefaultInitialInstancesPerCommandMobile = 2048;

        private static readonly int s_VisibleParticlesBufferId = Shader.PropertyToID("_VisibleParticlesBuffer");
        private static readonly int s_MaxInstancesPerCommandId = Shader.PropertyToID("_MaxInstancesPerCommand");
        private static readonly int s_BucketBaseCommandId = Shader.PropertyToID("_BucketBaseCommand");
        private static readonly int s_UnityParticleInstanceDataId = Shader.PropertyToID("unity_ParticleInstanceData");
        private static readonly int s_UnityParticleUvShiftDataId = Shader.PropertyToID("unity_ParticleUVShiftData");
        private static readonly int s_UnityParticleUseMeshColorsId = Shader.PropertyToID("unity_ParticleUseMeshColors");
        private static readonly int s_BaseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int s_MainTexId = Shader.PropertyToID("_MainTex");

        private static ShurikenBridgeManager s_Instance;
#if UNITY_EDITOR
        private const string EditorPrefSceneViewDebugModeKey = "ShurikenBridge.Debug.PlayModeSceneViewMode";
        private static bool s_SceneViewModeLoaded;
        private static ShurikenSceneViewDebugMode s_PlayModeSceneViewDebugMode = ShurikenSceneViewDebugMode.Off;
        private bool _hidNativeRenderersForNonSceneCamera;
#endif

        /// <summary>
        /// Global Editor-only Play Mode Scene View debugging mode:
        /// - Off (default & forced in all Player Builds): maximum performance, Scene View skipped in Play Mode.
        /// - GpuParticles: renders actual GPU particles in Scene View with individual click picking (high FPS).
        /// - NativeShuriken: Scene View runs 100% native CPU Shuriken (native click/marquee/gizmos) while Game View renders GPU ShurikenBridge.
        /// </summary>
        public static ShurikenSceneViewDebugMode PlayModeSceneViewDebugMode
        {
#if UNITY_EDITOR
            get
            {
                if (!s_SceneViewModeLoaded)
                {
                    s_SceneViewModeLoaded = true;
                    int saved = UnityEditor.EditorPrefs.GetInt(EditorPrefSceneViewDebugModeKey, (int)ShurikenSceneViewDebugMode.Off);
                    s_PlayModeSceneViewDebugMode = Enum.IsDefined(typeof(ShurikenSceneViewDebugMode), saved)
                        ? (ShurikenSceneViewDebugMode)saved
                        : ShurikenSceneViewDebugMode.Off;
                }
                return s_PlayModeSceneViewDebugMode;
            }
            set
            {
                s_SceneViewModeLoaded = true;
                if (s_PlayModeSceneViewDebugMode == value)
                {
                    return;
                }
                s_PlayModeSceneViewDebugMode = value;
                UnityEditor.EditorPrefs.SetInt(EditorPrefSceneViewDebugModeKey, (int)value);
                ShurikenBridgeResources res = ShurikenBridgeResources.Load();
                if (res != null)
                {
                    res.PlayModeSceneViewDebugMode = value;
                }
                if (s_Instance != null)
                {
                    s_Instance.ApplySceneViewDebugModeToAllEmitters();
                }
                UnityEditor.SceneView.RepaintAll();
            }
#else
            get => ShurikenSceneViewDebugMode.Off;
            set { }
#endif
        }

        /// <summary>
        /// Convenience boolean wrapper for PlayModeSceneViewDebugMode (true when GpuParticles or NativeShuriken).
        /// Guaranteed compile-time false in all Player Builds.
        /// </summary>
        public static bool RenderInPlayModeSceneView
        {
            get => PlayModeSceneViewDebugMode != ShurikenSceneViewDebugMode.Off;
            set => PlayModeSceneViewDebugMode = value ? ShurikenSceneViewDebugMode.GpuParticles : ShurikenSceneViewDebugMode.Off;
        }

        internal static bool IsNativeSceneViewDebugActive
        {
#if UNITY_EDITOR
            get => Application.isPlaying && PlayModeSceneViewDebugMode == ShurikenSceneViewDebugMode.NativeShuriken;
#else
            get => false;
#endif
        }

        /// <summary>
        /// True only when the GPU supports Compute Shaders, hardware instancing, AND vertex-stage StructuredBuffer reads.
        /// Safely detects older Android OpenGL ES 3.1 drivers (where maxComputeBufferInputsVertex == 0) so
        /// ShurikenBridge falls back cleanly to Unity's built-in CPU ParticleSystem instead of rendering pink.
        /// </summary>
        public static bool IsPlatformHardwareSupported =>
            SystemInfo.supportsComputeShaders &&
            SystemInfo.supportsInstancing &&
            SystemInfo.maxComputeBufferInputsVertex > 0;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            s_Instance = null;
        }

        public static ShurikenBridgeManager CurrentInstance => s_Instance;

        public static ShurikenBridgeManager Instance
        {
            get
            {
                if (s_Instance == null)
                {
                    s_Instance = FindFirstObjectByType<ShurikenBridgeManager>();
                }
                return s_Instance;
            }
        }

        [Header("Compute Shader & Adaptive Buffer Capacities")]
        [SerializeField] private ComputeShader _simulationComputeShader;
        [SerializeField] private int _maxEmitters = DefaultInitialMaxEmitters;
        [SerializeField] private int _particlesPerEmitter = DefaultParticlesPerEmitter;
        [SerializeField] private int _maxInstancesPerCommand = DefaultInitialInstancesPerCommandDesktop;

        [Header("GPU Frustum Culling & 3-Tier Distance LOD")]
        [SerializeField] private bool _enableFrustumCulling = true;
        [SerializeField] private bool _enableGpuLod = true;
        [SerializeField] private float _lod0ToLod1Distance = 26f;
        [SerializeField] private float _lod1ToLod2Distance = 56f;
        [SerializeField] private float _maxCullDistance = 260f;
        [SerializeField] private Camera _targetCamera;

        [Header("Default Fallback Materials")]
        [SerializeField] private Material _defaultLitMeshMaterial;
        [SerializeField] private Material _defaultAdditiveBillboardMaterial;

        private readonly struct BucketKey : IEquatable<BucketKey>
        {
            public readonly int MaterialId;
            public readonly int MeshId;
            public readonly ShurikenRenderShape Shape;
            public readonly int Layer;

            public BucketKey(int materialId, int meshId, ShurikenRenderShape shape, int layer)
            {
                MaterialId = materialId;
                MeshId = meshId;
                Shape = shape;
                Layer = layer;
            }

            public bool Equals(BucketKey other) =>
                MaterialId == other.MaterialId &&
                MeshId == other.MeshId &&
                Shape == other.Shape &&
                Layer == other.Layer;

            public override bool Equals(object obj) => obj is BucketKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = MaterialId;
                    h = (h * 397) ^ MeshId;
                    h = (h * 397) ^ (int)Shape;
                    h = (h * 397) ^ Layer;
                    return h;
                }
            }
        }

        private readonly List<ShurikenBridgeEmitter> _registeredEmitters = new List<ShurikenBridgeEmitter>(DefaultInitialMaxEmitters);
        private readonly List<ShurikenMaterialBucket> _materialBuckets = new List<ShurikenMaterialBucket>(MaxMaterialBuckets);
        private readonly Dictionary<BucketKey, int> _bucketLookup = new Dictionary<BucketKey, int>(MaxMaterialBuckets);
        private readonly List<ParticleSystemForceField> _cachedForceFields = new List<ParticleSystemForceField>(MaxExternalForceFields);
        private Camera[] _cameraScratch = new Camera[8];

        private GpuEmitterDescriptor[] _cpuEmitterDescriptors;
        private Vector4[] _cpuCollisionPlanes;
        private GpuForceFieldDescriptor[] _cpuForceFields;
        private uint[] _cpuIndirectArgsInit;
        private uint[] _readbackIndirectArgsScratch;

        private GraphicsBuffer _emittersBuffer;
        private GraphicsBuffer _collisionPlanesBuffer;
        private GraphicsBuffer _forceFieldsBuffer;
        private GraphicsBuffer _visibleParticlesBuffer;
        private GraphicsBuffer _indirectArgsBuffer;

        private Mesh _particleMegaMesh;
        private ShurikenSubMeshSlice[] _megaMeshSlices;
        private ShurikenBridgeCurveLutBaker _lutBaker;
        private Texture2D _defaultSoftParticleTexture;
        private ShurikenAutoInjectedPass _autoInjectedPass;

        private int _resetKernel = -1;
        private int _simulateKernel = -1;
        private int _buildUnityParticleDataKernel = -1;
        private bool _isInitialized;
        private bool _emittersBufferDirty;
        private bool _collisionPlanesDirty;
        private bool _forceFieldsDirty = true;
        private bool _indirectArgsTemplateDirty;
        private bool _gpuRenderingActive = true;
        private bool _loggedBucketOverflowWarning;
        private bool _loggedEmitterOverflowWarning;
        private int _activeForceFieldCount;
        private int _lastSyncedFrame = -1;
        private int _lastForceFieldScanFrame = -1000;

        public bool IsSupportedAndReady => _isInitialized && _gpuRenderingActive && IsPlatformHardwareSupported;
        public int RegisteredEmitterCount => _registeredEmitters.Count;
        public int ActiveMaterialBucketCount => _materialBuckets.Count;
        public int ActiveForceFieldCount => _activeForceFieldCount;
        public int ParticlesPerEmitter => _particlesPerEmitter;
        public int MaxInstancesPerCommand => _maxInstancesPerCommand;
        public int TotalSimulatedParticleBudget => _registeredEmitters.Count * _particlesPerEmitter;
        public ShurikenBridgeCurveLutBaker LutBaker => _lutBaker;
        public IReadOnlyList<ShurikenMaterialBucket> MaterialBuckets => _materialBuckets;

#if UNITY_EDITOR
        public IReadOnlyList<ShurikenBridgeEmitter> EditorRegisteredEmitters => _registeredEmitters;

        public bool EditorTryGetEmitterDescriptor(int slotIndex, out GpuEmitterDescriptor descriptor)
        {
            if (_cpuEmitterDescriptors != null && slotIndex >= 0 && slotIndex < _registeredEmitters.Count && slotIndex < _cpuEmitterDescriptors.Length)
            {
                descriptor = _cpuEmitterDescriptors[slotIndex];
                return true;
            }
            descriptor = default;
            return false;
        }

        public Vector4 EditorGetCollisionPlane(int slotIndex, int planeIndex)
        {
            if (_cpuCollisionPlanes == null || slotIndex < 0 || slotIndex >= _maxEmitters ||
                planeIndex < 0 || planeIndex >= MaxCollisionPlanesPerEmitter)
            {
                return new Vector4(0f, 1f, 0f, 0f);
            }
            return _cpuCollisionPlanes[slotIndex * MaxCollisionPlanesPerEmitter + planeIndex];
        }

        public bool EditorTryGetForceField(int index, out GpuForceFieldDescriptor forceField)
        {
            if (_cpuForceFields != null && index >= 0 && index < _activeForceFieldCount && index < _cpuForceFields.Length)
            {
                forceField = _cpuForceFields[index];
                return true;
            }
            forceField = default;
            return false;
        }
#endif

        public bool EnableFrustumCulling
        {
            get => _enableFrustumCulling;
            set => _enableFrustumCulling = value;
        }

        public bool EnableGpuLod
        {
            get => _enableGpuLod;
            set => _enableGpuLod = value;
        }

        public Camera TargetCamera
        {
            get => _targetCamera;
            set => _targetCamera = value;
        }

        public bool GpuRenderingActive
        {
            get => _gpuRenderingActive;
            set => _gpuRenderingActive = value;
        }

        public void ConfigureCapacities(
            ComputeShader computeShader,
            int maxEmitters,
            int particlesPerEmitter,
            int maxInstancesPerCommand,
            float lod0Dist,
            float lod1Dist,
            Camera targetCamera)
        {
            ReleaseResources();
            if (computeShader != null)
            {
                _simulationComputeShader = computeShader;
            }
            _maxEmitters = Mathf.Clamp(maxEmitters, 64, HardMaxEmitters);
            _particlesPerEmitter = Mathf.Clamp(particlesPerEmitter, 8, 1024);
            _maxInstancesPerCommand = Mathf.Clamp(maxInstancesPerCommand, 2048, HardMaxInstancesPerCommand);
            _lod0ToLod1Distance = lod0Dist;
            _lod1ToLod2Distance = lod1Dist;
            _targetCamera = targetCamera;
            InitializeIfNeeded();
        }

        public void RefreshForceFields()
        {
            _cachedForceFields.Clear();
            ParticleSystemForceField[] found = FindObjectsByType<ParticleSystemForceField>(FindObjectsSortMode.None);
            int limit = Mathf.Min(found.Length, MaxExternalForceFields);
            for (int i = 0; i < limit; i++)
            {
                if (found[i] != null)
                {
                    _cachedForceFields.Add(found[i]);
                }
            }
            _forceFieldsDirty = true;
            _lastForceFieldScanFrame = Time.frameCount;
        }

        /// <summary>
        /// Copies any updated artist edits (_BaseColor, _BaseMap, _Smoothness, _EmissionColor, etc.)
        /// from each bucket's SourceMaterial onto its RuntimeBucketMaterial while keeping GPU buffers intact.
        /// </summary>
        public void SyncBucketMaterialsFromSource()
        {
            if (!_isInitialized)
            {
                return;
            }

            for (int b = 0; b < _materialBuckets.Count; b++)
            {
                ShurikenMaterialBucket bucket = _materialBuckets[b];
                if (bucket.SourceMaterial == null || bucket.RuntimeBucketMaterial == null)
                {
                    continue;
                }

                bucket.RuntimeBucketMaterial.CopyMatchingPropertiesFromMaterial(bucket.SourceMaterial);

                if (!bucket.UsesUnityParticleInstancingProtocol)
                {
                    bucket.RuntimeBucketMaterial.SetBuffer(s_VisibleParticlesBufferId, _visibleParticlesBuffer);
                    bucket.RuntimeBucketMaterial.SetInt(s_MaxInstancesPerCommandId, _maxInstancesPerCommand);
                    bucket.RuntimeBucketMaterial.SetInt(s_BucketBaseCommandId, bucket.BaseCommandIndex);
                }
                else
                {
                    ApplyUnityInstancingBindings(
                        bucket.RuntimeBucketMaterial,
                        bucket.RenderShape,
                        bucket.LodUnityParticleBuffers[0],
                        bucket.UvShiftData);
                }
            }
        }

        private void OnEnable()
        {
            s_Instance = this;
            if (Application.isMobilePlatform && _maxInstancesPerCommand == DefaultInitialInstancesPerCommandDesktop)
            {
                _maxInstancesPerCommand = DefaultInitialInstancesPerCommandMobile;
            }

            InitializeIfNeeded();
            RefreshForceFields();

            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
#if UNITY_EDITOR
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
#endif
            Camera.onPreCull -= OnBuiltInCameraPreCull;
            Camera.onPreCull += OnBuiltInCameraPreCull;
            Camera.onPostRender -= OnBuiltInCameraPostRender;
            Camera.onPostRender += OnBuiltInCameraPostRender;

            RebindActiveSceneEmitters();
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            Camera.onPreCull -= OnBuiltInCameraPreCull;
            Camera.onPostRender -= OnBuiltInCameraPostRender;
#if UNITY_EDITOR
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
            if (_hidNativeRenderersForNonSceneCamera)
            {
                SetAllNativeRenderersForceRenderingOffForEditor(false);
                _hidNativeRenderersForNonSceneCamera = false;
            }
#endif
            ReleaseResources();
            if (s_Instance == this)
            {
                s_Instance = null;
            }
        }

        private void OnDestroy()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            Camera.onPreCull -= OnBuiltInCameraPreCull;
            Camera.onPostRender -= OnBuiltInCameraPostRender;
#if UNITY_EDITOR
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
            if (_hidNativeRenderersForNonSceneCamera)
            {
                SetAllNativeRenderersForceRenderingOffForEditor(false);
                _hidNativeRenderersForNonSceneCamera = false;
            }
#endif
            ReleaseResources();
            if (s_Instance == this)
            {
                s_Instance = null;
            }
        }

#if UNITY_EDITOR
        internal void ApplySceneViewDebugModeToAllEmitters()
        {
            bool nativeActive = IsNativeSceneViewDebugActive;
            int count = _registeredEmitters.Count;
            for (int i = 0; i < count; i++)
            {
                ShurikenBridgeEmitter emitter = _registeredEmitters[i];
                if (emitter != null)
                {
                    emitter.ApplySceneViewDebugMode(nativeActive);
                }
            }
            _hidNativeRenderersForNonSceneCamera = false;
        }

        private void SetAllNativeRenderersForceRenderingOffForEditor(bool forceOff)
        {
            int count = _registeredEmitters.Count;
            for (int i = 0; i < count; i++)
            {
                ShurikenBridgeEmitter emitter = _registeredEmitters[i];
                if (emitter != null)
                {
                    emitter.SetNativeRendererForceRenderingOffForEditor(forceOff);
                }
            }
        }

        private void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (_hidNativeRenderersForNonSceneCamera)
            {
                _hidNativeRenderersForNonSceneCamera = false;
                if (IsNativeSceneViewDebugActive)
                {
                    SetAllNativeRenderersForceRenderingOffForEditor(false);
                }
            }
        }
#endif

        /// <summary>
        /// Automatically expands Per-Emitter particle capacity upward (up to 1024) when an emitter
        /// requires more than the default 64 particles, while keeping global budget within 131,072 particles.
        /// </summary>
        public void RequestParticlesPerEmitterBudget(int desiredParticles, bool forceExpand = false)
        {
            if (desiredParticles <= _particlesPerEmitter)
            {
                return;
            }

            int target = Mathf.Clamp(Mathf.NextPowerOfTwo(desiredParticles), 8, 1024);
            int currentEmitterCount = Mathf.Max(1, _registeredEmitters.Count);
            if (forceExpand || (currentEmitterCount * target <= 131072))
            {
                if (target > _particlesPerEmitter)
                {
                    _particlesPerEmitter = target;
                    _emittersBufferDirty = true;
                }
            }
        }

        private void LateUpdate()
        {
            if (!Application.isPlaying || !_isInitialized || !_gpuRenderingActive || _registeredEmitters.Count == 0)
            {
                return;
            }

            SyncDirtyEmittersAndBuffers();

            // Submit Game Camera indirect draws BEFORE SRP begins camera rendering to avoid mid-SRP MainThread<->RenderThread stalls.
            // (SceneView cameras render inside EditorLoop and are submitted directly in OnBeginCameraRendering when RenderInPlayModeSceneView is enabled.)
            if (_targetCamera != null)
            {
                if (_targetCamera.isActiveAndEnabled)
                {
                    IssueIndirectDrawsForCamera(_targetCamera);
                }
                return;
            }

            int camCount = Camera.allCamerasCount;
            if (camCount <= 0)
            {
                return;
            }

            if (_cameraScratch == null || _cameraScratch.Length < camCount)
            {
                _cameraScratch = new Camera[Mathf.NextPowerOfTwo(camCount)];
            }

            int fetched = Camera.GetAllCameras(_cameraScratch);
            for (int i = 0; i < fetched; i++)
            {
                Camera cam = _cameraScratch[i];
                if (cam == null || !cam.isActiveAndEnabled ||
                    cam.cameraType == CameraType.Preview ||
                    cam.cameraType == CameraType.Reflection ||
                    cam.cameraType == CameraType.SceneView)
                {
                    continue;
                }
                IssueIndirectDrawsForCamera(cam);
            }
        }

        public void InitializeIfNeeded()
        {
            if (_isInitialized)
            {
                return;
            }

            if (!IsPlatformHardwareSupported)
            {
                Debug.LogWarning("[ShurikenBridge] Hardware does not support Compute Shaders + Vertex StructuredBuffers (maxComputeBufferInputsVertex <= 0). Falling back to built-in ParticleSystem.");
                return;
            }

            // 1. Load ComputeShader via Resources (Player-Build safe) before Editor AssetDatabase fallback
            if (_simulationComputeShader == null)
            {
                ShurikenBridgeResources res = ShurikenBridgeResources.Load();
                if (res != null && res.SimulationCompute != null)
                {
                    _simulationComputeShader = res.SimulationCompute;
                }
            }

            if (_simulationComputeShader == null)
            {
#if UNITY_EDITOR
                _simulationComputeShader = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(
                    "Assets/ShurikenBridge/Shaders/ShurikenBridgeSimulation.compute");
                if (_simulationComputeShader == null)
                {
                    _simulationComputeShader = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(
                        "Packages/com.shurikenbridge.core/Shaders/ShurikenBridgeSimulation.compute");
                }
#endif
                if (_simulationComputeShader == null)
                {
                    Debug.LogError("[ShurikenBridge] ShurikenBridgeSimulation.compute not found! Ensure Tools > ShurikenBridge > Verify Build Shader Resources has been run.");
                    return;
                }
            }

            _resetKernel = _simulationComputeShader.FindKernel("CSResetParticleIndirectArgs");
            _simulateKernel = _simulationComputeShader.FindKernel("CSSimulateAndCullParticles");
            _buildUnityParticleDataKernel = _simulationComputeShader.FindKernel("CSBuildUnityParticleInstanceData");

            _maxEmitters = Mathf.Clamp(_maxEmitters, 64, HardMaxEmitters);
            _particlesPerEmitter = Mathf.Clamp(_particlesPerEmitter, 8, 1024);
            _maxInstancesPerCommand = Mathf.Clamp(_maxInstancesPerCommand, 2048, HardMaxInstancesPerCommand);

            AllocateEmitterBuffers(_maxEmitters);
            AllocateForceFieldAndIndirectBuffers();
            AllocateVisibleParticlesBuffer(_maxInstancesPerCommand);

            _particleMegaMesh = ShurikenMegaMeshBuilder.BuildParticleMegaMesh(out _megaMeshSlices);
            _lutBaker = new ShurikenBridgeCurveLutBaker();
            _defaultSoftParticleTexture = CreateProceduralSoftParticleTexture();
            _autoInjectedPass = new ShurikenAutoInjectedPass(this, RenderPassEvent.BeforeRenderingOpaques);

            EnsureDefaultFallbackMaterials();
            RebuildAllIndirectArgsTemplates();

            _isInitialized = true;
            _emittersBufferDirty = true;
            _collisionPlanesDirty = true;
            _forceFieldsDirty = true;
            _loggedBucketOverflowWarning = false;
            _loggedEmitterOverflowWarning = false;

            for (int i = 0; i < _registeredEmitters.Count; i++)
            {
                if (_registeredEmitters[i] != null)
                {
                    _registeredEmitters[i].MarkDirty();
                }
            }
        }

        private void AllocateEmitterBuffers(int emitterCapacity)
        {
            GpuEmitterDescriptor[] prevDesc = _cpuEmitterDescriptors;
            Vector4[] prevPlanes = _cpuCollisionPlanes;

            _cpuEmitterDescriptors = new GpuEmitterDescriptor[emitterCapacity];
            _cpuCollisionPlanes = new Vector4[emitterCapacity * MaxCollisionPlanesPerEmitter];

            if (prevDesc != null)
            {
                int copyCount = Mathf.Min(prevDesc.Length, emitterCapacity);
                Array.Copy(prevDesc, _cpuEmitterDescriptors, copyCount);
            }
            if (prevPlanes != null)
            {
                int copyPlanes = Mathf.Min(prevPlanes.Length, _cpuCollisionPlanes.Length);
                Array.Copy(prevPlanes, _cpuCollisionPlanes, copyPlanes);
            }

            ReleaseBufferSafe(ref _emittersBuffer);
            ReleaseBufferSafe(ref _collisionPlanesBuffer);

            int emitterStride = Marshal.SizeOf<GpuEmitterDescriptor>(); // 256 Bytes
            _emittersBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, emitterCapacity, emitterStride);
            _collisionPlanesBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                emitterCapacity * MaxCollisionPlanesPerEmitter,
                sizeof(float) * 4);

            _emittersBufferDirty = true;
            _collisionPlanesDirty = true;
        }

        private void AllocateForceFieldAndIndirectBuffers()
        {
            _cpuForceFields = new GpuForceFieldDescriptor[MaxExternalForceFields];
            _cpuIndirectArgsInit = new uint[MaxTotalCommands * 5];
            _readbackIndirectArgsScratch = new uint[MaxTotalCommands * 5];

            int forceFieldStride = Marshal.SizeOf<GpuForceFieldDescriptor>(); // 64 Bytes
            _forceFieldsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, MaxExternalForceFields, forceFieldStride);
            _indirectArgsBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured,
                MaxTotalCommands * 5,
                sizeof(uint));
        }

        private void AllocateVisibleParticlesBuffer(int instancesPerCommand)
        {
            ReleaseBufferSafe(ref _visibleParticlesBuffer);

            int visibleStride = Marshal.SizeOf<GpuVisibleParticle>(); // 64 Bytes
            _visibleParticlesBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                MaxTotalCommands * instancesPerCommand,
                visibleStride);

            Shader.SetGlobalBuffer(s_VisibleParticlesBufferId, _visibleParticlesBuffer);
            Shader.SetGlobalInt(s_MaxInstancesPerCommandId, instancesPerCommand);

            // Rebind all existing material buckets to the new visible buffer / resized LOD unity particle buffers
            int unityParticleStride = Marshal.SizeOf<GpuUnityParticleInstanceData>(); // 56 Bytes
            for (int b = 0; b < _materialBuckets.Count; b++)
            {
                ShurikenMaterialBucket bucket = _materialBuckets[b];
                if (!bucket.UsesUnityParticleInstancingProtocol)
                {
                    if (bucket.RuntimeBucketMaterial != null)
                    {
                        bucket.RuntimeBucketMaterial.SetBuffer(s_VisibleParticlesBufferId, _visibleParticlesBuffer);
                        bucket.RuntimeBucketMaterial.SetInt(s_MaxInstancesPerCommandId, instancesPerCommand);
                    }
                }
                else
                {
                    int activeSlots = bucket.ActiveUnityLodBufferCount;
                    for (int lod = 0; lod < activeSlots; lod++)
                    {
                        if (bucket.LodUnityParticleBuffers[lod] != null)
                        {
                            bucket.LodUnityParticleBuffers[lod].Release();
                        }

                        GraphicsBuffer newBuf = new GraphicsBuffer(
                            GraphicsBuffer.Target.Structured,
                            instancesPerCommand,
                            unityParticleStride);
                        bucket.LodUnityParticleBuffers[lod] = newBuf;

                        if (bucket.LodPropertyBlocks[lod] != null)
                        {
                            bucket.LodPropertyBlocks[lod].SetBuffer(s_UnityParticleInstanceDataId, newBuf);
                        }
                    }

                    if (bucket.RuntimeBucketMaterial != null && bucket.LodUnityParticleBuffers[0] != null)
                    {
                        bucket.RuntimeBucketMaterial.SetBuffer(s_UnityParticleInstanceDataId, bucket.LodUnityParticleBuffers[0]);
                    }
                }
            }
        }

        private void EnsureAdaptiveInstanceCapacity()
        {
            int activeCount = _registeredEmitters.Count;
            if (activeCount <= 0 || _maxInstancesPerCommand >= HardMaxInstancesPerCommand)
            {
                return;
            }

            int bucketDivisor = Mathf.Max(1, _materialBuckets.Count);
            int estimatedPeakPerCmd = Mathf.CeilToInt((activeCount * _particlesPerEmitter) / (float)bucketDivisor * 1.15f);
            if (estimatedPeakPerCmd <= _maxInstancesPerCommand)
            {
                return;
            }

            int nextCapacity = _maxInstancesPerCommand;
            while (nextCapacity < estimatedPeakPerCmd && nextCapacity < HardMaxInstancesPerCommand)
            {
                nextCapacity = Mathf.Min(nextCapacity * 2, HardMaxInstancesPerCommand);
            }

            if (nextCapacity > _maxInstancesPerCommand)
            {
                _maxInstancesPerCommand = nextCapacity;
                AllocateVisibleParticlesBuffer(_maxInstancesPerCommand);
            }
        }

        private static Texture2D CreateProceduralSoftParticleTexture()
        {
            const int size = 64;
            // Use linear: true so radial falloff is NOT double-darkened by sRGB->Linear decoding in Linear Color Space
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false, true)
            {
                name = "ShurikenBridge_DefaultSoftParticleRadialTex",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };

            Color32[] pixels = new Color32[size * size];
            float inv = 2.0f / (size - 1);
            for (int y = 0; y < size; y++)
            {
                float ny = y * inv - 1.0f;
                for (int x = 0; x < size; x++)
                {
                    float nx = x * inv - 1.0f;
                    float rSq = nx * nx + ny * ny;
                    float falloff = Mathf.Clamp01(1.0f - rSq);
                    float alpha = Mathf.Sqrt(falloff) * falloff; // Crisp soft-edged glowing disc
                    byte aByte = (byte)Mathf.Clamp(Mathf.RoundToInt(alpha * 255f), 0, 255);
                    pixels[y * size + x] = new Color32(255, 255, 255, aByte);
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(false, true);
            return tex;
        }

        private void EnsureDefaultFallbackMaterials()
        {
            ShurikenBridgeResources res = ShurikenBridgeResources.Load();

            if (_defaultLitMeshMaterial == null)
            {
                Shader litShader = (res != null && res.DirectLitMeshShader != null)
                    ? res.DirectLitMeshShader
                    : Shader.Find("ShurikenBridge/LitMesh");
                if (litShader != null)
                {
                    _defaultLitMeshMaterial = new Material(litShader)
                    {
                        name = "Mat_ShurikenBridge_DefaultLitMesh",
                        hideFlags = HideFlags.HideAndDontSave
                    };
                }
            }

            if (_defaultAdditiveBillboardMaterial == null)
            {
                Shader addShader = (res != null && res.DirectAdditiveBillboardShader != null)
                    ? res.DirectAdditiveBillboardShader
                    : Shader.Find("ShurikenBridge/AdditiveBillboard");
                if (addShader != null)
                {
                    _defaultAdditiveBillboardMaterial = new Material(addShader)
                    {
                        name = "Mat_ShurikenBridge_DefaultAdditiveBillboard",
                        hideFlags = HideFlags.HideAndDontSave
                    };
                }
            }
        }

        public Material GetFallbackMaterial(ShurikenRenderShape shape)
        {
            EnsureDefaultFallbackMaterials();
            return (shape == ShurikenRenderShape.MeshSphere || shape == ShurikenRenderShape.MeshCube)
                ? _defaultLitMeshMaterial
                : _defaultAdditiveBillboardMaterial;
        }

        public static ShurikenShaderCompatibilityMode ClassifyMaterialCompatibility(
            Material mat,
            out Shader outBridgedParticleShader)
        {
            outBridgedParticleShader = null;
            if (mat == null || mat.shader == null)
            {
                return ShurikenShaderCompatibilityMode.DirectMegaMesh;
            }

            string shaderName = mat.shader.name;
            if (shaderName.StartsWith("ShurikenBridge/", StringComparison.Ordinal) ||
                mat.HasProperty(s_BucketBaseCommandId))
            {
                return ShurikenShaderCompatibilityMode.DirectMegaMesh;
            }

            ShurikenBridgeResources res = ShurikenBridgeResources.Load();
            bool isBuiltInRP = GraphicsSettings.currentRenderPipeline == null;

            if (isBuiltInRP)
            {
                if (shaderName.Equals("Standard", StringComparison.Ordinal) ||
                    shaderName.Equals("Universal Render Pipeline/Lit", StringComparison.Ordinal) ||
                    shaderName.Equals("Universal Render Pipeline/Particles/Lit", StringComparison.Ordinal) ||
                    shaderName.Equals("Universal Render Pipeline/Particles/Simple Lit", StringComparison.Ordinal))
                {
                    outBridgedParticleShader = (res != null && res.BuiltInParticlesStandardSurfaceShader != null)
                        ? res.BuiltInParticlesStandardSurfaceShader
                        : Shader.Find("Particles/Standard Surface");
                    if (outBridgedParticleShader != null)
                    {
                        return ShurikenShaderCompatibilityMode.AutoBridgedStandardShader;
                    }
                }
                else if (shaderName.Equals("Unlit/Color", StringComparison.Ordinal) ||
                         shaderName.Equals("Unlit/Texture", StringComparison.Ordinal) ||
                         shaderName.StartsWith("Legacy Shaders/Particles/", StringComparison.Ordinal) ||
                         shaderName.StartsWith("Mobile/Particles/", StringComparison.Ordinal) ||
                         shaderName.Equals("Universal Render Pipeline/Unlit", StringComparison.Ordinal) ||
                         shaderName.Equals("Universal Render Pipeline/Particles/Unlit", StringComparison.Ordinal))
                {
                    outBridgedParticleShader = (res != null && res.BuiltInParticlesStandardUnlitShader != null)
                        ? res.BuiltInParticlesStandardUnlitShader
                        : Shader.Find("Particles/Standard Unlit");
                    if (outBridgedParticleShader != null)
                    {
                        return ShurikenShaderCompatibilityMode.AutoBridgedStandardShader;
                    }
                }
            }

            if (shaderName.Equals("Universal Render Pipeline/Lit", StringComparison.Ordinal) ||
                shaderName.Equals("Standard", StringComparison.Ordinal))
            {
                outBridgedParticleShader = (res != null && res.UrpParticlesLitShader != null)
                    ? res.UrpParticlesLitShader
                    : Shader.Find("Universal Render Pipeline/Particles/Lit");
                if (outBridgedParticleShader != null)
                {
                    return ShurikenShaderCompatibilityMode.AutoBridgedStandardShader;
                }
            }
            else if (shaderName.Equals("Universal Render Pipeline/Simple Lit", StringComparison.Ordinal))
            {
                outBridgedParticleShader = (res != null && res.UrpParticlesSimpleLitShader != null)
                    ? res.UrpParticlesSimpleLitShader
                    : Shader.Find("Universal Render Pipeline/Particles/Simple Lit");
                if (outBridgedParticleShader != null)
                {
                    return ShurikenShaderCompatibilityMode.AutoBridgedStandardShader;
                }
            }
            else if (shaderName.Equals("Universal Render Pipeline/Unlit", StringComparison.Ordinal) ||
                     shaderName.Equals("Unlit/Color", StringComparison.Ordinal) ||
                     shaderName.Equals("Unlit/Texture", StringComparison.Ordinal))
            {
                outBridgedParticleShader = (res != null && res.UrpParticlesUnlitShader != null)
                    ? res.UrpParticlesUnlitShader
                    : Shader.Find("Universal Render Pipeline/Particles/Unlit");
                if (outBridgedParticleShader != null)
                {
                    return ShurikenShaderCompatibilityMode.AutoBridgedStandardShader;
                }
            }

            return ShurikenShaderCompatibilityMode.UnityParticleInstancing;
        }

        public void RegisterEmitter(ShurikenBridgeEmitter emitter)
        {
            if (emitter == null)
            {
                return;
            }

            InitializeIfNeeded();
            if (!_isInitialized)
            {
                return;
            }

            if (emitter.RegisteredSlotIndex >= 0 &&
                emitter.RegisteredSlotIndex < _registeredEmitters.Count &&
                _registeredEmitters[emitter.RegisteredSlotIndex] == emitter)
            {
                return;
            }

            if (_registeredEmitters.Count >= _maxEmitters)
            {
                if (_maxEmitters < HardMaxEmitters)
                {
                    _maxEmitters = Mathf.Min(_maxEmitters * 2, HardMaxEmitters);
                    AllocateEmitterBuffers(_maxEmitters);
                }
                else
                {
                    if (!_loggedEmitterOverflowWarning)
                    {
                        _loggedEmitterOverflowWarning = true;
                        Debug.LogWarning($"[ShurikenBridge] Reached HardMaxEmitters ({HardMaxEmitters}). Additional emitters will not be registered.");
                    }
                    return;
                }
            }

            int slot = _registeredEmitters.Count;
            _registeredEmitters.Add(emitter);
            emitter.RegisteredSlotIndex = slot;
            emitter.MarkDirty();
            _emittersBufferDirty = true;
        }

        public void UnregisterEmitter(ShurikenBridgeEmitter emitter)
        {
            if (emitter == null)
            {
                return;
            }

            int slot = emitter.RegisteredSlotIndex;
            if (slot < 0 || slot >= _registeredEmitters.Count || _registeredEmitters[slot] != emitter)
            {
                emitter.RegisteredSlotIndex = -1;
                return;
            }

            int lastIdx = _registeredEmitters.Count - 1;
            if (slot != lastIdx)
            {
                ShurikenBridgeEmitter lastEmitter = _registeredEmitters[lastIdx];
                _registeredEmitters[slot] = lastEmitter;
                if (lastEmitter != null)
                {
                    lastEmitter.RegisteredSlotIndex = slot;
                    if (_cpuEmitterDescriptors != null && slot < _cpuEmitterDescriptors.Length)
                    {
                        _cpuEmitterDescriptors[slot] = _cpuEmitterDescriptors[lastIdx];
                    }
                    if (_cpuCollisionPlanes != null)
                    {
                        int dstBase = slot * MaxCollisionPlanesPerEmitter;
                        int srcBase = lastIdx * MaxCollisionPlanesPerEmitter;
                        for (int p = 0; p < MaxCollisionPlanesPerEmitter; p++)
                        {
                            _cpuCollisionPlanes[dstBase + p] = _cpuCollisionPlanes[srcBase + p];
                        }
                        _collisionPlanesDirty = true;
                    }
                }
            }

            _registeredEmitters.RemoveAt(lastIdx);
            emitter.RegisteredSlotIndex = -1;
            _emittersBufferDirty = true;
        }

        public int GetOrCreateMaterialBucket(
            Material material,
            ShurikenRenderShape shape,
            Mesh customMesh = null,
            float velocityStretchMultiplier = 0.28f,
            float maxStretchRatio = 5.0f,
            int flipbookTilesX = 1,
            int flipbookTilesY = 1,
            int layer = 0)
        {
            InitializeIfNeeded();

            Material effectiveMat = material != null ? material : GetFallbackMaterial(shape);
            if (effectiveMat == null)
            {
                return 0;
            }

            int clampedLayer = (layer >= 0 && layer < 32) ? layer : 0;
            int matId = effectiveMat.GetInstanceID();
            int meshId = customMesh != null ? customMesh.GetInstanceID() : 0;
            BucketKey key = new BucketKey(matId, meshId, shape, clampedLayer);

            if (_bucketLookup.TryGetValue(key, out int existingBucketIdx))
            {
                return existingBucketIdx;
            }

            if (_materialBuckets.Count >= MaxMaterialBuckets)
            {
                if (!_loggedBucketOverflowWarning)
                {
                    _loggedBucketOverflowWarning = true;
                    Debug.LogWarning($"[ShurikenBridge] Reached MaxMaterialBuckets ({MaxMaterialBuckets}). Material '{effectiveMat.name}' will share the last bucket. Consider consolidating duplicate materials.");
                }
                return _materialBuckets.Count - 1;
            }

            int newBucketIdx = _materialBuckets.Count;
            int baseCmd = newBucketIdx * LodsPerBucket;

            ShurikenShaderCompatibilityMode compatMode = ClassifyMaterialCompatibility(
                effectiveMat,
                out Shader bridgedParticleShader);

            int totalFlipbookFrames = Mathf.Max(1, flipbookTilesX * flipbookTilesY);

            bool collapseLod = (compatMode != ShurikenShaderCompatibilityMode.DirectMegaMesh) &&
                               (customMesh != null ||
                                shape == ShurikenRenderShape.Billboard ||
                                shape == ShurikenRenderShape.StretchedBillboard);

            Vector4 uvShiftData = (totalFlipbookFrames > 1)
                ? new Vector4(1f, flipbookTilesX, 1f / Mathf.Max(1, flipbookTilesX), 1f / Mathf.Max(1, flipbookTilesY))
                : new Vector4(0f, 1f, 1f, 1f);

            ShurikenMaterialBucket bucket = new ShurikenMaterialBucket
            {
                BucketIndex = newBucketIdx,
                BaseCommandIndex = baseCmd,
                SourceMaterial = effectiveMat,
                RenderShape = shape,
                CustomMesh = customMesh,
                CompatibilityMode = compatMode,
                CollapseLodToSingleCommand = collapseLod,
                Layer = clampedLayer,
                ActiveEmitterCount = 0,
                StretchAndFlipbookParams = new Vector4(velocityStretchMultiplier, maxStretchRatio, totalFlipbookFrames, 0.0f),
                UvShiftData = uvShiftData
            };

            if (compatMode == ShurikenShaderCompatibilityMode.DirectMegaMesh)
            {
                Material runtimeMat = new Material(effectiveMat)
                {
                    name = $"{effectiveMat.name}_SBDirectBucket{newBucketIdx}_{shape}",
                    hideFlags = HideFlags.HideAndDontSave
                };
                runtimeMat.SetBuffer(s_VisibleParticlesBufferId, _visibleParticlesBuffer);
                runtimeMat.SetInt(s_MaxInstancesPerCommandId, _maxInstancesPerCommand);
                runtimeMat.SetInt(s_BucketBaseCommandId, baseCmd);

                bucket.RuntimeBucketMaterial = runtimeMat;
                bucket.RenderParams = new RenderParams(runtimeMat)
                {
                    worldBounds = new Bounds(Vector3.zero, new Vector3(4000f, 1000f, 4000f)),
                    shadowCastingMode = ShadowCastingMode.Off,
                    receiveShadows = false,
                    layer = clampedLayer
                };
            }
            else
            {
                int unityParticleStride = Marshal.SizeOf<GpuUnityParticleInstanceData>(); // 56 Bytes
                int activeBufferCount = collapseLod ? 1 : LodsPerBucket;
                for (int lod = 0; lod < activeBufferCount; lod++)
                {
                    bucket.LodUnityParticleBuffers[lod] = new GraphicsBuffer(
                        GraphicsBuffer.Target.Structured,
                        _maxInstancesPerCommand,
                        unityParticleStride);
                }

                Material sharedMat = CreateUnityInstancingRuntimeMaterial(
                    effectiveMat,
                    bridgedParticleShader,
                    shape,
                    newBucketIdx,
                    bucket.LodUnityParticleBuffers[0],
                    uvShiftData);
                bucket.RuntimeBucketMaterial = sharedMat;

                for (int lod = 0; lod < activeBufferCount; lod++)
                {
                    MaterialPropertyBlock mpb = null;
                    if (!collapseLod)
                    {
                        mpb = new MaterialPropertyBlock();
                        mpb.SetBuffer(s_UnityParticleInstanceDataId, bucket.LodUnityParticleBuffers[lod]);
                        bucket.LodPropertyBlocks[lod] = mpb;
                    }

                    bucket.LodRenderParams[lod] = new RenderParams(sharedMat)
                    {
                        worldBounds = new Bounds(Vector3.zero, new Vector3(4000f, 1000f, 4000f)),
                        shadowCastingMode = ShadowCastingMode.Off,
                        receiveShadows = false,
                        matProps = mpb,
                        layer = clampedLayer
                    };
                }
            }

            PopulateBucketIndirectArgsTemplate(bucket);

            _materialBuckets.Add(bucket);
            _bucketLookup[key] = newBucketIdx;
            _indirectArgsTemplateDirty = true;

            return newBucketIdx;
        }

        public bool IsBucketCollapsedToSingleCommand(int bucketIndex)
        {
            if (bucketIndex < 0 || bucketIndex >= _materialBuckets.Count)
            {
                return false;
            }
            return _materialBuckets[bucketIndex].CollapseLodToSingleCommand;
        }

        private Material CreateUnityInstancingRuntimeMaterial(
            Material sourceMat,
            Shader bridgedParticleShader,
            ShurikenRenderShape shape,
            int bucketIdx,
            GraphicsBuffer defaultInstanceBuffer,
            Vector4 uvShiftData)
        {
            Material runtimeMat = new Material(sourceMat)
            {
                name = $"{sourceMat.name}_SBUnityInstBucket{bucketIdx}_Shared",
                hideFlags = HideFlags.HideAndDontSave
            };

            if (bridgedParticleShader != null)
            {
                int origQueue = sourceMat.renderQueue;
                runtimeMat.shader = bridgedParticleShader;
                runtimeMat.CopyMatchingPropertiesFromMaterial(sourceMat);
                if (origQueue > 0)
                {
                    runtimeMat.renderQueue = origQueue;
                }
            }

            ApplyUnityInstancingBindings(runtimeMat, shape, defaultInstanceBuffer, uvShiftData);
            return runtimeMat;
        }

        private void ApplyUnityInstancingBindings(
            Material runtimeMat,
            ShurikenRenderShape shape,
            GraphicsBuffer defaultInstanceBuffer,
            Vector4 uvShiftData)
        {
            if ((shape == ShurikenRenderShape.Billboard || shape == ShurikenRenderShape.StretchedBillboard) &&
                _defaultSoftParticleTexture != null)
            {
                if (runtimeMat.HasProperty(s_BaseMapId) && runtimeMat.GetTexture(s_BaseMapId) == null)
                {
                    runtimeMat.SetTexture(s_BaseMapId, _defaultSoftParticleTexture);
                }
                if (runtimeMat.HasProperty(s_MainTexId) && runtimeMat.GetTexture(s_MainTexId) == null)
                {
                    runtimeMat.SetTexture(s_MainTexId, _defaultSoftParticleTexture);
                }
            }

            runtimeMat.enableInstancing = true;
            runtimeMat.EnableKeyword("PROCEDURAL_INSTANCING_ON");
            if (defaultInstanceBuffer != null)
            {
                runtimeMat.SetBuffer(s_UnityParticleInstanceDataId, defaultInstanceBuffer);
            }
            runtimeMat.SetVector(s_UnityParticleUvShiftDataId, uvShiftData);
            runtimeMat.SetFloat(s_UnityParticleUseMeshColorsId, 0.0f);
        }

        private void PopulateBucketIndirectArgsTemplate(ShurikenMaterialBucket bucket)
        {
            int baseCmd = bucket.BaseCommandIndex;

            if (bucket.CustomMesh != null && bucket.UsesUnityParticleInstancingProtocol)
            {
                uint customIndexCount = bucket.CustomMesh.GetIndexCount(0);
                uint customIndexStart = bucket.CustomMesh.GetIndexStart(0);
                uint customBaseVert = bucket.CustomMesh.GetBaseVertex(0);
                uint customTris = customIndexCount / 3u;

                for (int lod = 0; lod < LodsPerBucket; lod++)
                {
                    bucket.TrianglesPerLod[lod] = customTris;
                    int argOffset = (baseCmd + lod) * 5;
                    _cpuIndirectArgsInit[argOffset + 0] = customIndexCount;
                    _cpuIndirectArgsInit[argOffset + 1] = 0u;
                    _cpuIndirectArgsInit[argOffset + 2] = customIndexStart;
                    _cpuIndirectArgsInit[argOffset + 3] = customBaseVert;
                    _cpuIndirectArgsInit[argOffset + 4] = 0u;
                }
                return;
            }

            for (int lod = 0; lod < LodsPerBucket; lod++)
            {
                int sliceIdx = (int)bucket.RenderShape * LodsPerBucket + lod;
                ShurikenSubMeshSlice slice = _megaMeshSlices[sliceIdx];
                bucket.TrianglesPerLod[lod] = slice.TrianglesPerInstance;

                int argOffset = (baseCmd + lod) * 5;
                _cpuIndirectArgsInit[argOffset + 0] = slice.IndexCount;
                _cpuIndirectArgsInit[argOffset + 1] = 0u;
                _cpuIndirectArgsInit[argOffset + 2] = slice.StartIndex;
                _cpuIndirectArgsInit[argOffset + 3] = slice.BaseVertex;
                _cpuIndirectArgsInit[argOffset + 4] = 0u;
            }
        }

        private void RebuildAllIndirectArgsTemplates()
        {
            if (_cpuIndirectArgsInit == null || _megaMeshSlices == null)
            {
                return;
            }

            for (int b = 0; b < MaxMaterialBuckets; b++)
            {
                if (b < _materialBuckets.Count)
                {
                    PopulateBucketIndirectArgsTemplate(_materialBuckets[b]);
                }
                else
                {
                    int baseCmd = b * LodsPerBucket;
                    for (int lod = 0; lod < LodsPerBucket; lod++)
                    {
                        ShurikenSubMeshSlice slice = _megaMeshSlices[lod];
                        int argOffset = (baseCmd + lod) * 5;
                        _cpuIndirectArgsInit[argOffset + 0] = slice.IndexCount;
                        _cpuIndirectArgsInit[argOffset + 1] = 0u;
                        _cpuIndirectArgsInit[argOffset + 2] = slice.StartIndex;
                        _cpuIndirectArgsInit[argOffset + 3] = slice.BaseVertex;
                        _cpuIndirectArgsInit[argOffset + 4] = 0u;
                    }
                }
            }

            if (_indirectArgsBuffer != null && _indirectArgsBuffer.IsValid())
            {
                _indirectArgsBuffer.SetData(_cpuIndirectArgsInit);
            }
            _indirectArgsTemplateDirty = false;
        }

        public void UpdateEmitterDescriptorAtSlot(int slotIndex, in GpuEmitterDescriptor descriptor)
        {
            if (_cpuEmitterDescriptors == null || slotIndex < 0 || slotIndex >= _cpuEmitterDescriptors.Length)
            {
                return;
            }

            _cpuEmitterDescriptors[slotIndex] = descriptor;
            _emittersBufferDirty = true;
        }

        public void UpdateCollisionPlanesAtSlot(int slotIndex, Vector4[] planes, int planeCount)
        {
            if (_cpuCollisionPlanes == null || slotIndex < 0 || slotIndex >= _maxEmitters || planes == null)
            {
                return;
            }

            int baseIdx = slotIndex * MaxCollisionPlanesPerEmitter;
            int clamped = Mathf.Min(planeCount, MaxCollisionPlanesPerEmitter);
            for (int p = 0; p < clamped; p++)
            {
                _cpuCollisionPlanes[baseIdx + p] = planes[p];
            }
            _collisionPlanesDirty = true;
        }

        private void SyncForceFieldsIfNeeded()
        {
            if (_cpuForceFields == null || _forceFieldsBuffer == null || !_forceFieldsBuffer.IsValid())
            {
                return;
            }

            if (!Application.isPlaying && (Time.frameCount - _lastForceFieldScanFrame) > 30)
            {
                RefreshForceFields();
            }

            bool anyMoved = _forceFieldsDirty;
            int count = Mathf.Min(_cachedForceFields.Count, MaxExternalForceFields);

            for (int i = 0; i < count; i++)
            {
                ParticleSystemForceField ff = _cachedForceFields[i];
                if (ff == null)
                {
                    anyMoved = true;
                    continue;
                }
                if (ff.transform.hasChanged)
                {
                    ff.transform.hasChanged = false;
                    anyMoved = true;
                }
            }

            if (!anyMoved)
            {
                return;
            }

            int active = 0;
            for (int i = 0; i < count; i++)
            {
                ParticleSystemForceField ff = _cachedForceFields[i];
                if (ff == null || !ff.isActiveAndEnabled)
                {
                    continue;
                }

                Transform tr = ff.transform;
                Quaternion q = tr.rotation;
                Vector3 dirForce = new Vector3(
                    ff.directionX.constant,
                    ff.directionY.constant,
                    ff.directionZ.constant);

                _cpuForceFields[active++] = new GpuForceFieldDescriptor
                {
                    Position = tr.position,
                    EndRange = Mathf.Max(0.1f, ff.endRange),
                    RotationQuat = new Vector4(q.x, q.y, q.z, q.w),
                    DirectionForce = dirForce,
                    GravityStrength = ff.gravity.constant,
                    RotationSpeed = ff.rotationSpeed.constant,
                    DragStrength = ff.drag.constant,
                    StartRange = Mathf.Max(0f, ff.startRange),
                    EnabledFlag = 1.0f
                };
            }

            _activeForceFieldCount = active;
            if (active > 0)
            {
                _forceFieldsBuffer.SetData(_cpuForceFields, 0, 0, active);
            }
            _forceFieldsDirty = false;
        }

        public void SyncDirtyEmittersAndBuffers()
        {
            if (!_isInitialized || !_gpuRenderingActive)
            {
                return;
            }

            int currentFrame = Time.frameCount;
            if (Application.isPlaying && currentFrame == _lastSyncedFrame)
            {
                return;
            }
            _lastSyncedFrame = currentFrame;

            for (int b = 0; b < _materialBuckets.Count; b++)
            {
                _materialBuckets[b].ActiveEmitterCount = 0;
            }

            int activeCount = _registeredEmitters.Count;
            for (int i = activeCount - 1; i >= 0; i--)
            {
                if (i >= _registeredEmitters.Count)
                {
                    continue;
                }

                ShurikenBridgeEmitter emitter = _registeredEmitters[i];
                if (emitter == null)
                {
                    continue;
                }

                emitter.SyncToManagerIfDirty();
                int slot = emitter.RegisteredSlotIndex;
                if (slot >= 0 && slot < _registeredEmitters.Count && _cpuEmitterDescriptors[slot].ActiveParticleCount > 0u)
                {
                    int bucketIdx = (int)_cpuEmitterDescriptors[slot].MaterialBucketIndex;
                    if (bucketIdx >= 0 && bucketIdx < _materialBuckets.Count)
                    {
                        _materialBuckets[bucketIdx].ActiveEmitterCount++;
                    }
                }
            }
            activeCount = _registeredEmitters.Count;

            EnsureAdaptiveInstanceCapacity();
            SyncForceFieldsIfNeeded();

            if (!Application.isPlaying)
            {
                SyncBucketMaterialsFromSource();
            }

            if (_lutBaker != null)
            {
                _lutBaker.FlushIfDirty();
            }

            if (_indirectArgsTemplateDirty)
            {
                RebuildAllIndirectArgsTemplates();
            }

            if (_emittersBufferDirty && activeCount > 0 && _emittersBuffer != null && _emittersBuffer.IsValid())
            {
                _emittersBuffer.SetData(_cpuEmitterDescriptors, 0, 0, activeCount);
                _emittersBufferDirty = false;
            }

            if (_collisionPlanesDirty && activeCount > 0 && _collisionPlanesBuffer != null && _collisionPlanesBuffer.IsValid())
            {
                _collisionPlanesBuffer.SetData(_cpuCollisionPlanes, 0, 0, activeCount * MaxCollisionPlanesPerEmitter);
                _collisionPlanesDirty = false;
            }
        }

        /// <summary>
        /// Issues Graphics.RenderMeshIndirect scoped specifically to the given camera (rp.camera = camera),
        /// enabling rock-solid Multi-Camera, Split-Screen, Minimap, and live Edit Mode Scene View rendering.
        /// Note: Global buffers are bound once in AllocateVisibleParticlesBuffer() to avoid mid-frame GfxDevice sync flushes.
        /// </summary>
        public void IssueIndirectDrawsForCamera(Camera camera)
        {
            if (!_isInitialized || !_gpuRenderingActive || _particleMegaMesh == null || _indirectArgsBuffer == null || camera == null)
            {
                return;
            }

            int cameraCullingMask = camera.cullingMask;
            if (cameraCullingMask == 0)
            {
                return;
            }

            int bucketCount = _materialBuckets.Count;
            for (int b = 0; b < bucketCount; b++)
            {
                ShurikenMaterialBucket bucket = _materialBuckets[b];
                if (bucket.ActiveEmitterCount <= 0 || bucket.RuntimeBucketMaterial == null)
                {
                    continue;
                }

                if ((cameraCullingMask & (1 << bucket.Layer)) == 0)
                {
                    continue;
                }

                if (!bucket.UsesUnityParticleInstancingProtocol)
                {
                    RenderParams rp = bucket.RenderParams;
                    rp.camera = camera;
                    Graphics.RenderMeshIndirect(
                        in rp,
                        _particleMegaMesh,
                        _indirectArgsBuffer,
                        LodsPerBucket,
                        bucket.BaseCommandIndex);
                }
                else
                {
                    Mesh targetMesh = bucket.CustomMesh != null ? bucket.CustomMesh : _particleMegaMesh;
                    int activeSlots = bucket.ActiveUnityLodBufferCount;

                    // Back-to-Front LOD order (LOD 2 -> LOD 1 -> LOD 0)
                    for (int lod = activeSlots - 1; lod >= 0; lod--)
                    {
                        if (bucket.LodUnityParticleBuffers[lod] == null)
                        {
                            continue;
                        }

                        RenderParams rp = bucket.LodRenderParams[lod];
                        rp.camera = camera;
                        Graphics.RenderMeshIndirect(
                            in rp,
                            targetMesh,
                            _indirectArgsBuffer,
                            1,
                            bucket.BaseCommandIndex + lod);
                    }
                }
            }
        }

        internal bool ShouldRenderForCamera(Camera camera)
        {
            if (camera == null)
            {
                return false;
            }

            CameraType camType = camera.cameraType;
            if (camType == CameraType.Preview || camType == CameraType.Reflection)
            {
                return false;
            }

            if (camType == CameraType.SceneView)
            {
#if UNITY_EDITOR
                // Edit Mode: live GPU preview in Scene View.
                // Play Mode: render GPU particles in Scene View ONLY in GpuParticles mode
                // (in NativeShuriken mode, Scene View renders pure native CPU Shuriken instead).
                return !Application.isPlaying || PlayModeSceneViewDebugMode == ShurikenSceneViewDebugMode.GpuParticles;
#else
                return false;
#endif
            }

            if (_targetCamera != null && camera != _targetCamera)
            {
                return false;
            }

            return true;
        }

        private void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (!_isInitialized || !_gpuRenderingActive || _registeredEmitters.Count == 0 || camera == null)
            {
                return;
            }

#if UNITY_EDITOR
            // In NativeShuriken debug mode, hide native CPU ParticleSystemRenderers from Game/Preview cameras
            // (restored immediately in OnEndCameraRendering so Scene View & Scene View Picking always see native Shuriken!).
            if (IsNativeSceneViewDebugActive)
            {
                if (camera.cameraType != CameraType.SceneView)
                {
                    SetAllNativeRenderersForceRenderingOffForEditor(true);
                    _hidNativeRenderersForNonSceneCamera = true;
                }
                else
                {
                    if (_hidNativeRenderersForNonSceneCamera)
                    {
                        SetAllNativeRenderersForceRenderingOffForEditor(false);
                        _hidNativeRenderersForNonSceneCamera = false;
                    }
                    return; // Scene View renders 100% pure native CPU Shuriken in this mode
                }
            }
#endif

            if (!ShouldRenderForCamera(camera))
            {
                return;
            }

            SyncDirtyEmittersAndBuffers();

            UniversalAdditionalCameraData additionalCameraData = camera.GetUniversalAdditionalCameraData();
            if (additionalCameraData != null && additionalCameraData.scriptableRenderer != null && _autoInjectedPass != null)
            {
                additionalCameraData.scriptableRenderer.EnqueuePass(_autoInjectedPass);
            }

            // In Play Mode, Game Cameras are already submitted cleanly in LateUpdate() before SRP starts (zero RenderThread sync stall).
            // SceneView cameras render inside EditorLoop (with camera.enabled == false), so they MUST be submitted here
            // right before URP runs context.Cull() for the SceneView frame (works in Edit Mode, Play Mode, and Paused Play Mode).
            if (!Application.isPlaying || camera.cameraType == CameraType.SceneView)
            {
                IssueIndirectDrawsForCamera(camera);
            }
        }

        private void OnBuiltInCameraPreCull(Camera camera)
        {
            // Active only when running on Unity's Built-in Render Pipeline (no ScriptableRenderPipeline active)
            if (GraphicsSettings.currentRenderPipeline != null)
            {
                return;
            }

            if (!_isInitialized || !_gpuRenderingActive || _registeredEmitters.Count == 0 || camera == null)
            {
                return;
            }

#if UNITY_EDITOR
            if (IsNativeSceneViewDebugActive)
            {
                if (camera.cameraType != CameraType.SceneView)
                {
                    SetAllNativeRenderersForceRenderingOffForEditor(true);
                    _hidNativeRenderersForNonSceneCamera = true;
                }
                else
                {
                    if (_hidNativeRenderersForNonSceneCamera)
                    {
                        SetAllNativeRenderersForceRenderingOffForEditor(false);
                        _hidNativeRenderersForNonSceneCamera = false;
                    }
                    return;
                }
            }
#endif

            if (!ShouldRenderForCamera(camera))
            {
                return;
            }

            SyncDirtyEmittersAndBuffers();

            _autoInjectedPass?.ExecuteImmediateForBuiltInRP(camera);

            if (!Application.isPlaying || camera.cameraType == CameraType.SceneView)
            {
                IssueIndirectDrawsForCamera(camera);
            }
        }

        private void OnBuiltInCameraPostRender(Camera camera)
        {
            if (GraphicsSettings.currentRenderPipeline != null)
            {
                return;
            }

#if UNITY_EDITOR
            if (_hidNativeRenderersForNonSceneCamera)
            {
                _hidNativeRenderersForNonSceneCamera = false;
                if (IsNativeSceneViewDebugActive)
                {
                    SetAllNativeRenderersForceRenderingOffForEditor(false);
                }
            }
#endif
        }

        public void ReadbackVisibleParticleStats(
            out uint outTotalVisible,
            out uint outLod0Visible,
            out uint outLod1Visible,
            out uint outLod2Visible,
            out ulong outTotalTriangles)
        {
            outTotalVisible = 0u;
            outLod0Visible = 0u;
            outLod1Visible = 0u;
            outLod2Visible = 0u;
            outTotalTriangles = 0UL;

            if (!_isInitialized || _indirectArgsBuffer == null || !_indirectArgsBuffer.IsValid())
            {
                return;
            }

            _indirectArgsBuffer.GetData(_readbackIndirectArgsScratch);

            int bucketCount = _materialBuckets.Count;
            for (int b = 0; b < bucketCount; b++)
            {
                ShurikenMaterialBucket bucket = _materialBuckets[b];
                int baseCmd = bucket.BaseCommandIndex;

                uint c0 = _readbackIndirectArgsScratch[(baseCmd + 0) * 5 + 1];
                uint c1 = _readbackIndirectArgsScratch[(baseCmd + 1) * 5 + 1];
                uint c2 = _readbackIndirectArgsScratch[(baseCmd + 2) * 5 + 1];

                outLod0Visible += c0;
                outLod1Visible += c1;
                outLod2Visible += c2;
                outTotalVisible += c0 + c1 + c2;

                outTotalTriangles += (ulong)c0 * bucket.TrianglesPerLod[0]
                                   + (ulong)c1 * bucket.TrianglesPerLod[1]
                                   + (ulong)c2 * bucket.TrianglesPerLod[2];
            }
        }

        public void ReleaseResources()
        {
            _isInitialized = false;

            // Restore all registered emitters back to native CPU Shuriken before releasing GPU buffers
            for (int i = 0; i < _registeredEmitters.Count; i++)
            {
                ShurikenBridgeEmitter emitter = _registeredEmitters[i];
                if (emitter != null)
                {
                    emitter.OnManagerReleased();
                }
            }
            _registeredEmitters.Clear();

            ReleaseBufferSafe(ref _emittersBuffer);
            ReleaseBufferSafe(ref _collisionPlanesBuffer);
            ReleaseBufferSafe(ref _forceFieldsBuffer);
            ReleaseBufferSafe(ref _visibleParticlesBuffer);
            ReleaseBufferSafe(ref _indirectArgsBuffer);

            DestroyObjectSafe(ref _particleMegaMesh);
            DestroyObjectSafe(ref _defaultSoftParticleTexture);
            DestroyObjectSafe(ref _defaultLitMeshMaterial);
            DestroyObjectSafe(ref _defaultAdditiveBillboardMaterial);

            if (_lutBaker != null)
            {
                _lutBaker.Dispose();
                _lutBaker = null;
            }

            for (int i = 0; i < _materialBuckets.Count; i++)
            {
                ShurikenMaterialBucket bucket = _materialBuckets[i];
                if (bucket.RuntimeBucketMaterial != null)
                {
                    DestroyObjectSafe(ref bucket.RuntimeBucketMaterial);
                }

                for (int lod = 0; lod < LodsPerBucket; lod++)
                {
                    if (bucket.LodUnityParticleBuffers[lod] != null)
                    {
                        bucket.LodUnityParticleBuffers[lod].Release();
                        bucket.LodUnityParticleBuffers[lod] = null;
                    }
                    bucket.LodPropertyBlocks[lod] = null;
                }
            }

            _materialBuckets.Clear();
            _bucketLookup.Clear();
        }

        private void RebindActiveSceneEmitters()
        {
            if (!_isInitialized || !_gpuRenderingActive)
            {
                return;
            }

            ShurikenBridgeEmitter[] emitters = FindObjectsByType<ShurikenBridgeEmitter>(FindObjectsSortMode.None);
            for (int i = 0; i < emitters.Length; i++)
            {
                ShurikenBridgeEmitter e = emitters[i];
                if (e != null && e.isActiveAndEnabled && e.gameObject.scene.IsValid() && e.RegisteredSlotIndex < 0)
                {
                    e.OnManagerReenabled();
                }
            }
        }

        private static void ReleaseBufferSafe(ref GraphicsBuffer buffer)
        {
            if (buffer != null)
            {
                buffer.Release();
                buffer = null;
            }
        }

        private static void DestroyObjectSafe<T>(ref T obj) where T : UnityEngine.Object
        {
            if (obj != null)
            {
                if (Application.isPlaying)
                    Destroy(obj);
                else
                    DestroyImmediate(obj);
                obj = null;
            }
        }

        private sealed class ShurikenAutoInjectedPass : ScriptableRenderPass
        {
            private static readonly int s_EmittersBufferId = Shader.PropertyToID("_EmittersBuffer");
            private static readonly int s_CollisionPlanesBufferId = Shader.PropertyToID("_CollisionPlanesBuffer");
            private static readonly int s_ForceFieldsBufferId = Shader.PropertyToID("_ForceFieldsBuffer");
            private static readonly int s_CurveGradientLutTexId = Shader.PropertyToID("_CurveGradientLutTex");
            private static readonly int s_VisibleParticlesBufferPassId = Shader.PropertyToID("_VisibleParticlesBuffer");
            private static readonly int s_IndirectArgsBufferId = Shader.PropertyToID("_IndirectArgsBuffer");
            private static readonly int s_TargetUnityParticleBufferId = Shader.PropertyToID("_TargetUnityParticleBuffer");

            private static readonly int s_EmitterCountId = Shader.PropertyToID("_EmitterCount");
            private static readonly int s_ParticlesPerEmitterId = Shader.PropertyToID("_ParticlesPerEmitter");
            private static readonly int s_CommandCountId = Shader.PropertyToID("_CommandCount");
            private static readonly int s_MaxInstancesPerCommandPassId = Shader.PropertyToID("_MaxInstancesPerCommand");
            private static readonly int s_ForceFieldCountId = Shader.PropertyToID("_ForceFieldCount");
            private static readonly int s_GlobalTimeId = Shader.PropertyToID("_GlobalTime");
            private static readonly int s_EnableFrustumCullingId = Shader.PropertyToID("_EnableFrustumCulling");
            private static readonly int s_EnableGpuLodId = Shader.PropertyToID("_EnableGpuLod");
            private static readonly int s_CameraWorldPosId = Shader.PropertyToID("_CameraWorldPos");
            private static readonly int s_LodDistanceSqThresholdsId = Shader.PropertyToID("_LodDistanceSqThresholds");
            private static readonly int s_CameraFrustumPlanesId = Shader.PropertyToID("_CameraFrustumPlanes");

            private static readonly int s_SourceCommandIndexId = Shader.PropertyToID("_SourceCommandIndex");
            private static readonly int s_BucketRenderShapeId = Shader.PropertyToID("_BucketRenderShape");
            private static readonly int s_BucketStretchAndFlipbookId = Shader.PropertyToID("_BucketStretchAndFlipbook");

            private static readonly Plane[] s_ScratchPlanes = new Plane[6];
            private static readonly Vector4[] s_ImmediateScratchPlanesVec4 = new Vector4[6];
#if !UNITY_6000_0_OR_NEWER || URP_COMPATIBILITY_MODE
            private static readonly Vector4[] s_LegacyScratchPlanesVec4 = new Vector4[6];
#endif

            private readonly ShurikenBridgeManager _manager;
            private readonly ProfilingSampler _profilingSampler =
                new ProfilingSampler("ShurikenBridge_GpuSimCullLodAndCollision");

            /// <summary>
            /// Built-in Render Pipeline (BiRP) execution path invoked from Camera.onPreCull when no SRP is active.
            /// </summary>
            public void ExecuteImmediateForBuiltInRP(Camera cam)
            {
                if (_manager == null || !_manager.IsSupportedAndReady || _manager.RegisteredEmitterCount <= 0 || cam == null)
                {
                    return;
                }

                ComputeShader cs = _manager._simulationComputeShader;
                if (cs == null)
                {
                    return;
                }

                int activeCommands = Mathf.Max(LodsPerBucket, _manager._materialBuckets.Count * LodsPerBucket);
                int emitterCount = _manager._registeredEmitters.Count;
                int particlesPerEmitter = _manager._particlesPerEmitter;
                int totalThreads = emitterCount * particlesPerEmitter;

                GeometryUtility.CalculateFrustumPlanes(cam, s_ScratchPlanes);
                for (int i = 0; i < 6; i++)
                {
                    Plane p = s_ScratchPlanes[i];
                    Vector3 n = p.normal;
                    s_ImmediateScratchPlanesVec4[i] = new Vector4(n.x, n.y, n.z, p.distance);
                }

                Vector3 camPos = cam.transform.position;
                float d0Sq = _manager._lod0ToLod1Distance * _manager._lod0ToLod1Distance;
                float d1Sq = _manager._lod1ToLod2Distance * _manager._lod1ToLod2Distance;
                float maxCullSq = _manager._maxCullDistance * _manager._maxCullDistance;

                // 1. Reset IndirectArgs
                cs.SetInt(s_CommandCountId, activeCommands);
                cs.SetBuffer(_manager._resetKernel, s_IndirectArgsBufferId, _manager._indirectArgsBuffer);
                cs.Dispatch(_manager._resetKernel, (activeCommands + 15) / 16, 1, 1);

                // 2. Simulate, Collide, ForceField, Cull & LOD
                int simKernel = _manager._simulateKernel;
                cs.SetInt(s_EmitterCountId, emitterCount);
                cs.SetInt(s_ParticlesPerEmitterId, particlesPerEmitter);
                cs.SetInt(s_CommandCountId, activeCommands);
                cs.SetInt(s_MaxInstancesPerCommandPassId, _manager._maxInstancesPerCommand);
                cs.SetInt(s_ForceFieldCountId, _manager._activeForceFieldCount);
                cs.SetFloat(s_GlobalTimeId, Application.isPlaying ? Time.time : (float)Time.realtimeSinceStartup);
                cs.SetInt(s_EnableFrustumCullingId, _manager._enableFrustumCulling ? 1 : 0);
                cs.SetInt(s_EnableGpuLodId, _manager._enableGpuLod ? 1 : 0);
                cs.SetVector(s_CameraWorldPosId, new Vector4(camPos.x, camPos.y, camPos.z, 1.0f));
                cs.SetVector(s_LodDistanceSqThresholdsId, new Vector4(d0Sq, d1Sq, maxCullSq, 0.0f));
                cs.SetVectorArray(s_CameraFrustumPlanesId, s_ImmediateScratchPlanesVec4);

                Texture lutTex = _manager._lutBaker != null ? _manager._lutBaker.LutTexture : Texture2D.whiteTexture;
                cs.SetTexture(simKernel, s_CurveGradientLutTexId, lutTex);
                cs.SetBuffer(simKernel, s_EmittersBufferId, _manager._emittersBuffer);
                cs.SetBuffer(simKernel, s_CollisionPlanesBufferId, _manager._collisionPlanesBuffer);
                cs.SetBuffer(simKernel, s_ForceFieldsBufferId, _manager._forceFieldsBuffer);
                cs.SetBuffer(simKernel, s_VisibleParticlesBufferPassId, _manager._visibleParticlesBuffer);
                cs.SetBuffer(simKernel, s_IndirectArgsBufferId, _manager._indirectArgsBuffer);
                cs.Dispatch(simKernel, (totalThreads + 63) / 64, 1, 1);

                // 3. Build unity_ParticleInstanceData for Standard Unity / Custom Shaders
                int buildKernel = _manager._buildUnityParticleDataKernel;
                if (buildKernel >= 0)
                {
                    int maxConvertGroups = Mathf.Max(1, (MinInt(_manager._maxInstancesPerCommand, totalThreads) + 63) / 64);
                    for (int b = 0; b < _manager._materialBuckets.Count; b++)
                    {
                        ShurikenMaterialBucket bucket = _manager._materialBuckets[b];
                        if (!bucket.UsesUnityParticleInstancingProtocol || bucket.ActiveEmitterCount <= 0)
                        {
                            continue;
                        }

                        cs.SetInt(s_BucketRenderShapeId, (int)bucket.RenderShape);
                        cs.SetVector(s_BucketStretchAndFlipbookId, bucket.StretchAndFlipbookParams);
                        cs.SetBuffer(buildKernel, s_VisibleParticlesBufferPassId, _manager._visibleParticlesBuffer);
                        cs.SetBuffer(buildKernel, s_IndirectArgsBufferId, _manager._indirectArgsBuffer);

                        int activeSlots = bucket.ActiveUnityLodBufferCount;
                        for (int lod = 0; lod < activeSlots; lod++)
                        {
                            if (bucket.LodUnityParticleBuffers[lod] == null) continue;
                            cs.SetInt(s_SourceCommandIndexId, bucket.BaseCommandIndex + lod);
                            cs.SetBuffer(buildKernel, s_TargetUnityParticleBufferId, bucket.LodUnityParticleBuffers[lod]);
                            cs.Dispatch(buildKernel, maxConvertGroups, 1, 1);
                        }
                    }
                }
            }

#if UNITY_6000_0_OR_NEWER
            private sealed class PassData
            {
                public ComputeShader computeShader;
                public int resetKernel;
                public int simulateKernel;
                public int buildUnityParticleDataKernel;
                public BufferHandle emittersHandle;
                public BufferHandle collisionPlanesHandle;
                public BufferHandle forceFieldsHandle;
                public BufferHandle visibleParticlesHandle;
                public BufferHandle indirectArgsHandle;
                public Texture lutTexture;
                public int emitterCount;
                public int particlesPerEmitter;
                public int commandCount;
                public int maxInstancesPerCommand;
                public int forceFieldCount;
                public float globalTime;
                public int enableFrustumCulling;
                public int enableGpuLod;
                public Vector4 cameraWorldPos;
                public Vector4 lodDistanceSqThresholds;
                public readonly Vector4[] frustumPlanes = new Vector4[6];
                public List<ShurikenMaterialBucket> buckets;
            }
#endif

            public ShurikenAutoInjectedPass(ShurikenBridgeManager manager, RenderPassEvent passEvent)
            {
                _manager = manager;
                renderPassEvent = passEvent;
                base.profilingSampler = _profilingSampler;
            }

#if !UNITY_6000_0_OR_NEWER || URP_COMPATIBILITY_MODE
#if UNITY_6000_0_OR_NEWER
            [Obsolete("Compatibility Mode fallback for URP projects with Render Graph disabled.")]
#endif
            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (_manager == null || !_manager.IsSupportedAndReady || _manager.RegisteredEmitterCount <= 0)
                {
                    return;
                }

                Camera cam = renderingData.cameraData.camera;
                if (cam == null || renderingData.cameraData.isPreviewCamera || !_manager.ShouldRenderForCamera(cam))
                {
                    return;
                }

                CommandBuffer cmd = CommandBufferPool.Get("ShurikenBridge_GpuSimCullLodAndCollision");
                try
                {
                    using (new ProfilingScope(cmd, _profilingSampler))
                    {
                        ComputeShader cs = _manager._simulationComputeShader;
                        int activeCommands = Mathf.Max(LodsPerBucket, _manager._materialBuckets.Count * LodsPerBucket);
                        int emitterCount = _manager._registeredEmitters.Count;
                        int particlesPerEmitter = _manager._particlesPerEmitter;
                        int totalThreads = emitterCount * particlesPerEmitter;

                        GeometryUtility.CalculateFrustumPlanes(cam, s_ScratchPlanes);
                        for (int i = 0; i < 6; i++)
                        {
                            Plane p = s_ScratchPlanes[i];
                            Vector3 n = p.normal;
                            s_LegacyScratchPlanesVec4[i] = new Vector4(n.x, n.y, n.z, p.distance);
                        }

                        Vector3 camPos = cam.transform.position;
                        float d0Sq = _manager._lod0ToLod1Distance * _manager._lod0ToLod1Distance;
                        float d1Sq = _manager._lod1ToLod2Distance * _manager._lod1ToLod2Distance;
                        float maxCullSq = _manager._maxCullDistance * _manager._maxCullDistance;

                        // 1. Reset IndirectArgs
                        cmd.SetComputeIntParam(cs, s_CommandCountId, activeCommands);
                        cmd.SetComputeBufferParam(cs, _manager._resetKernel, s_IndirectArgsBufferId, _manager._indirectArgsBuffer);
                        cmd.DispatchCompute(cs, _manager._resetKernel, (activeCommands + 15) / 16, 1, 1);

                        // 2. Simulate, Collide, ForceField, Cull & LOD
                        int simKernel = _manager._simulateKernel;
                        cmd.SetComputeIntParam(cs, s_EmitterCountId, emitterCount);
                        cmd.SetComputeIntParam(cs, s_ParticlesPerEmitterId, particlesPerEmitter);
                        cmd.SetComputeIntParam(cs, s_CommandCountId, activeCommands);
                        cmd.SetComputeIntParam(cs, s_MaxInstancesPerCommandPassId, _manager._maxInstancesPerCommand);
                        cmd.SetComputeIntParam(cs, s_ForceFieldCountId, _manager._activeForceFieldCount);
                        cmd.SetComputeFloatParam(cs, s_GlobalTimeId, Application.isPlaying ? Time.time : (float)Time.realtimeSinceStartup);
                        cmd.SetComputeIntParam(cs, s_EnableFrustumCullingId, _manager._enableFrustumCulling ? 1 : 0);
                        cmd.SetComputeIntParam(cs, s_EnableGpuLodId, _manager._enableGpuLod ? 1 : 0);
                        cmd.SetComputeVectorParam(cs, s_CameraWorldPosId, new Vector4(camPos.x, camPos.y, camPos.z, 1.0f));
                        cmd.SetComputeVectorParam(cs, s_LodDistanceSqThresholdsId, new Vector4(d0Sq, d1Sq, maxCullSq, 0.0f));
                        cmd.SetComputeVectorArrayParam(cs, s_CameraFrustumPlanesId, s_LegacyScratchPlanesVec4);

                        Texture lutTex = _manager._lutBaker != null ? _manager._lutBaker.LutTexture : Texture2D.whiteTexture;
                        cmd.SetComputeTextureParam(cs, simKernel, s_CurveGradientLutTexId, lutTex);
                        cmd.SetComputeBufferParam(cs, simKernel, s_EmittersBufferId, _manager._emittersBuffer);
                        cmd.SetComputeBufferParam(cs, simKernel, s_CollisionPlanesBufferId, _manager._collisionPlanesBuffer);
                        cmd.SetComputeBufferParam(cs, simKernel, s_ForceFieldsBufferId, _manager._forceFieldsBuffer);
                        cmd.SetComputeBufferParam(cs, simKernel, s_VisibleParticlesBufferPassId, _manager._visibleParticlesBuffer);
                        cmd.SetComputeBufferParam(cs, simKernel, s_IndirectArgsBufferId, _manager._indirectArgsBuffer);
                        cmd.DispatchCompute(cs, simKernel, (totalThreads + 63) / 64, 1, 1);

                        // 3. Build unity_ParticleInstanceData for Standard Unity / Custom Shaders
                        int buildKernel = _manager._buildUnityParticleDataKernel;
                        if (buildKernel >= 0)
                        {
                            int maxConvertGroups = Mathf.Max(1, (MinInt(_manager._maxInstancesPerCommand, totalThreads) + 63) / 64);
                            for (int b = 0; b < _manager._materialBuckets.Count; b++)
                            {
                                ShurikenMaterialBucket bucket = _manager._materialBuckets[b];
                                if (!bucket.UsesUnityParticleInstancingProtocol || bucket.ActiveEmitterCount <= 0)
                                {
                                    continue;
                                }

                                cmd.SetComputeIntParam(cs, s_BucketRenderShapeId, (int)bucket.RenderShape);
                                cmd.SetComputeVectorParam(cs, s_BucketStretchAndFlipbookId, bucket.StretchAndFlipbookParams);
                                cmd.SetComputeBufferParam(cs, buildKernel, s_VisibleParticlesBufferPassId, _manager._visibleParticlesBuffer);
                                cmd.SetComputeBufferParam(cs, buildKernel, s_IndirectArgsBufferId, _manager._indirectArgsBuffer);

                                int activeSlots = bucket.ActiveUnityLodBufferCount;
                                for (int lod = 0; lod < activeSlots; lod++)
                                {
                                    if (bucket.LodUnityParticleBuffers[lod] == null) continue;
                                    cmd.SetComputeIntParam(cs, s_SourceCommandIndexId, bucket.BaseCommandIndex + lod);
                                    cmd.SetComputeBufferParam(cs, buildKernel, s_TargetUnityParticleBufferId, bucket.LodUnityParticleBuffers[lod]);
                                    cmd.DispatchCompute(cs, buildKernel, maxConvertGroups, 1, 1);
                                }
                            }
                        }
                    }

                    context.ExecuteCommandBuffer(cmd);
                }
                finally
                {
                    CommandBufferPool.Release(cmd);
                }
            }
#endif

#if UNITY_6000_0_OR_NEWER
            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (_manager == null || !_manager.IsSupportedAndReady || _manager.RegisteredEmitterCount <= 0)
                {
                    return;
                }

                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                if (cameraData == null || cameraData.camera == null || cameraData.isPreviewCamera ||
                    !_manager.ShouldRenderForCamera(cameraData.camera))
                {
                    return;
                }

                int activeCommands = Mathf.Max(LodsPerBucket, _manager._materialBuckets.Count * LodsPerBucket);

                using (IComputeRenderGraphBuilder builder = renderGraph.AddComputePass<PassData>(
                           "ShurikenBridge_GpuSimCullLodAndCollision",
                           out PassData passData,
                           _profilingSampler))
                {
                    GeometryUtility.CalculateFrustumPlanes(cameraData.camera, s_ScratchPlanes);
                    for (int i = 0; i < 6; i++)
                    {
                        Plane p = s_ScratchPlanes[i];
                        Vector3 n = p.normal;
                        passData.frustumPlanes[i] = new Vector4(n.x, n.y, n.z, p.distance);
                    }

                    Vector3 camPos = cameraData.camera.transform.position;
                    float d0Sq = _manager._lod0ToLod1Distance * _manager._lod0ToLod1Distance;
                    float d1Sq = _manager._lod1ToLod2Distance * _manager._lod1ToLod2Distance;
                    float maxCullSq = _manager._maxCullDistance * _manager._maxCullDistance;

                    passData.computeShader = _manager._simulationComputeShader;
                    passData.resetKernel = _manager._resetKernel;
                    passData.simulateKernel = _manager._simulateKernel;
                    passData.buildUnityParticleDataKernel = _manager._buildUnityParticleDataKernel;
                    passData.emittersHandle = renderGraph.ImportBuffer(_manager._emittersBuffer);
                    passData.collisionPlanesHandle = renderGraph.ImportBuffer(_manager._collisionPlanesBuffer);
                    passData.forceFieldsHandle = renderGraph.ImportBuffer(_manager._forceFieldsBuffer);
                    passData.visibleParticlesHandle = renderGraph.ImportBuffer(_manager._visibleParticlesBuffer);
                    passData.indirectArgsHandle = renderGraph.ImportBuffer(_manager._indirectArgsBuffer);
                    passData.lutTexture = _manager._lutBaker != null ? _manager._lutBaker.LutTexture : Texture2D.whiteTexture;
                    passData.emitterCount = _manager._registeredEmitters.Count;
                    passData.particlesPerEmitter = _manager._particlesPerEmitter;
                    passData.commandCount = activeCommands;
                    passData.maxInstancesPerCommand = _manager._maxInstancesPerCommand;
                    passData.forceFieldCount = _manager._activeForceFieldCount;
                    passData.globalTime = Application.isPlaying ? Time.time : (float)Time.realtimeSinceStartup;
                    passData.enableFrustumCulling = _manager._enableFrustumCulling ? 1 : 0;
                    passData.enableGpuLod = _manager._enableGpuLod ? 1 : 0;
                    passData.cameraWorldPos = new Vector4(camPos.x, camPos.y, camPos.z, 1.0f);
                    passData.lodDistanceSqThresholds = new Vector4(d0Sq, d1Sq, maxCullSq, 0.0f);
                    passData.buckets = _manager._materialBuckets;

                    builder.UseBuffer(passData.emittersHandle, AccessFlags.Read);
                    builder.UseBuffer(passData.collisionPlanesHandle, AccessFlags.Read);
                    builder.UseBuffer(passData.forceFieldsHandle, AccessFlags.Read);
                    builder.UseBuffer(passData.visibleParticlesHandle, AccessFlags.ReadWrite);
                    builder.UseBuffer(passData.indirectArgsHandle, AccessFlags.ReadWrite);

                    for (int b = 0; b < passData.buckets.Count; b++)
                    {
                        ShurikenMaterialBucket bucket = passData.buckets[b];
                        if (bucket.UsesUnityParticleInstancingProtocol && bucket.ActiveEmitterCount > 0)
                        {
                            int activeSlots = bucket.ActiveUnityLodBufferCount;
                            for (int lod = 0; lod < activeSlots; lod++)
                            {
                                if (bucket.LodUnityParticleBuffers[lod] != null)
                                {
                                    bucket.LodRenderGraphHandles[lod] = renderGraph.ImportBuffer(bucket.LodUnityParticleBuffers[lod]);
                                    builder.UseBuffer(bucket.LodRenderGraphHandles[lod], AccessFlags.Write);
                                }
                            }
                        }
                    }

                    builder.AllowPassCulling(false);

                    builder.SetRenderFunc(static (PassData data, ComputeGraphContext ctx) =>
                    {
                        ComputeCommandBuffer cmd = ctx.cmd;
                        ComputeShader cs = data.computeShader;

                        // 1. Reset instanceCount for all active [MaterialBucket x LOD] commands
                        cmd.SetComputeIntParam(cs, s_CommandCountId, data.commandCount);
                        cmd.SetComputeBufferParam(cs, data.resetKernel, s_IndirectArgsBufferId, data.indirectArgsHandle);
                        int resetGroups = (data.commandCount + 15) / 16;
                        cmd.DispatchCompute(cs, data.resetKernel, resetGroups, 1, 1);

                        // 2. Unified GPU Simulation, Analytical Plane Collision, External ForceFields, 6-Plane Frustum Culling & 3-Tier LOD
                        int simKernel = data.simulateKernel;
                        cmd.SetComputeIntParam(cs, s_EmitterCountId, data.emitterCount);
                        cmd.SetComputeIntParam(cs, s_ParticlesPerEmitterId, data.particlesPerEmitter);
                        cmd.SetComputeIntParam(cs, s_CommandCountId, data.commandCount);
                        cmd.SetComputeIntParam(cs, s_MaxInstancesPerCommandPassId, data.maxInstancesPerCommand);
                        cmd.SetComputeIntParam(cs, s_ForceFieldCountId, data.forceFieldCount);
                        cmd.SetComputeFloatParam(cs, s_GlobalTimeId, data.globalTime);
                        cmd.SetComputeIntParam(cs, s_EnableFrustumCullingId, data.enableFrustumCulling);
                        cmd.SetComputeIntParam(cs, s_EnableGpuLodId, data.enableGpuLod);
                        cmd.SetComputeVectorParam(cs, s_CameraWorldPosId, data.cameraWorldPos);
                        cmd.SetComputeVectorParam(cs, s_LodDistanceSqThresholdsId, data.lodDistanceSqThresholds);
                        cmd.SetComputeVectorArrayParam(cs, s_CameraFrustumPlanesId, data.frustumPlanes);

                        cs.SetTexture(simKernel, s_CurveGradientLutTexId, data.lutTexture);
                        cmd.SetComputeBufferParam(cs, simKernel, s_EmittersBufferId, data.emittersHandle);
                        cmd.SetComputeBufferParam(cs, simKernel, s_CollisionPlanesBufferId, data.collisionPlanesHandle);
                        cmd.SetComputeBufferParam(cs, simKernel, s_ForceFieldsBufferId, data.forceFieldsHandle);
                        cmd.SetComputeBufferParam(cs, simKernel, s_VisibleParticlesBufferPassId, data.visibleParticlesHandle);
                        cmd.SetComputeBufferParam(cs, simKernel, s_IndirectArgsBufferId, data.indirectArgsHandle);

                        int totalParticleThreads = data.emitterCount * data.particlesPerEmitter;
                        int threadGroups = (totalParticleThreads + 63) / 64;
                        cmd.DispatchCompute(cs, simKernel, threadGroups, 1, 1);

                        // 3. Populate 0-based unity_ParticleInstanceData buffers for standard Unity / User Custom Shaders
                        int buildKernel = data.buildUnityParticleDataKernel;
                        if (buildKernel >= 0 && data.buckets != null)
                        {
                            int maxConvertGroups = Mathf.Max(1, (MinInt(data.maxInstancesPerCommand, totalParticleThreads) + 63) / 64);

                            for (int b = 0; b < data.buckets.Count; b++)
                            {
                                ShurikenMaterialBucket bucket = data.buckets[b];
                                if (!bucket.UsesUnityParticleInstancingProtocol || bucket.ActiveEmitterCount <= 0)
                                {
                                    continue;
                                }

                                cmd.SetComputeIntParam(cs, s_BucketRenderShapeId, (int)bucket.RenderShape);
                                cmd.SetComputeVectorParam(cs, s_BucketStretchAndFlipbookId, bucket.StretchAndFlipbookParams);
                                cmd.SetComputeBufferParam(cs, buildKernel, s_VisibleParticlesBufferPassId, data.visibleParticlesHandle);
                                cmd.SetComputeBufferParam(cs, buildKernel, s_IndirectArgsBufferId, data.indirectArgsHandle);

                                int activeSlots = bucket.ActiveUnityLodBufferCount;
                                for (int lod = 0; lod < activeSlots; lod++)
                                {
                                    if (bucket.LodUnityParticleBuffers[lod] == null)
                                    {
                                        continue;
                                    }

                                    cmd.SetComputeIntParam(cs, s_SourceCommandIndexId, bucket.BaseCommandIndex + lod);
                                    cmd.SetComputeBufferParam(cs, buildKernel, s_TargetUnityParticleBufferId, bucket.LodRenderGraphHandles[lod]);
                                    cmd.DispatchCompute(cs, buildKernel, maxConvertGroups, 1, 1);
                                }
                            }
                        }
                    });
                }
            }
#endif

            private static int MinInt(int a, int b) => a < b ? a : b;
        }
    }
}
