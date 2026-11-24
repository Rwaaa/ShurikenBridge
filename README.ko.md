# ShurikenBridge (`com.shurikenbridge.core`)

**[English](README.md) | [한국어 (Korean)](README.ko.md)**

> **Unity 기본 `ParticleSystem`(Shuriken) 인스펙터와 기즈모를 100% 그대로 유지하면서 URP (14–17) 및 Built-in RP에서 동작하는 Zero-CPU 드롭인 GPU 파티클 브릿지**

Unity의 기본 파티클 시스템인 **Shuriken(`ParticleSystem`)**은 모든 파티클의 물리 시뮬레이션과 버텍스 생성을 CPU(`ParticleSystem.WaitForAsync` / `Update`)에서 수행합니다. 따라서 수백~수천 개의 이미터가 동시에 켜지면 메인 스레드가 **100 ms 이상** 병목에 걸리고 수천 번의 DrawCall이 발생합니다. Unity는 대안으로 **VFX Graph**를 제공하지만, 기존 프로젝트나 애셋 스토어에서 구매한 수백 개의 Shuriken 프리팹을 VFX Graph로 일일이 다시 제작하는 것은 막대한 비용이 듭니다.

**`ShurikenBridge`**는 기존 **`ParticleSystem` 인스펙터 UI, 커브, 그라디언트, 씬뷰 기즈모, 기존 URP/Built-in 머티리얼을 100% 그대로 유지**하면서, 컴포넌트 하나만 부착하면 시뮬레이션, 해석적 평면 바운스 충돌, 외부 ForceField, 6면 프러스텀 컬링, 3단계 거리 LOD, 다중 이미터 배칭을 전부 GPU Compute Shader와 `Graphics.RenderMeshIndirect`로 하이재킹합니다.

---

## 1. 어떤 성능 효과를 얻을 수 있는가? (2,048 이미터 실측 벤치마크)

### 측정 환경
- **하드웨어**: Apple M4 Pro (`1038×760`, `vSyncCount = 0`)
- **엔진 환경**: Unity 6000.x (URP 17.3 RenderGraph, Linear Color Space)
- **워크로드**: **2,048개 활성 이미터 / 131,072개 파티클 / 4종 머티리얼** (6대 고급 모듈 전부 ON: `Main`, `Multi-Burst`, `Donut/Cone/Sphere/Box Shape`, `Over Lifetime & By Speed 커브`, `Noise`, `Collision(Planes) 3회 바운스`, `ParticleSystemForceField` 외부 역장)

| 측정 지표 | Phase 1: 순정 CPU Shuriken (`ParticleSystem`) | Phase 2: `ShurikenBridge` 광역 뷰 (59,096 가시 파티클) | Phase 3: `ShurikenBridge` 섹터 줌인 뷰 (`81.6%` GPU 프러스텀 컬링) | 개선 효과 (Native $\to$ `ShurikenBridge`) |
| :--- | :---: | :---: | :---: | :---: |
| **평균 FPS** | `5.3 FPS` | **`555.6 FPS`** | **`537.0 FPS`** | **`104.8배` 향상 (`+10,446%`)** |
| **평균 Frame Time** | `189.809 ms` | **`1.800 ms`** | **`1.862 ms`** | **`-99.1%` 단축** |
| **PlayerLoop CPU** | `138.145 ms` | **`1.126 ms`** | **`1.179 ms`** | **`122.7배` CPU 부하 감소** |
| **순정 Shuriken CPU** | `97.674 ms` | **`0.001 ms`** | **`0.001 ms`** | **`-99.999%` 완전 제거** |
| **GPU Compute SimPass** | `0.000 ms` | **`0.020 ms` (`20 µs`)** | **`0.020 ms` (`20 µs`)** | **13.1만 파티클 GPU 연산 `20 µs`** |
| **>20ms 프레임 스파이크** | `90 / 90 frames` (`100%`) | **`0 / 90 frames` (`0%`)** | **`0 / 90 frames` (`0%`)** | **프레임 스파이크 100% 제거** |
| **Draw Calls** | `1,448.4` | **`26.0`** | **`23.0`** | **`-98.2%` 감소 (`1,448 -> 26`)** |
| **SetPass Calls** | `16.0` | **`9.0`** | **`10.0`** | **`-43.8%` 감소** |

---

## 2. 지원 환경 및 파이프라인 호환성

