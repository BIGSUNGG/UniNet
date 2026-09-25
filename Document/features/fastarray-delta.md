# FastArray 직렬화 (배열/컬렉션 요소 델타 동기화)

- **상태**: 구현됨 (2026-09-24 — ADR [[0020-직렬화-배열-델타-유니넷-단독-구현]] 기능 2/2, 제너레이터 0.2.1)
- **최초 작성**: 2026-09-24
- **마지막 갱신**: 2026-09-24

## 개요

`[Replicated]` 배열(`T[]`)·리스트(`List<T>`) 필드의 **요소 단위 델타 동기화** — 전체 재전송이 아니라 추가·삭제·변경된 요소만 옵(op)으로 전송한다. 필드 선언만으로 자동 인식하며 별도 API 없음.

UE FastArraySerializer / Mirror·FishNet SyncList 상응.

## 요구사항 / 목표

- `T[]`·`List<T>` 필드에 대해 요소 단위 옵 인코딩 (Set/Insert/RemoveAt/Clear)
- **MP 무수정** — 기존 페이로드 `byte[]` 파이프라인 안에서 UniNet 와이어로 표현
- 요소 직렬화 규칙은 필드 규칙과 통일: 기본형·string·[Message]·커스텀 직렬화기([[custom-netserialize]] `Serializer` 옵션) 지원
- 조건 전송(OwnerOnly/SkipOwner/InitialOnly)·휴면·주기 등 P3 정책은 기존 필드 단위 메커니즘에 그대로 탑재
- 온전한 도달 보장 — 기존 리플리케이션 전달 모드(`ReliableOrdered`)를 전제로 시퀀싱/리싱크 메커니즘 없이 설계

## 설계

### API

```csharp
[Replicated(Notify = nameof(OnInventory))]   // 일반 필드와 동일한 선언
private List<int> _inventory;                 // 요소 타입은 지원 규칙(위) 충족

[Replicated(Serializer = typeof(ItemQuantized))]   // 요소 단위 커스텀 직렬화 결합
private Item[] _slots;
```

제너레이터가 `T[]`/`List<T>` 필드를 인식해 배열 경로로 자동 배선. 추가 속성·등록 없음.

### 와이어 포맷 (UniNet 소유)

```
델타:   [mask:u32] ...해당 비트 시 → [opCount:u16] { [op:u8] [idx:u16] [elem...] }*
전체(스폰/캐치업/InitialOnly 기준선): [mask:u32] ...해당 비트 시 → [count:u32] [elem]*
```

- op: `0=Set(i)`, `1=Insert(i)`, `2=RemoveAt(i)`, `3=Clear`
- 옵 순서 = 서버 diff 산출 순서 그대로. `ReliableOrdered`라 유실·재정렬이 없어 **클라는 단순 리플레이만** 한다 (시퀀스 번호·ACK·리싱크 불필요 — 현 `SendReplicate`가 ReliableOrdered 고정인 점 확인 완료)
- 필드당 소비하는 마스크 비트는 1개 — 32필드 상한(UNINET008) 체계 불변

### 서버 diff 알고리즘

- 서버는 컬렉션 필드별 **섀도 스냅샷**(마지막 전송 요소 리스트)을 유지. 기존 `object[] seen` 슬롯에 컬렉션 복사본 저장
- dirty 판정: 요소 수 또는 요소 `Equals` 불일치 시 필드 dirty → 마스크 비트
- diff v1 — 공통 prefix/suffix 제외 후 중간 구간: 길이 같으면 Set 배치, 다르면 Remove/Insert 조합. `// ponytail: O(n) 휴리스틱 — append/말단 수정 패턴에 최적. 중간 대량 삽입이 잦아지면 (Y) LCS/안정 ID 키잉(v2) 업그레이드`
- [Message] 요소의 변경 감지는 **참조 비교** — 요소 내부 값 변경은 감지되지 않으므로 교체(replace)로 변경한다. 이 제약은 [Message] 불변성 관례와 일치하며 문서에 명시
- GC: v1은 diff·복사 할당 허용. `// ponytail: 틱마다 할당 — 측정 후 버퍼 풀링`

