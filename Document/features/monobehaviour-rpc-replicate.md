# MonoBehaviour RPC & 변수 Replicate

- **상태**: 구현됨 (P1 전체 + P2 전체 + 다중 컴포넌트 — [[0008-구현-아키텍처]]·[[0009-동적-스폰-조건부-리플리케이션]]·[[0010-2층-식별자-다중-컴포넌트]] 참조)
- **최초 작성**: 2026-09-14
- **마지막 갱신**: 2026-09-16

## 개요

`NetworkBehaviour` 파생 타입에 `[ServerRpc]`·`[ClientRpc(Delivery)]`·`[MulticastRpc]` 속성으로 partial 선언하고 본문을 `{Name}_Implementation`에, 선택으로 `{Name}_Validate`에 쓰면 — UniNet 소스 제너레이터가 DRPC 허브 배선과 직렬화를 생성해 서버-클라 간 오브젝트 단위 RPC가 동작한다. `[Replicated(Notify = nameof(...))]` 필드는 서버에서 변경 시 델타가 클라에 동기화되고 RepNotify(이전값) 콜백이 호출된다. 동적 오브젝트는 `UniNetManager.NetworkInstantiate`/`NetworkDestroy`로 생애주기를 동기화하고, `[Replicated(ReplicateCondition.OwnerOnly)]` 등 조건으로 수신자를 제한할 수 있다.

## 요구사항 / 목표

- 사용은 간단하게: 속성 + partial 선언만 (사용법: `Sandbox/Assets/Scripts/Player.cs`)
- 서버 권위: ServerRpc/리플리케이션 주도권은 서버. MulticastRpc 클라 호출은 로컬 전용·비전파
- 기본형·string·[Message] 타입 매개변수·필드 지원(다형성 공식 계약), 잘못된 선언은 컴파일 타임 진단(UNINET0xx)

## 설계

- `CodeGenerator/` (UniNet.CodeGenerator) — Roslyn 4.3 소스 제너레이터. 허브·partial 구현·델타 핸들·진단 방출. **DRPC/MP 제너레이터와 체이닝하지 않고 각 런타임 공개 API로 직접 배선**
- `Package/Runtime/UniNet.Core/Hosting/` — NetworkServer·NetworkClient·UniNetEnvironment(메인 펌프)·UniNetDispatch(전역 디스패치)·UniNetReplicationHandler·UniNetEndpointOptions·Fnv1a
- `Package/Runtime/UniNet.Unity/` — NetworkBehaviour(netId·IsOwner)·UniNetManager(Host/Server/ClientAsync)·UniNetDriver
- netId: 씬 경로 FNV-1a 64 해시 (양단 무합의 일치). 소유권: 라운드로빈(라이브러리 내부)
- 관련 ADR: [[0004-계약-자동-생성]]·[[0007-사용법-우선-api-확정]](변경 이력 — partial 재구조화)·[[0008-구현-아키텍처]]

## 동작 상세

- **RPC 호출 분기** — 서버/호스트: 검증→`_Implementation` 즉시(메인 큐). 전용 클라: 페이로드=`[netId][인자...]` 직렬화→DRPC 전송. 오프라인: 로컬 실행
- **MulticastRpc** — 호출측에서 `_Implementation` 로컬 실행 + (서버면) 전 클라 전파. 호스트 동일 인스턴스 이중 실행 방지(`IsServer` 가드)
- **ClientRpc** — 서버 호출만 전파(클라 호출 무시). UE ClientRpc와 동일
- **리플리케이션** — 서버 틱마다 스냅샷 폴링 비교→변경 필드만 `[mask][값...]` 델타→전 클라. 클라 적용 시 `__uninetSeen`(이전값) 기반으로 Notify(이전값) 호출
- **보안·속도 설정** — `UniNetEndpointOptions`(연결 키·타임아웃·연결 상한·CRC32c·DTLS 인증서/핀닝)가 DRPC 옵션으로 매핑. 게임 코드는 DRPC·MP 타입 노출 없음
- **스레딩** — DRPC 수신은 네트워크 스레드→메인 큐 적재→UniNetDriver.Update에서 실행 (Unity 스레드 안전)

