# 리플리케이션 P4 훅 (시간 동기화·예측·래그 컴펜세이션·그리드 가시성)

- **상태**: 구현됨
- **최초 작성**: 2026-09-20
- **마지막 갱신**: 2026-09-20

## 개요

UE Network Framework의 상호작용 축을 **훅(유저 코드 확장점)** 형태로 제공한다 — 서버 권위 단조 시계(`UniNetTime`), 클라 인터폴레이션 버퍼(`SnapshotBuffer<T>`), 서버 위치 히스토리·리와인드 질의(`PositionHistory` + `NetworkBehaviour.GetHistoryPosition`), 그리드 공간 분할 가시성(`SetVisibilityGrid`). 결정 배경: [[0012-P4-훅-시간동기화-인터폴레이션-리와인드-그리드]].

## 요구사항 / 목표

- 예측·보간·래그컴펜세이션에 필요한 공용 프리미티브를 라이브러리가 제공하고, 알고리즘은 게임 코드가 조립한다
- P2/P3 동작을 100% 보존한다 (즉시 모드·기존 계약 유지)
- 기반 스택(DRPC·MessageProtocol·Communication) 수정 없이 완결한다 (제너레이터 시스템 메시지 1건 추가는 UniNet 내부)

## 설계

- **`UniNetTime`** (`Package/Runtime/UniNet.Core/Hosting/UniNetTime.cs`) — 서버 권위 단조 시계. 서버=로컬 시계, 클라=로컬+EMA 오프셋. 단조 보장(역행 클램프)
- **`SnapshotBuffer<T>`** (`Hosting/SnapshotBuffer.cs`) — 클라 보간 버퍼: Add(시각, 값) + TrySample(renderTime)
- **`PositionHistory`** (`Hosting/PositionHistory.cs`) — 서버 위치 히스토리 링 버퍼(128 샘플): Record + Sample(과거 시점)
- **`NetworkBehaviour`** (`UniNet.Unity/NetworkBehaviour.cs`) — `NetworkRewindHistory`(옵트인 기록) + `GetHistoryPosition(serverTime, out pos)` 훅
- **`NetworkServer`** (`Hosting/NetworkServer.cs`) — `SetVisibilityGrid(cellSize, visibleRadius)` / `ClearVisibilityGrid()`; 시간 동기화 브로드캐스트용 `SendTimeSync`(시스템 메시지 5번 — 제너레이터 0.1.2)
- 관련 ADR: [[0012-P4-훅-시간동기화-인터폴레이션-리와인드-그리드]] · 선행: [[features/replication-p3-policy]]

## 동작 상세

- **시간 동기화**: 드라이버가 1초 주기로 서버 시각을 전 연결에 전파. 클라는 EMA(α=0.25)로 오프셋 갱신, 수신은 메인 큐 우회(Unity 시계 메인 전용). 서버·호스트는 자기 시계가 곧 권위라 오프셋 미적용. `Now`는 단조 보장
- **인터폴레이션**: 리모트 오브젝트를 `Now − InterpolationDelay` 시점으로 렌더 — 네트워크 지터가 화면에 직접 노출되지 않는다 (적용 시점 제어)
- **예측**: 게임이 이동 규칙을 단일 공급원(예: ArenaMovement.Step)으로 추출해 서버·클라가 공유 → 소유 클라는 입력 즉시 적용, 서버 상태 수신 시 ReconcileAxis(스냅/소프트)
- **래그 컴펜세이션**: `NetworkRewindHistory` 대상을 서버가 매 틱 기록 → 발사 ServerRpc에 발신자 조준 시각을 실어 보내면 서버가 `GetHistoryPosition(클램프된 hitTime)`으로 대상을 리와인드해 판정
- **그리드 가시성**: 셀 멤버십으로 거리 판정 대체(양자화 오차 cellSize 이하). 관련성 훅·P3 전이 계약(첫 평가 조용한 시드, 재진입 기준선 복구) 유지

```csharp
// 게임 코드 — 리와인드 대상 등록 (서버)
player.NetworkRewindHistory = true;

// 발사 ServerRpc — 발신자 조준 시각 첨부
private partial void RpcFire(float dirX, float dirY, double hitTime);
// 서버 구현 — 신뢰 경계: 과거/미래 클램프 후 판정
double t = Math.Clamp(hitTime, UniNetTime.Now - 1.0, UniNetTime.Now);
target.GetHistoryPosition(t, out float hx, out _, out float hz);
// 서버(부트스트랩) — 그리드 가시성 + 뷰어 위치
server.SetVisibilityGrid(10f, 30f);
server.SetViewerPosition(connId, x, y, z);
```

## 테스트 / 검증

- **EditMode 7종 신규** — `P4CoreTests` 5(UniNetTime·SnapshotBuffer·PositionHistory)·`GridVisibilityTests` 2
- **PlayMode 2종 신규** — `P4HookPlayTests`(TimeSync 동기화·리와인드 질의)
- **아레나 전환** — 예측 이동·히트스캔 래그컴펜세이션 실동작: [[examples/arena-shooter]]

## 변경 이력

- 2026-09-20 — 최초 작성 (P4 훅 — ADR-0012)
