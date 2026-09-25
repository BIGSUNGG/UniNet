# 커스텀 NetSerialize (사용자 정의 필드 직렬화)

- **상태**: 구현됨 (2026-09-24 — ADR [[0020-직렬화-배열-델타-유니넷-단독-구현]] 기능 1/2, 제너레이터 0.1.9)
- **최초 작성**: 2026-09-24
- **마지막 갱신**: 2026-09-24

## 개요

`[Replicated]` 필드를 게임이 직접 직렬화하는 훅. 위치 양자화(QuantizedVector)처럼 대역폭 최적화된 표현으로 전송한다. 부수 효과로 **지원 타입 확장** — 직렬화기를 지정하면 기본형·[Message]가 아닌 타입(예: `Vector3` 등 Unity 구조체)도 `[Replicated]` 필드로 쓸 수 있다.

UE Network Framework의 NetSerialize / QuantizedVector 상응.

## 요구사항 / 목표

- 사용법 우선 (ADR [[0007-사용법-우선-api-확정]]): 속성 한 줄 + 정적 메서드 쌍으로 계약 체결, 별도 등록 호출 없음
- **MP(MessageProtocol) 무수정** — 기존 `MessageBufferWriter`/`MessageBufferReader` public 프리미티브만 사용
- 델타 비교·조건 전송(OwnerOnly/SkipOwner/InitialOnly)·RepNotify·스폰/캐치업 전체 경로는 기존 파이프라인 재사용
- 직렬화기 서명 오류는 컴파일 타임 진단으로 차단 (기존 UNINET 진단 체계 승계)

## 설계

### API 계약

```csharp
// 게임 코드 — 정적 직렬화기 클래스 (등록 불필요)
public static class PositionQuantized
{
    public static void Write(ref MessageBufferWriter w, in Vector3 value)
    {
        w.WriteInt16(Quantize(value.x));   // MP public 프리미티브를 자유 조합
        w.WriteInt16(Quantize(value.y));
        w.WriteInt16(Quantize(value.z));
    }
    public static Vector3 Read(ref MessageBufferReader r)
        => new Vector3(Dequantize(r.ReadInt16()), Dequantize(r.ReadInt16()), Dequantize(r.ReadInt16()));
}

// NetworkBehaviour 필드
[Replicated(Serializer = typeof(PositionQuantized), Notify = nameof(OnPosition))]
private Vector3 _position;
```

- **속성 확장**: `ReplicatedAttribute.Serializer` (Type, 선택). 지정 시 해당 필드 직렬화를 이 타입의 정적 메서드로 위임
- **계약 서명**: `static void Write(ref MessageBufferWriter, in T)` + `static T Read(ref MessageBufferReader)` 쌍. C# 9 / netstandard2.1 제약상 static abstract 인터페이스 멤버를 못 쓰므로 attribute 타입 참조 방식
- **선택 계약**: `static bool Equals(in T, in T)` — 제공 시 델타 비교에 사용 (양자화 후 동일값의 불필요한 재전송 방지). **null 인자는 전달되지 않는다** — 프레임워크가 양방향 null을 먼저 처리한다(null↔null 생략, null↔값 강제 전송). 미제공 시 기존 `object.Equals`

### 제너레이터 분기

`Emitter.cs`의 필드 직렬화 지점 3곳에 분기 추가 (필드 모델에 `CustomSerializer` 속성 신설):

| 생성 지점 | 현재 | Serializer 지정 시 |
| --- | --- | --- |
| `__UniNetWriteDelta` | `w.WriteSingle(o.F)` / `MessageSerializer.SerializeToWriter` | `PositionQuantized.Write(ref w, o.F)` |
| `__UniNetWriteFull` | (동일) | (동일) |
| `__UniNetApplyIn` | `reader.ReadSingle()` / `DeserializeFromReader` | `o.F = PositionQuantized.Read(ref reader)` |