| 구분 | 상세 지원 스펙 |
| :--- | :--- |
| **Unity 버전** | **Unity 2022.3 LTS** 이상 (**Unity 6 / 6000.x** RenderGraph 및 Compatibility Mode 완벽 지원) |
| **렌더 파이프라인** | • **URP 14.0 – 17.x (`UniversalRenderer`)** (`RenderPipelineManager.beginCameraRendering` 기반 Zero-Config 자동 주입)<br>• **Built-in Render Pipeline (BiRP)** (`Camera.onPreCull` + Built-in SubShader 및 `Particles/Standard Unlit` · `Surface` 자동 브릿징) |
| **그래픽스 API** | DirectX 11/12, Metal (macOS/iOS), Vulkan, OpenGL ES 3.1+ (커널당 UAV $\le 2$ 준수 및 `maxComputeBufferInputsVertex > 0` 런타임 가드) |
| **외부 패키지 의존성** | **필수 외부 의존성 0개.** |

---

## 3. 설치 방법

### 방법 A: Unity Package Manager (`manifest.json` / Git URL)
유니티 에디터 상단 메뉴 **Window → Package Manager → + → Add package from git URL...**에 아래 주소를 입력합니다:
```text
https://github.com/Rwaaa/ShurikenBridge.git
```
또는 프로젝트의 `Packages/manifest.json`에 아래 항목을 직접 추가합니다:
```json
{
  "dependencies": {
    "com.shurikenbridge.core": "https://github.com/Rwaaa/ShurikenBridge.git"
  }
}
```

### 방법 B: `Assets/ShurikenBridge` 폴더 직접 복사
프로젝트의 `Assets/` 하위에 `Assets/ShurikenBridge` 폴더를 복사하면 에디터 로드 시 `ShurikenBridgeResourcesAutoSetup`이 자동으로 `ShurikenBridgeResources.asset`을 구성합니다.

---

## 4. 사용 방법 및 코드 예시

### 4-1. 1-Click 씬 일괄 변환 및 1-Click 원상복구
- **씬 일괄 변환**: 상단 메뉴 **`Tools → ShurikenBridge → Convert All Scene ParticleSystems to ShurikenBridge`** 클릭
  - 씬 내 모든 호환 `ParticleSystem`에 `ShurikenBridgeEmitter`를 자동 부착합니다.
  - `Trails`(검기/리본), `SubEmitters`(2차 폭발 파편), `Trigger`, `Lights`, `Mesh/SkinnedMesh` 표면 방출, `RenderMode.None`을 사용하는 파티클 레이어는 **자동으로 스킵**하여 복합 VFX 프리팹의 시각적 완성도를 100% 보존합니다.
- **씬 일괄 원상복구**: **`Tools → ShurikenBridge → Revert All Scene ShurikenBridge Emitters to Native`** 클릭 (Full Undo 지원, 모든 브릿지 컴포넌트를 제거하고 순정 CPU Shuriken으로 즉시 복원).

---

### 4-2. 드롭인 `ShurikenBridgeEmitter` 컴포넌트 사용
기존 `ParticleSystem` 오브젝트에 **`ShurikenBridgeEmitter`** (`Add Component → ShurikenBridge → Shuriken Bridge Emitter`)를 추가합니다.

- **순정 `ParticleSystem` 인스펙터에서 그대로 편집**:
  - `Start Lifetime`, `Start Speed`, `Start Size(1D/3D)`, `Start Rotation(1D/3D)`, `Start Color`, `Gravity Modifier`, `Simulation Space(World/Local)`, `Scaling Mode(Hierarchy/Local/Shape)`, `Prewarm`, `Emission Rate`, `Bursts`, `Shape(Cone, Sphere, Hemisphere, Box, Circle, Edge, Donut)`, `Velocity/Force/LimitVelocity Over Lifetime`, `Color/Size/Rotation Over Lifetime & By Speed`, `Noise`, `Texture Sheet Animation(Flipbook)`, `Collision(Planes / World y=0)`, `External Forces`를 순정 인스펙터에서 그대로 조작합니다.
  - 커브와 그라디언트는 해시 중복 제거를 거쳐 공용 `256×128 RGBAHalf` GPU 룩업 텍스처(`ShurikenBridgeCurveLutBaker`)로 자동 베이크됩니다.
- **런타임 프로퍼티 변경 자동 감지**:
  - `Auto Sync Runtime Properties`가 켜져 있으면(기본값 `true`), 기존 게임 코드에서 `particleSystem.Play()`, `emission.enabled = false`, `emission.rateOverTime`, `main.startColor`를 수정할 때 `MarkDirty()`를 따로 호출하지 않아도 즉시 GPU에 반영됩니다.
