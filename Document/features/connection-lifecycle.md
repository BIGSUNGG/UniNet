# 연결 수명주기 이벤트 (ClientConnected/ClientDisconnected)

- **상태**: 구현됨
- **최초 작성**: 2026-09-21
- **마지막 갱신**: 2026-10-20 (ADR-0045 — 씬 전환 잔여·0.8.0)

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

## 연결 승인 수명주기 (0.6.0 — ADR-0043)

버전 검사가 활성(기본 — `ProtocolVersion` 비었지 않음)인 한, 와이어 7 선언이 수용되기 전까지 연결은 **Welcome만 수신**한다. 미승인 연결은 소유권 재배정(후보·수신)·캐치업·복제 틱·스폰/파괴 통보·LoadScene 브로드캐스트·씬 준비 게이트·`ClientConnected`에서 제외되고, 생성 허브의 게임 ServerRpc는 `IsVersionApproved` 게이트에서 조용히 기각된다(`UnapprovedServerRpcDrops` 카운터). 시스템 와이어 7/8/10/12는 게이트 밖 — 핸드셰이크 생존 필수. 승인 시 보류 작업이 기존 순서(대기 씬→재배정→캐치업→`ClientConnected`)로 정확히 1회 실행되고 재선언은 멱등이다. 미승인 연결의 해제는 `ClientDisconnected`·유예 세션 없이 조용히 정리된다. 미선언 연결은 `VersionDeclareTimeoutMs`(기본 10000, 0=스윕 비활성) 후 기존 킥 경로로 `VersionMismatch` 퇴장된다. `ProtocolVersion`을 빈 문자열로 두면 즉시 동기 승인 — 기존 동작 완전 호환. 실제 클라이언트는 생성 허브가 자동 선언하므로 무변경.

## 씬 전환 잔여 (0.8.0 — ADR-0045)

완료된 전환의 씬이 `_currentScene`으로 추적돼 **이후 접속하는 모든 연결이 wire 11을 받는다**(SCN-001 — 이전엔 영구 누락). wire 11을 받은 연결은 ready 보고까지 게임 데이터(캐치업·복제 틱·스폰/파괴·소유권 통보)를 유보한다(SCN-002) — 보고는 `UniNetManager.OnSceneLoaded`가 등록 후 자동 송신하므로 캐치업 전체상태가 항상 등록된 객체에 적용된다(폐기 창 소멸). 유효 보고마다 해당 승인 연결에 대기 파괴→소유권 재배정→소유자 기준선→전체상태 캐치업을 실행한다. 기존 참여자도 유보 중 놓친 통보·값을 복구하고, 유보 중 후보가 없어 보류된 고아 입양도 재시도한다. `ClientConnected`는 유보된 신규 attach 꼬리에서만 캐치업 후 1회 발화하며 기존 참여자에게 재발화하지 않는다. CON-001 승인과 씬 레디는 순서 있는 2단계로 조합된다. 승인 플래그가 먼저 설정돼도 `PostApprovalRun`이 대기 중이면 `LoadScene`은 직접 송신하지 않고 승인 꼬리의 `RunPostApproval`에 전달을 맡긴다(같은 전환의 wire 11 중복과 두 번째 보고 요구 방지). 구독자 예외는 기존 격리·재전파를 유지하되 `finally`로 게이트 평가를 보장한다. SCN-003은 per-connection 송신 플래그+전환 세대로 **해당 연결의 현 wire 11 송신 전에 도착한 보고만 거부**한다(전환 없으면 현재 씬 귀속). 보고 귀속은 세대(generation) 기준으로도 이중 검증된다 — 생성 wire-12 핸들러가 디스패치 수신 시점에 `CaptureSceneLoadGeneration`으로 세대를 캡처해 메인 큐로 전달하고, 캡처 세대가 현재 세대와 일치하는 보고만 수용한다(송신 직후~메인 큐 처리 사이 낡은 보고 창 차단). 이미 송신된 이전 동명 씬 보고가 새 wire 11 송신 이후 도착하면 구별할 수 없고 조기 유보 해제·게이트 완료 가능성이 남는다 — ADR-0045에서 수용한 tokenless 한계이며 전환 토큰·와이어 변경은 범위 밖이다. 영구 미보고 연결은 유보 지속 — 킥/재시도는 게임 책임(ADR-0035). 전환 이력이 없는 서버는 현행 동작 그대로.

