# MonoBehaviour RPC & 변수 Replicate

- **상태**: 구현됨 (P1 전체 + P2 기본 — [[0008-구현-아키텍처]] 참조)
- **최초 작성**: 2026-09-14
- **마지막 갱신**: 2026-09-14

## 개요

`NetworkBehaviour` 파생 타입에 `[ServerRpc]`·`[ClientRpc(Delivery)]`·`[MulticastRpc]` 속성으로 partial 선언하고 본문을 `{Name}_Implementation`에, 선택으로 `{Name}_Validate`에 쓰면 — UniNet 소스 제너레이터가 DRPC 허브 배선과 직렬화를 생성해 서버-클라 간 오브젝트 단위 RPC가 동작한다. `[Replicated(Notify = nameof(...))]` 필드는 서버에서 변경 시 델타가 클라에 동기화되고 RepNotify(이전값) 콜백이 호출된다.

## 요구사항 / 목표

- 사용은 간단하게: 속성 + partial 선언만 (사용법: `Sandbox/Assets/Scripts/Player.cs`)
- 서버 권위: ServerRpc/리플리케이션 주도권은 서버. MulticastRpc 클라 호출은 로컬 전용·비전파
- 기본형(string 포함) 매개변수·필드 지원, 잘못된 선언은 컴파일 타임 진단(UNINET0xx)

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

## 알려진 한계 (신뢰 경계 포함)

- **ServerRpc 발신자 미검증** — 수신 핸들은 페이로드의 netId만으로 대상을 찾아 `_Implementation`를 실행한다. 악의적 클라가 다른 오브젝트의 netId로 페이로드를 조립하면 피해자 오브젝트의 ServerRpc가 실행될 수 있다. `_Validate`는 인자만 검증 가능(발신자 불가). 완화 플러밍은 마련됐다 — 연결별 허브가 발신 connId를 주입하고 디스패치까지 전달되므로(`senderConnId`), 소유권 대조 강화는 후속 과제다.
- **접속 직후 첫 전송 레이스** — 접속 완료 직후(Welcome/소유권 수신 전) 즉발 one-way RPC가 유실될 수 있음 (ADR-0008 알려진 한계 — 상류 조사 후보). 연결 확정 후 전송하는 자연 패턴은 무영향.
- **연결 종료 수명주기 미구현** — `Stop`/`Shutdown`이 없고 `ServerAsync` 재호출 시 이전 리슨 핸들이 교체만 된다. P1 범위 밖 — 후속 구현.
- **소유권은 라운드로빈 최소 정책**, dirty 검출은 틱마다 폴링 비교 (ADR-0008 — 위빙 배제의 대가).
- **RPC 매개변수·리플리케이션 필드는 기본형 + string만** (진단 UNINET002·UNINET008 — 32필드 상한).

## 테스트 / 검증

- EditMode 유닛 4종: `Sandbox/Assets/Tests/EditMode/` — FNV 안정성·옵션 매핑·소유권 정책·델타/RepNotify 이전값 (`tests-editmode.xml`)
- PlayMode 호스트 왕복: `Sandbox/Assets/Tests/PlayMode/HostRoundtripTests.cs` — 루프백 RUDP 전 경로 (`[UNINET-VERIFY]` 마커)
- 2-프로세스 왕복: `Sandbox/Assets/Tests/Fixtures/TwoProcessRunner.cs` (`--uninet-role=server|client`) — 프로세스 간 RUDP (`[UNINET-2PROC]` 마커, 로그: `Sandbox-2proc-*.log`)

## 변경 이력

- 2026-09-14 — 최초 작성 (구현 완료와 함께)