## 메시지 타입 파라미터 (MP [Message] 지원)

RPC 매개변수와 [Replicated] 필드에 MessageProtocol 메시지 타입을 쓸 수 있다 — object 직렬화 경로(MessageId 헤더 디스패치)로 **다형성을 공식 지원**한다:

```csharp
[Message] public partial class DamageMsg { public int Amount { get; set; } }
[Message] public partial class CriticalHitMsg : DamageMsg { public float Multiplier { get; set; } = 2f; }

[ServerRpc]
private partial void RpcApplyDamage(DamageMsg damage);   // 부모 타입 선언

RpcApplyDamage(new CriticalHitMsg { Amount = 20 });       // 자식 인스턴스 전달

private void RpcApplyDamage_Implementation(DamageMsg damage)
{
    if (damage is CriticalHitMsg crit)   // ✅ 수신측에서 자식 캐스팅·자식 필드 온전
        ...
}
```

- 직렬화: 송신 `MessageSerializer.SerializeToWriter`(MessageId 헤더 포함) / 수신 `DeserializeFromReader`(헤더로 구체 타입 복원 후 선언 타입으로 캐스팅)
- 전달하는 **모든 구체 타입에 [Message] 마킹 필요** (MessageKind.NonId는 object 디스패치 불가 → 미지원)
- [Replicated] 메시지 필드의 dirty 비교는 `object.Equals`(참조 비교) — 값 동일성이 필요하면 Equals 오버라이드. RepNotify는 이전 인스턴스 참조를 받는다
- 검증: EditMode(MessageSupportTests — 다형성 보존·메시지 필드 델타) + PlayMode 호스트 왕복(네트워크 경로 전체)

## ServerRpc 소유자 강제 (RequireOwnership)

기본으로 **오브젝트 소유자의 발신만 실행**된다 — 서버 디스패치가 발신 연결(`senderConnId`)과 소유자(`NetworkServer.GetOwner`)를 대조하고, 비소유 발신은 구현을 실행하지 않고 경고 1줄을 남긴다 (ADR-0016). 악의적 클라가 타 오브젝트의 netId로 페이로드를 조립해도 ServerRpc를 실행시킬 수 없다:

```csharp
[ServerRpc]                                   // 기본 = RequireOwnership: true — 소유자 전용
private partial void RpcSubmitAim(float yaw);

[ServerRpc(RequireOwnership = false)]         // 옵트아웃 — 전 클라 보고 허용 (서버가 권위 검증)
internal partial void RpcHeal(int amount);
```

- **로컬 권위 경로는 대조에서 제외**된다 — `senderConnId == 0`(서버·호스트 직접 호출·오프라인 실행)은 발신자 대조 없이 실행. 네트워크로 도달한 발신만 검사한다
- 거부는 조용히 막히지 않는다 — `"[UniNet] ServerRpc 거부 — 비소유 발신: <타입>.<메서드> netId=… sender=… owner=…"` 경고 로그로 식별된다 — 로그는 유량 제한된다(발신자별 최초 1회 + 전역 5초당 최대 1회, `NetworkServer.ReportServerRpcRejection` — 비소유 churn 시 로그 플러딩 방어)
- 호스트는 서버 권위 경로로 실행되므로 영향 없음. ClientRpc/MulticastRpc(서버→클라 방향)는 대상 아님
- **마이그레이션** — 기본값이 보안 우선으로 바뀌었다. 기존에 비소유 클라가 호출하던 크로스-클라 ServerRpc는 업그레이드 후 거부되므로 명시적 `RequireOwnership = false`가 필요하다

## 동적 스폰/파괴 (명시적 API)

서버에서 `NetworkInstantiate` 한 줄 — 원본(프리팹·템플릿)을 복제해 등록하고 전 클라에 스폰(netId·타입·변환·전체 상태)이 전파된다. **반환값이 등록된 인스턴스**다 (원본은 남으니 호출측에서 정리) (사용법: `Sandbox/Assets/Scripts/Arena/ArenaBootstrap.cs`):

```csharp
var projectile = UniNetManager.NetworkInstantiate(_projectilePrefab, pos, rot);
projectile.GetComponent<Projectile>().Launch(speed);   // 비직렬화 초기화는 스폰 후 로컬에서
// ... 수명 만료 시
UniNetManager.NetworkDestroy(gameObject);         // 전 클라 파괴 전파 + 로컬 파괴
```