**유보 중 파괴도 복구한다(리뷰 5)**. `DestroyObject`는 준비된 승인 연결에 즉시 `SendDestroy`하고, 승인된 `AwaitingSceneReady` 연결에는 netId를 연결별 지연 집합에 중복 없이 보관한다. 클라 등록 후 유효 ready 보고가 오면 소유권/캐치업 기준선보다 먼저 **서버 레지스트리에 여전히 없는 ID만** 파괴 통보한다. 따라서 `DontDestroyOnLoad` 또는 로컬 등록 씬 객체가 살아남아도 파괴를 영구 누락하지 않는다. 유보 중 같은 netId가 재등록되면 낡은 파괴는 폐기하고 대체 객체의 현재 기준선을 캐치업한다. 집합은 해제 시 분리해 후속 보고/전환에 재전송하지 않고, disconnect에서는 메인 큐에서 폐기해 전송 스레드의 detach와 HashSet 접근이 경합하지 않는다. 미승인 연결에는 집합을 만들거나 파괴를 통보하지 않는다. 공간 비용은 유보 연결마다 유보 중 파괴된 고유 ID 수에 비례하며, 영구 미보고 연결의 킥/타임아웃은 기존 게임 책임이다. 공개 API는 세대 캡처 2종(`CaptureSceneLoadGeneration`·3인자 `OnClientSceneLoaded`)이 추가됐고(구 생성 코드용 2인자 오버로드 유지), 와이어·페이로드는 불변이며 생성기는 0.2.11로 범핑됐다(패키지 동봉 — 소비자 조치 없음).

가시성 갱신(`UpdateVisibility`)도 해제·미승인·씬 유보 연결을 **가시성 기록 전에 제외**한다. 따라서 틱의 동적 스폰/재진입 기준선이 유보를 우회하지 않고, 받지 않은 상태를 `_visible`/`_everVisible`에 기록하지 않는다. ready 보고 후 정상 캐치업이 기준선을 전송하고 가시성을 기록한다.

예산 연기 큐는 **연기 당시의 안정 connId 수신 멤버십 AND 송신 시 현재 `IsRelevant`**를 모두 만족해야 전달한다(64명 초과도 동일). 큐에 들어간 뒤 비관련으로 바뀐 연결에는 낡은 델타를 누출하지 않는다. ready 캐치업 직전에 **보고한 연결만** 기존 큐의 수신 대상에서 제거하며, 공통 `SendBaseline`(캐치업·씬/동적 가시성 재진입)과 `BroadcastSpawn`도 해당 netId/connId의 낡은 큐 전달을 무효화한다. 따라서 비관련 기간에 큐가 남아 있어도 새 전체상태 이후 예전 델타가 재생되지 않는다. 다른 준비된 연결의 연기 변경은 그대로 보존된다. 비관련 전환과 해제 시에도 해당 수신자만 제거하고, 마지막 수신자가 사라진 항목은 예산 해제를 기다리지 않고 즉시 삭제·풀 배열 반환한다(휴면·씬 유보만으로 수신자를 제거하지 않음). 큐 전체 삭제·와이어 변경은 없다.

수신 표현의 비용 절충: 실제 연기가 있는 tick마다 정렬된 connId 배열 1개를 공유한다(`AttachConnection`은 잠금 안에서 단조 증가 ID를 append하고 detach는 순서 보존). 각 후보는 기존 inline 64비트 및 초과분 pooled `ulong` 비트셋만 보유한다. 초과분은 기존 visibility pass에서 수집하고 후보에 word 단위 복사하므로 연기 후보마다 연결을 다시 훑거나 HashSet/버킷을 할당하지 않는다. 항목별 비트만 제거하므로 공유 ID 배열의 다른 수신자·후보는 변하지 않는다. 완료/파괴/빈 후보의 초과 비트 배열은 pool에 반환한다. 사용자 hook/채널 예외로 tick이 중단돼도 `finally`가 현재·미처리 후보를 해제하며, `_queue`에 실제 남겨 둔 후보의 소유권만 보존한다. 반환 뒤 참조를 null로 만들어 후속 정리의 중복 반환을 막고, 큐가 참조 중인 배열은 반환하거나 지우지 않는다. 송신은 정렬된 공유 ID와 현재 연결을 단방향 cursor로 병합하므로 후보당 O(이전 수신 ID 수+현재 연결 수)이며 개별 무효화만 이진 검색을 유지한다. 추가 공간은 tick 공유 O(N)+후보별 O(N/64)이다. 기존 HashSet 대신 메모리를 줄이며, 송신 시 현재 relevancy hook/거리 검사를 다시 하는 CPU 비용은 누출 차단에 필요하다. ready 캐치업은 첫 기준선 전에 보고 연결의 기존 큐 전달을 한 번에 제거하고, 객체별 기준선에서는 중복 검사하지 않는다(큐 순회 1회/캐치업). 단일 객체 가시성 재진입·`BroadcastSpawn`은 해당 netId/connId만 제거하는 선형 검사를 유지한다(전환이 매우 잦고 큐가 크면 별도 인덱싱 검토; 이번 범위 밖).

