# Changelog

All notable changes to **ShurikenBridge** will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-11-19
### Added
- **Drop-In Shuriken Hijack Bridge (`ShurikenBridgeEmitter`)**: Uses `[RequireComponent(typeof(ParticleSystem))]` to keep 100% of Unity's native `ParticleSystem` Inspector UI and Scene View gizmos while pausing CPU Shuriken simulation (`0.000 ms` CPU cost).
- **Zero-Config URP Auto-Injection (`ShurikenBridgeManager`)**: Automatically injects a `BeforeRenderingOpaques` (L1203) Compute Pass via `RenderPipelineManager.beginCameraRendering` supporting Unity 2022.3 LTS+ (`URP 14.0+` `Execute`), Unity 6 `RenderGraph` (`RecordRenderGraph`), and Unity 6 `URP Compatibility Mode` with zero URP source code changes and zero manual `RendererFeature` asset setup.
- **Universal Shader, Material & Custom Mesh Compatibility**:
  - Direct 0-Copy Mega-Mesh mode for `ShurikenBridge/LitMesh` and `ShurikenBridge/AdditiveBillboard`.
  - Native Unity Particle Instancing (`unity_ParticleInstanceData` 56-Byte `DefaultParticleInstanceData`) GPU builder (`CSBuildUnityParticleInstanceData`) supporting unmodified `URP/Particles/Lit`, `URP/Particles/Simple Lit`, `URP/Particles/Unlit`, Shader Graph, and custom HLSL shaders for Billboards, Stretched Billboards, and 3D Meshes.
  - Automatic property-preserving bridging for standard mesh shaders (`URP/Lit`, `URP/Simple Lit`, `URP/Unlit`, `Standard`).
  - 1-Click Custom Shader Auto-Patcher in `ShurikenBridgeEmitterEditor` + `ShurikenBridgeCustomShaderInclude.hlsl` + `ShurikenBridgeShaderGraphNode.hlsl`.
  - Custom 3D mesh rendering directly from `ParticleSystemRenderer.mesh`.
- **Full GPU Shuriken Module Coverage**:
  - `Main` (Duration, Looping, Prewarm, StartDelay, StartLifetime, StartSpeed, StartSize / StartSize3D, StartRotation / StartRotation3D, StartColor, GravityModifier, SimulationSpeed, Local & World Simulation Space, RandomSeed)
  - `Emission` (RateOverTime + Synchronized Multi-Burst with Time, Count, RepeatInterval, CycleCount)
  - `Shape` (Cone, Sphere, Hemisphere, Box, Circle, Edge, Donut, RadiusThickness, Local Position/Rotation Offset)
  - `Velocity over Lifetime` (Linear, Orbital Swirl, Speed Modifier Curve) & `Force over Lifetime` & `Limit Velocity over Lifetime` (Drag)
  - `Color over Lifetime`, `Size over Lifetime`, `Rotation over Lifetime` (Hash-deduplicated 64×512 `RGBAHalf` LUT)
  - `Color by Speed`, `Size by Speed`, `Rotation by Speed`
  - `Noise` (3D Curl/Simplex Turbulence + Strength Curve)
  - `Collision` (Analytical Planes mode up to 6 planes per emitter & up to 3 consecutive parabolic bounces with Bounce, Dampen, and LifetimeLoss — 0 PhysX CPU cost, 0 extra UAVs)
  - `External Forces` (`ParticleSystemForceField` directional wind, radial gravity/repulsion, and vortex swirl — up to 32 active fields in scene)
  - `Texture Sheet Animation` (Flipbook UV tiling)
- **GPU Frustum Culling, 3-Tier Distance LOD & Draw Optimization**:
  - 6-Plane GPU Frustum Culling, 3-Tier Mesh LOD (`LOD 0/1/2`), Stochastic Distance Thinning, Single Shared Material + Back-to-Front `MaterialPropertyBlock` sorting, and `CollapseLodToCommand0` single-buffer consolidation for Billboards & Custom Meshes.
- **Cross-Platform Compliance & Graceful Fallback**:
  - Strictly $\le 2$ UAVs per compute kernel, `startInstance = 0`, `baseVertex = 0`, 16-byte struct alignment (Metal, DX11, DX12, Vulkan, OpenGL ES 3.1), and automatic fallback to native `ParticleSystem` when `!SystemInfo.supportsComputeShaders`.
- **Built-in Render Pipeline (BiRP) Compatibility**:
  - Automatic `Camera.onPreCull` / `Camera.onPostRender` compute & indirect draw fallback when `GraphicsSettings.currentRenderPipeline == null`, plus Built-in RP SubShaders in `ShurikenBridgeAdditiveBillboard.shader` and `ShurikenBridgeLitMesh.shader` and automatic property bridging for `Particles/Standard Unlit` and `Particles/Standard Surface`.
- **Release Hardening & Mobile Vulkan Verification**:
  - Non-serialized `Renderer.forceRenderingOff` suppression (0 dirty scene/prefab diffs in Edit Mode), unsupported module auto-fallback (`Trails`, `SubEmitters`, `Trigger`, `Lights`, `MeshShape`), one-shot `IsAlive()` + `stopAction` (`Disable`/`Destroy`/`Callback`) parity, `main.scalingMode` (`Hierarchy`/`Local`/`Shape`) parity, per-layer `Camera.cullingMask` bucket isolation, adaptive `RequestParticlesPerEmitterBudget()`, 1-Click Scene Convert/Revert tools, and verified mobile performance on Qualcomm Adreno 650 (Android Vulkan).
- **Standalone UPM Sample (`Samples~/FactoryBenchmark`)**: Self-contained 2,048-emitter (131,072 particles) 3-Phase A/B benchmark using `Resources.GetBuiltinResource<Mesh>` for mobile IL2CPP engine-code-stripping safety.
