# UniNet — 상용 유니티 게임 서버용 네트워크 프레임워크

MonoBehaviour 기준 RPC + 변수 자동 Replicate. 서버 권위, Unity 6000.0+, netstandard2.1.

## 설치 (Git URL)

프로젝트 `Packages/manifest.json`의 `dependencies`에 한 줄 추가:

```json
"com.ds.uninet": "https://github.com/BIGSUNGG/UniNet.git?path=/Package#v0.1.0"
```

- `BIGSUNGG/UniNet` → 실제 저장소 주소로 교체
- `#v0.1.0` → 버전 태그. 생략 시 기본 브랜치 HEAD 추적
- 사설 저장소: Unity가 git clone에 로컬 git 자격증명(credential helper / SSH)을 사용한다. 에디터 실행 계정에서 `git clone`이 되면 UPM도 된다.

의존성은 패키지 안에 전부 동봉되어 있다 — 별도 NuGet 피드·추가 설정 불필요.

## 퀵스타트

```csharp
using System.Threading.Tasks;
using UniNet.Core;
using UniNet.Unity;
using UnityEngine;

public sealed partial class Player : NetworkBehaviour
{
    [Replicated(Notify = nameof(OnHpChanged))]   // 서버 권위 변수 — 전 클라 자동 동기화
    private int _hp = 100;

    private void OnHpChanged(int prevHp) => Debug.Log($"HP {prevHp} -> {_hp}");

    [ServerRpc(Validate = true)]                  // 클라 → 서버 (소유자 전용 + 검증 훅)
    private partial void RpcHit(int damage);
    private Task<bool> RpcHit_Validate(int damage) => Task.FromResult(damage > 0);
    private void RpcHit_Implementation(int damage)
    {
        _hp -= damage;
        RpcHitFx(damage);
    }

    [ClientRpc(Delivery.Unreliable)]              // 서버 → 클라
    private partial void RpcHitFx(int damage);
    private void RpcHitFx_Implementation(int damage) => Debug.Log($"hit fx -{damage}");

    private void Update()
    {
        if (!IsOwner) return;
        if (Input.GetKeyDown(KeyCode.Space)) RpcHit(10);
    }
}

public static class Game
{
    public static Task Main() => UniNetManager.HostAsync(7777);  // 서버+클라 한 프로세스
    // 전용 서버: UniNetManager.ServerAsync(7777) / 클라: UniNetManager.ClientAsync("127.0.0.1", 7777)
}
```

- 전체 API: 저장소 `README.md` (RPC 4종 · 동적 스폰/파괴 · 조건부 리플리케이션 · 가시성/우선순위/휴면/주기/예산 · NetworkTransform · 시간 동기화/예측/리와인드/그리드)
- 동작 예제: Package Manager → UniNet → Samples → Basics 가져오기

## 패키지 구성

| 경로 | 내용 |
| --- | --- |
| `Runtime/UniNet.Core` | 엔진 비의존 코어 (계약·호스팅·직렬화) |
| `Runtime/UniNet.Unity` | MonoBehaviour 바인딩 (NetworkBehaviour · UniNetManager · NetworkTransform) |
| `Runtime/Dependencies` | 기반 스택 DLL (DRPC · MessageProtocol · Communication · LiteNetLib · BouncyCastle) |
| `Runtime/UniNet.Unity/Analyzers` | Roslyn 소스 생성기 3종 |

라이선스: MIT (`LICENSE.md`)
