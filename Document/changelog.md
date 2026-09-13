# changelog — 변경 기록

의미 있는 모든 변경(기능 추가/수정/제거, 규약, 구조, 하네스)을 기록한다.
형식: 날짜 그룹 아래 `### Added / Changed / Removed / Fixed`. 최신 날짜가 위로 오게 관리한다.

## [2026-09-14]

### Changed

- **리뷰어 품질 게이트 범위 확대** — `AGENTS.md` 품질 게이트 문구를 "의미 있는 코드 변경 후 검토"에서 "**코드나 개발 환경이 수정되었을 때(기능 추가/수정/제거, 리팩토링, 버그 수정, 빌드·설정 변경 등) 구조·보안·속도 측면을 검토**"로 변경
- **README 갱신 규칙 추가** — `AGENTS.md` 문서 우선 원칙에 "기능 추가·수정 시 `README.md`에도 반영(없으면 최초 변경 시 생성)" 규칙 추가

### Added (리뷰 차원 스킬)

- **차원별 리뷰 스킬 3개 신설** — reviewer가 구조·보안·속도를 차원별로 검토할 때 참조하는 기준 문서
  - `.pi/skills/review-structure/` — 레이어링·의존성·SOLID·과잉 추상화(YAGNI)
  - `.pi/skills/review-security/` — 서버 권위·신뢰 경계·RPC/직렬화 안전성·리소스 소진(DoS)
  - `.pi/skills/review-performance/` — hot path GC 압력·할당·잠금·알고리즘 복잡도 (측정 근거 없는 최적화 요구 금지)
- **reviewer 에이전트 검토 기준 확대** — `.pi/agents/reviewer.md` 기준을 정확성 + 구조·보안·속도(스킬 참조, 필수 적용) + 규약 + 문서 동기화(README 포함) + 요청 적합성으로 재구성
- **review-until-clean 스킬 동기화** — 트리거를 "코드·개발 환경 수정"으로 확대, 검토 기준 목록을 차원 3개 + README 반영으로 갱신
- **doc-sync 스킬 동기화** — 갱신 표의 기능 추가·수정 행에 `README.md` 반영(추가 시 신규 생성 포함)을 갱신 대상으로 명시
- **하네스 문서 동기화** — [[harness]] 구성 요소 표에 차원 스킬 3개 행 추가, 리뷰 루프 트리거 문구를 신규 규칙과 일치시킴; 결정 기록: [[decisions/0006-리뷰-품질-게이트-확대|0006-리뷰-품질-게이트-확대]]

### Added

- **개발 환경 구조 확정** — Unity 샌드박스 + UPM 로컬 참조
  - UniNet 저장소 자체를 UPM 패키지로 구성(`package.json` + asmdef), 저장소 내 `/Sandbox` Unity 6 프로젝트가 `file:` 경로로 참조
  - sln은 Unity가 자동 생성, 코어 테스트는 Unity Test Framework EditMode로 시작 — dotnet 이중 파이프라인은 필요 시 재검토
  - 결정 기록: [[decisions/0005-개발-환경-샌드박스-upm|0005-개발-환경-샌드박스-upm]]

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
