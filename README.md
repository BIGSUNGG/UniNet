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

## 사용법 (구현됨 — P1 전체 + P2 전체 + P3 + P4 훅)

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

// 클라 생성용 프리팹 카탈로그 (선택 — 커스텀 비주얼/사전 구성용. 미등록 시 빈 GameObject+AddComponent로 생성되며,
// 서버의 서브 구성(NetworkTransform 등 추가 컴포넌트 포함)은 스폰 메시지의 typeKeys로 자동 복원된다)
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
// 다중 컴포넌트도 동일 — 서브 구성은 스폰 메시지 typeKeys로 클라에 자동 복원 (ADR-0014)
// RegisterPrefab은 커스텀 비주얼·사전 구성이 필요할 때만 사용
```

### 리플리케이션 고급 정책 (P3 — 가시성·우선순위·휴면·주기·채널 예산)

```csharp
public sealed partial class Bullet : NetworkBehaviour
{
    public void Init()
    {
        // 서버 틱이 읽는 정책 — 설정만 하면 동작한다 (UE NetUpdateFrequency/NetCullDistance 상응)
        NetworkUpdateFrequencyHz = 30f;   // P3-④ 궤적 전송을 30Hz로 제한 (미도달 변경분은 최신값으로 합쳐짐)
        NetworkCullDistance = 24f;        // P3-① 이 반경 밖 연결에는 전송하지 않음 (접근 시 스폰 자동 전달)
    }

    public override bool IsNetworkRelevant(long viewerConnId)      // P3-① 사용자 정의 관련성 훅 (선택)
        => viewerConnId == _allowedConnId;
}

public sealed partial class Player : NetworkBehaviour
{
    private void Die()
    {
        NetworkDormant = true;            // P3-③ 휴면 — 델타 비교·전송 중단 (유휴 오브젝트 비용 제거)
    }

    private void Respawn()
    {
        _hp = MaxHp;
        FlushNetworkDormancy();           // P3-③ 해제 — 휴면 중 누적 변경분이 한 번에 전송된다
        NetworkPriority = 2f;             // P3-② 대역폭 부족 시 높은 우선순위부터 전송
    }
}

// 서버(부트스트랩) — 컬 판정 기준점·대역폭 예산 제공
server.SetViewerPosition(connId, x, y, z);                 // P3-① 뷰어 위치 (미설정 연결은 컬 무효)
server.SetReplicationChannelBudget(typeof(Bullet), 512);   // P3-⑤ 유형별 틱 예산 — 총알 홍수가 타 유형을 굶기지 않음
server.ReplicationBudgetPerTickBytes = 8192;               // P3-② 전역 틱 예산 (0 = 무제한 기본, 초과분은 기아 보정 후 다음 틱)
```

### NetworkTransform 컴포넌트 (P4 — 이동 예측·보간 내장)

```csharp
// 위치 복제·소유 클라 예측·리모트 인터폴레이션을 컴포넌트 하나로 — 게임은 이동 규칙만 주입
public sealed partial class Character : NetworkBehaviour
{
    private NetworkTransform _nt;

    private void Awake()
    {
        _nt = GetComponent<NetworkTransform>();                    // [RequireComponent]로 자동 결합
        _nt.MovementRule = (ref float x, ref float y, ref float z,
                            float ix, float iy, float iz, float dt) =>
        {
            x += ix * 5f * dt;                                     // 게임 이동 규칙 (서버·예측 공유)
        };
    }

    private void HandleInput()
    {
        _nt.SubmitMove(inputX, inputY, 0);   // 소유 클라: 예측 즉시 반영 + 서버 전송
        _nt.SimulationEnabled = IsAlive();   // 게이트 — 서버·예측 동시 정지/재개
    }
}
// 리모트 클라: NetworkTransform이 위치를 수신하고 InterpolationDelay(기본 0.12s) 뒤 시점으로 렌더
```

### P4 훅 — 시간 동기화·예측·래그 컴펜세이션·그리드 가시성

```csharp
public sealed partial class Player : NetworkBehaviour
{
    private void Awake()
    {
        NetworkRewindHistory = true;   // P4-② 서버가 위치 히스토리를 기록 (리와인드 대상)
    }