## 연결 승인 콜백 (0.10.0 — ADR-0047)

게임이 자격 증명을 검사해 접속을 승인/거부하는 확장점(CON-002). 클라 옵션 `ConnectionCredential`이 설정되면 와이어 7 버전 선언에 자격 증명 문자열이 동봉된다(페이로드 `[version][credential]` — 상한 4096바이트, 초과는 파싱 전 조용 폐기; 구 클라는 문자열 1개라 잔여 0으로 credential="" 처리). 서버 옵션 `ConnectionApprover`가 설정되면 승인 흐름이 확장된다: 버전 수용 → **승인 콜백**(메인 스레드 시작, `UniNetApprovalRequest{ConnId, ProtocolVersion, Credential}` 전달, `Task<bool>` — DB/HTTP 검증 가능) → true면 기존 순서(대기 씬→재배정→캐치업→ClientConnected), false·예외면 `DisconnectClient(ApprovalRejected)`로 마감(클라 `ClientDisconnected(ApprovalRejected)` 관측). 콜백 대기 중 연결은 CON-001 유예 상태를 그대로 유지한다(게임 데이터 전면 유보·게임 ServerRpc 게이트). await 재개는 thread pool → `QueueOnMain`으로 메인 복귀 — 상태 변경은 메인 계약 유지. **게임 콜백의 반환 Task는 메인 스레드 펌프와 무관하게 완료 가능해야 한다**(DB/HTTP I/O형 — 내부 await에 `ConfigureAwait(false)` 권장; 메인 캡처 continuation은 메인이 막혀 있으면 완료가 굶는다). 타임아웃: `VersionDeclareTimeoutMs`는 선언+콜백 완료까지의 총 예산 — 스윕이 콜백 진행 중 연결도 VersionMismatch 킥으로 마감(무기한 유보 없음; 느린 인증 게임은 상향). 재접속(ResumeSession) 재선언도 콜백 1회 통과 — 게임이 재검증하거나 통과시킨다. 콜백 미설정(null, 기본)은 CON-001 동작과 완전히 동일하고 구 클라와 양방향 호환이다.

## 자발 퇴장 통보와 유예 즉시 폐기 (0.9.0 — ADR-0046)

자발적 `UniNetManager.ClientStop()`은 재개 토큰을 소각하는 계약(재개 불가)이므로, 유예 세션을 저장하면 재개 불가능한 세션이 유예 창 동안 고아로 잔존해 신규 입양을 막았다. 이제 ClientStop은 transport close 직전 **시스템 와이어 15 `IntentionalLeave`(C→S, 빈 페이로드)**를 발사하고 서버는 이 통보를 받은 연결의 유예 저장을 생략한다 — 대신 토큰을 툼스톤(`UnknownToken`, 수명 = 폐기+유예 창)하고 소유권을 즉시 재배정해 후발 클라가 고아를 즉시 입양한다. 통보는 전송 Task 250ms 유계 대기 + 150ms 드레인 창(ADR-0027 선례)으로 신뢰 전달되며, close 레이스 유실 시 기존 유예 동작으로 강등된다(무해). 구 클라(미통보)는 기존 유예 동작 그대로 — 하위 호환 회귀 0. 낡은 wire 10 재제시는 `SessionNotFound` (재시도 창) 대신 `UnknownToken`(즉시 종결)로 정확히 끝난다. `IntentionalLeaves` 카운터로 도달을 관측할 수 있다.

## 재개 결과 통보와 자동 재시도 (0.7.0 — ADR-0044)

