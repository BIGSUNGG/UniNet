# 연결 수명주기 이벤트 (ClientConnected/ClientDisconnected)

- **상태**: 구현됨
- **최초 작성**: 2026-09-21
- **마지막 갱신**: 2026-10-12 (ADR-0027 — 킥·종료 사유)

## 개요

연결 수명주기를 게임 코드에 알리는 **게이트웨이 API** — 서버는 `NetworkServer.ClientConnected`/`ClientDisconnected` 이벤트(connId)와 **강제 퇴장 `DisconnectClient`**, 클라는 `UniNetManager.ClientDisconnected` 이벤트(**사유 인자**)와 `UniNetManager.IsClientConnected` 상태 조회를 제공한다. 결정 배경: [[0015-연결-수명주기-이벤트-게이트웨이]] · [[0027-킥-연결-종료-사유]].

배경 문제: 클라가 끊겨도 게임에 알림이 없어 퇴장 플레이어의 아바타가 좀비로 잔존했다(다른 연결로 소유권만 재배정). Arena 예제는 0.5초 폴링으로 "잉여 플레이어"를 간접 정리했지만 연결-오브젝트 정밀 대응이 불가능했다.

## 요구사항 / 목표

- 접속·해제 시점을 게임이 메인 스레드에서 안전하게 관찰한다 (Unity 객체 접근 가능)
- 퇴장 연결이 소유한 오브젝트를 게임이 식별해 처리할 수 있다 (파괴 등 — 프레임워크 자동 파괴 아님)
- 클라는 세션 종료(원격 종료·네트워크 단절)를 콜백으로 인지하고 상태를 조회할 수 있다

## 설계

- **`NetworkServer.ClientConnected`** (`Package/Runtime/UniNet.Core/Hosting/NetworkServer.cs`) — `event Action<long>`. 환영·소유권 배정·캐치업까지 끝난 뒤 메인 스레드에서 발화. 게임은 여기서 아바타 스폰·HUD 갱신
- **`NetworkServer.ClientDisconnected`** — `event Action<long>`. **소유권 재배정 이전**에 메인 스레드에서 발화 — 핸들러가 `GetEntry(netId).OwnerConnId`로 퇴장 연결 소유 오브젝트를 식별할 수 있다. 발화 후 프레임워크가 재배정하며, **구독자 예외와 무관하게 재배정은 항상 실행된다** (`try/finally` 보장 — 죽은 연결이 소유자로 남는 것을 막는 서버 불변식)
- **`UniNetManager.ClientDisconnected`** (`Package/Runtime/UniNet.Unity/UniNetManager.cs`) - `event Action<UniNetDisconnectReason>` (ADR-0027). 클라 허브의 세션 종료(Disconnected)를 관측해 메인 스레드에서 발화하며, 사유는 **Kicked / ServerShutdown / ConnectionLost / VersionMismatch**로 구분된다 (공지 미수신 시 ConnectionLost). 자발적 `ClientStop`은 여전히 미발화
- **프로토콜 버전 핸드셰이크** (ADR-0028) - `UniNetEndpointOptions.ProtocolVersion`(기본 = 패키지 버전)을 클라가 접속 직후 선언(와이어 7)하고 서버가 `ExpectedProtocolVersion`과 비교 — 불일치 클라는 `VersionMismatch` 사유로 거부(킥). Welcome에 서버 버전이 실려 오고 `NetworkClient.ServerProtocolVersion`으로 열람
- **플레이어 오브젝트 매핑·퇴장 정리** (ADR-0031) - `SetPlayerObject(connId, netId)` 등록(연결 분리 시 자동 해제)·`GetPlayerObject`/`GetPlayerConnection` 조회 — "내 아바타 찾기" 상용구 제거. `DestroyObjectsOwnedBy(connId)`는 서버 엔트리 기반으로 퇴장자 소유 동적 오브젝트만 일괄 파괴(씬 스캔 불필요 — ClientDisconnected 재배정 전 발화 계약과 결합)
- **`NetworkServer.DisconnectClient(connId, reason)`** (ADR-0027) — 강제 퇴장. 사유 공지(와이어 시스템 메시지 6)를 플러시한 뒤 150ms 유예 후 전송 폐쇄 — 이후 재배정·수명주기 이벤트는 기존 경로 그대로. `BroadcastDisconnectNoticeAsync`는 `ServerStopAsync`가 서버 종료 사유를 전 연결에 전달하는 데 사용
- **`UniNetManager.IsClientConnected`** — `ClientAsync` 성공 후 true, 세션 종료·`ClientStop` 후 false
- 격리 계약: 구독자별 try/catch — 첫 구독자의 예외가 나머지 구독자를 묻지 않는다. 마지막 예외는 원본 스택 트레이스를 보존해(`ExceptionDispatchInfo`) 재던져 드라이버의 메인 펌프 보호(`catch` + `Debug.LogException`)가 로그하고, 펌프 잔여 작업은 다음 프레임 드레인에서 실행된다 (생성 코드 펌프 보호와 동일 패턴). 해제 경로의 소유권 재배정은 `finally`로 보장돼 예외가 잔여 문장을 건너뛰지 않는다

## 동작 상세

