# ADR-0051 — 대상 지정 ClientRpc 동반 메서드 네이밍 `{Name}To` / `{Name}ToOwner`

- 상태: 채택 (2026-10-21 — 사용자 승인)
- 관련: ADR-0016(ServerRpc 소유권 강제), ADR-0028(프로토콜 버전), RPC-001(감사 항목)
- 버전: 0.15.0 (생성기 0.2.14)

## 배경

`[ClientRpc]`는 브로드캐스트만 가능했다(생성 sender가 `SnapshotConnections()` 전체 순회). 소유자 전용 알림(픽업·차량·킬피드)과 특정 연결 응답은 서버 권위로 표현할 수 없었다.

## 결정

1. **생성기가 모든 `[ClientRpc]`마다 동반 2메서드를 emit한다**: `{Name}To(long connId, args)`(특정 연결 1회 송신 — FishNet TargetRpc 패리티)와 `{Name}ToOwner(args)`(오브젝트 소유자 1회 송신 — UE owner ClientRpc 패리티). 네이밍은 사용자 승인(접미사 방식).
2. **기존 `{Name}(args)`는 완전 불변** — 브로드캐스트 시맨틱 유지(파괴적 변경 0). 접근자는 `rpc.Accessibility` 미러, methodId·payload·delivery 불변(**와이어 무변경** — 수신자 필터링은 서버 송신 루프에서만 일어난다).
3. **동반 메서드는 `partial`이 아닌 정규 메서드로 emit한다.** 기존 `{Name}(args)` emission이 partial 구현부인 이유는 사용자의 `[ClientRpc] partial void Name(...)` 선언이 정의부를 제공하기 때문이다 — 동반 메서드에는 사용자 선언이 없으므로 partial 구현부로 emit하면 CS0759. 정규 메서드로 emit하고 이름 충돌(사용자 코드에 이미 `{Name}To`가 있는 경우 CS0111)은 수용된 리스크로 남긴다(명확한 컴파일 오류로 조기 발견 — 묵시적 덮어쓰기 없음).
4. **대상 필터링은 서버 송신 루프에서만.** `To`는 연결 목록에서 connId 일치+`!Disconnected`+채널 캐스트 통과 시 정확히 1회 송신, 미발견은 무예외 무송신. `ToOwner`는 `server.GetOwner(NetId)`로 확정, owner==0이면 경고 1줄+미송신(저빈도 프로그래밍 오류 신호 — 유량 제한 불필요).
5. **승인 게이트는 현행 브로드캐스트와 동일**(Disconnected만 검사) — 복제 경로의 `VersionApproved` 게이트와의 불일치는 RPC-001 범위 밖 관찰 사항으로 문서화만.
6. **MulticastRpc는 제외** — UE NetMulticast 패리티(전체+로컬 실행)가 타입 정의 자체다. 대상 지정이 필요하면 ClientRpc 변형을 사용한다.

## 기각한 대안

- **`[ClientRpc(Target=...)]` 속성**: 특정 connId는 선언 시점 정적 속성으로 표현 불가 — 결국 런타임 메커니즘이 별도 필요(2중 surface).
- **FishNet식 예약 파라미터(`[Target] long conn`)**: 파라미터별 와이어 제외 특수 규칙이 인코더·파서·`[Message]` 파이프라인을 복잡화. "사용은 간단하게" 원칙과 불일치.
- **오버로드 단일화(`{Name}(long connId, args)`)**: 첫 매개변수가 connId인지 args인지 호출부 구분이 모호하고, args 첫 타입이 long이면 충돌. 소유자 대상 표현도 부자연스러움.

## 예약 이름 (r21 N1)

생성 코드가 `{Name}To`의 파라미터로 `connId`를, `ToOwner`의 로컬로 `owner`(및 `server`·`ch`)를 사용한다. 사용자 RPC 파라미터가 이 이름과 겹치면 CS0100/CS0128로 **컴파일 타임에 즉시 실패**한다(기존 브로드캐스트도 `server` 등 로컬 예약이 있는 동일 계약). 사용자 RPC는 파라미터명으로 `connId`·`owner`·`server`·`ch`를 피해야 한다 — 이 네이밍 규약은 ADR 문서로 관리하고 코드로 회피하지 않는다(명시성 원칙).

## 결과

- 사용자 API: `rpc.ShowDamageToOwner(25f)` / `rpc.ShowDamageTo(connId, 25f)` / 기존 `rpc.ShowDamage(25f)`(불변).
- 생성기 0.2.14(동반 emission 추가) — 분석기 DLL(`Package/Runtime/UniNet.Unity/Analyzers/`)과 로컬 NuGet 피드에 재배포 필요(기존 절차).
- 와이어·프로토콜 버전·Core 런타임 무변경.

## 검증

EditMode T1~T6(`TargetedRpcTests` — 브로드캐스트 불변·ToOwner 단일 수신+타 채널 0회·To 지정 연결+methodId·payload 동일성·owner==0 경고·끊긴 connId 무예외·클라 가드) + PlayMode T7(`TargetedRpcPlayTests` — 실루프백 2연결 비소유 미수신). GREEN EditMode 254/254(job `fe312da4`)·PlayMode 42/42 ×2(job `fa6dd906`·`06f32ce5`).