`ResumeSession`은 `GraceResumeResult`(Reattached / SessionNotFound / Expired / UnknownToken)를 반환하고, **모든 종결 사유가 wire 14 `ResumeResult{ok, reason, retryableRemainingMs}`로 요청 연결에 통보**된다. 클라는 `NetworkClient.LastResumeResult`와 `ResumeResultReceived` 이벤트(디스패치 스레드 — `QueueOnMain` 마셜)로 관측한다. `ReconnectAsync`의 반환값은 여전히 연결 수립일 뿐이므로 재부착 성공 여부는 이 이벤트로 판별한다. 소비·스윕만료 토큰은 툼스톤(원 만료 시점까지)되어 이중 소비 패자는 UnknownToken·스윕 만료는 Expired로 정확히 종결된다. **디태치 경합**(서버가 구연결 죽음을 감지하기 전 재접속 — 세션이 아직 저장 안 됨)은 SessionNotFound + 재시도 창으로 응답되고 클라가 0.5초 간격으로 wire 10을 재전송(`TickResumeRetry`, 드라이버 캐던스)해 창 내 재부착으로 수렴한다. 어떤 실패도 세션을 소진하지 않는다(SEC-001). 토큰 없는 신규 접속은 wire 14를 받지 않는다.

## 재접속 토큰 보호 (0.4.0 — ADR-0041)

결정 배경: [[0041-세션-토큰-암호학적-난수-전환]]. 이번 변경은 예측 가능한 증가 토큰을 교체하는 SEC-001이며, 재접속 전체 수명주기의 완결을 의미하지 않는다.

| API / 설정 | 계약 |
| --- | --- |
| `NetworkClient.SessionToken` / `ServerConnection.SessionToken` | `string`. 암호학적 난수 32바이트의 opaque Base64 44자이며 빈 문자열/null은 비활성이다. 게임에서 값의 의미를 해석하거나 로그에 출력하지 않는다 |
| `NetworkServer.IssueSessionToken()` | `string` 반환. 접속 처리에서 자동 발급하며 활성 연결·유예 세션의 토큰과 충돌하지 않는다. 일반 게임 코드는 직접 발급할 필요가 없다 |
| `NetworkServer.SessionGraceSeconds` | 기본 0은 유예 비활성. 양수로 설정해야 연결 해제 후 재부착할 유예 세션이 보존된다 |
| `NetworkServer.ResumeSession(string token, long newConnId)` | 메인 스레드의 서버 처리. 미만료 유예 토큰과 실제 살아 있는 새 연결이 모두 확인된 경우에만 소유권·플레이어 매핑을 이전하고 토큰을 소비한다. 잘못된 토큰·죽은/미지 연결은 세션을 소진하지 않는다 |

- **와이어·입력 경계**: Welcome(와이어 1)과 ResumeRequest(와이어 10)의 토큰은 `string`이다. 재개 RPC 페이로드 상한은 int32 UTF-8 길이 접두 4바이트 + Base64 본문 44바이트 = **48바이트**(`MaxResumeRequestPayloadBytes`). 전송 계층의 헤더·암호화 오버헤드는 이 수치에 포함하지 않는다. 초과·잘림·위조 길이 입력은 조용히 폐기하며 수신 페이로드 카운터에는 집계한다.
- **마이그레이션**: 토큰 저장·테스트 채널의 `ulong`을 `string`으로, `token == 0` 검사를 `string.IsNullOrEmpty(token)`으로 바꾼다. 생성기는 0.2.8이다. 기본 `ProtocolVersion`은 패키지 버전 비교로 0.3.x/0.4.0 혼속을 거부하지만, 커스텀 값을 쓰면 SDK/wire 변경 배포에서 앱 프로토콜 식별자도 양단 함께 올려야 한다. Arena는 `arena-2-uninet-0.4`를 사용한다.
- **비밀 취급**: 토큰은 보유자가 세션을 재개할 수 있는 bearer 비밀이다. 난수화는 추측 방지이지 인증·기밀성 자체의 보장이 아니다. 실서비스는 인증·DTLS 설정과 함께 사용하고 토큰을 로그·HUD·분석 이벤트에 넣지 않는다. 에디터 디버거도 원문 대신 발급 여부·길이만 표시한다.
- **기존 한계**: `ReconnectAsync`의 반환값은 서버의 재부착 성공 응답이 아니다. 빠른 재접속 경합·결과 통보·아바타 정리와 유예의 상호작용은 REC-001, 연결 승인/독립 wire 버전 게이트는 CON-001/002에 남아 있다. 자발적 `ClientStop`은 클라이언트 환경을 지우므로 일반적인 원격 연결 상실 후 재접속과 구분한다.
- **Arena 예시**: Play Mode의 `ArenaBootstrap` 메뉴 `Diag/SEC-001 위조 토큰 재개 거부 (기존 세션 정지 후)`는 기존 데모 세션을 명시적으로 정지하고 실제 RUDP로 위조 토큰 거부·정상 재부착·플레이어 매핑 복원을 검증한다. 비동적 마커를 사용하며 **아바타 복구 검증은 아니다**. 진단의 환경/토큰 주입은 공격 재현용 테스트 절차이지 게임 수명주기의 권장 사용법이 아니다. 성공 마커는 `[UNINET-VERIFY] SEC-001 DIAG PASS`다.

