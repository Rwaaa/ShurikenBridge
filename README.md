# ShurikenBridge (`com.shurikenbridge.core`)

**[English](README.md) | [한국어 (Korean)](README.ko.md)**

> **Zero-CPU Drop-In GPU Particle System Bridge for Unity's Built-In `ParticleSystem` (Shuriken) on URP 14–17 & Built-in RP**

Unity's built-in **Shuriken (`ParticleSystem`)** simulates and sorts every particle on the CPU (`ParticleSystem.WaitForAsync` / `Update`), causing severe main-thread bottlenecks (`100+ ms/frame`) and thousands of draw calls when hundreds or thousands of emitters are active. While Unity offers **VFX Graph**, migrating hundreds of existing Shuriken prefabs (or Asset Store VFX packs) to VFX Graph requires rebuilding every effect from scratch.

**`ShurikenBridge`** lets you keep **100% of Unity's native `ParticleSystem` Inspector UI, curves, gradients, and Scene View gizmos**, while hijacking simulation, analytical plane-bounce collision, external force fields, frustum culling, 3-tier distance LOD, and multi-emitter batching entirely onto the GPU via Compute Shaders and `Graphics.RenderMeshIndirect`.

---

## 1. What Performance Gains Can You Expect? (Measured Benchmark)

### Test Environment
- **Hardware**: Apple M4 Pro (`1038×760`, `vSyncCount = 0`)
- **Engine**: Unity 6000.x (URP 17.3 RenderGraph, Linear Color Space)
- **Workload**: **2,048 Active Emitters / 131,072 Particles across 4 Materials** (All 6 advanced modules enabled: `Main`, `Multi-Burst`, `Donut/Cone/Sphere/Box Shapes`, `Color/Size/Rotation Over Lifetime & By Speed`, `Noise`, `Analytical Plane Collision` up to 3 bounces, and `ParticleSystemForceField` External Forces)

| Metric | Phase 1: Built-in CPU Shuriken (`ParticleSystem`) | Phase 2: `ShurikenBridge` Wide View (59,096 Visible Particles) | Phase 3: `ShurikenBridge` Zoomed Sector (`81.6%` GPU Frustum Culled) | Improvement (Native $\to$ `ShurikenBridge`) |
| :--- | :---: | :---: | :---: | :---: |
| **Average FPS** | `5.3 FPS` | **`555.6 FPS`** | **`537.0 FPS`** | **`104.8x` Faster (`+10,446%`)** |
| **Average Frame Time** | `189.809 ms` | **`1.800 ms`** | **`1.862 ms`** | **`-99.1%` Frame Time** |
| **PlayerLoop CPU** | `138.145 ms` | **`1.126 ms`** | **`1.179 ms`** | **`122.7x` Lower CPU Cost** |
| **Native Shuriken CPU** | `97.674 ms` | **`0.001 ms`** | **`0.001 ms`** | **`-99.999%` Eliminated** |
| **GPU Compute SimPass** | `0.000 ms` | **`0.020 ms` (`20 µs`)** | **`0.020 ms` (`20 µs`)** | **131k Particles Simulated in `20 µs`** |
| **>20ms Frame Spikes** | `90 / 90 frames` (`100%`) | **`0 / 90 frames` (`0%`)** | **`0 / 90 frames` (`0%`)** | **100% Spike Elimination** |
| **Draw Calls** | `1,448.4` | **`26.0`** | **`23.0`** | **`-98.2%` Draw Call Reduction** |
| **SetPass Calls** | `16.0` | **`9.0`** | **`10.0`** | **`-43.8%` Reduction** |

---

## 2. Requirements & Pipeline Compatibility

