# harness — AI 협업 하네스 구성

이 저장소의 AI(pi) 하네스 구성과 그 목적. 하네스를 변경하면 이 문서와 [[changelog]]를 갱신한다.

## 핵심 원칙 (AGENTS.md에도 명시됨)

1. **문서 우선** — 기능 추가/수정/제거 시 `Document/` Vault에 상세히 문서화. 작업 시 필요한 문서를 먼저 읽음
2. **능동 질문** — 모호하면 반드시 질문. 추측으로 진행 금지
3. **OOP/SOLID 준수** — 모든 코드 구현 (상세: [[conventions]])
4. **요청 비판적 검토** — 사용자 요청의 문제점을 리스크와 함께 제시하고 진행 여부를 확인

## 구성 요소

| 요소 | 위치 | 역할 |
| --- | --- | --- |
| 컨텍스트 규칙 | `AGENTS.md` (루트) | 4원칙 + 워크플로. pi가 모든 세션에 자동 로드 |
| 문서 갱신 절차 | `.pi/skills/doc-sync/` | 무엇을 언제 어떻게 갱신할지, 작업 시작 시 무엇을 읽을지 |
| 리뷰 루프 절차 | `.pi/skills/review-until-clean/` | 코드 변경 후 reviewer Clean 판정까지 수정-재리뷰 반복 |
| 문서 감시 훅 | `.pi/extensions/doc-guard.ts` | agent_settled 시 git 상태 검사. 코드 변경 있는데 `Document/` 미갱신이면 경고 표시 + follow-up 재촉(최대 2회). 대화형(tui) 세션에서만 작동 |
| 리뷰어 에이전트 | `.pi/agents/reviewer.md` | 읽기 전용 검토. 첫 줄 `CLEAN` 또는 `ISSUES` 판정. 빌트인 reviewer를 프로젝트 스코프로 재정의 |

## 동작 방식

### 문서 동기화

1. 세션/작업 시작: `[[00-INDEX]]` → `[[overview]]` → 작업 영역 문서 순서로 읽음 (doc-sync 스킬)
2. 변경 발생: doc-sync 스킬의 표에 따라 기능 문서/changelog/architecture/ADR 갱신
3. 누락 방어: doc-guard 훅이 agent 종료 시점에 git 상태를 검사 — 코드 변경 + 문서 미변경이면 상태 표시줄 경고 + 자동 재촉

### 리뷰 루프 (review-until-clean)

1. 의미 있는 코드 변경 완료 → reviewer 서브에이전트에 diff·맥락 전달해 검토 요청
2. 판정이 `ISSUES`면 이슈 수정 → 재검토 (무제한 반복, 단 정체 시 사용자 보고)
3. `CLEAN` 판정 후 최종 보고

## 변경 방법

- 규칙 수정: `AGENTS.md` (그리고 필요시 이 문서)
- 절차 수정: 해당 스킬의 `SKILL.md`
- 훅 수정: `.pi/extensions/doc-guard.ts` — 수정 후 pi 세션에서 `/reload`
- 리뷰 기준 수정: `.pi/agents/reviewer.md`
- 모든 하네스 변경은 ADR + [[changelog]] 기록 (doc-sync)

## 제약 / 비고

- `.pi/` 하위 요소는 프로젝트 신뢰 후 로드됨 (현재 `C:\Projects` 경로 신뢰됨)
- doc-guard는 대화형 세션에서만 재촉 — 서브에이전트·헤드리스 세션은 재촉하지 않음 (부모 세션이 책임)
- Vault는 `Document/` 폴더 전체. Obsidian에서 "폴더를 Vault로 열기"로 사용