### 클라 적용

- 온전한 순서 보장 하 순차 리플레이 → 컬렉션 변경 → RepNotify 1회 (델타 배치당)
- 호스트는 서버 원본 보존 규칙(기존) 동일 — 클라 모드에서만 적용

## 동작 상세 — 경계 조건

- **ReplayState(후발 접속 캐치업)**: `WriteFull` 경로 = 전체 count 인코딩. 기존 캐치업 메커니즘 무변경
- **InitialOnly 배열 필드**: 기준선(스폰 시 1회)만 전체 전송, 이후 틱 제외 — 기존 InitialOnly 규칙 동일
- **Owner/Others 분기**: 서버 섀도 스냅샷은 단일(서버 원본 기준), 오디언스별 페이로드는 기존 마스크 분기 방식 그대로
- **요소 상한**: `opCount:u16`·`idx:u16` — 요소 65,535 상한. 초과 시 진단으로 차단 (신규 UNINET014 계열)
- **중첩 컬렉션 미지원** (`List<List<T>>`) — 진단 차단, 요소는 스칼라·[Message]·직렬화기 타입만

### 진단 (신규)

- **UNINET014** (Error) — 지원하지 않는 컬렉션 요소 타입 (기본형·string·[Message]·Serializer 지정 외)
- **UNINET015** (Error) — 중첩 컬렉션 또는 요소 수 상한 초과

## 테스트 / 검증 (실측 — 2026-09-24)

- EditMode **76/76** (신규 8종: append 단일 Insert·중간 변경 리플레이·Clear 단일 옵 와이어 검사·무변경 억제·전체 상태 복원(null 요소·커스텀 직렬화 배열 포함)·RepNotify 얕은 복사 prev·null 요소 왕복·T[] 중간 변경)
- PlayMode **17/17** (신규 1종: 루프백 RUDP 요소 델타·RepNotify prev·무변경 억제)
- **2-프로세스 실기 PASS** — 프로세스 간 RUDP에서 삭제·삽입·append 온패 리플레이 + 양자화 위치 동시 검증 (`Sandbox-2proc-client.log` — `[UNINET-2PROC] SERIALIZATION PASS`)
- GenDump 진단 0 + 음성 케이스: UNINET014(지원 밖 요소 타입)·UNINET015(중첩 컬렉션) 발화 확인
- 생성 코드 독립 구문 검증(전용 컴파일) — `global::string[]`류 CS1001 회귀 방지

- **사용 예시**: `Sandbox/Assets/Scripts/Usage/SerializationUsage.cs` (`InventoryRig` — Loot/Drop/Swap/DropAll 뮤테이터 ↔ Insert/RemoveAt/Set/Clear 오프) · 라이브 데모: 에디터 메뉴 `UniNet ▸ Usage ▸ 직렬화 사용 예시 (호스트 루프백)`

## 구현 규모 (실측)

중 — Parser 컬렉션 감지(배열·List 요소 추출·검증) + Emitter 컬렉션 배선 4곥(스냅샷 섀도·델터 비교·쓰기·적용) + 필드별 생성 헬퍼 5종(dirty/write/writeFull/apply/copy) + 진단 2종 + 테스트 9종. 런타임 신규 타입 0 — 전부 생성 코드 내 배선.

## 미확정 사항

1. **RepNotify prev 전달** — **확정: 얕은 복사** (목표 계약)
2. **인덱스-옵 vs UE식 안정 ID 키잉** — **확정: 인덱스-옵** (목표 계약 — 안정 ID는 v2)
3. **컬렉션 변경 알림 이벤트**(Mirror의 `SyncList.OnChange`류 콜백) — v1은 RepNotify로 충당. 요소 단위 콜백 필요성이 실증되면 추가

## 변경 이력

- 2026-09-24 — 구현 완료 (제너레이터 0.2.1 — 필드별 생성 헬퍼 5종·오프라인 구문 검증 포함)
