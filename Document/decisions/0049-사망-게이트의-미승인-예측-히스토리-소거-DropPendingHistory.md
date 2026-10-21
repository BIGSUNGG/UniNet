# ADR-0049 — 사망 게이트의 미승인 예측 히스토리 소거 (DropPendingHistory)

- 상태: 채택 (2026-10-21)
- 관련: ADR-0038(예측·롤백), ADR-0042(소유권 전이 스트림), MOV-001(MOV v0.5.0), MOV-002(감사 항목)
- 버전: 0.13.0

## 배경

`NetworkTransform.SimulationEnabled` 게이트(사망·일시 비활성)는 소유자 예측의 스텝만 멈췄다 재개할 뿐, 게이트 진입 시점에 무승인(pending) 상태로 링에 상주하던 예측 히스토리를 정리하지 않았다. 60Hz 소유자는 RTT 수만큼 입력이 상시 pending이므로 "pending>0인 채 사망"이 기본 상황이다. 이때 사망→원격 리스폰(`SetNetworkPosition`)→재활성화 흐름에서:

1. 재활성화 프레임의 재앵커는 `PendingCount==0` 전제라 발동하지 않고, 렌더는 사망 직전 예측 좌표를 즉시 출력한다(오래된 좌표 렌더).
2. 재개 후 첫 ack 롤백이 죽은 시대 입력을 리스폰 상태 위에 재시뮬한다(되감기 아티팩트; 링 128 초과 시에야 하드 스냅으로 수렴).

RED 증거: drop을 비활성화한 상태에서 EditMode 회귀가 결함 지점에서 실패(job `b0d852be` — "Expected: 0 But was: 4/5").

## 결정

1. **`INetworkMoveSimulation.DropPendingHistory()` 인터페이스 멤버(C# 기본 인터페이스 멤버, DIM)를 추가한다.** 기본 구현은 `=> OnTeleport()`(전체 소거) — 사용자 구현체는 코드 파괴 없이 새 멤버를 받는다. `MoveSimulation`은 이를 재구현해 **큐+히스토리 링만 비우고 권위 TState와 최신 입력은 보존**한다(컷신 일시정지 후 속도 유지 같은 게임 상태 보존; 위치는 재앵커·ack 롤백으로 수렴).
2. **`NetworkTransform`은 `SimulationEnabled` true→false 진입 에지에서 1회 `DropPendingHistory()`를 호출한다.** 에지 트리거(레벨 아님)인 이유: 게이트 내 예측을 합법적으로 하는 게임의 입력을 매 프레임 반복 소거하지 않기 위해. 효과: (a) 사망 중 도착하는 ack는 빈 링 미스 → 하드 스냅으로 권위 좌표 자기정렬, (b) 재활성화 시 pending==0 보장 → **기존(변경 없는) 텔레포트 재앵커**가 리스폰 좌표를 채택, (c) 죽은 입력 재생 경로 소멸.
3. **재앵커·롤백·MOV-001 임기 경계 계약은 변경하지 않는다.** 소유권 전이는 ADR-0042의 `ResetOwnershipStream`이 담당하고, 게이트는 사망·비활성만 담당 — 두 경계는 별개 계약으로 유지한다(게이트에서 임기 API를 재사용하면 `_lastSeenSeq` 무의미한 0화 등 잠재 결합 발생 — 기각).

## 기각한 대안

- **재활성화 에지에서만 소거** — 사망 기간 stale-ack 재생 위험이 남는다(진입 시 소거가 맞다).
- **`ResetOwnershipStream()` 재사용** — 임기 전용 이름·계약(시퀀스 상태 0화)과 셈antics 불일치.
- **`OnTeleport()` 직접 호출** — 게임 상태(TState)까지 소거해 컷신 변형을 훼손한다.
- **무수정** — 사용자 가시 아티팩트(오래된 좌표 렌더·되감기)로 감사 항목 해결 불가.

## 결정 2 — 퇴장 에지에서 재앵커 선실행 (r17 ISSUE-1, 같은 릴리스)

재활성화 프레임에서 스텝 블록이 재앵커 검사보다 먼저 도는 순서 문제로, `Time.deltaTime >= 스텝 간격`인 프레임(프레임 스파이크 시 흔함)에 죽은 시대 `_pred`에서 1스텝이 실행돼 pending=1이 되고 재앵커(PendingCount==0 전제)가 우회될 수 있었다 — 리스폰 즉시 스냅 대신 ~RTT 지연 수렴으로 열화. 수정: `TrackGateEdge`를 스텝 블록 **앞**으로 이동하고 **퇴장 에지(false→true)에서 `TryReAnchor()`를 선실행**한다(인라인 재앵커 블록을 메서드 추출, 로직 동일). 진입 에지 drop도 스텝 앞으로 이동해 사망 프레임 자체의 스텝을 차단한다. RED job `0bbdb4dd`(T2c "Expected: 100 But was: 0.4166") → 수정 후 GREEN.

## 테스트 범위 한계 (r17 ISSUE-2)

PlayMode `DeathGatePlayTests`는 호스트 모드 특성상 소유자 예측 레그(`isPredictor=false`, pending 상시 0)를 실행하지 않는 **흐름 스모크**다 — 에지 drop·하드스냅·재앵커의 결함 고정은 EditMode T1/T2a/T2b/T2c/T3/T5가 담당한다. 와이어를 포함한 2프로세스(또는 실소유자 클라) 레그 통합 테스트는 의도적 후속 과제로 남긴다.

## 결과

- 사용자 계약: `SimulationEnabled` 게이트 진입 시 미승인 예측 히스토리가 소거되고, 재개 시 서버 상태에 재앵커된다. 게이트 중 입력은 폐기되지 않는다(에지성 보장) — 게이트 중 직접 `PredictStep`으로 재적재된 히스토리는 재개 때 ack 롤백 경로로 정상 정산된다.
- DIM 기본값(`= OnTeleport`)을 받는 사용자 구현체는 게이트 진입 시 전체 소거 동작을 얻는다 — 문서로 명시.
- 와이어·생성기 무변경.

## 검증

EditMode 6종(`Mov002DeathGateTests` — T1 단위 계약·T2a 에지성·T2b 재활성화 재앵커·T2c 스텝 유입 프레임 재앵커 선실행·T3 사망 중 ack 하드스냅·T5 pending==0 음성 대조) + PlayMode 1종(`DeathGatePlayTests` — 사망→리스폰→재개 흐름 스모크). RED job `b0d852be`(초기 결함)·`0bbdb4dd`(r17 ISSUE-1), GREEN EditMode 241/241 ×2(job `17a4efdf`·`cb9288d1`)·PlayMode 41/41 ×2(job `6c6090ac`·`cfb0e695`).
