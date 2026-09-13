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

- (아직 없음 — 기능 추가 시 `_templates/feature.md`로 작성)

### decisions/ — 아키텍처 결정 기록 (ADR)

- [[0001-하네스-엔지니어링-도입]] — AI 협업 하네스와 문서 규칙 도입
- [[0002-기반-스택과-스코프-확정]] — 상용 유니티 게임 서버 라이브러리로 정의, DRPC/MessageProtocol 재사용, Unity 6, Unity 서버 빌드
- [[0003-패키지-참조-고정]] — 기반 스택을 NuGet+UPM 패키지 참조로 고정
- [[0004-계약-자동-생성]] — MonoBehaviour RPC 계약 자동 생성 (UniNet 소스젠, 수동 폴백)
- [[0005-개발-환경-샌드박스-upm]] — 개발 환경 확정: Unity 샌드박스 + UPM 로컬 참조 (저장소 내 `/Sandbox`)

### _templates/ — 문서 템플릿

- `_templates/feature.md` — 기능 문서 템플릿
- `_templates/adr.md` — ADR 템플릿

## 최근 변경 (자세한 것은 [[changelog]])

- 2026-09-14 — 개발 환경 확정 (ADR-0005: Unity 샌드박스 + UPM 로컬 참조, 저장소 내 `/Sandbox`)
- 2026-09-13 — 구현 플랜 확정 (plan·ADR-0003 패키지 참조·ADR-0004 계약 자동 생성)
- 2026-09-13 — 프로젝트 정의·기반 스택·스코프 확정 (overview·architecture·roadmap·ADR-0002)
- 2026-09-13 — 하네스 엔지니어링 도입 (문서 Vault + AGENTS.md + 스킬 + 훅 + 리뷰어)