- **원샷(`loop = false`) 생명주기 및 `StopAction` 1:1 호환**:
  - 루프가 꺼진 원샷 폭발/피격 이펙트는 `StartDelay + Duration + StartLifetimeMax` 경과 시 `main.stopAction`(`Disable`, `Destroy`, `Callback` $\to$ `OnParticleSystemStopped()`)을 순정과 동일하게 자동 실행합니다.

#### C# 스크립트 및 오브젝트 풀링 연동 예시
```csharp
using ShurikenBridge;
using UnityEngine;

public class PooledExplosionVfx : MonoBehaviour
{
    [SerializeField] private ShurikenBridgeEmitter _emitter;

    private void OnEnable()
    {
        // t = 0부터 GPU 파티클 방출 재시작
        _emitter.Restart();
    }

    private void Update()
    {
        // ParticleSystem.IsAlive()와 동일하게 원샷 이펙트 종료 여부 확인
        if (!_emitter.IsAlive())
        {
            gameObject.SetActive(false); // 오브젝트 풀로 반환
        }
    }
}
```

---

### 4-3. 셰이더 호환성 및 1-Click 커스텀 셰이더 패처

`ShurikenBridge`는 머티리얼의 셰이더를 분석하여 3가지 모드로 자동 연결합니다.

1. **Direct Mega-Mesh 셰이더 (`0-Copy` 단일 Indirect Draw)**:
   - `ShurikenBridge/AdditiveBillboard` (프로시저럴 소프트 파티클 및 속도 기반 Stretched Billboard, URP + Built-in RP 지원)
   - `ShurikenBridge/LitMesh` (3D 메시 파티클 조명, 림 라이트 및 열기 발광, URP + Built-in RP 지원)
2. **순정 URP / Built-in 파티클 셰이더 자동 브릿징**:
   - `Universal Render Pipeline/Particles/Lit`, `Simple Lit`, `Unlit`, `URP/Lit`, `Standard` 및 Built-in RP의 `Particles/Standard Unlit`, `Particles/Standard Surface`, `Legacy Shaders/Particles/*`를 사용하는 머티리얼은 Unity 표준 56바이트 `unity_ParticleInstanceData` GPU 버퍼(`CSBuildUnityParticleInstanceData`)로 자동 연결됩니다.
3. **커스텀 HLSL 셰이더 & Shader Graph**:
   - 커스텀 `.shader` 파일에 `#pragma instancing_options procedural:ParticleInstancingSetup`이 없으면 `ShurikenBridgeEmitter` 인스펙터 하단에 **`1-Click Auto-Patch` 버튼이 자동 노출**되어 클릭 한 번으로 패치됩니다.
   - Shader Graph에서는 `Assets/ShurikenBridge/Shaders/ShurikenBridgeShaderGraphNode.hlsl`을 Custom Function 노드로 연결해 사용할 수 있습니다.

---

## 5. 3-Tier Play Mode 씬뷰 디버그 모드

상단 메뉴 **`Tools → ShurikenBridge → Play Mode Scene View`** 또는 인스펙터 드롭다운에서 전환할 수 있습니다.

| 디버그 모드 | 동작 방식 | 주요 용도 |
| :--- | :--- | :--- |
| **`Off` *(기본값)*** | Play Mode 중 씬뷰 시뮬레이션을 생략하여 최고 성능을 유지합니다. **`IPreprocessBuildWithReport` 및 `#if !UNITY_EDITOR` 2중 가드로 모든 플레이어 빌드에서 강제 `Off` 고정**됩니다. | 실전 게임플레이 및 최대 FPS 벤치마크 |
| **`GpuParticles`** | 씬뷰에 GPU 파티클을 렌더링하며, **`ShurikenBridgeSceneViewPicker`**를 통해 **날아가는 개별 GPU 파티클 하나를 마우스로 클릭**해 소속 `ShurikenBridgeEmitter` 오브젝트를 선택하고 실시간 궤적 링·나이·속도 HUD를 확인할 수 있습니다. | 레벨 이펙트 디버깅 및 개별 파티클 클릭 선택 |
| **`NativeShuriken`** | 카메라별 `Renderer.forceRenderingOff` 격리를 통해 **씬뷰는 100% 순정 CPU Shuriken**(네이티브 클릭·박스 선택·기즈모), **게임뷰는 GPU `ShurikenBridge`**로 동시에 렌더링합니다. | 궤적, 색상, 바운스 충돌 1:1 패리티 실시간 대조 |

---

## 6. 지원 모듈 vs 자동 폴백 매트릭스 및 주의사항

### 모듈별 지원 현황 및 자동 폴백

