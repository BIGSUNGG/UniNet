# architecture — 시스템 구조

> 초기 설계안. 구현이 진행되면 실제 구조로 갱신한다 (doc-sync).

## 계층 구조

```text
┌─────────────────────────────────────────────┐
│ 게임 코드 (MonoBehaviour, 스크립트)          │
├─────────────────────────────────────────────┤
│ UniNet — Unity 통합 계층                     │
│  · MonoBehaviour RPC (기능 1)                │
│  · 변수 리플리케이션 엔진 (기능 2)            │
│  · 네트워크 오브젝트 ID·역할·가시성 관리      │
├──────────────────────┬──────────────────────┤
│ DRPC                │ MessageProtocol      │
│  계약 기반 RPC 허브   │  이진 직렬화 (소스젠) │
├──────────────────────┴──────────────────────┤
│ Communication — RUDP 전송 + DTLS 1.2         │
└─────────────────────────────────────────────┘
```

## 구성 요소 (구현 — [[0008-구현-아키텍처]])

- **UniNet.CodeGenerator** (`CodeGenerator/`) — Roslyn 4.3 소스 제너레이터. NetworkBehaviour 파생의 RPC·리플리케이션 멤버를 스캔해 DRPC 런타임 수동 구성 API로 허브 배선·타입별 partial 구현·델타 핸들을 방출한다. DRPC/MP 제너레이터와는 체이닝하지 않는다.
- **UniNet.Core.Hosting** (`Package/Runtime/UniNet.Core/Hosting/`) — 순수 C# 런타임. NetworkServer(연결·소유권·리플리케이션 틱 — P3 가시성·우선순위·휴면·주기·채널 예산 스케줄링 포함)·NetworkClient(Welcome·소유권 맵)·UniNetEnvironment(메인 스레드 펌프)·UniNetDispatch(전역 디스패치 — 다중 어셈블리)·UniNetEndpointOptions(연결/보안 설정 래핑)·IUniNetReplicationPolicy(오브젝트 정책 계약 — [[0011-P3-리플리케이션-고급-정책]])·UniNetTime(서버 권위 단조 시계)·SnapshotBuffer(인터폴레이션 버퍼)·PositionHistory(리와인드 히스토리) — [[0012-P4-훅-시간동기화-인터폴레이션-리와인드-그리드]].
- **UniNet.Unity** (`Package/Runtime/UniNet.Unity/`) — MonoBehaviour 바인딩. NetworkBehaviour(netId=씬 경로 해시·IsOwner)·UniNetManager(연결 수명주기)·UniNetDriver(구동기).
- **RPC 매핑** — 사용법 partial 메서드 → 생성 코드가 DRPC 허브 프로시저로 변환. 전달 모드는 `Delivery`(ReliableOrdered·Unreliable) → `RpcDeliveryMode` 매핑.
- **직렬화** — 모든 와이어 포맷은 MessageProtocol 런타임 프리미티브(`MessageBufferWriter/Reader`)로 생성 코드가 직접 방출 ([netId][인자/델타]).

## 의존성 규칙

1. 게임 코드는 **UniNet API만** 사용한다. **DRPC·MessageProtocol 타입은 게임 코드에 노출하지 않는다를 원칙으로 한다** — 보안(연결 키·DTLS)·속도(전달 모드) 설정은 `UniNetEndpointOptions`처럼 UniNet 타입으로 래핑해 제공한다. (ADR-0008)
2. UniNet은 기존 스택(DRPC·MessageProtocol·Communication)을 **우선 재사용**하고, 부족한 기능만 자체 구현한다. 우회·중복 구현이 필요해지면 ADR로 기록한다.
3. 기존 스택 수정이 필요하면 각 저장소에서 처리하고 버전으로 참조한다 (참조 방식은 패키지 고정 — ADR-0003).

## 데이터 흐름 (개념)

- **RPC 호출**: 게임 코드 메서드 호출 → UniNet이 오브젝트 ID·메서드를 DRPC 계약 호출로 변환 → 상대측에서 해당 오브젝트의 메서드 실행
- **리플리케이션**: 서버에서 변수 변경 감지 → 델타 메시지 생성(MessageProtocol) → 대상 연결 선별(가시성·소유권) → 클라이언트 적용·변경 콜백

## 외부 의존성

- Unity **6000.0.83f1** (6.0 LTS, 샌드박스 고정)
- DRPC 3.5.0·MessageProtocol 3.2.0·Communication(RUDP) 2.7.0 — **패키지 참조 고정** (ADR-0003), 로컬 소스 `unity-nuget/` + NuGetForUnity 4.5.0(OpenUPM)으로 Unity에 공급
- 그 외 라이브러리는 필요 시 추가 (ADR로 기록)

## 미정 사항

- 어셈블리 구조(asmdef 분할) — [[plan]] Phase 0 스파이크 후 확정 (현행 스캐폴드: `Package/Runtime`에 UniNet.Core·UniNet.Unity 2종)
- UPM-NuGet 연결 방식 — 1차 확정 NuGetForUnity 4.5.0, 스파이크 1(소스젠 동작) 통과 시 최종 확정 ([[plan]] Phase 0)
- 클라이언트-서버 간 공유 계약 코드 배치 방식
