# 00-INDEX — UniNet 문서 지도

> **AI 세션 시작 규칙**: 작업 시작 전 이 문서와 [[overview]]를 먼저 읽는다.
> 작업 영역과 관련된 문서를 아래에서 찾아 읽은 후 시작한다. 문서 갱신은 `doc-sync` 스킬을 따른다.
> **사람 참고 규칙**: Obsidian으로 이 폴더(`Document/`)를 Vault로 열어 사용한다.

## 핵심 문서

| 문서 | 내용 | 갱신 시점 |
| --- | --- | --- |
| [[overview]] | 프로젝트 정의·목표·기술 스택 | 방향이 바뀔 때 |
| [[architecture]] | 시스템 구조·모듈·의존성 규칙 | 구조가 바뀔 때 |
| [[conventions]] | 코딩 규약 (OOP/SOLID, 네이밍, 문서 규약) | 규약이 정해지거나 바뀔 때 |
| [[roadmap]] | UE Network Framework 패리티 매트릭스·단계 | 기능 상태가 바뀔 때 |
| [[plan]] | 구현 진행 플랜 (Phase 0~4·완료 정의·난점) | 단계 진입·완료 시 |
| [[changelog]] | 모든 변경의 기록 (기능 단위) | 모든 의미 있는 변경 시 |
| [[harness]] | AI 하네스 구성 (AGENTS.md, 스킬, 훅, 에이전트) | 하네스를 바꿀 때 |

## 영역별 문서

### features/ — 기능 문서

- [[features/monobehaviour-rpc-replicate|monobehaviour-rpc-replicate]] — MonoBehaviour RPC 3종 + [Replicated]/RepNotify + 동적 스폰/파괴·조건부 (P1 전체 + P2 전체, 구현됨)
- [[features/connection-lifecycle|connection-lifecycle]] — 연결 수명주기 이벤트: 서버 접속/해제 이벤트·클라 해제 콜백·상태 조회 (구현됨)
- [[features/replication-p3-policy|replication-p3-policy]] — 리플리케이션 고급 정책: 가시성·우선순위·휴면·전송 주기·채널 예산 (P3, 구현됨)
- [[features/replication-p4-hooks|replication-p4-hooks]] — P4 훅: UniNetTime·SnapshotBuffer·PositionHistory 리와인드·그리드 가시성 (구현됨)

### examples/ — 예시 게임 문서

- [[examples/arena-shooter|arena-shooter]] — Sandbox 아레나 슈팅 예시 게임 (구현 기능 전부 활용·MPPM 실행 가이드·기능 매트릭스·2-프로세스 검증, 구현됨)

### upstream-blockers — 상류 의존 과제

- [[upstream-blockers]] — DRPC/MessageProtocol/Communication 저장소 수정이 필요한 잔여 과제 (커스텀 NetSerialize·FastArray — UniNet 단독 불가, 문서화만)

### decisions/ — 아키텍처 결정 기록 (ADR)