| Category | Supported Specifications |
| :--- | :--- |
| **Unity Version** | **Unity 2022.3 LTS** or newer (including **Unity 6 / 6000.x** RenderGraph & Compatibility Mode) |
| **Render Pipelines** | • **URP 14.0 – 17.x (`UniversalRenderer`)** (Zero-config pass injection via `RenderPipelineManager.beginCameraRendering`)<br>• **Built-in Render Pipeline (BiRP)** (Automatic fallback via `Camera.onPreCull` + Built-in SubShaders & `Particles/Standard Unlit` / `Surface` bridging) |
| **Graphics APIs** | DirectX 11/12, Metal (macOS/iOS), Vulkan, OpenGL ES 3.1+ (Strictly $\le 2$ UAVs per Compute Kernel, `maxComputeBufferInputsVertex > 0` runtime guard) |
| **External Dependencies** | **Zero mandatory external dependencies.** |

---

## 3. Installation

### Option A: Package Manager (`manifest.json` / Git URL)
Add `com.shurikenbridge.core` via **Window → Package Manager → + → Add package from git URL...**:
```text
https://github.com/Rwaaa/ShurikenBridge.git
```
Or add it directly to your project's `Packages/manifest.json`:
```json
{
  "dependencies": {
    "com.shurikenbridge.core": "https://github.com/Rwaaa/ShurikenBridge.git"
  }
}
```

### Option B: Direct `Assets/ShurikenBridge` Folder
Copy the `Assets/ShurikenBridge` folder into your Unity project's `Assets/` directory. `ShurikenBridgeResourcesAutoSetup` will automatically validate and populate `ShurikenBridgeResources.asset`.

---

## 4. How to Use (Step-by-Step Guide & Code Examples)

### 4-1. 1-Click Scene Conversion & 1-Click Revert
- **Convert Scene**: Click **`Tools → ShurikenBridge → Convert All Scene ParticleSystems to ShurikenBridge`**.
  - Automatically attaches `ShurikenBridgeEmitter` to all compatible `ParticleSystem`s in the scene.
  - Automatically skips particle systems using unsupported stateful modules (`Trails`, `SubEmitters`, `Trigger`, `Lights`, `Mesh/SkinnedMesh` surface emission, or `RenderMode.None`) so complex multi-layered VFX prefabs keep 100% visual fidelity.
- **Revert Scene**: Click **`Tools → ShurikenBridge → Revert All Scene ShurikenBridge Emitters to Native`** (Full Undo support, removes `ShurikenBridgeEmitter` and restores pure native CPU Shuriken).

---

### 4-2. Drop-In `ShurikenBridgeEmitter` Component
Attach **`ShurikenBridgeEmitter`** (`Add Component → ShurikenBridge → Shuriken Bridge Emitter`) to any `GameObject` with a `ParticleSystem`:

- **Keep Editing in the Native `ParticleSystem` Inspector**:
  - Edit `Start Lifetime`, `Start Speed`, `Start Size (1D/3D)`, `Start Rotation (1D/3D)`, `Start Color`, `Gravity Modifier`, `Simulation Space (World/Local)`, `Scaling Mode (Hierarchy/Local/Shape)`, `Prewarm`, `Emission Rate`, `Bursts`, `Shape (Cone, Sphere, Hemisphere, Box, Circle, Edge, Donut)`, `Velocity/Force/LimitVelocity Over Lifetime`, `Color/Size/Rotation Over Lifetime & By Speed`, `Noise`, `Texture Sheet Animation (Flipbook)`, `Collision (Planes / World y=0)`, and `External Forces`.
  - Curves and gradients are automatically baked into a shared `256×128 RGBAHalf` GPU Lookup Texture (`ShurikenBridgeCurveLutBaker`) with hash deduplication.
- **Automatic Runtime Property Sync**:
  - With `Auto Sync Runtime Properties` enabled (`true` by default), your existing gameplay code can call `particleSystem.Play()`, toggle `emission.enabled = false`, adjust `emission.rateOverTime`, or change `main.startColor` at runtime **without needing manual `MarkDirty()` calls**.