- 발화 스레드: 전부 메인 스레드 (네트워크 스레드 관측분은 `QueueOnMain`으로 넘김)
- 발화 시점 순서 (해제): 뷰어/가시성 상태 정리 → **ClientDisconnected 발화** → 소유권 재배정. 게임이 여기서 `UniNetManager.NetworkDestroy`하면 재배정 대상에서 자연스럽게 제외된다
- 발화 시점 순서 (접속): 환영 → 소유권 배정 → 캐치업 → **ClientConnected 발화**. 이 시점에 스폰해도 캐치업과 충돌하지 않는다
- 클라 콜백은 세션이 실제로 끊어졌을 때 발화한다 (서버 종료·네트워크 단절·킥). 자발 `ClientStop`은 `IsClientConnected` 상태만 즉시 해제하고 이벤트는 발화하지 않는다
- 킥 흐름 (ADR-0027): `DisconnectClient` → 사유 공지 와이어 플러시 → 150ms 유예(클라 비동기 디스패치 확보) → 전송 폐쇄 → 클라에서 사유와 함께 `ClientDisconnected` 발화. 최선 노력: 공지 유실 시 ConnectionLost로 강등
- 이벤트 스톰: 접속·해제는 연결당 1회씩 메인 스레드에서 발화 — 대량 churn 시 콜백 비용이 비례한다. 콜백에서 전 씬 스캔(`FindObjectsByType`) 대신 connId→오브젝트 맵 유지를 권장한다

```csharp
// 서버 (권위) — Arena 예제 참고
_server.ClientConnected += connId => { /* HUD 갱신, 즉시 스폰 */ };
_server.ClientDisconnected += connId =>
{
    foreach (var player in FindObjectsByType<ArenaPlayer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
    {
        var entry = server.GetEntry(player.NetId);
        if (entry == null || !entry.IsDynamic || entry.OwnerConnId != connId) continue;
        UniNetManager.NetworkDestroy(player.gameObject);   // 퇴장 아바타 정밀 파괴 → 전 클라 despawn 전파
    }
};

// 클라이언트
UniNetManager.ClientDisconnected += reason =>   // ADR-0027·0028 — Kicked / ServerShutdown / ConnectionLost / VersionMismatch
{
    /* 사유별 분기: 킥이면 안내 후 메뉴, 버전 불일치면 강제 업데이트, 서버 종료면 재접속 안내, 유실이면 재접속 프롬프트 */
};
if (UniNetManager.IsClientConnected) { /* 세션 살아있음 */ }
```

## 테스트 / 검증

- EditMode `LifecycleEventTests` 4종 — 발화 순서(접속→해제)·소유권 재배정 전 발화 계약·접속 경로 구독자 예외 격리와 펌프 잔여 작업 보존·해제 경로 예외 무관 재배정 보장
- PlayMode `LifecycleEventPlayTests` 2종 — 실제 루프백 RUDP 세션에서 서버/클라 이벤트·상태 조회(환영 ID 일치·원격 종료 콜백 + **서버 정지 사유 ServerShutdown 검증**), Arena 방식 퇴장 아바타 파괴와 서버 등록 해제
- EditMode `DisconnectNoticeTests` 3종 (ADR-0027) — 킥 공지 대상 연결 한정 전송·미지 연결 false·서버 종료 공지 전 연결 전달
- PlayMode `DisconnectReasonPlayTests` 2종 (ADR-0027) — 실세션 킥 사유(Kicked) 수신·자발 ClientStop 미발화 계약
- PlayMode `VersionMismatchPlayTests` 2종 (ADR-0028) — 버전 불일치 클라가 VersionMismatch 사유로 거부됨·일치 커스텀 버전 정상 접속(Welcome에 서버 버전 수신)
- EditMode `VersionHandshakeTests` 3종 (ADR-0028) — 불일치 거부·일치 수용·검사 비활성(빈 기대 버전)
- 2-프로세스 `KICK-REASON PASS` (ADR-0027) + `VERSION-MISMATCH PASS` (ADR-0028) — 서버 킥 → 클라 사유 수신 왕복·버전 불일치 재접속 거부 (기존 3종 PASS 유지)
- 2026-10-12: EditMode 87/87 · PlayMode 22/22 (MCP 에디터) · 2-프로세스 킥·버전 왕복 PASS

## 변경 이력

- 2026-10-12 — ADR-0028 프로토콜/빌드 버전 핸드셰이크: `UniNetEndpointOptions.ProtocolVersion`·와이어 7 버전 선언·Welcome 서버 버전 필드·불일치 `VersionMismatch` 거부(제너레이터 0.2.3)
- 2026-10-12 — ADR-0027 킥·종료 사유: `DisconnectClient` API·와이어 시스템 메시지 6·`ClientDisconnected` 사유 인자로 서명 변경(파괴적·마이그레이션 노트 ADR 참조)·제너레이터 0.2.2
- 2026-09-21 — 리뷰 라운드 1 수정 반영 (해제 경로 재배정 finally 보장·플래그 레이스 제거·허브 동일성 필터·ClientStop 서술 정정·EDI 재던짐·스톰 가이드)
- 2026-09-21 — 최초 작성 (구현 완료)