| Shuriken 모듈 / 기능 | 지원 상태 | 상세 내용 |
| :--- | :---: | :--- |
| **Main 모듈** (Duration, Loop, Prewarm, Delay, Lifetime, Speed, 1D/3D Size & Rotation, Color, Gravity, Local/World Space, ScalingMode) | **GPU 가속** | 무상태 결정론적 `PcgHash` 해석 (`0.000 ms` CPU) |
| **Emission & 다중 Burst** (RateOverTime + 반복 Burst) | **GPU 가속** | 정확한 사이클 및 방출 위상 계산 |
| **Shape 모듈** (`Cone`, `Sphere`, `Hemisphere`, `Box`, `Circle`, `Edge`, `Donut` + `RadiusThickness` + 로컬 오프셋/회전) | **GPU 가속** | 순정 방출 축 1:1 패리티 (`Cone/Hemisphere/Box`는 `+Z`, `Circle/Donut`은 `XY`) |
| **Over Lifetime & By Speed** (`Velocity`, `Force`, `LimitVelocity/Drag`, `Color`, `Size`, `Rotation`) | **GPU 가속** | 공용 `256×128 RGBAHalf` 커브/그라디언트 LUT |
| **Noise (Curl Turbulence)** | **GPU 가속** | 3D 삼각함수 기반 난류장 GPU 연산 |
| **Texture Sheet Animation (Flipbook)** (`Tiles X/Y`) | **GPU 가속** | `unity_ParticleUVShiftData` 기반 UV 프레임 애니메이션 |
| **Collision 모듈** (`Planes` 최대 6개 Transform 평면 + `World` 모드 `y=0` 바닥 폴백) | **GPU 가속** | 해석적 2차 방정식 궤적 해법 (최대 3회 연속 반사, Bounce/Dampen/LifetimeLoss, PhysX 비용 0) |
| **External Forces 모듈** (`ParticleSystemForceField` 중력/방향/소용돌이) | **GPU 가속** | 씬 내 최대 32개 `ParticleSystemForceField` 동시 반영 |
| **멀티 카메라 `Camera.cullingMask` & `gameObject.layer`** | **GPU 가속** | 레이어별 버킷 격리 (UI/미니맵 카메라 중복 렌더링 방지) |
| **비직렬화 `Renderer.forceRenderingOff` 억제** | **기본 내장** | Edit Mode에서 `ParticleSystemRenderer.enabled`를 변경하지 않아 `.unity`/`.prefab` Git 오염 0건 |
| **Trails(궤적/리본)**, **Sub-Emitters**, **Trigger**, **Lights**, **Mesh/SkinnedMesh 표면 방출**, **`RenderMode.None`** | **자동 폴백** | `Auto Fallback Unsupported Modules`(기본 `true`)에 의해 자동으로 순정 CPU Shuriken 유지 + 인스펙터 사유 표시 |

### 사용 시 주의사항 (Best Practices)
1. **고속 이동체와 `Simulation Space = World`**:
   - `ShurikenBridge`는 매 프레임 파티클 상태를 버퍼에 적분하지 않고 현재 시간 $t$에서 $O(1)$ 해석적 수식으로 위치를 계산하므로, 고속으로 이동하는 발사체의 잔상(과거 월드 위치 궤적)이 필요한 이펙트는 `Trails` 모듈을 켜거나(자동으로 순정 CPU Shuriken 폴백) 해당 레이어만 `_hijackNativeParticleSystem = false`로 두는 것을 권장합니다.
2. **이미터당 파티클 예산 (`ParticlesPerEmitter`)**:
   - `ShurikenBridgeManager`는 기본 슬롯당 `64`개(`2,048 이미터 × 64 = 131,072 파티클`)로 시작하며, 64개를 초과하는 대량 방출 이미터가 등록되면 전체 예산(`131,072`) 범위 내에서 **최대 `1,024`개까지 자동 확장**합니다. 인스펙터의 **`Expand Manager Per-Emitter Cap`** 버튼으로 수동 상향할 수도 있습니다.
3. **Collision 모듈 사용 팁**:
   - `CollisionType.Planes`에서는 최대 6개의 바닥/벽면 `Transform`을 지정할 수 있습니다(평면을 0개 지정하면 순정 Shuriken과 동일하게 충돌이 비활성화됩니다). `CollisionType.World`로 두면 PhysX 레이캐스트 비용 없이 자동으로 `y = 0` 수평 바닥면 반사가 적용됩니다.

---

## 라이선스
MIT License ([LICENSE.md](LICENSE.md) 참조).
