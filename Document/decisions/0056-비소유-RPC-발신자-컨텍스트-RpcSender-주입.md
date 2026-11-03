# 0056. 비소유 ServerRpc 발신자 컨텍스트 — RpcSender 주입 파라미터

- **상태**: 승인됨
- **날짜**: 2026-11-02
- **결정자**: 사용자(방향 선택) · 부모 에이전트 설계·구현

## 배경 (Context)

상용화 검토 블로커 3번(2026-11-02 세션): `RequireOwnership = false` ServerRpc(채팅·월드 상호작용·상점 — 상용 게임 필수 패턴)에서 서버이 누가 호출했는지 알 방법이 없었다. 생성 dispatch(`Emitter`의 `__UniNetServerDispatch_*`)는 전송 계층이 부여한 실제 `senderConnId`를 갖고 있으나 소유권 검사(ADR-0016)에만 쓰고 폐기했고, `_Validate`/`_Implementation`에는 게임 인자만 전달했다. 인자로 connId를 받는 방법은 클라이언트가 위조할 수 있어 인증 근거가 못 된다(기능 검토 FR 문서의 지적). Mirror(선택적 NetworkConnection)·FishNet·NGO(RpcParams) 모두 발신자 컨텍스트를 명시 파라미터로 제공한다.

대안 검토: (a) 앰비언트 `NetworkServer.CurrentRpcSender` — `QueueOnMain` 클로저 안에서 set/restore 필요, 숨은 상태·재진입·누출 위험, 테스트성 나쁨. 기각. (b) RPC 선언부에 발신자 파라미터 — 호출자 시그니처가 오염되고 클라이언트는 의미 없는 값을 전달해야 함. 기각.

## 결정 (Decision)

1. **끝에 `RpcSender` 파라미터 선언 시 주입** — ServerRpc의 `_Implementation`/`_Validate` 시그니처가 선언부 인자 + 마지막 `RpcSender`(값 타입, `UniNet.Core.Hosting`)면, 생성 dispatch가 전송 계층의 `senderConnId`로 생성해 전달한다. **RPC 선언부·호출부·와이어 프레임은 불변** — 발신자는 직렬화되지 않는다(헤드리스 회귀가 프레임 바이트로 고정).
2. **`RpcSender` 계약** — `ConnId`(전송 계층 부여·위조 불가)·`IsLocal`(connId 0 — 서버/호스트 직접 호출)·`IsValid`·`PlayerNetId`(`GetPlayerObject` 편의 래퍼 — 검증 로직에 매번 필요). 메인 스레드(구현·검증 실행 지점)에서만 유효.
3. **서버 전용 계약** — Client/Multicast 구현은 클라이언트에서 실행되어 발신자 개념이 없다. 끝에 `RpcSender`가 있으면 **UNINET018 에러**. RPC 선언부에 `RpcSender`를 쓰면 **UNINET017 에러**(선언이 아니라 `_Implementation`/`_Validate` 마지막에 선언하라고 안내). 소유권 강제(ADR-0016)와 직교 — `RequireOwnership=true`에도 주입 가능.
4. **`_Validate` 우선 적용** — 검증이 발신자를 가장 필요로 하는 지점이다. validate만·구현만 각각 독립적으로 마지막 인자를 추가할 수 있다.
5. **호환성** — 기존 RPC의 생성 코드는 동일(무발신자 경로 변화 없음). 와이어 불변. 선택 사항이므로 강제 마이그레이션 없음. 기존 "인자 connId" 패턴은 마이그레이션 가이드로 대체 권장.

## 결과 (Consequences)

- **긍정**: 비소유 RPC의 서버 검증이 실제 발신자 기준으로 가능해진다 — 상용화 블로커 3번 해소. 인자 위조 공격 표면 제거. 선언부·호출부 무변경으로 도입 비용이 구현 시그니처 1개 인자뿐.
- **부담 / 리스크**: 생성기가 두 시그니처 변형(±후행 RpcSender)을 검사·방출한다 — 파서 복잡도 소폭 증가(헤드리스 진단 회귀로 고정). `PlayerNetId`는 서버 레지스트리 조회 — 메인 스레드 외 접근 계약 문서화.
- **폐기 방법**: 파서의 후행 검사·에미터 접미 삽입·`RpcSender` 타입을 제거하면 기존 생성 코드로 즉시 복귀(와이어 불변이므로 롤백 무결).

## 마이그레이션

기존 코드 무변경. 발신자가 필요한 RPC만 `_Implementation`(및 `_Validate`) 끝에 `RpcSender sender`를 추가한다. 인자로 connId를 받던 기존 패턴은 제거하고 `sender.ConnId`로 대체할 것(위조 가능 인자 제거). 패키지 0.24.0 · 생성기 0.3.0.