- **복제는 직렬화 복사** — `Object.Instantiate`는 public·[SerializeField] 필드만 복사한다. private [Replicated] 초기화(InitialOnly 기준선 등)는 **configure 콜백**으로 세팅한다 — 콜백은 복제 직후·전파 직전에 클론으로 실행된다 (ADR-0017): `NetworkInstantiate(original, clone => clone.GetComponent<Player>().InitServerState("Alpha", 123))`. 델리게이트·서버 동작 플래그(MovementRule·NetworkCullDistance 등 비전파 상태)는 스폰 후 클론에 주입해도 무관하다 (기준선에 실리지 않음)

- **클라 생성 우선순위**: `UniNetManager.RegisterPrefab<T>(prefab)` 타입 카탈록(한 타입=프리팹 1개, 선택) → 소스젠 기본 팩토리(빈 GameObject+AddComponent). 누락 서브는 스폰 메시지의 typeKeys로 자동 복원(ADR-0014)
- **변환(위치·회전)은 스폰 시 1회 전파** — 이후 이동은 [Replicated] 필드(예: 좌표 float)로 게임 코드가 동기화
- **후발 접속 캐치업** — 연결 확정 시 기존 동적 오브젝트(스폰+전체 상태)·씬 오브젝트(전체 상태) 일괄 합류
- **호스트** — 서버 인스턴스를 클라 등록으로 재사용(이중 생성 없음). 일반 `Destroy`로 사라진 등록 오브젝트도 드라이버가 감지해 파괴 전파(고스트 방지)

## 조건부 리플리케이션 (ReplicateCondition)

`[Replicated]` 생성자에 flags enum 인자로 수신 조건을 지정한다 (비트 조합 가능):

```csharp
[Replicated] private float _x;                                        // 전 클라 항상
[Replicated(ReplicateCondition.OwnerOnly, Notify = nameof(OnDamage))] private int _damage;  // 소유 클라만
[Replicated(ReplicateCondition.SkipOwner)] private int _teamId;        // 소유자 제외 전체
[Replicated(ReplicateCondition.InitialOnly)] private int _seed;        // 스폰 시 1회만
```

- **OwnerOnly/SkipOwner** — 서버 틱이 소유자용/비소유자용 페이로드를 분리해 전송. 캐치업 전체 상태에도 동일 적용
- **InitialOnly** — 스폰/캐치업 전체 상태에만 포함, 이후 값 변경은 전파 안 됨(스폰 시점 값 고정이 아닌 전송 제외)
- **OwnerOnly|SkipOwner 동시 지정** — 모순이라 컴파일 타임 진단(UNINET010)
- **스냅샷 계약** — 서버 스냅샷은 수신 그룹별로 분리하지 않는다: 소유자가 없는 상태에서 변경된 OwnerOnly 필드는 새 소유자에게 중간값이 유실될 수 있다 (ADR-0009)
- **호스트 RepNotify** — 호스트(서버=클라 동일 인스턴스)는 권위 원본이라 값 재기록 없이 Notify만 호출된다

## 다중 NetworkBehaviour (2층 식별자)

한 게임오브젝트에 여러 NetworkBehaviour가 있으면 각자 `SubId`(슬롯 = GetComponents 순서)를 받아 **독립적으로 RPC·리플리케이션**된다 (ADR-0010 — UE Actor/Component 상당). 소유권·파괴는 오브젝트 단위:

```csharp
// 한 오브젝트에 이동·체력 서브오브젝트 — 각자 [Replicated]·[ServerRpc] 선언 가능
public sealed partial class MovementBrain : NetworkBehaviour { [Replicated] public int Speed; ... }
public sealed partial class HealthTank : NetworkBehaviour { [Replicated(ReplicateCondition.OwnerOnly)] public int Armor; ... }

// 스폰 — 템플릿을 복제·등록한다 (원본의 public 필드 값이 기준선에 실린다)
var template = new GameObject("robot");
template.AddComponent<MovementBrain>();
template.AddComponent<HealthTank>();
var go = UniNetManager.NetworkInstantiate(template);   // 서브 구성은 스폰 메시지의 typeKeys로 클라에 자동 복원된다 (ADR-0014)
```

