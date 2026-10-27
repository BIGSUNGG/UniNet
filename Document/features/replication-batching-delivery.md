# replication-batching-delivery — 복제 배칭과 오브젝트별 전송 신뢰성

- **상태**: 구현됨
- **최초 작성**: 2026-10-13
- **마지막 갱신**: 2026-10-26

## 개요

복제 틱의 델타 전송을 (1) 커넥션별·틱별로 신뢰성별 배치 1개씩 묶어 보내고(wire 13), (2) 오브젝트 단위로 와이어 신뢰성을 고를 수 있게 한다(`NetworkReplicationDelivery`). `Unreliable` 오브젝트는 Sequenced(유실 허용·순서 보장) 스트림으로 나가며, 멈춘 뒤 유실된 최종 상태는 정지 시 신뢰 재전송(quiescence resend)으로 복구한다. 결정 배경: [[0039-복제-배칭과-신뢰성-분리]].

## 요구사항 / 목표

- 아레나 데모에서 관찰된 서버→클라 메시지 수 과다(델타 1개×커넥션 1개=메시지 1개, 전량 신뢰 채널)를 구조적으로 절감
- 이동 스트림 같은 고빈도 상태에서 RUDP ACK 왕복 제거
- 최종 상태 유실이 클라를 영구 정지시키지 않을 것

## 설계

- **`NetworkBehaviour`** (`Package/Runtime/UniNet.Unity/NetworkBehaviour.cs`) — `NetworkReplicationDelivery` 설정 멤버 (기본 `Delivery.ReliableOrdered`). `FlushNetworkDormancy()`는 환경의 서버에 `MarkNextSendReliable`를 호출해 깨어난 첫 델타를 신뢰로 강제
- **`IUniNetReplicationPolicy`** (`Package/Runtime/UniNet.Core/Hosting/IUniNetReplicationPolicy.cs`) — `NetworkReplicationDelivery` 정책 멤버 추가
- **`NetworkServer`** (`Package/Runtime/UniNet.Core/Hosting/NetworkServer.cs`) — 틱 전송 루프를 커넥션별 `BatchBuffer`(신뢰/비신뢰 2개) 누적으로 교체, 틱 끝에 `FlushBatch` 2회. `SubObjectEntry`에 `LastChangeTime`·`QuiescenceResent`·`NextSendReliable` 추가. 배치 상한 `MaxReplicateBatchBytes = 1000`(Sequenced 단편화 불가 — MTU 보호), 재전송 지연 `QuiescenceResendDelaySeconds = 0.25`
- **`IUniNetSystemChannel` / 생성 허브** (`CodeGenerator/Emitter.cs`) — `SendReplicateBatch(byte[] blob, RpcDeliveryMode mode)` (wire 13). 클라 수신자 `__ReplicateBatch_Requested`가 `UniNetBatchRegistry`(methodId→applier, 전역)로 엔트리를 적용 — 어떤 어셈블리가 수신해도 모든 등록 타입 적용. 메인 큐에서 배치 단위로 적용, 엔트리별 예외 격리
- **`UniNetReplicationHandler`** (`Package/Runtime/UniNet.Core/Hosting/UniNetReplicationHandler.cs`) — 델타 처리 계약. **`UniNetDeltaApplier`** (`UniNetDeltaApplier.cs`) — 배치 엔트리 적용 위임. **`UniNetBatchRegistry`** (`UniNetBatchRegistry.cs`) — wire 13 수신 라우팅용 전역 methodId→applier 등록소
- **`NetworkTransform`** (`Package/Runtime/UniNet.Unity/NetworkTransform.cs`) — `Awake`에서 `NetworkReplicationDelivery = Delivery.Unreliable` (위치·ack 스트림이 기본적으로 유실 허용 스트림을 탄다)

## 동작 상세

