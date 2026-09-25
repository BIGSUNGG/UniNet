# upstream-blockers — 상류 저장소 수정이 필요한 잔여 과제

UniNet은 기반 스택(DRPC·MessageProtocol·Communication)을 **패키지 참조 고정**으로 재사용하며 상류 저장소를 수정하지 않는 것이 원칙이다 ([[0003-패키지-참조-고정]]). 따라서 상류 수정이 필요한 과제는 이 문서에 기록만 하고, 실제 해결은 해당 저장소 세션에서 진행한다.

> **2026-09-24 — 하기 MP 2건의 단독 구현 전환 제안**: 코드 검증 결과 제너레이터 필드 배선 구조상 MP 수정 없이 UniNet 단독 구현 가능으로 확인됨 (ADR [[0020-직렬화-배열-델타-유니넷-단독-구현]] 제안 — 승인 시 이 문서의 2건은 해결 경로 확정으로 폐기). 상세 설계: [[features/custom-netserialize]]·[[features/fastarray-delta]]

- **최초 작성**: 2026-09-21
- **마지막 갱신**: 2026-09-24 (직렬화 2건의 UniNet 단독 구현 전환 노트 추가)

## MessageProtocol 수정 필요 (2건 — 2026-09-24 폐기: UniNet 단독 구현으로 전환 완료)

> **폐기 완료**: 하기 2건은 ADR [[0020-직렬화-배열-델타-유니넷-단독-구현]](승인됨)에 따라 MP 수정 없이 UniNet 단독으로 구현 완료됐다 — 커스텀 NetSerialize([[features/custom-netserialize|custom-netserialize]])·FastArray([[features/fastarray-delta|fastarray-delta]]) 모두 구현·검증 완료(2-프로세스 실기 PASS). 아래 서술은 역사 기록으로 보존한다.

### 1. 커스텀 NetSerialize (UE NetSerialize / QuantizedVector 상응)

- **목표**: `[Replicated]` 필드를 게임이 직접 직렬화하는 훅 — 위치 양자화(QuantizedVector)처럼 대역폭 최적화된 표현으로 전송
- **UniNet 단독 불가 사유**: 리플리케이션 페이로드는 생성 코드가 MessageProtocol 코덱으로만 만든다. 사용자 정의 직렬화(코덱 등록·커스텀 writer/reader)가 MP 코덱 계층에 없으면 생성 코드가 호출할 대상이 없다
- **MP에 필요한 변경 요지**: 사용자 정의 타입 코덱 등록 경로 (커스텀 serializer 인터페이스 + 레지스트리) — 생성 코드가 MP 공용 코덱 대신 등록된 커스텀 직렬화를 호출할 수 있게
- **UniNet 측 준비 상태**: 페이로드 `byte[]` 파이프라인·델타 비교(`CompareAndWriteDelta`)·조건 전송은 완비 — 코덱 교체만 끼워 넣으면 동작하는 구조 ([[0011-P3-리플리케이션-고급-정책]] 페이로드 경로)

### 2. FastArray 직렬화 (UE FastArraySerializer 상응)

- **목표**: 배열/컬렉션 `[Replicated]` 필드의 **델타 동기화** — 전체 재전송이 아니라 추가·삭제·변경 요소만 전송
- **UniNet 단독 불가 사유**: 현재 델타 비교는 필드 단위 값 비교(dirty 비교)라 요소 단위 변경 추적이 불가능. 요소 키잉·체인저(Changer) 직렬화는 MP 계층의 배열 코덱 확장이 전제
- **MP에 필요한 변경 요지**: 요소 식별자 기반 배열 델타 코덱 (요소 추가/삭제/변경을 와이어로 표현) + UniNet 측 컴펜세이션 훅과 맞물릴 콜백 형태
- **UniNet 측 준비 상태**: 상태 없음 — MP 코덱 확정 후 생성기 확장(`[Replicated]` 배열 지원)과 함께 설계 필요

## DRPC / Communication

- 현재 잔여 의존 과제 없음 — 연결 수명주기·신뢰성(RUDP 5 전달 모드)·암호화(DTLS 1.2)는 상류 제공 기능을 그대로 사용 중이고, roadmap P1~P4의 상류 매핑 항목은 전부 충족 상태다 ([[roadmap]] 참조)
- 참고(수정 아님): UniNet 시스템 메시지 추가 시 상류 재배포 없이 UniNet 제너레이터(Emitter)만으로 처리한다 — 와이어 methodId는 UniNet 도메인에서 관리 (TimeSync 5번이 선례)

## 관리 규칙

- 상류 수정으로 해결 가능해지면 해당 저장소에서 구현하고, 이 문서 항목에 해결 경로와 커밋을 남긴 뒤 roadmap 상태를 갱신한다
- 새 상류 의존 과제 발견 시 roadmap에 잔여 표기와 함께 이 문서에 추가한다
