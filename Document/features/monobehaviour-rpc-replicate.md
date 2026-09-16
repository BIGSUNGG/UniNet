# MonoBehaviour RPC & 변수 Replicate

- **상태**: 구현됨 (P1 전체 + P2 전체 — [[0008-구현-아키텍처]]·[[0009-동적-스폰-조건부-리플리케이션]] 참조)
- **최초 작성**: 2026-09-14
- **마지막 갱신**: 2026-09-16

## 개요

`NetworkBehaviour` 파생 타입에 `[ServerRpc]`·`[ClientRpc(Delivery)]`·`[MulticastRpc]` 속성으로 partial 선언하고 본문을 `{Name}_Implementation`에, 선택으로 `{Name}_Validate`에 쓰면 — UniNet 소스 제너레이터가 DRPC 허브 배선과 직렬화를 생성해 서버-클라 간 오브젝트 단위 RPC가 동작한다. `[Replicated(Notify = nameof(...))]` 필드는 서버에서 변경 시 델타가 클라에 동기화되고 RepNotify(이전값) 콜백이 호출된다. 동적 오브젝트는 `UniNetManager.Spawn`/`NetworkDestroy`로 생애주기를 동기화하고, `[Replicated(ReplicateCondition.OwnerOnly)]` 등 조건으로 수신자를 제한할 수 있다.

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

## 동적 스폰/파괴 (명시적 API)

서버에서 일반 `Instantiate` 후 `Spawn` 한 줄 — 전 클라에 스폰(netId·타입·변환·전체 상태)이 전파된다 (사용법: `Sandbox/Assets/Scripts/Weapon.cs`·`Projectile.cs`):

```csharp
var projectile = Instantiate(_projectilePrefab, pos, rot);
projectile.Launch(speed);
UniNetManager.Spawn(projectile.gameObject);      // netId 할당·소유권·전 클라 전파
// ... 수명 만료 시
UniNetManager.NetworkDestroy(gameObject);         // 전 클라 파괴 전파 + 로컬 파괴
```

- **클라 생성 우선순위**: `UniNetManager.RegisterPrefab<T>(prefab)` 타입 카탈록(한 타입=프리팹 1개) → 소스젠 기본 팩토리(빈 GameObject+AddComponent)
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

## 알려진 한계 (신뢰 경계 포함)

- **ServerRpc 발신자 미검증** — 수신 핸들은 페이로드의 netId만으로 대상을 찾아 `_Implementation`를 실행한다. 악의적 클라가 다른 오브젝트의 netId로 페이로드를 조립하면 피해자 오브젝트의 ServerRpc가 실행될 수 있다. `_Validate`는 인자만 검증 가능(발신자 불가). 완화 플러밍은 마련됐다 — 연결별 허브가 발신 connId를 주입하고 디스패치까지 전달되므로(`senderConnId`), 소유권 대조 강화는 후속 과제다.
- **접속 직후 첫 전송 레이스** — 접속 완료 직후(Welcome/소유권 수신 전) 즉발 one-way RPC가 유실될 수 있음 (ADR-0008 알려진 한계 — 상류 조사 후보). 연결 확정 후 전송하는 자연 패턴은 무영향.
- **연결 종료 수명주기 미구현** — `Stop`/`Shutdown`이 없고 `ServerAsync` 재호출 시 이전 리슨 핸들이 교체만 된다. P1 범위 밖 — 후속 구현.
- **소유권은 라운드로빈 최소 정책**, dirty 검출은 틱마다 폴링 비교 (ADR-0008 — 위빙 배제의 대가).
- **RPC 매개변수·리플리케이션 필드의 기본형 한계 해소** — MessageProtocol `[Message]` 타입(class·struct, MessageKind.NonId 제외)을 지원한다 (위 "메시지 타입 파라미터" 섹션 참조). 컬렉션(List<T> 등)의 직접 파라미터는 여전히 미지원 — 메시지 내부 필드로 담아 전달(MP가 처리). 32필드 상한(UNINET008) 유지.
- **동적 스폰 제약** — 한 오브젝트의 **첫 NetworkBehaviour만** 스폰·리플리케이션 대상(프리팹에 NetworkBehaviour를 2개 이상 두면 나머지는 조용히 제외 — Spawn 시 경고 로그), 타입-프리팹 카탈로그 1:1(한 타입에 프리팹 여러 개 불가), 스폰 후 이동은 [Replicated] 필드로 동기화(변환은 스폰 시 1회), parenting(계층 구조) 미지원, OwnerOnly 필드의 소유자 부재 시 변경 유실 (ADR-0009)
- **같은 프로세스 재시작 미지원** — 호스트/서버 재시작은 이전 스택 미정리(Stop 미구현의 연장 — ADR-0009 알려진 한계)

## 테스트 / 검증

- EditMode 유닛 13종: `Sandbox/Assets/Tests/EditMode/` — FNV 안정성·옵션 매핑·소유권 정책·델타/RepNotify 이전값 + MessageSupportTests(다형성 보존·메시지 필드 델타/이전 참조) + SpawnConditionTests 7종(그룹별 마스크·수신자 적용·InitialOnly·동적 netId·스폰 브로드캐스트·파괴·후발 캐치업) (`tests-editmode-p2.xml`)
- PlayMode 호스트 왕복 2종: `Sandbox/Assets/Tests/PlayMode/HostRoundtripTests.cs` — 루프백 RUDP 전 경로 + 동적 스폰/파괴·조건부 (`[UNINET-VERIFY]` 마커 2종)
- 2-프로세스 왕복: `Sandbox/Assets/Tests/Fixtures/TwoProcessRunner.cs` (`--uninet-role=server|client`) — 프로세스 간 RUDP — RPC·리플리케이션 + `[UNINET-2PROC] DYNAMIC-SPAWN-DESTROY PASS`(캐치업·브로드캐스트·조건 3종·파괴) (로그: `Sandbox-2proc-*.log`)
- 사용법 예제: `Sandbox/Assets/Scripts/` — Player(RPC 3종·메시지 파라미터)·Weapon(스폰 발사)·Projectile(조건 3종·NetworkDestroy)

## 변경 이력

- 2026-09-14 — 최초 작성 (구현 완료와 함께)
- 2026-09-15 — [Message] 타입 매개변수·필드 지원 추가 (다형성 공식 계약 — MessageSupportTests·호스트 왕복 검증)
- 2026-09-16 — P2 완결 — 동적 스폰/파괴(Spawn/NetworkDestroy)·조건부(ReplicateCondition 3종)·후발 접속 캐치업 추가 (ADR-0009 — SpawnConditionTests·DynamicSpawnDestroy·2-프로세스 DYNAMIC-SPAWN-DESTROY 검증)