- 스냅샷(`object[] seen`)·마스크 비트·조건 필터링·RepNotify prev 전달은 전부 기존 로직 무변경
- 타입 검증 완화: `Serializer` 지정 필드는 기본형·[Message] 검증(UNINET002 계열)을 통과한 것으로 취급

### 와이어 포맷

변경 없음 — 기존 `[mask:u32][변경 필드값...]` 페이로드에서 해당 필드의 바이트 표현만 직렬화기 산출물로 교체. 페이로드는 UniNet 소유 `byte[]`이고 MP/DRPC는 `WriteBytes`로 순수 전송(`SendReplicate` → ReliableOrdered).

### 진단 (신규)

- **UNINET013** (Error) — 직렬화기 서명 불일치: `Serializer`로 지정된 타입에 `Write`/`Read` 정적 쌍이 없거나 시그니처가 계약과 다름

## 동작 상세

- 서버: 틱마다 스냅샷과 필드 값 비교(기존 `object.Equals` 또는 선택 `Equals`) → dirty면 직렬화기 `Write`로 페이로드 생성
- 클라: 마스크 비트 확인 → 직렬화기 `Read`로 복원 → `o.F` 대입(호스트는 서버 원본 보존 기존 규칙 동일) → RepNotify(prev)
- 스폰/캐치업(`WriteFull`)도 동일 분기를 지나 초기 상태가 양자화 표현으로 전송됨
- 직렬화기 예외는 생성 코드 try/catch 격리 규칙(기존 디스패치 격리)과 별개로 **서버 페이로드 생성 경로에서 발생**하므로, 여기서의 예외는 틱 로그로 격리하는 규칙을 명시한다

## 테스트 / 검증 (실측 — 2026-09-24)

- EditMode **68/68** (신규 8종: 왕복 정밀도·양자화 델타 적용·버킷 생략·Equals 미제공 기본 경로·WriteFull·RepNotify 원본 prev·듀얼 Equals 필드 독립 억제·operator== 없는 구조체(CS0019 회귀))
- PlayMode **16/16** (신규 1종: 루프백 RUDP 왕복 — 양자화 델타·RepNotify prev·버킷 억제)
- GenDump 픽스처 갱신 — Serializer 분기 배선·UNINET013 음성 케이스(Read 누락) 발화 확인·오버로드 허브(미끼 오버로드 + 상속 Read) 오탐 없음 확인
- dotnet 빌드 녹색 · Unity 컴파일 에러 0

- **사용 예시**: `Sandbox/Assets/Scripts/Usage/SerializationUsage.cs` (`QuantizedWeapon` — 양자화 조준점·선택 Equals·float→byte) · 라이브 데모: 에디터 메뉴 `UniNet ▸ Usage ▸ 직렬화 사용 예시 (호스트 루프백)`

## 구현 규모 추정

소·중 — 필드 모델 3속성(SerializerType·HasCustomEquals·CanBeNull) + attribute 1속성 + Emitter 분기 3곳 + 검증기 1종(오버로드·베이스 체인 스캔) + 진단 1종 + 서버 격리(`UniNetEnvironment.LogFault` 훅 + 틱/WriteFull try-skip) + 테스트 9종.

## 미확정 사항

1. 선택 계약 `Equals(in T, in T)` 포함 — **확정: 포함** (목표 계약, 양자화 재전송 억제)
2. RPC 매개변수에 대한 동일 직렬화기 적용 — v1 스코프 외 (동일 분기 패턴으로 확장 가능)
3. `Write(ref w, in T)`의 `in` 한정자 — **확정: `in`·무수정 둘 다 허용** (검증 `RefKind.In || None`)

## 변경 이력

- 2026-09-24 — 구현 완료 (제너레이터 0.1.9 — 리뷰 4라운드 이슈 수정 반영: `__s{index}` 고유명·오버로드 관대 검증·틱 예외 격리·양방향 null 가드(CanBeNull 분기 — 값 타입 CS0019 회귀 픽스처 포함)·문서 동기화)
- 2026-09-24 — 최초 작성 (설계 문서 — ADR-0020 제안과 함께)