## 테스트 / 검증

- **리뷰 5 검증**: `SceneTransitionTests`에 파괴 유보/재등록 2케이스와 disconnect 정리 1케이스를 추가했다(총 34케이스). 즉시 송신 없음·잘못된 보고 유지·ready 후 파괴 1회 및 기준선보다 먼저 전달·중복 보고/다음 전환 재송신 없음·준비된 연결 즉시 수신·미승인 무통보·재등록 대체 객체 Score=42 적용·연결별 상태 폐기를 단언한다. 순차 Core/EditMode/PlayMode `dotnet build --no-restore` 및 패키지 정적 검증 PASS(기존 CS8632 경고). 독립 headless Core는 기존/후발 참여자 × 파괴/재등록 4종 + 전송 스레드 detach 정리 1종 **5/5 PASS**, 기존 리뷰 4 probe 재실행 **22/22 PASS**. 수정 전 소스 음성 대조는 `held destroy delivered exactly once after ready`에서 예상 실패했다. 재현 소스/수정 전 소스/로그: `.pi-subagents/artifacts/commercial-netcode/scn-r5/`. 초기 probe 파일 분리 시 using 누락 빌드 실패 기록도 보존했으며 수정 후 전체 검증을 완료했다. worker는 Unity MCP를 사용하지 않았으나 부모가 최신 코드에서 `SceneTransitionTests` **34/34 EditMode PASS**(job `ece5ab18f3f34cccb3cc7813ad733e98`)·`CurrentSceneJoinPlayTests` **1/1 PlayMode PASS**(job `6c7c7a306ca94ef0826f0d90d68c0860`)를 실행했다. 필수 독립 reviewer CLEAN 대기, 커밋/스테이징/릴리스 없음.

- **리뷰 4 검증**: Core→EditMode→PlayMode `dotnet build --no-restore` 순차 빌드·패키지 검증·`git diff --check` PASS. EditMode에 승인 꼬리/LoadScene 경합 1종과 2/66명 × 비관련/해제 마지막 수신자 정리 4케이스 추가. 독립 headless Core **22/22 PASS**: 기존 9종+경합 1종+2/66/130명 정리 6종+생성 relevancy/지연 송신 relevancy/중간 batch flush 채널 예외 3종+3/66/130명 append 및 선행/중간 제거 후 병합 3종. 예외 probe는 fresh·현재·미처리 후보의 overflow 참조 해제와 기존/재연기 큐의 동일 배열·비트 보존, 회복 후 peers에 정확히 1회 전달을 확인한다. 이전 소스에 같은 경합/정리/예외 probe를 실행하면 각각 예상 실패한다. 재현 소스·전 소스·순차 빌드·음성 대조 로그: `.pi-subagents/artifacts/commercial-netcode/scn-r4/`.
- **리뷰 4 동일 Release headless benchmark**(리뷰 3의 `Program.cs` 그대로, 66연결·32객체·100회 압박/배출·warm-up 10회): round-4 전 **127.36ms / 22,933,216 bytes** → 후 **112.14ms / 22,933,216 bytes**. 최신 재실행 관측이며 이전 측정 로그도 `perf-*-initial.log`로 보존했다. 각 시간은 단일 합성 실행 관측으로 통계적/Unity 성능 개선 주장이 아니다. `PerfProgram.cs`·`BeforeNetworkServer.cs`·`perf-before.log`·`perf-after.log`로 재현 가능하다. 이후 hook/채널 예외의 기존 비트랜잭션 송신/스냅샷 동작은 변경하지 않았다(미완료 전달을 새로 재시도하지 않음).

- ready 캐치업 중복 순회 보정: Core/EditMode/PlayMode `dotnet build --no-restore`·패키지 정적 검증·`git diff --check` 통과. 기존 headless Core 회귀 **9/9 PASS**를 재사용했고, 호출 경로 정적 단언으로 전체 무효화가 첫 기준선 전에 1회 수행되며 캐치업은 쌍별 순회를 생략하고 재진입은 유지함을 확인했다. 로그: `.pi-subagents/artifacts/commercial-netcode/scn-ready-prune/`. Unity suite 재실행·독립 reviewer CLEAN은 이번 worker에서 수행하지 않았다. 공개 동작 변경 없는 내부 성능 수정이므로 소비자 스킬은 추가 변경하지 않았다.

