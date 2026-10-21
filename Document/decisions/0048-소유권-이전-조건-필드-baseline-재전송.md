# 0048. 소유권 이전 시 조건 필드 baseline 재전송

- **상태**: 승인됨
- **날짜**: 2026-10-21
- **결정자**: 사용자(일괄 사전승인 범위) · 플래너 설계(zai/glm-5.3-flash) · 워커 구현

## 배경 (Context)

감사 대장 OWN-002(재현 확정): 소유권 이전 3경로(`SetOwner`/`ResumeSession`/`ReassignOwnership`)가 모두 OwnerUpdate만 전파한다. 서버의 델타 baseline(`SubObjectEntry.Snapshot`)은 서브당 1개 전역 last-sent 스냅샷이라 이전 시 리셋·재전송이 없어, (a) 새 owner는 비변경 OwnerOnly/InitialOnly 필드를 영구 기본값으로 보고, (b) **이전 owner도 SkipOwner 필드를 기본값으로 본다**(감사 항목이 가정하지 않은 추가 피해), (c) RepNotify는 전송 자체가 없어 발화하지 않고 이후 첫 델타의 prev도 수신자 기본값이 된다. ResumeSession 무씬(로비) 경로는 캐치업이 owner=구 connId 뷰를 먼저 보낸 뒤 OwnerUpdate만 와 누락이 확정적이며, 씬 존재 시 SCN-002 ready 홀드가 순서를 뒤집어 우연히 정상화되는 비결정성이 있었다.

## 결정 (Decision)

1. **소유권 이전 시 그룹별 전체상태 baseline 재전송(상태 전용)** — `NetworkServer.SendOwnershipBaseline(수신자, netId, entry, isOwner)` 신설: 사전 `InvalidateDeferredRecipient(netId, 수신자)`(지연 델타가 baseline을 뒤덮는 스테일 오버라이드 방지 — SendBaseline 기존 계약) → per-sub `TryWriteFull`(fault 격리) + `SendReplicate`. 기존 wire 11/13 WriteFull 페이로드 재사용 — 신규 와이어·생성기·클라 변경 없음(비파괴).
2. **3경로 적용 규칙** — owner 교체·전파 후: 신규 owner(생존·승인·씬홀드 아님)에 isOwner=true baseline, 직전 owner(생존·승인·owner≠0)에 isOwner=false baseline. 소유자 없는 입양(구 owner 0)은 신규 owner만. connId 0 해제는 구 owner만. 씬홀드 수신자는 기존 계약대로 ready 보고의 캐치업이 커버.
3. **정교화 2건(구현 확정)** — (a) 헬퍼에 `IsRelevant` 게이트: 관련성 훅/컬 거부 연결로의 상태 유출 차단(기존 baseline 경로 전체의 계약; 관련 재진입 시 SendBaseline이 회복). (b) RunPostApproval/RunPostSceneReady의 즉시 채택 연결은 baseline 억제(직후 SendCatchup이 owner 뷰 전체상태를 이미 전송 — 이중 전송 제거); DetachConnection/SweepGraceSessions 경로(캐치업 없는 실제 결함 경로)는 baseline 유지.
4. **Notify 트레이드오프 수용** — baseline 수신 시 마스크 내 전 필드에 RepNotify 발화(동일값 포함) — 스폰/캐치업과 동일한 기존 계약이며 새 owner가 실제 값으로 초기 OnRep을 맞는 것이 바람직.
5. **기각 대안** — 스냅샷 리셋→강제 델타: InitialOnly 미커버·다음 틱 지연. 클라 풀상태 요청 RPC: 신규 와이어·과잉.

## 결과 (Consequences)

- **긍정**: 소유권 이전 후 새 owner가 OwnerOnly/InitialOnly 현재값을 즉시 수신하고, 이전 owner도 SkipOwner 현재값을 유지하며, RepNotify가 올바른 prev로 발화한다. 재부착(ResumeSession) 누락도 해소. 지연 큐 프라이버시 경계(감사 오탐 메모)는 flush 시점 그룹 선택으로 재확인·회귀 테스트로 고정.
- **부담 / 리스크**: baseline은 마스크 내 전 필드를 무조건 재전송(이전 빈도가 낮아 수용 — 측정 필요 시 후속). dormancy flush 회계는 스냅샷 미갱신으로 불변.
- **폐기 방법**: `SendOwnershipBaseline`과 3경로 호출을 제거하면 된다(단일 파일).

## 마이그레이션

없음 — 와이어·생성기·공개 API 무변경. 사용자 가시 동작 변화: 소유권 이전 시 새 owner·직전 owner에게 조건 필드 현재값이 재전달되고 RepNotify가 발화한다(기존에는 누락). 패키지 0.11.0.
