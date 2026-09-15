# UniNet

상용 유니티 게임 서버용 네트워크 프레임워크 라이브러리. Unity `MonoBehaviour`를 기준으로
RPC 호출과 변수 리플리케이션을 제공하고, 언리얼 Network Framework의 기능 수준을 목표로 한다.

> 원칙 — **"사용은 간단하게, 기능은 강력하게"**

## 저장소 구조

| 경로 | 내용 |
| --- | --- |
| `Package/` | UniNet UPM 패키지 (`com.ds.uninet`) — 라이브러리 본체 |
| `Sandbox/` | Unity 6000.0.83f1(6.0 LTS) 샌드박스 프로젝트 — 스파이크·데모·테스트 (폐기 가능) |
| `Document/` | Obsidian Vault — 정의·구조·플랜·결정 기록 (SSoT) |

## 사용법 (구현됨 — P1 전체 + P2 기본)

RPC 메서드는 `partial` 선언 + 본문은 `{Name}_Implementation`에, 선택 검증은 `{Name}_Validate`에 작성한다 (ADR-0007 변경 이력·ADR-0008).

```csharp
public sealed partial class Player : NetworkBehaviour
{
    [Replicated(Notify = nameof(OnHpChanged))]  // 서버 권위 변수 + RepNotify 콜백
    private int _hp = 100;

    // 클라에서 _hp가 네트워크로 변경될 때마다 실행 (이전값 1개 인자)
    private void OnHpChanged(int prevHp) { /* UI 갱신 */ }

    [ServerRpc]                            // 클라 → 서버 (서버 권위)
    private partial void RpcRequestHit(int damage);

    private Task<bool> RpcRequestHit_Validate(int damage)   // 선택 검증 후크
        => Task.FromResult(damage > 0);

    private void RpcRequestHit_Implementation(int damage)    // 서버에서만 실행
    {
        _hp -= damage;
        if (_hp <= 0) RpcPlayDeathFx();
    }

    [ClientRpc(Delivery.Unreliable)]       // 서버 → 클라(들), 전달 모드 지정
    private partial void RpcPlayHitFx(int damage);
    private void RpcPlayHitFx_Implementation(int damage) { /* FX */ }

    [MulticastRpc]                          // 서버 → 서버+전 클라 (클라 호출 시 로컬 전용)
    private partial void RpcPlayDeathFx();
    private void RpcPlayDeathFx_Implementation() { /* 사망 FX */ }

    private void Update()
    {
        if (!IsOwner) return;
        if (Input.GetKeyDown(KeyCode.Space))
            RpcRequestHit(10);
    }
}

// 연결 시작 (보안·타임아웃·DTLS는 UniNetEndpointOptions로 지정)
await UniNetManager.HostAsync(7777);          // 서버+클라 한 프로세스 (개발용)
// await UniNetManager.ServerAsync(7777);     // 전용 서버
// await UniNetManager.ClientAsync("127.0.0.1", 7777);  // 클라이언트
```

전체 사용법: `Sandbox/Assets/Scripts/` · 검증: EditMode/PlayMode 유닛 테스트 + 2-프로세스 RUDP 왕복 (`Sandbox/Assets/Tests/`)

## 개발 환경

- 샌드박스가 패키지를 로컬 참조한다: `Sandbox/Packages/manifest.json` → `"com.ds.uninet": "file:../../Package"`
- 기반 스택(DRPC·MessageProtocol·Communication)은 NuGetForUnity 4.5.0 + 로컬 소스 `../unity-nuget/`로 로드
- sln/csproj는 Unity가 asmdef 기준 자동 생성 (직접 작성하지 않는다)
- 배치 검증: `-batchmode -quit -nographics -disable-assembly-updater`

문서: [Document/00-INDEX.md](Document/00-INDEX.md) · 환경 결정: [Document/decisions/0005-개발-환경-샌드박스-upm.md](Document/decisions/0005-개발-환경-샌드박스-upm.md)
