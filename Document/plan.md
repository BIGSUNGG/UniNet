# plan — 구현 진행 플랜

로드맵([[roadmap]])을 실행 단위로 푼 플랜. 핵심 원칙: **얇은 수직 슬라이스** —
"연결 1개 → 오브젝트 RPC 1번 → 변수 1개 동기화"가 도는 최소 경로를 먼저 만들고 폭을 넓힌다.
단계 진입·완료 시 이 문서의 상태와 [[roadmap]] 매트릭스, [[changelog]]를 함께 갱신한다.

## Phase 0 — 결정·스캐폴드 (상태: 진행중 — 결정 4건 확정 · 개발 환경 구축 완료 · 사용법 API 확정)

| 항목 | 상태 | 비고 |
| --- | --- | --- |
| 개발 환경 | **구축 완료** — Unity 6000.0.83f1 · NuGetForUnity 4.5.0 | 2026-09-14. 패키지 `Package/` 하위 + `file:../../Package` 참조 (ADR-0005 변경 이력 참고) |
| 참조 방식 | **확정** — 패키지 고정 (ADR-0003) · 버전 확정: DRPC 3.5.0 / MessageProtocol 3.2.0 / Communication(RUDP) 2.7.0 | 로컬 소스 `unity-nuget/` |
| RPC 계약 방식 | **확정** — 자동 생성 (ADR-0004) | UniNet 자체 소스젠 |
| 공개 API 스타일 | **확정** — Mirror/Netcode류 속성 (`[ServerRpc]`·`[ClientRpc]`·`[MulticastRpc]`·`[Replicated(Notify)]`·`IsOwner`·`UniNetManager`) | 2026-09-14. 사용법 우선 확정 (ADR-0007) — RepNotify(이전값 1개 콜백)·MulticastRpc 확장 포함, `Sandbox/Assets/Scripts/` 사용법 + Package API 스텁, batch 컴파일 녹색 |
| 어셈블리 구조 확정 | **확정(1차)** — `UniNet.Core`(순수 C#·Hosting 런타임) / `UniNet.Unity`(바인딩) / 독립 `CodeGenerator/`(dotnet·Roslyn 4.3) / 테스트 3종(Fixtures·EditMode·PlayMode) | ADR-0008. 스파이크 대상이던 소스젠 2종은 3.5.0 라인에서 Roslyn 4.3 메인라인 전환 확인(경고 소멸) |
| UPM-NuGet 연결 방식 | **1차 확정** — NuGetForUnity 4.5.0 (OpenUPM 고정) | 소스젠 2종 동작을 스파이크 1에서 검증 후 최종 확정 |

**스파이크 (기술 리스크 조기 제거, 순서대로):**

1. 저장소 내 `/Sandbox` Unity 6 프로젝트(ADR-0005)에서 MessageProtocol + DRPC 패키지 로드 → 에코 RPC 왕복 (소스젠 2종이 Unity 컴파일 파이프라인에서 동작하는가)
2. 전용서버 빌드(전용 서버 빌드 옵션)에서 Communication RUDP 동작
3. IL2CPP AOT 빌드 확인 (reflection 금지 설계 검증)

## Phase 1 — MonoBehaviour RPC (핵심 기능 1, 상태: **구현됨 — 2026-09-14, ADR-0008**)

| 순서 | 작업 | 비고 |
| --- | --- | --- |
| 1 | NetworkManager — 연결 수명주기 래핑 | Communication/DRPC 위 얇은 계층 |
| 2 | NetworkObject — 오브젝트↔네트워크 ID 등록·조회 + 스폰/파괴 동기화 최소치 | 라우팅·리플리케이션의 기반. 전체 스폰/파괴 동기화는 P2 (roadmap) |
| 3 | RPC 바인딩 — 메서드 속성 선언 → 자동 생성 계약 (ADR-0004) | 전달 모드 5종 노출 |
| 4 | 네트워크 역할(권위/프록시) 최소 개념 | 서버 권위 모델 기반 |
| 5 | 샌드박스 데모 + 유닛 테스트 | 서버·클라 2 인스턴스 |

**완료 정의**: 오브젝트 대상 Server/Client/Multicast RPC가 양방향 왕복하고, 유닛 테스트가 이를 검증한다.

## Phase 2 — 변수 Replicate (핵심 기능 2, 상태: **기본 구현됨 — 델타·RepNotify·소유권·초기 전송(씬 오브젝트 기준). 잔여: 동적 스폰/파괴 동기화·조건부(COND_OwnerOnly)·InitialOnly 스폰 시 전체 전송**)

변경 감지(dirty) → 델타 직렬화(MessageProtocol 위) → `[Replicated]` 마킹과 코드 생성 → 조건(OwnerOnly 등)·소유권·RepNotify → 스폰 시 초기 전송.

**완료 정의**: HP·위치 등이 조건·델타로 동기화되고 변경 콜백이 호출된다.

## Phase 3·4 — roadmap 매트릭스 순서대로

가시성 → 우선순위 → dormancy → 예측·래그컴펜세이션 훅. 세부는 [[roadmap]] 참조.

## 설계 난점 (설계 중심 관건)

1. **오브젝트 라우팅** — DRPC 허브는 연결 대상 RPC다. 네트워크 ID 기반 오브젝트 분배 계층이 P1의 본질적 난점
2. **코어-바인딩 분리** — 라우팅·리플리케이션 엔진은 순수 C#(UniNet.Core), MonoBehaviour는 얇은 바인딩(UniNet.Unity). 테스트 가능성 + AOT 안전 + 이식성
3. **소스젠 이중화** — MP·DRPC·UniNet 제너레이터 병렬 동작 파이프라인 검증

## 리스크 제거 순서

Unity+소스젠 호환 → 전용서버 RUDP → IL2CPP AOT → 성능 벤치 (`_bench` 패턴 재활용).

## 관리 규칙

- 각 Phase 착수·완료 시 이 문서 상태 표 + [[roadmap]] 상태 + [[changelog]] 갱신
- Phase 0의 미정 항목이 확정되면 해당 ADR 또는 [[architecture]]에 반영
- 플랜 변경은 ADR 또는 이 문서 갱신으로 남긴다 (리뷰어 검토 대상)