- [[0001-하네스-엔지니어링-도입]] — AI 협업 하네스와 문서 규칙 도입
- [[0002-기반-스택과-스코프-확정]] — 상용 유니티 게임 서버 라이브러리로 정의, DRPC/MessageProtocol 재사용, Unity 6, Unity 서버 빌드
- [[0003-패키지-참조-고정]] — 기반 스택을 NuGet+UPM 패키지 참조로 고정
- [[0004-계약-자동-생성]] — MonoBehaviour RPC 계약 자동 생성 (UniNet 소스젠, 수동 폴백)
- [[0005-개발-환경-샌드박스-upm]] — 개발 환경 확정: Unity 샌드박스 + UPM 로컬 참조 (저장소 내 `/Sandbox`)
- [[0006-리뷰-품질-게이트-확대]] — 리뷰 품질 게이트 확대: 트리거 확대·차원 스킬 3종(구조/보안/속도)·README 갱신 규칙
- [[0007-사용법-우선-api-확정]] — 사용법 우선 개발 + 공개 API 스타일 확정 (Mirror/Netcode류 속성; 변경 이력 — partial 재구조화로 ADR-0008 승계)
- [[0008-구현-아키텍처]] — P1 RPC + Replicate 기본 구현: 소스젠(로슬린 4.3) 직접 배선·다중 어셈블리·씬경로 netId·라운드로빈 소유권·검증 증거
- [[0009-동적-스폰-조건부-리플리케이션]] — P2 완결: 명시적 Spawn/NetworkDestroy·타입 카탈로그·캐치업·ReplicateCondition(OwnerOnly/SkipOwner/InitialOnly)·호스트 권위 원본 보존
- [[0010-2층-식별자-다중-컴포넌트]] — 다중 NetworkBehaviour 지원: GameObject 단위 netId + SubId 슬롯·다중 서브 스폰·슬롯 대조
- [[0011-P3-리플리케이션-고급-정책]] — P3 완결: IUniNetReplicationPolicy·틱 이중 모드·가시성 계약·기아 보정 우선순위·채널 예산·휴면 2상태·MP 의존 2종 P4 이관
- [[0012-P4-훅-시간동기화-인터폴레이션-리와인드-그리드]] — P4 완결: UniNetTime(TimeSync 와이어 5번)·SnapshotBuffer·PositionHistory 리와인드·그리드 가시성·아레나 예측 전환
- [[0013-NetworkTransform-컴포넌트]] — NetworkTransform 컴포넌트: 이동 예측의 라이브러리 승격(조합 채택 — 제너레이터 리프 스캔 한계)·MovementRule 주입·제너레이터 겹리(0.1.2)
- [[0014-스폰-서브-자동-복원]] — 멀티 컴포넌트 스폰 슬롯 불일치 근본 해결: 클라 서브 자동 복원(typeKeys)·RegisterPrefab 필수 → 선택 완화
- [[0015-연결-수명주기-이벤트-게이트웨이]] — 연결 수명주기 이벤트: 게임 콜백 위임 채택·발화 순서 계약(재배정 전/캐치업 후)·구독자 격리
- [[0016-ServerRpc-소유자-자동-강제]] — ServerRpc 발신자-소유자 자동 대조(보안 기본값)·RequireOwnership 옵트아웃·마이그레이션 노트
- [[0017-동적-스폰-NetworkInstantiate-통합]] — Spawn(등록 전용) 제거·NetworkInstantiate(복제 겸함) 단독 API·configure 콜백(비직렬화 InitialOnly 초기화)
- [[0018-ServerRpc-Validate-옵트인]] — `_Validate` 자동 감지 제거·`[ServerRpc(Validate = true)]` 옵트인·불일치 진단(UNINET011 에러·UNINET012 경고)·마이그레이션 노트

### _templates/ — 문서 템플릿

- `_templates/feature.md` — 기능 문서 템플릿
- `_templates/adr.md` — ADR 템플릿

## 최근 변경 (자세한 것은 [[changelog]])