- **슬롯 불변식**: 슬롯 순서는 GetComponents 순서 — 런타임 AddComponent/Destroy로 NetworkBehaviour를 증감하면 안 된다 (양단 같은 프리팩/씬이 전제). 클라는 스폰 메시지의 서브 구성과 생성 오브젝트를 슬롯·타입별 대조해 불일치 시 거부(진단)
- **같은 타입 중복 허용** — 슬롯이 구분한다
- 예제: `Scripts/GadgetCarrier.cs`(Weapon과 같은 오브젝트)

## 알려진 한계 (신뢰 경계 포함)

- **ServerRpc 발신자 미검증** — 해소됨 (ADR-0016, 2026-09-21): 서버 디스패치가 발신자-소유자를 대조해 비소유 발신을 거부한다 (위 "ServerRpc 소유자 강제" 섹션). 네트워크로 도달한 발신만 검사하고, 정상 크로스-클라 RPC는 `RequireOwnership = false`로 옵트아웃한다. `_Validate`는 계속 인자 검증용 — 발신자 검증은 강제 계층이 담당한다
- **접속 직후 첫 전송 레이스** — 접속 완료 직후(Welcome/소유권 수신 전) 즉발 one-way RPC가 유실될 수 있음 (ADR-0008 알려진 한계 — 상류 조사 후보). 연결 확정 후 전송하는 자연 패턴은 무영향.
- **연결 종료 수명주기** — 해소됨: 아래 "수명주기 종료 (Stop API)" 섹션 참조 (2026-09-17 — 명시적 Stop API 구현)
- **소유권은 라운드로빈 최소 정책**, dirty 검출은 틱마다 폴링 비교 (ADR-0008 — 위빙 배제의 대가).
- **RPC 매개변수·리플리케이션 필드의 기본형 한계 해소** — MessageProtocol `[Message]` 타입(class·struct, MessageKind.NonId 제외)을 지원한다 (위 "메시지 타입 파라미터" 섹션 참조). 컬렉션(List<T> 등)의 직접 파라미터는 여전히 미지원 — 메시지 내부 필드로 담아 전달(MP가 처리). 32필드 상한(UNINET008) 유지.
- **동적 스폰 제약** — 스폰 메시지의 typeKeys 순서대로 누락 서브가 클라에서 자동 복원된다(ADR-0014 — RegisterPrefab은 커스텀 비주얼·사전 구성용 선택). 타입-프리팹 카탈로그 1:1(카탈로그 키=첫 NetworkBehaviour 타입), 스폰 후 이동은 [Replicated] 필드로 동기화(변환은 스폰 시 1회), parenting(계층 구조) 미지원, OwnerOnly 필드의 소유자 부재 시 변경 유실 (ADR-0009)
- **슬롯 불변식** — 런타임 AddComponent/Destroy로 NetworkBehaviour를 증감하면 안 된다(ADR-0010). 씬 오브젝트의 증감은 감지 창구가 없어 계약으로만 방어, 동적 스폰은 구성 대조로 거부. 오브젝트당 NetworkBehaviour 상한 **255개**(SubId byte — 초과 시 등록·스폰 거부)
- **같은 프로세스 재시작** — 해소됨: Stop으로 정리 후 동일 포트 재리슨 검증 (`LifecycleStopTests.호스트_정지후_재시작_성공`)

## 수명주기 종료 (Stop API)

서버/클라/호스트는 시작만큼 명시적으로 정지해야 한다 — 정지하지 않으면 리스너 스레드·소켓이 살아 있어
동일 포트 재리슨이 "RUDP 리스너 바인딩 실패"로 거부된다. `UniNetManager`는 핸들(“RpcListenHandle”)을
저장만 하던 이력이 있어, 2026-09-17에 명시적 종료 API를 추가했다:

```csharp
await UniNetManager.ServerStopAsync();   // 리스너 정지(DisposeAsync) + 환경 상태 정리
UniNetManager.ServerStop();              // 동기 버전 — 애플리케이션 종료 직전 경로용
UniNetManager.ClientStop();              // 허브 Disconnect/Dispose + 환경 상태 정리 (동기)
await UniNetManager.HostStopAsync();     // ClientStop + ServerStopAsync
UniNetManager.HostStop();                // 동기 조합 — 종료 직전 경로용
```

- 멱등 — 리슨 중이 아니면 아무것도 하지 않는다
- 환경 정리 — `UniNetEnvironment`의 Server/Client/ClientSender 참조를 해제해 `IsServer`·`IsClient`가 즉시 false
- 게임은 `OnApplicationQuit`(또는 컴포넌트 비활성)에서 동기 버전을 호출한다 — 예: Sandbox `ArenaBootstrap`
- 정지 경로에서는 이미 닫힌 세션으로의 잔여 송신이 InvalidOperationException(“세션이 끊겨…”)로 실패할 수 있다 —
  생성 코드(FireAndForget)는 이 경우를 예외 대신 경고 로그로 처리한다 (UniNet.CodeGenerator 0.1.1)

## 테스트 / 검증

- EditMode 유닛 20종: `Sandbox/Assets/Tests/EditMode/` — FNV 안정성·옵션 매핑·소유권 정책·델타/RepNotify 이전값 + MessageSupportTests(다형성 보존·메시지 필드 델타/이전 참조) + SpawnConditionTests 7종(그룹별 마스크·수신자 적용·InitialOnly·동적 netId·스폰 브로드캐스트·파괴·후발 캐치업)
- PlayMode 호스트 왕복 3종: `Sandbox/Assets/Tests/PlayMode/HostRoundtripTests.cs` — 루프백 RUDP 전 경로 + 동적 스폰/파괴·조건부 + 다중 컴포넌트 (`[UNINET-VERIFY]` 마커 3종)
- PlayMode 수명주기 3종: `Sandbox/Assets/Tests/PlayMode/LifecycleStopTests.cs` — 리슨→Stop→동일 포트 재리슨·클라 정지/재접속·호스트 재시작 (Stop API 회귀 방지)
- 2-프로세스 왕복: `Sandbox/Assets/Tests/Fixtures/TwoProcessRunner.cs` (`--uninet-role=server|client`) — 프로세스 간 RUDP — RPC·리플리케이션 + `[UNINET-2PROC] DYNAMIC-SPAWN-DESTROY PASS`(캐치업·브로드캐스트·조건 3종·파괴) + `[UNINET-2PROC] MULTI-COMPONENT PASS`(다중 서브 스폰·서브별 델타·슬롯·파괴) (로그: `Sandbox-2proc-*.log`)
- 사용법 예제: `Sandbox/Assets/Scripts/Arena/` — ArenaBootstrap(관리 스폰 NetworkInstantiate+configure)·ArenaPlayer(RPC 3종·조건부 리플리케이션 전종·RepNotify)

## 변경 이력

- 2026-09-14 — 최초 작성 (구현 완료와 함께)
- 2026-09-15 — [Message] 타입 매개변수·필드 지원 추가 (다형성 공식 계약 — MessageSupportTests·호스트 왕복 검증)
- 2026-09-16 — P2 완결 — 동적 스폰/파괴(Spawn/NetworkDestroy)·조건부(ReplicateCondition 3종)·후발 접속 캐치업 추가 (ADR-0009)
- 2026-09-16 — 다중 NetworkBehaviour 지원 — 2층 식별자(netId+SubId)·다중 서브 스폰/델타·슬롯 대조 (ADR-0010 — MultiComponentTests·PlayMode·2-프로세스 검증)
- 2026-09-17 — 수명주기 종료 API 추가 — ServerStopAsync/ServerStop/ClientStop/HostStopAsync/HostStop (포트 잔존 바인딩 실패 근본 해소 — LifecycleStopTests 3종·동일 포트 재리슨 검증) + UniNet.CodeGenerator 0.1.1 (정지 경로 잔여 송신 예외를 경고 수준으로)
- 2026-09-22 — 동적 스폰 API 교체 — `Spawn(instance)` 제거, `NetworkInstantiate`(복제 겸함·3종 오버로드·configure 콜백)로 통합 (ADR-0017)
