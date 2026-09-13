---
name: doc-sync
description: UniNet Document/ 문서 갱신 절차. 기능을 추가·수정·제거했거나, 아키텍처·규약·결정 사항이 바뀌었거나, 세션 시작 시 관련 문서를 읽어야 할 때 사용.
---

# UniNet 문서 동기화 (doc-sync)

`Document/` 폴더는 사람과 AI가 함께 참조하는 Obsidian Vault이자 단일 진실 공급원(SSoT)이다.
코드가 바뀌었는데 문서가 안 바뀌면 문서는 거짓말을 한다. 이 스킬은 그 간극을 막는 절차다.

## When to Use

- 코드에서 **기능 추가 / 수정 / 제거**가 일어났을 때 (커밋 직전, 세션 마무리 직전 포함)
- 아키텍처, 코딩 규약, 기술 스택, 결정 사항이 바뀌었거나 새로 정해졌을 때
- 세션/작업 시작 시 무엇을 읽어야 할지 판단이 필요할 때 (아래 "읽기 우선순위" 참조)

## 읽기 우선순위 (작업 시작 시)

1. [[00-INDEX]] — 문서 지도. 작업 대상 영역 파악
2. [[overview]] — 프로젝트 정의·목표
3. 작업 영역에 해당하는 `features/`, [[architecture]], [[conventions]] 문서
4. 관련 결정이 의심되면 `decisions/` 의 ADR

## Procedure (갱신)

변경 성격에 따라 갱신 대상이 달라진다. 해당하는 것을 **모두** 갱신한다.

| 변경 종류 | 갱신 대상 |
| --- | --- |
| 기능 추가 | `_templates/feature.md`로 `features/<기능명>.md` 신규 작성 + `README.md` 반영(없으면 신규 생성) + [[changelog]] |
| 기능 수정 | 해당 `features/*.md` 본문 갱신 + 하단 변경 이력 추가 + `README.md` 반영 + [[changelog]] |
| 기능 제거 | 해당 `features/*.md` 삭제 또는 상단에 "제거됨" 명시 + [[changelog]] |
| 구조·모듈·의존성 변경 | [[architecture]] + [[changelog]] |
| 규약·관례 변경 | [[conventions]] + ADR + [[changelog]] |
| 중요한 결정(기술 선택, 트레이드오프) | `decisions/NNNN-*.md` ADR 신규 + [[changelog]] |
| 하네스 구성 변경 | [[harness]] + [[changelog]] |

작성 규칙:

- 언어는 **한국어** (코드·기술 용어는 영어 그대로)
- 파일명은 kebab-case, 내부 링크는 Obsidian 위키링크 `[[문서명]]`
- 기능 문서는 템플릿(`_templates/feature.md`) 구조를 따른다
- changelog는 날짜 그룹(`## [YYYY-MM-DD]`) 아래 `### Added/Changed/Removed/Fixed` 분류

## Pitfalls

- "나중에 쓰겠다" 금지 — 코드 변경과 같은 세션에서 문서를 갱신한다. doc-guard 훅이 감시한다.
- changelog에만 한 줄 쓰고 기능 문서를 안 쓰는 것은 불완전 갱신이다.
- 문서 갱신이 정말 불필요한 변경(예: 오타 수정, 주석)은 갱신 없이 사용자에게 이유를 보고한다.
- 문서끼리 모순이 생기면 오래된 쪽을 고치고, 어느 쪽이 맞는지 애매하면 사용자에게 질문한다.

## Verification

- 변경 후 `[[00-INDEX]]` 링크가 유효한가 (삭제된 문서가 링크에 남지 않았는가)
- doc-guard 상태 표시가 경고를 내고 있지 않은가
- 기능 문서가 실제 동작과 일치하는가 (리뷰어가 이를 검증한다)
