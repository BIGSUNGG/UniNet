# changelog — 변경 기록

의미 있는 모든 변경(기능 추가/수정/제거, 규약, 구조, 하네스)을 기록한다.
형식: 날짜 그룹 아래 `### Added / Changed / Removed / Fixed`. 최신 날짜가 위로 오게 관리한다.

## [2026-09-13]

### Added

- **프로젝트 정의·스코프 확정** — 상용 유니티 게임 서버용 네트워크 프레임워크 라이브러리로 정의. 원칙 “사용은 간단하게, 기능은 강력하게”
  - 기능 목표: MonoBehaviour 기준 RPC / 변수 자동 Replicate / UE Network Framework 패리티
  - 기반 스택: MessageProtocol + DRPC(Communication 간접) 적극 재사용, 최소 Unity 6, Unity 서버 빌드(서버 권위)
  - 신규 문서: [[overview]] 전면 갱신, [[architecture]] 초기 설계안, [[roadmap]] UE 패리티 매트릭스(P1~P4)
  - 결정 기록: [[decisions/0002-기반-스택과-스코프-확정|0002-기반-스택과-스코프-확정]]

### Added (하네스)

- **하네스 엔지니어링 도입** — AI 협업 규칙(문서 우선, 능동 질문, OOP/SOLID, 요청 비판적 검토)을 pi 하네스에 구성
  - `Document/` Obsidian Vault (사람+AI 공동 참조, 변경 시 갱신 의무)
  - `AGENTS.md` — 핵심 4원칙과 워크플로 (모든 세션에 자동 로드)
  - `.pi/skills/doc-sync` — 문서 갱신 절차 스킬
  - `.pi/skills/review-until-clean` — 리뷰어 Clean-판정 루프 스킬
  - `.pi/extensions/doc-guard.ts` — 코드 변경 시 문서 미갱신 감지·재촉 훅
  - `.pi/agents/reviewer.md` — ISSUES/CLEAN 판정 리뷰어 (프로젝트 스코프)
  - 자세한 내용: [[harness]], [[decisions/0001-하네스-엔지니어링-도입|0001-하네스-엔지니어링-도입]]

### Added (구현 플랜)

- **구현 진행 플랜 확정** — 얇은 수직 슬라이스 원칙, Phase 0(스파이크)→P1(RPC)→P2(Replicate) 순서, 완료 정의, 설계 난점 3건. [[plan]]
  - ADR-0003: 기반 스택 **패키지 참조 고정** (NuGet + UPM)
  - ADR-0004: MonoBehaviour RPC 계약 **자동 생성** (UniNet 자체 소스젠, 수동 폴백 유지)