- **배칭**: 틱당 커넥션당 신뢰 배치 1개(`ReliableOrdered`) + 비신뢰 배치 1개(`Sequenced`), 최대 2메시지. 우선순위 순서는 배치 엔트리 순서에 보존. 예산·디퍼 정책은 기존과 동일(바이트 기준). 배치 1개는 트래픽 카운터에 1메시지로 집계(ADR-0029)
- **정지 재전송**: `Unreliable` 오브젝트가 마지막 델타 후 0.25s 이상 조용해지면 현재 풀 상태(수신 그룹 조건 필터 적용)를 신뢰 배치로 1회 재전송. 조용한 구간당 1회. 이동 중 유실은 다음 델타가 덮음. 신뢰 오브젝트는 재전송 없음
- **주기 기준선 (FR-02, 2026-10-26)**: 스냅샷은 델타 생산 즉시 전진하므로, 유실된 델타의 비트는 다른 필드가 계속 변경되는 동안 이후 델타에 재등장하지 않는다(정지 재전송은 서브 전체가 조용해져야 발동). `Unreliable` 서브가 델타를 계속 생산하는 동안 `UnreliableBaselineIntervalSeconds`(1.0s)마다 현재 풀 상태를 신뢰 배치로 재전송한다(첫 틱 평가에서 무장, 스폰/캐치업 기준선이 이미 전체 상태를 전달). 발동은 in-motion 게이트 — 최근 변경이 간격 내에 있는 서브만 재전송하며, 조용한 서브는 정지 재전송 1회로 끝난다(무한 재전송 방지). 수신 ack·와이어 변경 없이 수신자는 어떤 유실이든 한 간격 내 최신 권위 상태로 수렴한다. 컬렉션은 op 델타(tag-0) 재생이 아닌 최신 전체(tag-1) 치환으로만 수렴. 정지 재전송과 주기 전송은 서로 타이머를 재시작(중복 전송 방지 — 휴면 플러시는 누적 델타의 신뢰 강제일 뿐 타이머와 무관). 비용 상한: 가시 서브·연결당 간격마다 풀 상태 엔트리 1개
- **휴면 플러시**: 깨어난 첫 델타는 신뢰 강제 — 플러시 유실로 오브젝트가 잠긴 상태로 남는 경우를 차단
- **적체 정책 (FR-06, 2026-10-26)**: 예산 초과 연기 항목은 같은 (NetId, SubId)의 기존 연기 항목을 **최신 권위 전체 상태로 대체 병합**한다(캐치업 기준선과 동일한 와이어 형태 — 임의 이전 상태에서 수렴, FastArray op(tag-0)도 최신 full(tag-1) 치환). Seq·EnqueuedAt·수신자 비트(합집합)는 유지돼 공정 순서·기아 부스트·무손실이 보존되고, 병합 성공 시 연기 큐는 고유 서브 키 수에 묶여 시간에 비례한 무한 축적이 불가능하다(병합 실패 폴백 — 매 틱 직렬화 장애·조건 비대칭의 병리 경로만 — 은 LogFault로 가시화되며 항목을 유지한다). 큐 상한 `MaxDeferredDeltas`(기본 1024) 도달 시 초과 후보는 강제 송신되고(`DeferredForcedSendCount` 관측 — 실제 전송된 경우만 계수, 예산 초과 단일 델타 반전송 선례) — 드롭이 아니므로 수신자는 항상 수렴한다. 휴면 보류 항목(스냅샷 이미 전진)은 드롭 금지 계약 유지(휴면 중 생산 중단으로 객체 수에 유한)
- **메인 큐·수신 관측 (FR-06)**: `UniNetEnvironment.MainQueueCapacity`(기본 65536) 상한 초과 적재는 `LogFault` 예외 + `DroppedMainActions` 카운터로 명시 드롭된다(전량 drain 계약 불변). 수신 측 슬롯 부재/미등록 methodId skip은 `DroppedReplicateEntries`로 관측된다(생성 코드 경로 — 동봉 analyzer 재빌드 후 적용)
- **버전**: 와이어 포맷 변경 — `UniNetInfo.Version` 0.3.0 (ADR-0028 핸드셰이크가 구버전 혼속 거부)
- 스폰·캐치업·OwnerUpdate·킬피드 같은 이벤트 RPC는 기존 신뢰 경로 그대로

## 테스트 / 검증

- **EditMode** `ReplicationDeliveryBatchTests` 10종 — 기존 7종(기본 정책→신뢰 배치, Unreliable→Sequenced, 같은 틱 병합, 상한 분할, 정지 재전송 1회성·신뢰성·풀상태, 신뢰 무재전송, 휴면 플러시 강제) + FR-02 3종(다른 필드 60Hz 지속 변경 중 1회 유실 델타의 주기 기준선 수렴 — 현재는 영원히 어긋나던 반례, FastArray op 델타 유실 시 최신 전체 치환 수렴, 조용한 서브의 주기 재전송 부재·정지 재전송 1회 단언)
- **EditMode** `DeferredBacklogTests` 4종(FR-06) — 120틱 지속 과부하에서 연기 큐가 키 수에 묶이고(무한 축적 아님) 병합 카운터 관측 후 수신자 최신 권위 값 수렴, FastArray 변경의 최신 전체 수렴, 큐 상한 도달 초과분의 강제 송신 명시성(`DeferredForcedSendCount`+실제 전송 단언), 메인 큐 상한 초과의 LogFault+카운터 명시 관측과 전량 드레인 계약 유지
- **EditMode** `AoiReentryBaselineTests` 3종(FR-04) — 동적 스폰→AOI 이탈→서버 값 변경→재진입 스폰 수신 시 기존 인스턴스 값·트랜스폼 갱신·무재생성·SubId 유지, 호스트 원본 분기 무간섭, 슬롯 불일치 거부·기존 값 보존
- 기존 테스트 채널(`RecordingChannel` 등)은 배치를 엔트리로 풀어 기존 단언을 유지 — 전 테스트 회귀 없음 (EditMode 131/131·PlayMode 27/27, 2026-10-13)

## 변경 이력

- 2026-10-13 — 최초 작성 (배칭·신뢰성 분리·정지 재전송 구현)
- 2026-10-26 — FR-02 주기 기준선(이동 중 유실 수렴) 추가·문서화
- 2026-10-26 — FR-06 유한 적체 정책(키별 최신 전체 병합·상한 강제 송신)·메인 큐 상한·수신 관측 카운터 추가·문서화