    // 발사 — 발신자가 조준한 서버 시각을 보내면 서버가 대상을 리와인드해 판정한다
    private partial void RpcFire(float dirX, float dirY, double hitTime);
    private void RpcFire_Implementation(float dirX, float dirY, double hitTime)
    {
        double t = Math.Clamp(hitTime, UniNetTime.Now - 1.0, UniNetTime.Now);   // 신뢰 경계 클램프
        foreach (var target in FindTargets())
        {
            target.GetHistoryPosition(t, out float hx, out _, out float hz);   // P4-② 과거 위치 질의
            if (HitTest(dirX, dirY, hx, hz)) { target.ApplyDamage(); break; }
        }
    }
}

// 서버(부트스트랩) — 시간 동기화는 드라이버가 자동 전파 (UniNetTime.Now로 어디서든 서버 시각 조회)
server.SetVisibilityGrid(10f, 30f);   // P4-③ 그리드 공간 분할 가시성 (선택)

// 클라 — 예측·인터폴레이션
NetworkUpdateFrequencyHz = 30f;       // (P3) 전송 주기 제어
// 소유 오브젝트: 입력 즉시 적용(예측) + 서버 상태 수신 시 조정
// 리모트 오브젝트: SnapshotBuffer<T>로 Now - InterpolationDelay 시점 렌더 (적용 시점 제어)
```

전체 사용법: `Sandbox/Assets/Scripts/` · 검증: EditMode/PlayMode 유닛 테스트 + 2-프로세스 RUDP 왕복 (`Sandbox/Assets/Tests/`)

## 수명주기 종료 (Stop API)

서버·클라·호스트는 종료 시 명시적으로 정지해야 동일 포트 재리슨이 바인딩 실패 없이 성공한다:

```csharp
await UniNetManager.ServerStopAsync();   // 리스너 정지 + 환경 정리 (동기 ServerStop / ClientStop / HostStopAsync / HostStop도 제공)
```

게임은 `OnApplicationQuit` 등 종료 경로에서 동기 버전(`ServerStop`·`ClientStop`·`HostStop`)을 호출한다. 멱등 — 리슨 중이 아니면 무작동.

## 예시 게임 (Sandbox)

**아레나 슈팅** — 구현된 기능 전부(P1 RPC 3종·검증 후크·오브젝트 RPC / P2 조건부 리플리케이션·RepNotify·동적 스폰/파괴 / P3 가시성·우선순위·휴면·주기·채널 예산 / P4 클라 예측 이동·히트스캔 래그컴펜세이션·TimeSync·그리드 가시성)를 활용하는 탑다운 2~4인 슈팅 데모. 에셋 없이 Unity 기본 도형만 사용하며, MPPM(Multiplayer Play Mode)으로 메인 에디터=서버 + 가상 플레이어 2=클라이언트를 한 에디터에서 실행한다.

- 씬: `Sandbox/Assets/Scenes/Arena.unity` · 코드: `Sandbox/Assets/Scripts/Arena/`
- 실행·기능 매트릭스·자동 검증: [Document/examples/arena-shooter.md](Document/examples/arena-shooter.md)

## 개발 환경

- 샌드박스가 패키지를 로컬 참조한다: `Sandbox/Packages/manifest.json` → `"com.ds.uninet": "file:../../Package"`
- 기반 스택(DRPC·MessageProtocol·Communication)은 NuGetForUnity 4.5.0 + 로컬 소스 `../unity-nuget/`로 로드
- sln/csproj는 Unity가 asmdef 기준 자동 생성 (직접 작성하지 않는다)
- 배치 검증: `-batchmode -quit -nographics -disable-assembly-updater`

문서: [Document/00-INDEX.md](Document/00-INDEX.md) · 환경 결정: [Document/decisions/0005-개발-환경-샌드박스-upm.md](Document/decisions/0005-개발-환경-샌드박스-upm.md)
