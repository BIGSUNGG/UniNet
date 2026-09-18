# 리플리케이션 고급 정책 (P3)

- **상태**: 구현됨
- **최초 작성**: 2026-09-18
- **마지막 갱신**: 2026-09-18

## 개요

서버 리플리케이션 틱에 UE Network Framework 패리티의 스케줄링 정책을 추가한다 — **누구에게**(가시성/Relevancy), **무엇 먼저**(우선순위·채널 예산), **얼마나 자주**(NetUpdateFrequency), **언제 멈추는가**(Dormancy). 게임 코드는 `NetworkBehaviour`의 정책 멤버를 설정하는 것만으로 동작한다. 결정 배경: [[0011-P3-리플리케이션-고급-정책]].

## 요구사항 / 목표

- 대역폭·CPU를 오브젝트 중요도에 따라 배분한다 (상용 서버 수준)
- P2의 동작(즉시 모드)을 100% 보존한다 — 정책 미설정 오브젝트는 기존과 동일
- 기반 스택(DRPC·MessageProtocol·Communication)을 수정하지 않는다 — UniNet 저장소만으로 완결

## 설계

- **`IUniNetReplicationPolicy`** (`Package/Runtime/UniNet.Core/Hosting/IUniNetReplicationPolicy.cs`) — 서버가 읽는 정책 계약 (우선순위·주기·컬거리·휴면·관련성 훅)
- **`NetworkBehaviour`** (`Package/Runtime/UniNet.Unity/NetworkBehaviour.cs`) — 정책 구현. 사용자가 재정의하는 가상 멤버: `IsNetworkRelevant(connId)`, 설정 멤버: `NetworkPriority`·`NetworkUpdateFrequencyHz`·`NetworkCullDistance`·`NetworkDormant`·`FlushNetworkDormancy()`
- **`NetworkServer`** (`Package/Runtime/UniNet.Core/Hosting/NetworkServer.cs`) — 틱 엔진 확장: 가시성 추적(`SetViewerPosition`·연결별 관련 맵)·휴면·주기 필터 → 델타 후보 큐 → 기아 보정 우선순위 정렬 → 전역/유형별 예산 전송
- 관련 ADR: [[0011-P3-리플리케이션-고급-정책]] · 선행: [[features/monobehaviour-rpc-replicate]]·ADR-0009·ADR-0010

## 동작 상세

- **즉시 모드 vs 시간 모드**: `TickReplication(objects)`는 P2 호환(정책 미적용). 드라이버는 `TickReplication(objects, Time.unscaledTimeAsDouble)`로 실제 시계를 제공해 정책을 활성화한다
- **가시성**: 판정 = `IsNetworkRelevant(connId)` AND 거리(`SetViewerPosition` 필요 — 미설정 연결은 컬 무효). 씬 오브젝트 첫 평가는 조용한 시드(P2 계약 유지), 재진입·동적 첫 평가는 스폰/전체 상태로 기준선 복구. 비관련 전환 시 델타만 중단(클라는 마지막 상태 유지 — 전파 파괴 미지원)
- **우선순위·기아**: 후보+연기분 병합 후 `priority × (1 + 대기초/0.1)` 순 전송(UE GetNetPriority 공식). 전역 예산 `ReplicationBudgetPerTickBytes`(0=무제한), 유형별 `SetReplicationChannelBudget(Type, bytes)` — 유형 초과분만 연기. 예산보다 큰 단일 델타는 강제 전송
- **휴면**: `NetworkDormant = true` 동안 델타 비교 생략(스냅샷 동결). `FlushNetworkDormancy()`로 해제 — 누적 변경분이 한 번에 전송
- **주기**: `NetworkUpdateFrequencyHz` Hz 미도달 틱은 비교 생략, 도달 틱에 최신 변경분 전송(최신값 승)

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

- **EditMode 12종 신규** — `RelevancyTests`(4)·`DormancyTests`(3)·`UpdateFrequencyTests`(3)·`ChannelBudgetTests`(2) + `PriorityTests` 6종(스펙 → 구현)
- **PlayMode 7/7** — 드라이버 시간 모드 전환 회귀 포함
- **아레나 예시 통합** — 총알(주기·컬)·플레이어(우선순위·사망 휴면/리스폰 플러시)·부트스트랩(뷰어 위치·채널 예산): [[examples/arena-shooter]]

## 변경 이력

- 2026-09-18 — 최초 작성 (P3 완결 — ADR-0011)
