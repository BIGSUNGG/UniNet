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

## 사용법 (구현됨 — P1 전체 + P2 전체)

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

    [ServerRpc]                             // MP [Message] 타입 파라미터 + 다형성 공식 지원
    private partial void RpcApplyDamage(DamageMsg damage);
    private void RpcApplyDamage_Implementation(DamageMsg damage)
    {
        if (damage is CriticalHitMsg crit)  // 자식 인스턴스 → 자식 캐스팅·필드 온전
            _hp -= (int)(damage.Amount * (crit.Multiplier - 1f));
    }

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

### 동적 스폰/파괴 + 조건부 리플리케이션 (P2)

```csharp
// 서버에서 일반 Instantiate 후 Spawn 한 줄 — 전 클라에 스폰 전파 (사용법: Scripts/Weapon.cs)
var projectile = Instantiate(_projectilePrefab, pos, rot);
UniNetManager.Spawn(projectile.gameObject);
UniNetManager.NetworkDestroy(projectile.gameObject);   // 파괴 동기화

// 클라 생성용 프리팹 카탈로그 (미등록 시 빈 GameObject+AddComponent로 생성)
UniNetManager.RegisterPrefab<Projectile>(projectilePrefab);

public sealed partial class Projectile : NetworkBehaviour
{
    [Replicated] private float _x;                                              // 전 클라 항상
    [Replicated(ReplicateCondition.OwnerOnly, Notify = nameof(OnDamageChanged))]
    private int _damage;                                                        // 소유 클라만
    [Replicated(ReplicateCondition.InitialOnly)] private int _seed;            // 스폰 시 1회만

    private void Update()
    {
        if (!IsServer) return;            // 서버 권위 시뮬레이션
        _x += 8f * Time.deltaTime;
        if (_age >= _lifetime) UniNetManager.NetworkDestroy(gameObject);
    }
}
// 후발 접속 클라에는 기존 동적 오브젝트가 자동 합류된다 (캐치업)
```

### 다중 NetworkBehaviour — 한 오브젝트에 여러 네트워크 컴포넌트 (2층 식별자)

```csharp
// 이동·체력 서브오브젝트를 한 오브젝트에 — 각자 RPC·[Replicated] 선언 가능 (ADR-0010)
public sealed partial class MovementBrain : NetworkBehaviour { [Replicated] public int Speed; ... }
public sealed partial class HealthTank : NetworkBehaviour
{
    [Replicated(ReplicateCondition.OwnerOnly)] public int Armor;
}

var go = new GameObject("robot");
go.AddComponent<MovementBrain>();
go.AddComponent<HealthTank>();
UniNetManager.Spawn(go);   // 전 컴포넌트가 서브 테이블(SubId 슬롯)로 등록된다
// 다중 컴포넌트 동적 스폰은 RegisterPrefab 필수 — 소유권·파괴는 오브젝트 단위
```

전체 사용법: `Sandbox/Assets/Scripts/` · 검증: EditMode/PlayMode 유닛 테스트 + 2-프로세스 RUDP 왕복 (`Sandbox/Assets/Tests/`)

## 수명주기 종료 (Stop API)

서버·클라·호스트는 종료 시 명시적으로 정지해야 동일 포트 재리슨이 바인딩 실패 없이 성공한다:

```csharp
await UniNetManager.ServerStopAsync();   // 리스너 정지 + 환경 정리 (동기 ServerStop / ClientStop / HostStopAsync / HostStop도 제공)
```

게임은 `OnApplicationQuit` 등 종료 경로에서 동기 버전(`ServerStop`·`ClientStop`·`HostStop`)을 호출한다. 멱등 — 리슨 중이 아니면 무작동.

## 예시 게임 (Sandbox)

**아레나 슈팅** — 구현된 기능 전부(P1 RPC 3종·검증 후크·오브젝트 RPC / P2 조건부 리플리케이션·RepNotify·동적 스폰/파괴)를 활용하는 탑다운 2~4인 슈팅 데모. 에셋 없이 Unity 기본 도형만 사용하며, MPPM(Multiplayer Play Mode)으로 메인 에디터=서버 + 가상 플레이어 2=클라이언트를 한 에디터에서 실행한다.

- 씬: `Sandbox/Assets/Scenes/Arena.unity` · 코드: `Sandbox/Assets/Scripts/Arena/`
- 실행·기능 매트릭스·자동 검증: [Document/examples/arena-shooter.md](Document/examples/arena-shooter.md)

## 개발 환경

- 샌드박스가 패키지를 로컬 참조한다: `Sandbox/Packages/manifest.json` → `"com.ds.uninet": "file:../../Package"`
- 기반 스택(DRPC·MessageProtocol·Communication)은 NuGetForUnity 4.5.0 + 로컬 소스 `../unity-nuget/`로 로드
- sln/csproj는 Unity가 asmdef 기준 자동 생성 (직접 작성하지 않는다)
- 배치 검증: `-batchmode -quit -nographics -disable-assembly-updater`

문서: [Document/00-INDEX.md](Document/00-INDEX.md) · 환경 결정: [Document/decisions/0005-개발-환경-샌드박스-upm.md](Document/decisions/0005-개발-환경-샌드박스-upm.md)
