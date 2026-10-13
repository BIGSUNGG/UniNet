# 0037. 예측 full CSP&R 내장

- **상태**: 승인됨
- **날짜**: 2026-10-12
- **결정자**: /list 항목 12 (예측 full CSP&R 내장)

## 배경 (Context)

ADR-0013 NetworkTransform의 소프트/스냅 정정은 근사치(soft correction)로, 레이턴시 스파이크·입력 유실에서 예측 오차가 누적될 수 있었다. Arena는 이미 수동으로 ReconcileAxis(스냅/소프트)를 사용 중이었으나 UE/Fusion급의 "롤백+재시뮬레이션"이 없었다. 실사용 갭 검토 높음 ⑪: "full CSP&R이 필요하면 사용자 작업량이 Fusion/FishNet 대비 큼" → 프레임워크 내장으로 해소.

## 결정 (Decision)

1. **`NetworkTransform.FullReconciliation`** (기본 false) — 옵트인. true 시 소유 클라의 리컨사일이 소프트/스냅 대신 **롤백+재시뮬**:
   - 입력 시퀀싱: 클라 매 입력마다 `_clientSeq++` → `RpcSubmitMoveSequenced(seq, input)` 전송
   - 서버 에코: `_ackSeq`(OwnerOnly Replicated)에 마지막 처리 시퀀스 기록 → 소유 클라에 전파
   - 입력 히스토리: 클라 Update마다 (seq, input, predictedPos) 링버퍼(128 엔트리) 기록
   - 리컨사일: `_ackSeq` 변경 시 히스토리에서 해당 seq의 예측 위치와 서버 위치 비교 → 불일치 시 서버 위치로 롤백 후 미승인 입력(> ackSeq) 재시뮬 → 히스토리에서 승인분(≤ ackSeq) 폐기
2. **기존 소프트/스냅 유지** — FullReconciliation=false 시 기존 ReconcileAxis 동작 (하위 호환).
3. **Arena 전환** — `ArenaPlayer._nt.FullReconciliation = true` 설정.

## 결과 (Consequences)

- **긍정**: 레이턴시 스파이크에서 정확한 예측 복구(오차가 누적되지 않고 서버 상태로 롤백 후 재시뮬). Fusion/FishNet급 CSP&R을 옵트인 한 줄로 제공.
- **부담 / 리스크**: 시퀀스 RPC 추가(와이어 오버헤드 seq 4B/입력). 히스토리 128 엔트리 × ~28B = ~3.5KB/오브젝트. 재시뮬 안전 상한 120 스텝(폭주 방지). dt 근사(Time.deltaTime 단일 값 사용 — 고정 스텝이 아님). ackSeq를 히스토리에서 못 찾으면 소프트/스냅 폴백.
- **텔레포트 재앵커**: 미승인 입력 없음(_historyCount==0) + 서버 위치 급이동(> SnapThreshold) 시 Update에서 _pred=_px 재앵커(부활·순간이동).
- **폐기 방법**: FullReconciliation = false (기본값).
