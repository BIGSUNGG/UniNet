# 리플리케이션 고급 정책 (P3)

- **상태**: 구현됨
- **최초 작성**: 2026-09-18
- **마지막 갱신**: 2026-10-27 (AUDIT-F15 — 같은 키 연기분의 우선순위 추월 차단)

## 개요

서버 리플리케이션 틱에 UE Network Framework 패리티의 스케줄링 정책을 추가한다 — **누구에게**(가시성/Relevancy), **무엇 먼저**(우선순위·채널 예산), **얼마나 자주**(NetUpdateFrequency), **언제 멈추는가**(Dormancy). 게임 코드는 `NetworkBehaviour`의 정책 멤버를 설정하는 것만으로 동작한다. 결정 배경: [[0011-P3-리플리케이션-고급-정책]].

## 요구사항 / 목표

- 대역폭·CPU를 오브젝트 중요도에 따라 배분한다 (상용 서버 수준)
- P2의 동작(즉시 모드)을 100% 보존한다 — 정책 미설정 오브젝트는 기존과 동일
- 기반 스택(DRPC·MessageProtocol·Communication)을 수정하지 않는다 — UniNet 저장소만으로 완결

## 설계

- **`IUniNetReplicationPolicy`** (`Package/Runtime/UniNet.Core/Hosting/IUniNetReplicationPolicy.cs`) — 서버가 읽는 정책 계약 (우선순위·주기·컬거리·휴면·관련성 훅·전송 신뢰성)
- **`NetworkBehaviour`** (`Package/Runtime/UniNet.Unity/NetworkBehaviour.cs`) — 정책 구현. 사용자가 재정의하는 가상 멤버: `IsNetworkRelevant(connId)`, 설정 멤버: `NetworkPriority`·`NetworkUpdateFrequencyHz`·`NetworkCullDistance`·`NetworkDormant`·`NetworkReplicationDelivery`·`FlushNetworkDormancy()`
- **`NetworkServer`** (`Package/Runtime/UniNet.Core/Hosting/NetworkServer.cs`) — 틱 엔진 확장: 가시성 추적(`SetViewerPosition`·연결별 관련 맵)·휴면·주기 필터 → 델타 후보 큐 → 기아 보정 우선순위 정렬 → 전역/유형별 예산 전송
- 관련 ADR: [[0011-P3-리플리케이션-고급-정책]] · 선행: [[features/monobehaviour-rpc-replicate]]·ADR-0009·ADR-0010

## 동작 상세

- **즉시 모드 vs 시간 모드**: `TickReplication(objects)`는 P2 호환(정책 미적용). 드라이버는 `TickReplication(objects, Time.unscaledTimeAsDouble)`로 실제 시계를 제공해 정책을 활성화한다
- **가시성**: 판정 = `IsNetworkRelevant(connId)` AND 거리(`SetViewerPosition` 필요 — 미설정 연결은 컬 무효). 씬 오브젝트 첫 평가는 조용한 시드(P2 계약 유지), 재진입·동적 첫 평가는 스폰/전체 상태으로 기준선 복구 — 진입 기준선에는 캐치업과 동일하게 객체별 소유권 통보가 상태보다 먼저 포함된다(OWN-001, 소유자 0 포함). 동적 객체 재진입 기준선은 클라에 이미 인스턴스가 있어도 적용된다(FR-04, 2026-10-26): `UniNetSpawn.Apply`가 기존 인스턴스의 트랜스폼+서브별 전체 상태를 갱신하고(무재생성·SubId 슬롯·typeKeys 검증 유지, 불일치 시 거부·기존 값 보존), 호스트 중복은 서버 원본 불변. 비관련 전환 시 델타만 중단(클라는 마지막 상태 유지 — 전파 파괴 미지원)
- **우선순위·기아**: 후보+연기분 병합 후 `priority × (1 + 대기초/0.1)` 순 전송(UE GetNetPriority 공식). **같은 키의 이전 연기분과 새 후보는 하나의 순서 의존 그룹**이다(AUDIT-F15). 그룹 내 최대 기아 보정 점수를 공유한 뒤 Seq로 정렬하므로, 우선순위를 1→20000으로 올리고 예산을 풀어도 새 값 2 다음에 이전 값 1을 보내지 않는다. 다른 키 간에는 이 그룹 점수로 우선순위가 유지된다. scratch 사전은 연기된 키만 보관해 재사용하고, 연기분이 없는 정상 틱에는 추가 후보 순회를 하지 않는다. 전역 예산 `ReplicationBudgetPerTickBytes`(0=무제한), 유형별 `SetReplicationChannelBudget(Type, bytes)` — 유형 초과분만 연기. 예산보다 큰 단일 델타는 강제 전송. **연기 큐는 유한하다(FR-06, 2026-10-26)**: 같은 (NetId, SubId)의 연기 항목은 최신 권위 전체 상태로 대체 병합돼 큐가 키 수에 묶이고(시간 무한 축적 불가, `DeferredCoalescedCount` 관측), 상한 `MaxDeferredDeltas`(기본 1024) 도달 초과분은 강제 송신된다(`DeferredForcedSendCount` 관측 — 조용한 누락 금지). 휴면 보류 항목은 기존 드롭 금지 계약 유지. 상세는 [[features/replication-batching-delivery]]
- **휴면**: `NetworkDormant = true` 동안 델타 비교 생략(스냅샷 동결). `FlushNetworkDormancy()`로 해제 — 누적 변경분이 한 번에 전송
- **주기**: `NetworkUpdateFrequencyHz` — 기본 30Hz (ADR-0040 — 무제한 기본값이 메시지 수 과다를 만들지 않도록). 미도달 틱은 비교 생략, 도달 틱에 최신 변경분 전송(최신값 승). `0` 설정으로 매 틱 전송 옵트인
- **전송 신뢰성 (v0.3.0)**: `NetworkReplicationDelivery` — 기본 `ReliableOrdered`, `Unreliable`은 Sequenced 스트림. 배칭·정지 재전송 포함 상세는 [[features/replication-batching-delivery]] ([[0039-복제-배칭과-신뢰성-분리]])