- **One-Shot (`loop = false`) Lifecycle & `StopAction` Parity**:
  - Non-looping effects automatically execute `main.stopAction` (`Disable`, `Destroy`, or `Callback` $\to$ `OnParticleSystemStopped()`) when `StartDelay + Duration + StartLifetimeMax` completes.

#### Controlling Playback & Object Pooling from C#
```csharp
using ShurikenBridge;
using UnityEngine;

public class VfxPooledEffect : MonoBehaviour
{
    [SerializeField] private ShurikenBridgeEmitter _emitter;

    private void OnEnable()
    {
        // Starts or restarts GPU emission from t = 0
        _emitter.Restart();
    }

    private void Update()
    {
        // Equivalent to ParticleSystem.IsAlive() for one-shot effects
        if (!_emitter.IsAlive())
        {
            gameObject.SetActive(false); // Return to pool
        }
    }
}
```

---

### 4-3. Shader Compatibility & Custom Shader Auto-Patcher

`ShurikenBridge` automatically classifies and supports 3 tiers of materials without manual shader rewrites:

1. **Direct Mega-Mesh Shaders (`0-Copy` Single Indirect Draw)**:
   - `ShurikenBridge/AdditiveBillboard` (Procedural radial soft-particle & velocity-stretched billboard, URP + Built-in RP)
   - `ShurikenBridge/LitMesh` (3D Mesh particle lighting with volumetric rim & heat emission, URP + Built-in RP)
2. **Auto-Bridged Standard Shaders**:
   - Materials using `Universal Render Pipeline/Particles/Lit`, `Simple Lit`, `Unlit`, `URP/Lit`, `Standard`, or Built-in RP `Particles/Standard Unlit`, `Particles/Standard Surface`, `Legacy Shaders/Particles/*` are automatically bridged via Unity's 56-byte `unity_ParticleInstanceData` GPU buffer (`CSBuildUnityParticleInstanceData`).
3. **Custom HLSL Shaders & Shader Graph**:
   - **1-Click Inspector Auto-Patcher**: If you assign a custom `.shader` that lacks `#pragma instancing_options procedural:ParticleInstancingSetup`, a **1-Click Auto-Patch** button appears directly in the `ShurikenBridgeEmitter` Inspector.
   - **Shader Graph**: Add a **Custom Function** node pointing to `Assets/ShurikenBridge/Shaders/ShurikenBridgeShaderGraphNode.hlsl`.

---

## 5. 3-Tier Play Mode Scene View Debug Mode

Switch modes anytime via **`Tools → ShurikenBridge → Play Mode Scene View`** or from the `ShurikenBridgeEmitter` Inspector:

| Mode | Behavior | Use Case |
| :--- | :--- | :--- |
| **`Off` *(Default)*** | Skips Scene View GPU simulation during Play Mode. **Automatically forced `Off` in all Player Builds** via `IPreprocessBuildWithReport` and `#if !UNITY_EDITOR`. | Maximum gameplay & benchmark FPS |
| **`GpuParticles`** | Renders GPU particles in Scene View with **`ShurikenBridgeSceneViewPicker`**: click **any individual flying GPU particle** in Scene View to select its owning `ShurikenBridgeEmitter` GameObject and view its live trajectory ring, age, and speed HUD! | Level editing & clicking flying particles in Scene View |
| **`NativeShuriken`** | Uses per-camera `Renderer.forceRenderingOff` toggling so **Scene View runs 100% pure native CPU Shuriken** (native click/box-select/gizmos) while **Game View renders GPU `ShurikenBridge` simultaneously**. | Instant side-by-side 1:1 visual parity verification (trajectory, color, collision bounce) |

---

## 6. Supported vs. Auto-Fallback Modules & Cautions

### Module Support Matrix

