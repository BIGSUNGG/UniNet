# 0057. additive 씬 동기화 — LoadSceneAdditive / UnloadScene

- **상태**: 승인됨
- **날짜**: 2026-11-02
- **결정자**: 사용자(방향 선택) · 부모 에이전트 설계·구현

## 배경 (Context)

상용화 검토 블로커 5번(2026-11-02 세션): 네트워크 씬 관리가 단일 씬 전환(ADR-0035)뿐이었다 — `_pendingScene`이 단일 문자열, 전 연결 Single 로드+보고 게이트, additive 적재·언로드·활성 집합 개념 없음. 세계 확장(스테이지 추가 적재·정리) 게임에는 필수. 같은 에셋 다중 로드(씬 인스턴스)·연결별 씬(인스턴스 던전)은 검토에서 명시적으로 **범위 밖**으로 남긴다(후자는 UniNetScope 인스턴스화 — 검토 문서 2026-11-02 참조).

## 결정 (Decision)

1. **API 3종·한 번에 한 전환** — `LoadScene`(Replace, 기존)·`LoadSceneAdditive(name)`·`UnloadScene(name)`. 전환 진행 중 두 번째 시작은 `InvalidOperationException`(직렬화된 전환 — `AllClientsSceneLoaded` 대기 후 다음). additive 중복 적재·비-additive 언로드는 `ArgumentException`.
2. **와이어 11 확장 `[mode byte][sceneName]`** — mode 0=Replace/1=Additive/2=Unload(`SceneLoadMode` enum). **하위호환 없음**: 구버전 클라는 첫 바이트를 문자열 길이로 오인 — 핸드셰이크(ADR-0028, 기본=패키지 버전)가 0.24 미만 접속을 VersionMismatch로 거부하므로 실질 혼선 경로 없음. 생성기는 1-arg `SendLoadScene` 추상 멤버를 유지하고 모드 변형을 인터페이스 DIM 기본(Replace 전달)으로 둔다 — 테스트 채널 17종 무변경.
3. **연결별 대기 씬 집합** — `ServerConnection.AwaitingSceneReady` bool → `PendingScenes` 리스트(항목마다 wire-11 송신 시점 세대 각인) + 계산 속성. 기존 수신 게이트 14곳은 계산 속성 읽기로 무변경. 보고(와이어 12)는 씬 이름으로 `PendingScenes`에서 제거 — 마지막 하나가 비워질 때 SCN-002 유보 해제·attach 꼬리·캐치업 실행. SCN-003 세대 가드는 **항목별 세대**로 정밀화된다(리뷰 r1-1: 그 씬의 wire-11 송신 이전에 캡처된 보고만 기각 — 조인 시퀀스 중 새 전환이 연결 스칼라를 올려도 기존 씬의 유효 보고는 소진된다. 동명 보고의 토큰 없는 모호성은 ADR-0045 수용 한계 유지). Replace 전환 수신 시 연결의 잔여 대기 항목을 소거한다(세계 교체 후에는 보고가 오지 않는 씬들 — 영구 교찰 방지). 마지막 보고의 해제 중 구독자 예외가 보고를 되살리지 않는다(기존 불변식, 리뷰 r1-2). 보류 파괴 드레인은 기존 계약 유지.
4. **조인 시퀀스 재생** — 승인 시점 조인자는 활성 집합 전체를 순서대로 수신: `[base(Replace)] + additive들(Additive) + [진행 중 전환]`. 진행 중 Replace는 이전 세계를 무시하고 새 씬만, 진행 중 Unload는 희생 씬을 제외(파괴된 객체를 조인자가 등록하는 고아 방지). 전 보고 전 게임 데이터 유보 유지.
5. **씬 태그·언로드 파괴** — `RegisterSceneObject(netId, components, sceneName="")`로 항목에 씬 태그. `UnloadScene`은 그 씬의 등록 객체를 먼저 `DestroyObject`(파괴 와이어가 언로드 지시보다 같은 신뢰 스트림에서 선행 — 클라는 살아있는 객체부터 파괴 후 씬 제거), 이후 전환 게이트. 동적 객체는 씬 태그가 없어 언로드 파괴 대상 아님 — 게임이 언로드 전 정리(계약 문서화).
6. **클라 측** — 모드 dispatch: Replace=기존 `LoadScene`(Single)·Additive=`LoadSceneAsync(Additive)`·Unload=`UnloadSceneAsync`. additive 적재는 기존 sceneLoaded 훅(ADR-0025)이 등록+보고하고, 언로드는 드라이버에 `sceneUnloaded` 훅 추가로 보고(wire 12 동일). 언로드 지시 씬이 없으면 즉시 보고(게이트 교착 방지).
7. **서버 본체 씬 적재는 게임 책임(기존 계약 확장)** — Replace와 동일하게 additive/unload에서도 서버 자기 씬 적재/제거는 게임이 한다. catch-up baseline이 ready 보고 시점에 등록된 객체에만 전달되므로, 서버는 전환 시작과 같은 프레임에 적재할 것을 권장.

## 결과 (Consequences)

- **긍정**: 세계 확장 장르(스테이지 추가) 지원. 유실 없는 언로드(파괴 선행 순서). 다중 씬 조인자의 유보·캐치업이 기존 SCN-002/003 계약 위에서 재사용. 기각 대안(전환별 게이트 재작성) 대비 게이트 기계 유지보수 1곳.
- **부담 / 리스크**: 와이어 11 파괴 변경(핸드셰이크로 게이트 — 마이그레이션 참조). 조인 시퀀스 재생은 씬 수만큼 왕복(수용 — 최초 접속 1회). ready 보고 무효 씬 이름은 무시(위조 보고 방어, 회귀 T8). `IsClientSceneReady` 의미가 "현재 전환 보고" → "대기 씬 없음"으로 일반화(전환 밖에서도 true — 더 유용한 진실값).
- **폐기 방법**: 모드 바이트 제거·`PendingScenes`를 bool로 복원·`sceneUnloaded` 훅 제거. 와이어 되돌림은 핸드셰이크 버전 상호 불일치로 안전.

## 마이그레이션

구버전(≤0.23) 클라이언트/서버 혼합 불가 — 프로토콜 버전 핸드셰이크가 거부(VersionMismatch). 전원 0.24.0 동시 갱신. 게임 코드 영향: `LoadScene` 호출부 무변경, additive/unload는 신규 API 선택 사용. `IsClientSceneReady` 시맨틱 변화에 의존한 코드는 없을 것(정보 조회용). 패키지 0.24.0 · 생성기 0.3.0.
