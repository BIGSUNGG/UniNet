# v0.23.0 마이그레이션 가이드

v0.22.0 → v0.23.0 전환 안내. 이번 릴리스는 [[research/unity-server-functional-review]]의 FR-01~07 안정화 릴리스다. **와이어 메시지 형식은 전부 불변**이지만 기본 핸드셰이크 버전이 패키지 버전을 따라 0.22.0 → 0.23.0으로 상승한다(ADR-0028 관례).

## 호환성 요약

| 항목 | 변경 | 조치 |
| --- | --- | --- |
| 와이어 형식 | 불변 | 없음 |
| 핸드셰이크 기본 버전 | `UniNetInfo.Version` 0.23.0 상승 | 기본 `ProtocolVersion`을 쓰면 0.22.0 클라와 0.23.0 서버(및 그 반대)가 거부된다 — 동시에 업데이트하거나 게임 식별자 `ProtocolVersion`을 직접 설정할 것 |
| 공개 API | `ServerConnection.CanReceiveGameData`, `INetworkMoveSimulation.ReconcileState`(DIM 기본값), `UniNetEnvironment.MainQueueCapacity/DroppedMainActions/DroppedReplicateEntries`, `NetworkServer.DeferredCoalescedCount/DeferredForcedSendCount`, `UniNetEnvironment.TryQueueOnMain/ReservedCleanupCount/ReservedCleanupDrainedCount` 추가(`MaxDeferredDeltas`는 내부 상한으로 공개 설정 API가 아님) | 기존 API 파괴 없음. 커스텀 `INetworkMoveSimulation` 구현은 아무것도 하지 않아도 된다(기본 no-op — 기존 discard-only 동작 유지) |
| 생성기 | 0.2.18(소스 빌드 복구 + 코덱/진단 수정) → 0.2.19(수신 배열 선할당 경계 강화 — 와이어 불변) | 패키지 갱신만으로 동봉 analyzer가 교체된다. 생성 코드 재생성 필요 없음(와이어 불변) |

## 동작 변경 (게임이 체감하는 차이)

