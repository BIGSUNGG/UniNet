# replication-batching-delivery — 복제 배칭과 오브젝트별 전송 신뢰성

- **상태**: 구현됨
- **최초 작성**: 2026-10-13
- **마지막 갱신**: 2026-10-13

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
- **휴면 플러시**: 깨어난 첫 델타는 신뢰 강제 — 플러시 유실로 오브젝트가 잠긴 상태로 남는 경우를 차단
- **버전**: 와이어 포맷 변경 — `UniNetInfo.Version` 0.3.0 (ADR-0028 핸드셰이크가 구버전 혼속 거부)
- 스폰·캐치업·OwnerUpdate·킬피드 같은 이벤트 RPC는 기존 신뢰 경로 그대로

## 테스트 / 검증

- **EditMode** `ReplicationDeliveryBatchTests` 7종 — 기본 정책→신뢰 배치, Unreliable 정책→Sequenced 배치, 같은 틱 여러 델타→배치 1개 병합, 배치 상한 초과 시 같은 틱 내 분할, 정지 재전송 1회성·신뢰성·풀상태, 신뢰 오브젝트 무재전송, 휴면 플러시 신뢰 강제
- 기존 테스트 채널(`RecordingChannel` 등)은 배치를 엔트리로 풀어 기존 단언을 유지 — 전 테스트 회귀 없음 (EditMode 131/131·PlayMode 27/27, 2026-10-13)

## 변경 이력

- 2026-10-13 — 최초 작성 (배칭·신뢰성 분리·정지 재전송 구현)