```csharp
public sealed partial class Bullet : NetworkBehaviour
{
    public void Init()
    {
        NetworkUpdateFrequencyHz = 30f;   // P3-④ 궤적은 30Hz로 충분
        NetworkCullDistance = 24f;        // P3-① 멀리 있는 연결에는 전송하지 않는다
    }
}

// 서버(부트스트랩) — 컬 판정 기준점과 유형별 대역폭 제공
server.SetViewerPosition(connId, x, y, z);                      // P3-① 뷰어 위치
server.SetReplicationChannelBudget(typeof(Bullet), 512);        // P3-⑤ 유형별 틱 예산
server.ReplicationBudgetPerTickBytes = 8192;                    // P3-② 전역 틱 예산 (0=무제한)
```

## 테스트 / 검증

- **2026-10-20 OWN-001 검증** — 재진입에서도 owner-before-state(0 포함) 기준선을 전달. `OwnerBaselineTests` 8/8·EditMode 전수 146/146·PlayMode 전수 27/27·Arena 실제 RUDP 단일 프로세스 마커 진단 PASS. 타임드 틱으로 비관련/관련 전환 후 진단용 스테일 캐시 주입을 실제 와이어가 복원하는지 확인했다(일반 게임 사용 패턴이나 최종 다중 프로세스 검증이 아님). 원시 증거·개별 배포 상태는 `Document/research/commercial-netcode-evidence.json` 참조.
- 아래 수치는 P3 도입 당시 검증 기록이다.

- **EditMode 12종 신규** — `RelevancyTests`(4)·`DormancyTests`(3)·`UpdateFrequencyTests`(3)·`ChannelBudgetTests`(2) + `PriorityTests` 6종(스펙 → 구현)
- **PlayMode 7/7** — 드라이버 시간 모드 전환 회귀 포함
- **아레나 예시 통합** — 총알(주기·컬)·플레이어(우선순위·사망 휴면/리스폰 플러시)·부트스트랩(뷰어 위치·채널 예산): [[examples/arena-shooter]]

## 변경 이력

- 2026-10-27 — AUDIT-F15: 같은 키의 queued/fresh 후보가 우선순위 변화로 역전하지 않도록 최대 유효 우선순위 그룹+Seq 정렬. 실제 Core 반례 권위값2→수신값1을 수정하고 증가/감소·다른 키 혼합 회귀 2개를 `CoreInvariantRegressionTests` 15/15 실행에 포함했다.
- 2026-10-26 — FR-06: 연기 큐를 유한 적체로 전환(키별 최신 전체 상태 병합·상한 초과 강제 송신·관측 카운터). 메인 큐 상한(`UniNetEnvironment.MainQueueCapacity`)도 이 단계에 추가 — 상세는 [[features/replication-batching-delivery]].
- 2026-10-26 — FR-04: 동적 객체 재진입 기준선이 클라 기존 인스턴스에 적용(`UniNetSpawn.Apply` 분기 — 무재생성·SubId 슬롯·typeKeys 검증·불일치 거부·호스트 원본 불변). `AoiReentryBaselineTests` 3종 신규.
- 2026-10-20 — OWN-001: 가시성 복귀의 씬/동적 소유권 기준선을 후발 캐치업과 공유하고 0 소유자도 전송, 상태/Notify보다 앞선 캐시 갱신을 보장. 초기 동적 스폰도 동일 순서로 정렬. 관련성 필터·휴면·예산 정책은 유지.
- 2026-09-18 — 최초 작성 (P3 완결 — ADR-0011)