1. **승인/씬 준비 전 게임 RPC 송신이 즉시 드롭된다 (FR-01)** — 방송 `[ClientRpc]`·`To`/`ToOwner`·`[MulticastRpc]`의 네트워크 송신이 승인 전 연결·씬 로드 대기 연결에는 더 이상 나가지 않는다. 이전에 미승인 연결로 게임 RPC를 보내 "나중에 도착"을 기대했다면 그 패킷은 이제 영원히 가지 않는다(시스템 핸드셰이크는 그대로). 접속 직후 게임 데이터를 보내려면 승인·씬 준비 완료 후 보내라 — `ServerConnection.CanReceiveGameData`로 송신 가능 여부를 확인할 수 있다.
2. **RPC 컬렉션 인자의 상한·경계 (FR-03)** — 배열/List 인자가 65,535개를 넘으면 직렬화 전에 `ArgumentException`이 발생한다(이전에는 길이 접두가 잘려 수신측이 조용히 어긋났다). 비어 있지 않은 List 인자가 올바르게 복원된다. 수신 페이로드에 미소비 bytes가 남으면 구현이 호출되지 않고 드롭된다. 수신측은 배열 개수가 남은 페이로드 바이트보다 크면 선할당 전에 거부한다(지원 요소마다 와이어 ≥1바이트, analyzer 0.2.19). 잘린 입력을 빈/짧은 배열로 바꿔 받아들이지 않는다.
3. **AOI 재진입 시 기존 인스턴스가 갱신된다 (FR-04)** — 관심영역에서 나갔다가 돌아온 동적 객체의 스폰 기준선(트랜스폼+전체 상태)이 클라이언트의 기존 인스턴스에 그대로 적용된다. 이전처럼 "재진입 메시지가 무시돼 옛 값이 남는" 동작에 의존하는 코드는 없어야 한다(그 동작 자체가 결함이었다).
4. **Unreliable 이동 중 유실이 1초 내 수렴한다 (FR-02)** — 계속 변경되는 `Delivery.Unreliable` 객체는 1.0초마다 신뢰 배치로 전체 상태 기준선을 재전송받는다(가시 서브·연결당 간격마다 전체 상태 1엔트리의 추가 대역폭). 모든 필드가 멈춘 객체는 기존 0.25초 정지 재전송만 발동한다.
5. **과부하 중 연기 항목이 최신 전체 상태로 병합된다 (FR-06)** — 예산 초과로 연기된 변경은 같은 객체의 대기 항목에 최신 전체 상태로 합쳐진다. 수신자는 "낡은 값 → 최신 값"의 순차 중간 전달 대신 최신 전체 상태 1회를 받는다(수렴은 동일·순서 보존). 연기 큐는 1024항목 상한, 초과분은 강제 송신된다.
6. **메인 큐가 유한하다 (FR-06)** — `PumpMain`이 프레임마다 큐를 비우지 못하는 병리 상황에서 65,536개를 넘는 작업은 드롭되고 `LogFault`+`DroppedMainActions`로 명시 관측된다. 이전의 무한 성장과 달리 조용히 쌓이지 않는다 — 이 로그를 본다면 메인 스레드가 블록되고 있다는 신호다.
7. **직렬화 장애 중에도 연기 큐 상한을 우회하지 않는다 (AUDIT-F11)** — 매 틱 `WriteFull`이 실패하는 커스텀 핸들러(정당한 공개 확장 계약)가 있어도 같은 (NetId, SubId)의 연기 항목이 매 틱 추가되지 않는다. 병합 불가 시 Seq가 오래된 원본이 예산 무시 신뢰 배치로 강제 배출되고 최신 원본도 동일 reliable 레인을 타도록 표시해 큐에 남긴다. 또한 한 뷰만 직렬화에 실패해도(예: owner 뷰만) 병합은 무효 — 새 owner 델타가 폐기되지 않고, 장애 회복 후 정상 송신 가능한 적격 수신 그룹은 값 재변경 없이 권위값으로 수렴한다(채널 예외까지 자동 재시도하는 계약은 아님).
8. **포화·예약 정리 적체 중 신규 입장이 실제로 거부된다 (AUDIT-F12)** — `AttachConnection`은 일반 큐가 포화되었거나 예약 cleanup이 남았으면 0 반환·완전 롤백·hub disconnect로 입장을 거부한다. 일반 작업 예외로 예약이 아직 실행되지 않았는데 빈 일반 큐만 보고 재입장시키지 않는다. 적체는 일반 상한 + 이미 수락된 연결 집단의 일회성 정리에 묶이며, 예약 드레인 후 입장이 재개된다. 펌프 예외 전파·전량 처리·재진입 계약은 불변이다.
9. **우선순위 변경도 같은 키의 이전 연기분을 추월하지 않는다 (AUDIT-F15)** — 이전 연기분과 새 후보는 그룹의 최대 유효 우선순위를 공유하고 Seq로 정렬한다. 예산 개방과 우선순위 상승이 동시에 일어나도 새 delta 다음에 낡은 delta를 전송하지 않는다. 다른 키는 기존 유효 우선순위 규칙을 따른다.

## 관측 지점 (운영)

- `NetworkServer.DeferredCoalescedCount` / `DeferredForcedSendCount` — 과부하 병합·상한 강제 송신 횟수.
- `UniNetEnvironment.DroppedMainActions` — 메인 큐 상한 초과 드롭(정상 0).
- `UniNetEnvironment.DroppedReplicateEntries` — 수신측 슬롯 부재/미등록 methodId 드롭(버전 불일치·스폰/파괴 경합 신호).
- `UniNetEnvironment.ReservedCleanupCount` / `ReservedCleanupDrainedCount` — 예약 FIFO 입장/dequeue 누계(차감은 처리 중 작업을 제외한 대략적 대기 깊이, 정상 0). `PendingMainActions`는 일반+예약 대기 수다.

## 서버 배포 순서 (권장)

1. 서버와 클라이언트를 같은 빌드(0.23.0)로 함께 배포한다(기본 `ProtocolVersion` 사용 시 필수).
2. 커스텀 `ProtocolVersion` 식별자를 쓰는 게임은 이번에 와이어 변경이 없으므로 식별자를 그대로 두면 구 클라와 혼합 가능하다 — 단, 1·3번 동작 변경은 클라이언트 측 효과이므로 양단 0.23.0을 권장한다.

## 관련 문서

- [[changelog]] · [[features/replication-batching-delivery]] · [[features/replication-p3-policy]] · [[features/movement-prediction]] · [[research/unity-server-functional-review]] · [[research/v0-23-stabilization-review]]