- 2026-09-22 — **ServerRpc 검증 훅 옵트인화** (ADR-0018: `_Validate` 자동 감지 제거·`[ServerRpc(Validate = true)]` 옵트인 — UNINET011 에러·UNINET012 경고 신설. 제너레이터 0.1.5. in-repo `_Validate` 5곳 마이그레이션. EditMode 60/60·PlayMode 15/15)
- 2026-09-22 — **ServerRpc 소유자 자동 강제** (ADR-0016: 비소유 발신 거부 + RequireOwnership 옵트아웃 — netId 위조로 타 오브젝트 RPC 실행 불가화. 제너레이터 0.1.3. EditMode 60/60·PlayMode 15/15)
- 2026-09-22 — **동적 스폰 API 교체** (ADR-0017: Spawn 제거·NetworkInstantiate(복제 겸함·configure 콜백) 통합·NetworkDestroy 유지. EditMode 60/60·PlayMode 15/15)
- 2026-09-21 — **연결 수명주기 이벤트 — 게임 콜백 위임** (ADR-0015: 서버 접속/해제 이벤트·클라 해제 콜백·상태 조회 + Arena 퇴장 처리 + 상류 의존 잔여 과제 문서 신설. EditMode 56/56·PlayMode 14/14)
- 2026-09-20 — **NetworkTransform 컴포넌트 + 스폰 서브 자동 복원** (ADR-0013·0014: 이동 예측 라이브러리 컴포넌트화 + 멀티 컴포넌트 스폰 슬롯 불일치 근본 해결. EditMode 52/52·PlayMode 12/12)
- 2026-09-20 — **P4 훅 완결 — 시간 동기화·인터폴레이션·리와인드·그리드 가시성 구현** (ADR-0012: UniNet 단독 구현분 전부. EditMode 45/45·PlayMode 10/10·아레나 예측/히트스캔 전환)
- 2026-09-18 — **P3 완결 — 리플리케이션 고급 정책 구현** (ADR-0011: 가시성·우선순위·휴면·전송 주기·채널 예산 — UniNet 단독 구현분 5종. EditMode 38/38·PlayMode 7/7·아레나 예시 통합)
- 2026-09-18 — **Sandbox Usage 예시 7종 제거** (아레나와 무관한 사용법 예제 정리 — README 코드 예시로 대체)
- 2026-09-17 — **수명주기 종료 API 추가 — RUDP 포트 잔존 바인딩 실패 근본 해소** (ServerStopAsync/ServerStop/ClientStop/HostStopAsync/HostStop — LifecycleStopTests 3종·PlayMode 7/7×2회·EditMode 20/20 통과, UniNet.CodeGenerator 0.1.1)
- 2026-09-17 — **Sandbox 아레나 슈팅 예시 게임 구현** (examples/arena-shooter: 구현 기능 P1+P2 전부 활용 — MPPM 설치·메인=서버/가상 플레이어=클라 토폴로지·Arena 씬·PlayMode 테스트 통과·2-프로세스 클라 2 접속 검증 PASS)

- 2026-09-16 — **다중 NetworkBehaviour 지원 — 2층 식별자(netId+SubId) 구현** (ADR-0010: 다중 서브 스폰·subId RPC 라우팅·슬롯 대조. EditMode 19종·PlayMode 3종·2-프로세스 MULTI-COMPONENT PASS)
- 2026-09-16 — **P2 완결 — 동적 스폰/파괴·조건부 리플리케이션·InitialOnly 구현 완료** (ADR-0009: 명시적 Spawn/NetworkDestroy API·타입 카탈로그·후발 접속 캐치업·호스트 권위 원본 보존. EditMode 13종·PlayMode 2종·2-프로세스 DYNAMIC-SPAWN-DESTROY PASS)
- 2026-09-14 — **P1 RPC + P2 Replicate 기본 구현 완료** (ADR-0008: 소스젠·런타임·2-프로세스 RUDP 왕복 검증 포함. 첫 기능 문서 등재)
- 2026-09-14 — 사용법 우선 API 확정 (ADR-0007: 공개 API 스타일 Mirror/Netcode류·Sandbox 사용법 예제·Package API 스텁 · 변경 이력 — RepNotify·MulticastRpc 확장 후 partial 재구조화)
- 2026-09-14 — 기반 패키지 최신화: DRPC 3.5.0 / MessageProtocol 3.2.0 / Communication(RUDP) 2.7.0 (ADR-0005 변경 이력)
- 2026-09-14 — 리뷰 품질 게이트 확대 (ADR-0006: 트리거 확대·차원 스킬 3종·README 갱신 규칙)
- 2026-09-14 — 개발 환경 구축 완료 (ADR-0005 변경 이력: Package/ 분리·6000.0.83f1·NuGetForUnity 4.5.0·기반 패키지 버전 확정)
- 2026-09-14 — 개발 환경 확정 (ADR-0005: Unity 샌드박스 + UPM 로컬 참조, 저장소 내 `/Sandbox`)
- 2026-09-13 — 구현 플랜 확정 (plan·ADR-0003 패키지 참조·ADR-0004 계약 자동 생성)
- 2026-09-13 — 프로젝트 정의·기반 스택·스코프 확정 (overview·architecture·roadmap·ADR-0002)
- 2026-09-13 — 하네스 엔지니어링 도입 (문서 Vault + AGENTS.md + 스킬 + 훅 + 리뷰어)