- **Unity 실행 증거**: 리뷰 3 수정 뒤 부모가 `SceneTransitionTests` 26/26 EditMode·`CurrentSceneJoinPlayTests` 1/1 PlayMode, 리뷰 4 변경 뒤 31/31·1/1 PASS를 실행했다. 리뷰 5의 파괴 유보/재등록 수정 이후 최신 코드에서도 부모가 `SceneTransitionTests` **34/34 EditMode PASS** 및 `CurrentSceneJoinPlayTests` **1/1 PlayMode PASS**를 확인했다(위 job ID 참조). 17/17·20/20·26/26·31/31 및 worker의 Unity MCP 부재 기록은 과거 이력이다. 이는 SCN 대상 테스트이며 전체 Sandbox suite 완료를 뜻하지 않는다. 독립 reviewer CLEAN·배포는 여전히 별도 게이트다.
- 리뷰 3라운드: `SceneTransitionTests`에 2/66/130 연결 × 비관련 상태 배출/재진입 후 배출 **6케이스** 추가(비관련 송신 없음·42 기준선 후 낡은 델타 없음·다른 수신자의 10→42 보존). dotnet Core/EditMode/PlayMode 빌드·패키지 정적 검증 통과, Unity와 분리한 headless Core probe **9/9 PASS**(기존 인덱스 이동/ready 3종 포함), 수정 전 소스는 비관련 누출 단언으로 예상 실패. worker는 Unity suite를 실행하지 않았다.
- 제한된 Release headless 비용 probe(66연결·32오브젝트·100회 예산 압박/배출, warm-up 10회): 할당 **33,342,816 → 22,933,216 bytes(약 31% 감소)**, 단일 실행 시간 **110.54 → 130.39ms**. 전체 Core tick/송신을 포함하며 Unity/네이티브 호출 없는 합성 측정이므로 Unity 속도 개선 주장이 아니다. 현재 relevancy 재검사·이진 검색의 CPU 절충을 명시적으로 수용한 범위다. 재현용 probe·전후 소스·로그: `.pi-subagents/artifacts/commercial-netcode/scn-r3/`(로컬 검증 산출물, 배포물 아님).
- 2026-10-20 SCN 리뷰 2라운드 큐 수정 — `dotnet build`로 `UniNet.Core`·`UniNet.Tests.EditMode`·`UniNet.Tests.PlayMode` 컴파일 및 패키지 정적 검증 통과(EditMode 기존 CS8632 2개). Unity와 분리된 임시 headless Core probe로 2/3/66 연결의 큐 역행 방지·다른 수신자 보존 3/3 확인, 동일 probe는 수정 전 소스에서 낡은 전달 단언으로 예상 실패했다. 추가한 실제 Unity EditMode 3케이스는 worker의 Unity 도구 부재로 **미실행**이며 이전 17/17·1/1 결과를 재실행 증거로 대체하지 않는다.
- 2026-10-19 SEC-001 배포 준비 검증 — `ReconnectGraceTests` 8종 + `ResumeRequestWireTests` 2종 **10/10**, EditMode 전수 **138/138**, PlayMode 전수 **27/27**, Arena 실제 RUDP 진단 **DIAG PASS**. 잘못된 연결·중복 재개·위조 토큰·48바이트 정상 요청·초과/잘림/위조 접두 후 정상 재개·폐기 트래픽 카운터를 확인했다. 원시 증거와 개별 배포 상태는 `Document/research/commercial-netcode-evidence.json` 참조
- EditMode `LifecycleEventTests` 4종 — 발화 순서(접속→해제)·소유권 재배정 전 발화 계약·접속 경로 구독자 예외 격리와 펌프 잔여 작업 보존·해제 경로 예외 무관 재배정 보장
- PlayMode `LifecycleEventPlayTests` 2종 — 실제 루프백 RUDP 세션에서 서버/클라 이벤트·상태 조회(환영 ID 일치·원격 종료 콜백 + **서버 정지 사유 ServerShutdown 검증**), Arena 방식 퇴장 아바타 파괴와 서버 등록 해제
- EditMode `DisconnectNoticeTests` 3종 (ADR-0027) — 킥 공지 대상 연결 한정 전송·미지 연결 false·서버 종료 공지 전 연결 전달
- PlayMode `DisconnectReasonPlayTests` 2종 (ADR-0027) — 실세션 킥 사유(Kicked) 수신·자발 ClientStop 미발화 계약
- PlayMode `VersionMismatchPlayTests` 2종 (ADR-0028) — 버전 불일치 클라가 VersionMismatch 사유로 거부됨·일치 커스텀 버전 정상 접속(Welcome에 서버 버전 수신)
- EditMode `VersionHandshakeTests` 3종 (ADR-0028) — 불일치 거부·일치 수용·검사 비활성(빈 기대 버전)
- 2-프로세스 `KICK-REASON PASS` (ADR-0027) + `VERSION-MISMATCH PASS` (ADR-0028) — 서버 킥 → 클라 사유 수신 왕복·버전 불일치 재접속 거부 (기존 3종 PASS 유지)
- 2026-10-12: EditMode 87/87 · PlayMode 22/22 (MCP 에디터) · 2-프로세스 킥·버전 왕복 PASS

