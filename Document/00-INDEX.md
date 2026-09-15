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

- [[features/monobehaviour-rpc-replicate|monobehaviour-rpc-replicate]] — MonoBehaviour RPC 3종 + [Replicated]/RepNotify (P1 전체 + P2 기본, 구현됨)

### decisions/ — 아키텍처 결정 기록 (ADR)

- [[0001-하네스-엔지니어링-도입]] — AI 협업 하네스와 문서 규칙 도입
- [[0002-기반-스택과-스코프-확정]] — 상용 유니티 게임 서버 라이브러리로 정의, DRPC/MessageProtocol 재사용, Unity 6, Unity 서버 빌드
- [[0003-패키지-참조-고정]] — 기반 스택을 NuGet+UPM 패키지 참조로 고정
- [[0004-계약-자동-생성]] — MonoBehaviour RPC 계약 자동 생성 (UniNet 소스젠, 수동 폴백)
- [[0005-개발-환경-샌드박스-upm]] — 개발 환경 확정: Unity 샌드박스 + UPM 로컬 참조 (저장소 내 `/Sandbox`)
- [[0006-리뷰-품질-게이트-확대]] — 리뷰 품질 게이트 확대: 트리거 확대·차원 스킬 3종(구조/보안/속도)·README 갱신 규칙
- [[0007-사용법-우선-api-확정]] — 사용법 우선 개발 + 공개 API 스타일 확정 (Mirror/Netcode류 속성; 변경 이력 — partial 재구조화로 ADR-0008 승계)
- [[0008-구현-아키텍처]] — P1 RPC + Replicate 기본 구현: 소스젠(로슬린 4.3) 직접 배선·다중 어셈블리·씬경로 netId·라운드로빈 소유권·검증 증거

### _templates/ — 문서 템플릿

- `_templates/feature.md` — 기능 문서 템플릿
- `_templates/adr.md` — ADR 템플릿

## 최근 변경 (자세한 것은 [[changelog]])

- 2026-09-14 — **P1 RPC + P2 Replicate 기본 구현 완료** (ADR-0008: 소스젠·런타임·2-프로세스 RUDP 왕복 검증 포함. 첫 기능 문서 등재)
- 2026-09-14 — 사용법 우선 API 확정 (ADR-0007: 공개 API 스타일 Mirror/Netcode류·Sandbox 사용법 예제·Package API 스텁 · 변경 이력 — RepNotify·MulticastRpc 확장 후 partial 재구조화)
- 2026-09-14 — 기반 패키지 최신화: DRPC 3.5.0 / MessageProtocol 3.2.0 / Communication(RUDP) 2.7.0 (ADR-0005 변경 이력)
- 2026-09-14 — 리뷰 품질 게이트 확대 (ADR-0006: 트리거 확대·차원 스킬 3종·README 갱신 규칙)
- 2026-09-14 — 개발 환경 구축 완료 (ADR-0005 변경 이력: Package/ 분리·6000.0.83f1·NuGetForUnity 4.5.0·기반 패키지 버전 확정)
- 2026-09-14 — 개발 환경 확정 (ADR-0005: Unity 샌드박스 + UPM 로컬 참조, 저장소 내 `/Sandbox`)
- 2026-09-13 — 구현 플랜 확정 (plan·ADR-0003 패키지 참조·ADR-0004 계약 자동 생성)
- 2026-09-13 — 프로젝트 정의·기반 스택·스코프 확정 (overview·architecture·roadmap·ADR-0002)
- 2026-09-13 — 하네스 엔지니어링 도입 (문서 Vault + AGENTS.md + 스킬 + 훅 + 리뷰어)