| Shuriken Module / Feature | Status | Details |
| :--- | :---: | :--- |
| **Main Module** (Duration, Loop, Prewarm, Delay, Lifetime, Speed, 1D/3D Size & Rotation, Color, Gravity, Local/World Space, ScalingMode) | **GPU Accelerated** | Stateless deterministic `PcgHash` evaluation (`0.000 ms` CPU) |
| **Emission & Multi-Burst** (RateOverTime + Repeat Bursts) | **GPU Accelerated** | Exact cycle & phase evaluation |
| **Shape Module** (`Cone`, `Sphere`, `Hemisphere`, `Box`, `Circle`, `Edge`, `Donut` + `RadiusThickness` + Local Offset/Rotation) | **GPU Accelerated** | 100% axis parity (`+Z` emission on Cone/Hemisphere/Box, `XY` on Circle/Donut) |
| **Over Lifetime & By Speed** (`Velocity`, `Force`, `LimitVelocity/Drag`, `Color`, `Size`, `Rotation`) | **GPU Accelerated** | Shared `256×128 RGBAHalf` Curve/Gradient LUT |
| **Noise / Curl Turbulence** | **GPU Accelerated** | 3D trigonometric curl field on GPU |
| **Texture Sheet Animation (Flipbook)** (`Tiles X/Y`) | **GPU Accelerated** | Automatic UV frame stepping via `unity_ParticleUVShiftData` |
| **Collision Module** (`Planes` up to 6 transforms + `World` `y=0` floor fallback) | **GPU Accelerated** | Analytical quadratic trajectory solver (up to 3 exact bounces, Bounce/Dampen/LifetimeLoss, 0 PhysX CPU cost) |
| **External Forces Module** (`ParticleSystemForceField` Gravity/Direction/Vortex) | **GPU Accelerated** | Up to 32 simultaneous `ParticleSystemForceField`s in scene |
| **Multi-Camera `Camera.cullingMask` & `gameObject.layer`** | **GPU Accelerated** | Per-layer bucket isolation (UI/Minimap camera safe) |
| **Non-Serialized `Renderer.forceRenderingOff` Suppression** | **Built-In** | Never modifies `ParticleSystemRenderer.enabled` in Edit Mode (0 dirty `.unity`/`.prefab` diffs in Git) |
| **Trails / Ribbons**, **Sub-Emitters**, **Trigger**, **Lights**, **Mesh/SkinnedMesh Emitter Shape**, **`RenderMode.None`** | **Auto-Fallback** | Automatically left on native CPU Shuriken when `Auto Fallback Unsupported Modules` is enabled (`true` by default) |

### Important Cautions & Best Practices
1. **Moving Emitters & `Simulation Space = World`**:
   - Because `ShurikenBridge` evaluates particle trajectories analytically in $O(1)$ without per-frame state history buffers, particles emitted from a rapidly moving emitter in `World` space move relative to the emitter's current transform (or use `Local` space). For sword trails or projectiles requiring historical world-space breadcrumbs, enable the `Trails` module (which automatically falls back to native CPU Shuriken) or keep that specific emitter on native Shuriken.
2. **Per-Emitter Particle Cap (`ParticlesPerEmitter`)**:
   - `ShurikenBridgeManager` starts with a default slot budget of `64` particles per emitter (`2,048 emitters × 64 = 131,072 particles`) and **automatically expands** `_particlesPerEmitter` up to `1,024` when an emitter requests more particles (as long as the total scene budget stays within `131,072`). You can also click **Expand Manager Per-Emitter Cap** directly in the `ShurikenBridgeEmitter` Inspector.
3. **Collision Module Setup**:
   - In `CollisionType.Planes`, assign up to 6 floor/wall `Transform`s in the `ParticleSystem` Collision module. If 0 planes are assigned in `Planes` mode, collision is disabled (matching native Shuriken 1:1). In `CollisionType.World`, `ShurikenBridge` uses an analytical horizontal ground plane (`y = 0`) with zero PhysX raycast overhead.

---

## License
MIT License. See [LICENSE.md](LICENSE.md).