- **종료·실패·재시작 대칭 정리 (LIF-001/ADR-0052)** — `ServerStopAsync`도 동기 정지와 동일하게 동적 스폰을 스윕하고(메인 스레드 계약), `ServerAsync` listen 실패는 환경 탈착 후 재던진다. 등록 추적은 세대 스탬프(`NetworkServer.Generation`/`NetworkClient.Generation`)로 교체 — 새 서버/클라 인스턴스마다 씬 객체가 자동 재등록되고 같은 세대 재호출은 멱등이다. 세대 값은 와이어 미전송(0.19.0).

## 변경 이력
- 2026-10-21 — ADR-0047 연결 승인 콜백: 와이어 7 페이로드 확장 `[version][credential]`(조건부 읽기로 구 클라 호환)·`UniNetEndpointOptions.ConnectionApprover/ConnectionCredential`·`UniNetDisconnectReason.ApprovalRejected = 6`·콜백 대기 중 CON-001 유예 상태 재사용·`VersionDeclareTimeoutMs` 총예산 재사용. 0.10.0 (CON-002 CLOSED — EditMode 221/221·PlayMode 35/35·실RUDP 승인/거부/초과페이로드 PASS·리뷰 r11→r12 CLEAN)
- 2026-10-21 — ADR-0046 자발 퇴장 통보: 시스템 와이어 15 `IntentionalLeave`(C→S) 신설 — ClientStop이 close 직전 통보, 서버는 유예 저장 생략+토큰 툼스톤+소유권 즉시 재배정. 전송 신뢰성: SendRPC Task 250ms 유계 대기 + 150ms 드레인 창. 구 클라 호환(미통보 = 기존 유예). 0.9.0 (REC-002 CLOSED — 실루프백 3/3·Arena 진단 PASS·전수 209/32 PASS)

- 2026-10-20 — 리뷰 5: 승인된 씬 유보 연결에 파괴 ID를 보관하고 등록 후 유효 보고에서 부재 ID만 기준선 전에 1회 전달. 같은 netId 재등록은 현재 캐치업으로 대체하고 disconnect는 메인 큐에서 폐기한다. EditMode 3케이스·headless 5+기존 22종·음성 대조 검증, Unity 실행 및 독립 리뷰는 별도 게이트. README/소비자 스킬도 동기화.

- 2026-10-20 — 리뷰 4: 승인 꼬리 전 LoadScene 중복 방지·overflow 비트 소유권의 예외 안전 정리·정렬 ID 선형 병합 송신·비관련/해제 마지막 수신자 즉시 폐기. 새 5케이스 추가, headless 22종/음성 대조/동일 benchmark와 부모의 변경 후 Unity `SceneTransitionTests` 31/31 EditMode·`CurrentSceneJoinPlayTests` 1/1 PlayMode PASS. 필수 독립 reviewer CLEAN·배포 대기, 커밋 없음.

- 2026-10-20 — ready 캐치업의 객체별 중복 큐 순회 제거: `RunPostSceneReady`의 전체 수신자 무효화를 첫 기준선 전에 유지하고 `SendCatchup`에서만 객체별 무효화를 생략한다. 단일 객체 가시성 재진입과 스폰의 쌍별 무효화·공개 API·전달 계약은 그대로다.

