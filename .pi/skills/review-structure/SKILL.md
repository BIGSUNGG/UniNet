---
name: review-structure
description: UniNet 리뷰 차원 — 구조. 코드 변경 시 레이어링·의존성·SOLID·과잉 추상화를 검토하는 reviewer용 기준. reviewer 서브에이전트가 diff 검토 시 참조한다.
---

# 리뷰 차원: 구조 (review-structure)

reviewer 서브에이전트가 코드 변경의 **구조적 건전성**을 검토할 때 적용하는 기준. 판정 형식(ISSUES/CLEAN)은 reviewer 에이전트 계약을 따른다.

## When to Use

- 새 타입/어셈블리/네임스페이스 추가, 참조 방향 변경
- API 서피스(public 멤버) 추가·변경, 기존 클래스 책임 확장
- 리팩토링·파일 이동 후

## Checklist

1. **SOLID 위반** — `Document/conventions.md` 기준. SRP(한 클래스 두 책임), OCP(수정 폐쇄), LSP·ISP·DIP 순으로 점검.
2. **과잉 추상화 (YAGNI)** — 구현 1개짜리 인터페이스, 호출처 1곳짜리 간접층, 쓰이지 않는 팩토리/설정/스캐폴딩도 구조 위반으로 지적.
3. **레이어링·의존성 방향** — 프레임워크 코어 ↔ 사용자 코드(MonoBehaviour) 경계 침범, 하위 계층이 상위 계층 참조, 순환 참조.
4. **응집도·결합도** — 서로 다른 책임이 한 파일에 뭉쳤는지, 한 변경이 여러 모듈을 강제로 건드리는지.
5. **배치·네이밍 규약** — 파일 위치, 네임스페이스, 타입/멤버 명명이 `Document/conventions.md`와 일치하는지.
6. **문서 반영** — 구조가 바뀌었는데 `Document/architecture.md` 등에 미반영이면 구조 이슈로 함께 지적.

## Pitfalls

- 어셈블리 구조·참조 방식은 아직 미확정(`Document/overview.md`) — 확정되지 않은 구조를 요구하지 말고, 현재 문서화된 규약만 근거로 삼는다.
- "취향" 지적 금지 — 문서화된 규약이나 SOLID로 정당화 안 되면 지적하지 않는다.
- 정확성·보안·속도 이슈는 해당 차원 스킬의 영역 — 중복 지적하지 말고 그 차원으로 넘긴다.

## Verification

- 모든 구조 이슈가 파일:줄 위치 + `Document/conventions.md` 또는 SOLID 조항 근거를 갖는가?
- 과잉 추상화 지적에 "지우면 무엇이 단순해지는가"가 포함되어 있는가?
