# 0046. 자발 퇴장 통보 — 와이어 15 IntentionalLeave와 유예 세션 즉시 폐기

- **상태**: 승인됨
- **날짜**: 2026-10-21
- **결정자**: 사용자(옵션 A — ClientStop 유계 플러시 대기 승인) · 플래너 설계(zai/glm-5.3-flash) · 워커 구현

## 배경 (Context)

감사 대장 REC-002: 자발적 `ClientStop`은 클라가 재개 토큰을 소각(재개 불가 계약)하지만, 서버의 `DetachConnection`은 승인된 단절이면 무조건 `SaveGraceSession` — '자발적 퇴장'과 단절을 구분할 신호가 없었다. 결과: 재개 불가능한 세션이 유예 창(`SessionGraceSeconds`) 동안 잔존하고 `IsInGraceSession`이 고아 입양을 계속 블로킹했다. 전송 계층(DRPC/Communication DLL)에는 disconnect 사유 콜백이 없어 애플리케이션 와이어가 필요했다.

## 결정 (Decision)

1. **새 시스템 와이어 15 `IntentionalLeave`(C→S)** — ReliableOrdered, 0바이트 페이로드(발신자 신원은 채널 connId — 자기 세션만 통보 가능, 위조 표적 없음). `ClientStop`이 `hub.Disconnect()` 직전 발사한다. 생성 허브는 와이어 7/8/10/12와 동일하게 등록(승인 게이트 밖 — 미승인 연결은 유예 자체가 없어 무해).
2. **서버: 즉시 폐기, 옵션 없음** — 통보된 연결의 detach는 `SaveGraceSession` 대신 토큰 툼스톤(`UnknownToken`, 수명 = 폐기+유예 창 — REC-001 기계 재사용) + 즉시 `ReassignOwnership()`. 유예 유지 옵션은 클라 계약(토큰 소각 = 재개 불가)과 모순되는 죽은 설정이라 배제. "퇴장하되 세션 유지(suspend)"는 비목표(별도 기능).
3. **전달 신뢰성: 유계 플러시 창(사용자 승인)** — `UniNetSend`(fire-and-forget) + 즉시 close는 전송 Task가 큐 수락 시점에 완료되어 close가 파이프라인 쓰기 전에 세션을 찢는 간헐 유실을 발생(실루프백 관측). 이에 (a) 보호된 `HubBase.SendRPC`를 반사 호출해 전송 Task 획득 → 250ms 유계 대기, (b) 150ms 드레인 창(ADR-0027 `ServerStopAsync`의 150ms 클라이언트 디스패치 그레이스 선례) 후 close. DRPC 버전은 `update-dlls.sh` 고정이라 반사 모양 변경 리스크는 통제되며, 실패 시 fire-and-forget 강등(레거시 동작 = 무해). ClientStop은 동기 유지(최악 ~400ms 유계 블록).
4. **wire 14 확장 기각** — 자발 퇴장 클라는 토큰을 소각해 재개 요청 자체가 불가하다. 수신할 합법적 당사자가 없고(열거형 추가는 관측 소비자 0개인 파괴적 변경), 낡은 중복 재제시는 툼스톤 `UnknownToken`이 이미 정확히 종결한다.
5. **하위 호환** — 구 클라(미전송)+신 서버: 플래그 false → 기존 유예 동작(회귀 0). 신 클라+구 서버: 미지 와이어는 기본 `ProtocolVersion` 핸드셰이크가 혼속을 차단, 커스텀 버전 게임의 최악도 상태 오염 없음. 파괴 변경 없음 — 추가만 존재(생성기 0.2.12, 패키지 0.9.0).

## 결과 (Consequences)

- **긍정**: 자발 퇴장 시 고아 세션이 유예 창 전체를 점유하지 않는다 — 후발 클라가 즉시 입양하고(실루프백·Arena 실RUDP 검증), 낡은 wire 10 재제시는 `UnknownToken`으로 정확히 종결된다(재시도 창 오무장 없음).
- **부담 / 리스크**: ClientStop이 유계 블록을 갖는다(최악 ~400ms: 전송 Task 대기 250ms + 드레인 150ms; 통상 ~150ms — 드레인 창이 지배). 랩어라운드 없는 할당자·고정 DRPC 버전이 전제 — DRPC 갱신 시 반사 모양 재확인 필요. 실측: 드레인 창 없이는 간헐 유실(1/2), 적용 후 실루프백 3/3·Arena 진단 PASS.
- **폐기 방법**: 와이어 15 등록·`OnIntentionalLeave`·`ServerConnection.IntentionalLeave`·Detach 3분기 중 의도 분기·`ClientStop`의 발사+플러시 창을 제거하고 `DetachConnection`을 무조건 `SaveGraceSession`으로 되돌린다.

## 마이그레이션

없음 — 기존 공개 API·와이어 무변경(추가만). `ClientStop`의 동작 계약이 강화된다(서버에 의도 통보 → 유예 없이 즉시 정리) — 게임 코드는 투명하다(호출 방식 동일). 연결 단절(비자발) 시나리오의 유예 재접속은 종전대로 동작한다.