- 2026-10-20 — 리뷰 3라운드: 큐 송신은 안정 수신 멤버십과 현재 relevancy의 교집합, 모든 객체 기준선은 해당 수신자의 낡은 큐 전달을 대체. 후보별 HashSet/연결 재스캔을 공유 ID 배열+inline/pooled 비트셋으로 교체하고 6케이스 회귀·합성 비용 측정을 추가. 리뷰 2 수정 이후 부모 Unity 20/20·1/1 PASS를 최신 실행 이력으로 반영하되, 이번 추가 변경 이후 Unity 재실행·독립 CLEAN은 별도 게이트로 남긴다.

- 2026-10-20 — 리뷰 2라운드: ready 캐치업 직전에 보고 연결의 낡은 큐 전달만 무효화. 실제 예산/휴면 연기 때만 connId 수신 집합을 확보해 연결 이탈에 따른 인덱스 이동과 64명 초과도 처리. `SceneTransitionTests`에 2연결·선행 연결 1명/64명 이탈(총 3케이스) 회귀 추가: 예산으로 남은 이전값 10, 유보 중 스냅샷 42 전진, A 캐치업 후 값 역행 없음, B의 연기 42 전달 보존. 이 변경의 Unity 실행·독립 재리뷰는 별도 게이트이며 SCN-003 tokenless 범위는 유지.

- 2026-10-20 — 독립 리뷰 후속: 기존 참여자 ready 시 소유권·전체상태 복구(접속 이벤트 중복 없음), 마지막 보고의 접속 구독자 예외에도 게이트 평가 보장. EditMode 회귀 3종 추가, PlayMode는 baseline 수신 후 신호를 초기화하고 실제 RepNotify·이전 수신값 100을 단언하도록 수정. SCN-003은 송신 전 도착 보고 차단에 한정하고 수용된 동명 in-flight 한계를 명시. Unity 실행·독립 재리뷰·릴리스는 미완료.

- 2026-10-20 — SCN 검증 후속 수정: 가시성 갱신의 기준선 누출·허위 가시성 기록 차단. `SceneTransitionTests`에 해제/미승인/씬 유보 3종의 무송신·기록 없음 회귀 추가, 레디 후 동적 스폰 복구 단언 강화. 실제 Unity 재실행·리뷰는 별도 게이트.
- 2026-10-20 — ADR-0045 씬 전환 잔여: 현재 씬 추적(SCN-001)·레디 보고 전 캐치업 유보·2단계 조합(SCN-002)·낡은 보고 귀속 차단(SCN-003 — 송신 플래그+디스패치 수신 시점 세대 캡처). 와이어 불변·생성기 0.2.11(세대 캡처 핸들러·신규 API 2종 — 구 오버로드 호환). 0.8.0 (독립 리뷰 r6에서 무변경 표기 정정)

- 2026-10-20 — ADR-0044 재개 결과 통보·자동 재시도: `GraceResumeResult` 열거형(파괴)·wire 14 결과 통보·소비/만료 툼스톤·`LastResumeResult`/`ResumeResultReceived`·SessionNotFound 자동 재시도(0.5s). 제너레이터 0.2.10. 0.7.0 (REC-001)

- 2026-10-20 — ADR-0043 연결 승인 수명주기: 미선언 연결 게임 데이터 유보(소유권·캐치업·복제·이벤트·RPC 게이트)·승인 시 순서 보장 해제·미선언 타임아웃 킥(`VersionDeclareTimeoutMs`)·제너레이터 0.2.9. 0.6.0 (CON-001)

- 2026-10-19 — ADR-0041 세션 토큰 보호: 256비트 난수 Base64 `string`, 재부착 전 유효 연결 검증, 48바이트 재개 입력 상한·조용한 폐기·수신 카운터, 디버거 마스킹. 0.4.0 API/wire 마이그레이션·재접속 잔여 한계·Arena 마커 진단 및 실행 근거 반영
- 2026-10-12 — ADR-0028 프로토콜/빌드 버전 핸드셰이크: `UniNetEndpointOptions.ProtocolVersion`·와이어 7 버전 선언·Welcome 서버 버전 필드·불일치 `VersionMismatch` 거부(제너레이터 0.2.3)
- 2026-10-12 — ADR-0027 킥·종료 사유: `DisconnectClient` API·와이어 시스템 메시지 6·`ClientDisconnected` 사유 인자로 서명 변경(파괴적·마이그레이션 노트 ADR 참조)·제너레이터 0.2.2
- 2026-09-21 — 리뷰 라운드 1 수정 반영 (해제 경로 재배정 finally 보장·플래그 레이스 제거·허브 동일성 필터·ClientStop 서술 정정·EDI 재던짐·스톰 가이드)
- 2026-09-21 — 최초 작성 (구현 완료)
