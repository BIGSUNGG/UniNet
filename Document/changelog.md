# changelog — 변경 기록

의미 있는 모든 변경(기능 추가/수정/제거, 규약, 구조, 하네스)을 기록한다.
형식: 날짜 그룹 아래 `### Added / Changed / Removed / Fixed`. 최신 날짜가 위로 오게 관리한다.

## [2026-09-17]

### Fixed (수명주기 종료 API — RUDP 포트 잔존 바인딩 실패 근본 해소)

- **ServerAsync가 RpcListenHandle을 저장만 하고 Dispose하지 않는 결함 해소** — Play 모드 종료·재진입 시 “RUDP 리스너 바인딩 실패 (0.0.0.0:포트)”가 발생하던 문제
  - **명시적 종료 API 추가** (`UniNetManager`): `ServerStopAsync`(DisposeAsync 기반 리스너 정지 + 환경 정리)·`ServerStop`(동기 — 종료 직전 경로용)·`ClientStop`(HubBase Disconnect/Dispose + 환경 정리)·`HostStopAsync`·`HostStop`. 멱등 — 리슨 중이 아니면 무작동
  - **Sandbox 아레나 부트스트랩 연결** — `OnApplicationQuit`에서 역할별 동기 Stop 호출 → 서버 재시작 시 동일 포트(7777) 재리슨 성공
  - **UniNet.CodeGenerator 0.1.1** — FireAndForget continuation에서 정지·재접속 경로의 잔여 송신 실패(InvalidOperationException “세션이 끊겨…”)를 예외 대신 경고 로그로 처리(정상 종료 노이즈 — 삼키지 않고 수준 낮춤)
  - **테스트**: PlayMode `LifecycleStopTests` 3종 신규(리슨→Stop→동일 포트 재리슨·클라 정지/재접속·호스트 재시작) — PlayMode 7/7×2회·EditMode 20/20 통과. 기존 테스트들도 TearDown에서 Stop 정리해 테스트 간 잔존 리스너 소멸
- **Sandbox 아레나 예시 게임 테스트 강건화** — 치명타 무작위성에 따른 확률적 실패 제거(사망까지 연사), 게임 시간(dt 합산) 기반 대기(에디터 스로틀링 환경의 deltaTime 왜곡 무관), 씬 부트스트랩 간섭 방지(SetUp 비활성화)

### Added (Sandbox 아레나 슈팅 예시 게임 — 구현 기능 전부 활용)

- **Sandbox에 UniNet 구현 기능(P1 전체 + P2 전체)을 활용하는 탑다운 2~4인 슈팅 아레나 예시 게임 구현** (`Sandbox/Assets/Scripts/Arena/` · 씬 `Sandbox/Assets/Scenes/Arena.unity` · 문서 [[examples/arena-shooter]])
  - **MPPM 도입**: `com.unity.multiplayer.playmode` 1.6.3 설치. 토폴로지 — 메인 에디터=서버 / MPPM 가상 플레이어 2=클라이언트, 태그(UniNetServer/UniNetClient/UniNetHost)·인스펙터 강제값 오버라이드 (`ArenaRoleResolver`)
  - **게임 구성**: 에셋 없이 기본 도형·파티클만 — 기둥 4개 아레나, 동적 스폰 플레이어(WASD·마우스 조준·좌클릭 발사·탄약 재생·리스폰), 총알(치명타), 로컬 FX, OnGUI HUD(네임플레이트·킬피드), 탑다운 추적 카메라. 규격은 `ArenaConfig` 단일 진실 공급원
  - **기능 커버리지**: ServerRpc 3종+검증 후크 / ClientRpc(킬피드) / MulticastRpc(FX — 비신뢰·신뢰 혼용) / [Replicated] 조건 4종(None·OwnerOnly 탄약·SkipOwner 조준각·InitialOnly 이름·색·시드) / RepNotify 3종 / 동적 스폰·파괴 / 소유권(IsOwner)·역할(IsServer·IsClient) 게이트
  - **서버 관리**: 접속 수 ↔ 동적 플레이어 수 동기화(스폰·잉여 파괴) — 소유권은 라이브러리 라운드로빈 정책에 위임, 게임은 IsOwner 탐색만 사용. 다른 런타임이 환경을 점유하면 관리에서 물러나는 가드
  - **검증**: PlayMode `ArenaRoundtripTests` (스폰→RPC→리플리케이션→킬플로우→리스폰 단언, 프레임 기반 대기로 스로틀링 환경 강건) — PlayMode 4/4·EditMode 20/20 통과. 2-프로세스 검증기 `ArenaTwoProcessRunner`(사본 2개·batchmode) — 클라 2 접속에서 InitialOnly 전파·SkipOwner 조각(B만 수신)·OwnerOnly 비전파·동적 스폰/파괴·피해 리플리케이션+RepNotify·ClientRpc 킬피드·MulticastRpc FX 전부 관찰 (`[ARENA-2PROC]` HOST-DONE + CLIENT-DONE)
  - **발견·해소**: ① 소유 클라 로컬 입력 핸들러가 프로그램 입력(테스트·검증기)을 덮어쓰는 문제 → `LocalInputEnabled` 게이트 ② 서버 Update가 스폰 트랜스폼을 흡수하지 않아 플레이어가 원점으로 복귀하는 결함 → `InitServerState`에서 복제 좌표 초기화 ③ 프레임 스파이크(dt 클램프)에서 점 거리 명중 판정이 대상을 터널링으로 통과하는 결함 → 이동 선분 스윕 판정으로 교체 ④ batchmode에서 생성 등록(RuntimeInitializeOnLoadMethod) 미실행 → 러너가 명시 등록
  - **알려진 한계 문서화**: 생성된 `_Implementation`에 발신 연결 ID 미전달 — 서버가 RPC 발신자-소유자 일치를 검증할 수 없음(값 범위 검증만). 라이브러리 P1 후속 과제로 examples 문서에 기록

## [2026-09-16]

### Added (2층 식별자 — 다중 NetworkBehaviour 지원)

- **한 게임오브젝트에 여러 NetworkBehaviour가 각자 네트워킹되는 2층 식별자 구현** (ADR-0010 — UE Actor/Component·Mirror componentId 선례 채택)
  - **식별 모델**: 네트워크 엔티티=GameObject 단위 netId 1개(할당 규칙 유지) + NetworkBehaviour별 `SubId`(슬롯 byte). 소유권·파괴·가시성(P3)은 오브젝트 단위, RPC·리플리케이션은 서브 단위. 컴포넌트별 독립 netId는 P3 단위 붕괴로 기각
  - **와이어**: RPC/리플리케이션에 `[subId]` 추가, 스폰 메시지가 `[subCount][{typeKey, stateLen, state}×N]`로 서브 구성 나열(순서 암시·명시 길이 프레이밍) — 포맷 변경 수용(0.x)
  - **등록**: 씬 등록 오브젝트당 1회+컴포넌트 배열(경로 해시 충돌 소멸), `Spawn` 전 컴포넌트 등록(“첫 것만” 제약·경고 삭제), 클라 스폰 시 서브 구성·타입 슬롯별 대조(불일치 진단 후 거부). 다중 컴포넌트 동적 스폰은 RegisterPrefab 필수(기본 팩토리 단일 컴포넌트)
  - **Core**: ServerObjectEntry 서브 테이블(SubObjectEntry — instance·핸들러·typeKey·스냅샷), NetworkClient `object[]` 등록, `Get(netId, subId)` 조회, 캐치업 서브별 전체 상태
  - **Sandbox 예제**: `GadgetCarrier.cs`(Weapon과 같은 오브젝트에 붙는 장비 서브오브젝트 — OwnerOnly 잔량) + Weapon 다중 컴포넌트 프리팩 등록 안내
  - **검증**: 배치 컴파일 녹색(run160) · EditMode 20/20(MultiComponentTests 7종 — subId RPC 라우팅·서브별 델타·슬롯 대조·상한 가드 등) · PlayMode 3/3(MultiComponent 왕복 신규, [UNINET-VERIFY] 3종) · 2-프로세스 [UNINET-2PROC] MULTI-COMPONENT PASS(다중 서브 스폰·서브별 델타 무조건+OwnerOnly·슬롯·파괴)
  - 테스트 함정 2건 발견·해소(ADR-0010 기록): 카탈로그 템플릿의 씬 등록 섞임(접속 후 생성), 검증 대상 파괴 타이밍 경합(파괴 단계 분리)
  - **리뷰 라운드 1 반영 (ISSUES 1건 → 수용)**: SubId byte 상한 255 미검증 — 256+ 컴포넌트에서 byte 루프 랩어라운드 무한 행업·스폰 subCount 무음 절단 위험 → 3층 방어(Spawn 가드·씬 등록 가드×2·AttachSubs 최종 방어 예외) + ADR-0010 결정 5·feature 문서 계약화 + 회귀 테스트(256개 거부·예외) · 재검증 run170-172

### Added (P2 완결 — 동적 스폰/파괴·조건부·InitialOnly)

- **P2 잔여 기능 전부 구현으로 P2 완결** (ADR-0009, 기능 문서 갱신)
  - **동적 스폰/파괴 — 명시적 API**: `UniNetManager.Spawn(instance)`(netId 할당·소유권 배정·전 클라 스폰 전파) / `UniNetManager.NetworkDestroy(instance)`(파괴 전파+로컬 파괴). 서버 netId 증번(씬 해시와 공간 분리), 클라 생성은 `RegisterPrefab<T>` 타입 카탈로그 → 소스젠 팩토리(GameObject+AddComponent) 폴백. 스폰 메시지=[netId][typeKey][변환 7값][전체 상태]. **후발 접속 캐치업** — Welcome→소유권 재배정→기존 오브젝트 일괄 합류(동적=스폰, 씬=전체 상태). 호스트는 서버 인스턴스 클라 등록 재사용(이중 생성 방지)
  - **조건부 리플리케이션 + InitialOnly**: `[Replicated(ReplicateCondition.OwnerOnly, Notify=...)]` — flags enum(OwnerOnly/SkipOwner/InitialOnly 조합). 수신 그룹별(소유자/비소유자) 페이로드 분리 — 조건 필드 없는 타입은 단일 페이로드 재사용(핫패스 불변). InitialOnly는 델타 추적 제외·스폰/캐치업 전체 상태에만 포함. OwnerOnly|SkipOwner 모순 진단 **UNINET010** 신규
  - **호스트 권위 원본 보존**(버그 수정 동반): 리플리케이션 적용 시 IsServer 인스턴스는 값 재기록 스킵(Notify 유지) — [Message] 참조 필드가 역직렬화 사본으로 교체되며 스냅샷 dirty가 매 틱 재발하던 무한 churn 발견·해소(캐치업 전체 상태 적용에서 재현, PlayMode로 검증)
  - **고스트 등록 방어**: 드라이버가 일반 Destroy로 사라진 등록 오브젝트를 감지해 파괴 전파(리소스 소진 방어)
  - **API 정리**: `NetworkClient.RegisterSceneObject` → `Register`/`Unregister` 통일. 시스템 채널에 SendSpawn/SendDestroy 확장. 생성 코드: 조건 마스크 상수·그룹별 델타·WriteFull·스폰 팩토리 등록 방출, `_Validate` 없는 RPC의 async 무경고 제거(CS1998)
  - **Sandbox 예제**: `Scripts/Weapon.cs`·`Projectile.cs` — ServerRpc 발사→Instantiate+Spawn·조건 3종(무조건/OwnerOnly/InitialOnly)·수명 만료 NetworkDestroy·RegisterPrefab 사용법
  - **검증**: 배치 컴파일 녹색(run140) · EditMode 13/13(SpawnConditionTests 7종 신규) · PlayMode 2/2(DynamicSpawnDestroy 신규, [UNINET-VERIFY] 2종) · 2-프로세스 DYNAMIC-SPAWN-DESTROY PASS — 캐치업/브로드캐스트/OwnerOnly 전파·SkipOwner·InitialOnly 미전파/파괴
  - **리뷰 라운드 1 반영 (ISSUES 6건 → 전부 수용)**: 캐치업 씬 전체 상태 WriteFull null NRE 방지(전 필드 조건 제외 타입) · Spawn 무효 호출 시 인스턴스 파괴 제거(ADR 계약과 일치 — 경고 후 무동작) · 프레임당 오브젝트 스냅샷 1회 공유(고스트 스윕×틱 복사 2회 → 1회) · __Destroy_Requested 악성 페이로드 try/catch 격리(Spawn 핸들과 대칭) · 다중 NetworkBehaviour 프리팹 제약 경고+문서화 · 스크래치 정리(.tmp-p*/·GenDump 산출물 gitignore) — 재검증 전량 통과(run140-142 + 2proc)

## [2026-09-15]

### Added (MP 메시지 타입 지원)

- **RPC 매개변수·[Replicated] 필드에 MessageProtocol [Message] 타입 지원** — object 직렬화 경로(MessageId 헤더 디스패치: `MessageSerializer.SerializeToWriter`/`DeserializeFromReader`)로 **Parent/Child 다형성 공식 지원**(부모 선언 파라미터에 자식 인스턴스 전달 → 수신측 자식 캐스팅·자식 필드 온전). MessageKind.NonId는 object 디스패치 불가로 미지원(UNINET002 안내 갱신)
  - 제너레이터: [Message] 타입 감지(파서) + 인코더/디스패치/델타 방출에 object 경로 분기(이미터) — MP 제너레이터와 체이닝 없음(메시지 DTO는 사용자가 직접 작성해 MP 제너레이터가 처리 — 기존 아키텍처 원칙 유지)
  - 사용법 예제: `Assets/Scripts/DamageMsg`·`CriticalHitMsg`(부모/자식) + Player.RpcApplyDamage(K 키 시연)
  - [Replicated] 메시지 필드: dirty 비교는 object.Equals(참조 비교) — 값 동일성 필요 시 Equals 오버라이드, RepNotify는 이전 인스턴스 참조 전달
  - 검증: EditMode 6/6(신규 MessageSupportTests — 다형성 보존·메시지 필드 델타/이전 참조) + PlayMode 호스트 왕복(네트워크 전 경로 — [UNINET-VERIFY] 마커 갱신) + 배치 컴파일 녹색(run53 — 해시 검증 배포 빌드)
  - 상류 무수정(MP 런타임 공개 API로 해결)

### Changed (개발 환경 — sln 브라우징)

- **사용법 예제 폴더 이동** — `Sandbox/Assets/Usage/` → `Sandbox/Assets/Scripts/` (IDE 솔루션에서 브라우징하기 좋은 관례명으로. 어셈블리·netId·동작 무변경 — Assembly-CSharp 소속 그대로). ADR-0007 변경 이력·README·features·plan 경로 동기 갱신
- **IDE 통합 패키지 추가** — com.unity.ide.visualstudio 2.0.22 · com.unity.ide.rider 3.0.31 (기본 스크립트 에디터 Rider 2025.3.3 연동)
- **sln 재생성 진입점 신설** — `Sandbox/Assets/Editor/UniNetSolutionGenerator.cs`: 배치 `-executeMethod UniNet.Editor.UniNetSolutionGenerator.Generate`로 등록 에디터(VS/Rider 공히)의 SyncAll을 호출해 `Sandbox/*.sln`+csproj 7종 생성. sln/csproj는 gitignore 유지(자동 생성물 — ADR-0005 원칙), 로컬 브라우징용
  - 검증: 생성된 csproj에 `Scripts\Player.cs`·`UsageBootstrap.cs` 포함 + UniNet.CodeGenerator가 Analyzer로 등록(Rider/VS에서 생성 코드 인텔리센스 동작), 배치 컴파일 녹색(run41, CS 0건)

## [2026-09-14]

### Added (P1 RPC + Replicate 기본 구현 완료)

- **UniNet 첫 구현 — 사용법 API 전부 실동작** (ADR-0008: 설계·검증 증거 포함, 기능 문서 [[features/monobehaviour-rpc-replicate|monobehaviour-rpc-replicate]])
  - **사용법 재구조화(사용자 승인)**: 소스젠은 메서드 본문 진입을 못 가로채므로 RPC를 `partial` 선언 + `{Name}_Implementation` + 선택 `{Name}_Validate`(Task<bool>, DRPC Validation 패리티) 패턴으로 변경 — ADR-0007 변경 이력 기록
  - **UniNet.CodeGenerator** (`CodeGenerator/`, Roslyn 4.3·netstandard2.0): NetworkBehaviour 스캔 → DRPC 런타임 수동 구성 API로 허브 배선·타입별 partial 구현·리플리케이션 델타 핸들·UNINET0xx 진단 방출. DRPC/MP 제너레이터와 체이닝하지 않음(동일 패스 출력 불가 문제 원천 회피) — 직렬화는 MP `MessageBufferWriter/Reader` 프리미티브 직접 방출. nupkg → `unity-nuget/` → NuGetForUnity(RoslynAnalyzer 라벨)로 Sandbox 설치
  - **런타임**: `UniNet.Core.Hosting`(NetworkServer·NetworkClient·UniNetEnvironment 메인 펌프·UniNetDispatch 전역 디스패치 — 다중 어셈블리 지원·UniNetEndpointOptions — DRPC 옵션 래핑·Fnv1a) + `UniNet.Unity`(NetworkBehaviour netId·IsOwner·UniNetManager Host/Server/ClientAsync·UniNetDriver). netId=씬 경로 해시 무합의 일치, 소유권=라운드로빈 최소 정책, dirty=폴링 비교, 메인 스레드 펌프(Unity 스레드 안전)
  - **상류 무수정**: DRPC 3.5.0 런타임 공개 API로 전부 해결 — DS_RPC·DS_MessageProtocol 수정 불필요 (Orca 오케스트레이션 경로 미발동)
  - **보안·속도**: 연결 키·타임아웃·연결 상한·CRC32c·DTLS 1.2(인증서/핀닝)를 UniNetEndpointOptions로 노출 — 게임 코드의 DRPC·MP 타입 직접 노출 없음 (architecture 의존성 규칙으로 문서화)
  - **검증**: 배치 컴파일 녹색(Sandbox-impl-run9) · EditMode 유닛 4/4(tests-editmode.xml — FNV·옵션 매핑·소유권·델타/RepNotify 이전값) · PlayMode 호스트 왕복 PASS + [UNINET-VERIFY](Sandbox-impl-run21) · **2-프로세스(전용 서버+클라 별도 프로세스) 실제 RUDP 왕복 PASS** [UNINET-2PROC](Sandbox-2proc-*.log) — RPC 3종·검증 후크·소유권·리플리케이션 전 경로
  - 부수: `.pi-lens.json` 신규(CodeGenerator dotnet 프로젝트 분석 제외 — LSP 오판), Sandbox manifest에 com.unity.test-framework 1.4.5 추가
  - **리뷰 라운드 1 반영 (ISSUES 10건 → 전부 수용)**: 생성 심볼명 FullName 살균(`__Req_<Type>_<Method>` — 타입 간 동일 메서드명 충돌 방지 + UNINET009 진단) · ServerRpc 발신자 식별 플러밍(연결별 허브 connId 주입 → `senderConnId` 디스패치 전달) + 신뢰 경계 한계 문서화 · UniNetDispatch doc 정합·충돌 덮어쓰기 추적 · [Replicated] 32필드 상한 진단(UNINET008 — 마스크 uint) · 수신 핸들 try/catch(악성 페이로드 연결 단위 격리) · 틱당 연결 스냅샷 1회 캡처 · 리플리케이션 핸들 기반 타입 체인 탐색 · IUniNetClientSender 파일 분리(타입 1개/파일) · 픽스처 공용 필드 PascalCase · 수명주기 한계 문서화
  - **검증 (최종 코드)**: 컴파일 녹색(run33) · EditMode 4/4(run31) · PlayMode 호스트 왕복(run32) · 2-프로세스 왕복 PASS(ServerRpc/소유권/ClientRpc/Multicast/리플리케이션·RepNotify 이전값) · 접속 직후 첫 전송 레이스 발견·기록(ADR-0008 알려진 한계)

### Added (API 확장 — RepNotify·MulticastRpc)

- **공개 API 2종 확장** (ADR-0007 변경 이력) — 사용법·스텁 확장 후 batch 컴파일 녹색 (error/warning CS 0건)
  - **RepNotify** — `[Replicated(Notify = nameof(OnHpChanged))]`. 클라에서 값이 네트워크로 변경될 때 콜백이 **이전값 1개**를 인자로 호출되고, 현재값은 필드에서 직접 읽음 (사용자 결정). 스텁: `ReplicatedAttribute.Notify` 추가
  - **MulticastRpc** — `[MulticastRpc(Delivery)]`. 서버 → 서버+전 클라 (UE NetMulticastRpc 패리티). 스텁: `MulticastRpcAttribute` 신설
  - 사용법 예제 갱신: `Player.cs` — RepNotify 콜백(`OnHpChanged(int prevHp)`)·서버 권위 흐름(클라 요청 → 서버 판정 → Multicast 전파) 반영

### Added (사용법 우선 API 확정)

- **사용법 우선 개발 + 공개 API 스타일 확정** — 실제 구현 전에 Sandbox에서 사용법 코드를 먼저 확정하고, 이에 맞춰 Package에 컴파일 가능한 API 스텁(시그니처 + `NotImplementedException`)을 두었다. 스파이크 1(소스젠) 블로커와 무관하게 API 계약을 조기 고정. 결정 기록: [[decisions/0007-사용법-우선-api-확정|0007-사용법-우선-api-확정]]
  - **공개 API 스타일 (Mirror/Netcode류)** — `[ServerRpc]` · `[ClientRpc(Delivery)]` · `[Replicated]` · `NetworkBehaviour.IsOwner` · `UniNetManager.HostAsync/ServerAsync/ClientAsync`(정적, Task 반환)
  - **사용법 예제** — `Sandbox/Assets/Usage/` (`Player.cs`: ServerRpc·ClientRpc·Replicated 최소 수직 슬라이스, `UsageBootstrap.cs`: 연결 수명주기)
  - **API 스텁** — `Package/Runtime/UniNet.Core/` 계약 4종(`Delivery` enum: ReliableOrdered·Unreliable 우선 정의, DRPC 5종 매핑은 구현 시 확정 / `ServerRpcAttribute` / `ClientRpcAttribute` / `ReplicatedAttribute`), `Package/Runtime/UniNet.Unity/` 바인딩 2종(`NetworkBehaviour`, `UniNetManager`)
  - 검증: Unity 6000.0.83f1 batchmode 컴파일 녹색 (`error CS` 0건)

### Changed (기반 패키지 최신화)

- **DRPC 3.5.0 라인 업그레이드** — Sandbox 기반 패키지를 전부 최신으로 상향 후 배치 컴파일 녹색 확인 (ADR-0005 변경 이력 기록)
  - DRPC 3.2.0 → **3.5.0** / MessageProtocol 3.0.0 → **3.2.0** / Communication(RUDP) 2.5.1 → **2.7.0**
  - 로컬 feed `unity-nuget/`에 없던 nupkg 보강: DS_RPC에서 런타임 4종(Attribute·Shared·Client·Server) 3.5.0 pack, DS_Communication에서 4종(Shared·RUDP.Shared·Client·Server) 2.7.0 pack — 각 저장소는 이미 릴리스 커밋 상태
  - DRPC.CodeGenerator 3.5.0은 Roslyn 4.3 메인라인으로 `-unity` 재빌드 불필요 (기존 nupkg 사용), MessageProtocol.CodeGenerator 3.2.0은 전이 의존으로 자동 설치
  - 문서 동기화: [[architecture]]·[[plan]] 버전 표기 갱신
  - 부수 정리: 전이 의존(BouncyCastle·LiteNetLib)의 `manuallyInstalled` 플래그 제거로 config 기준 통일, 루트 `.obsidian/`(머신 종속 볼트 상태) gitignore 추가

### Changed

- **리뷰어 품질 게이트 범위 확대** — `AGENTS.md` 품질 게이트 문구를 "의미 있는 코드 변경 후 검토"에서 "**코드나 개발 환경이 수정되었을 때(기능 추가/수정/제거, 리팩토링, 버그 수정, 빌드·설정 변경 등) 구조·보안·속도 측면을 검토**"로 변경
- **README 갱신 규칙 추가** — `AGENTS.md` 문서 우선 원칙에 "기능 추가·수정 시 `README.md`에도 반영(없으면 최초 변경 시 생성)" 규칙 추가

### Added (리뷰 차원 스킬)

- **차원별 리뷰 스킬 3개 신설** — reviewer가 구조·보안·속도를 차원별로 검토할 때 참조하는 기준 문서
  - `.pi/skills/review-structure/` — 레이어링·의존성·SOLID·과잉 추상화(YAGNI)
  - `.pi/skills/review-security/` — 서버 권위·신뢰 경계·RPC/직렬화 안전성·리소스 소진(DoS)
  - `.pi/skills/review-performance/` — hot path GC 압력·할당·잠금·알고리즘 복잡도 (측정 근거 없는 최적화 요구 금지)
- **reviewer 에이전트 검토 기준 확대** — `.pi/agents/reviewer.md` 기준을 정확성 + 구조·보안·속도(스킬 참조, 필수 적용) + 규약 + 문서 동기화(README 포함) + 요청 적합성으로 재구성
- **review-until-clean 스킬 동기화** — 트리거를 "코드·개발 환경 수정"으로 확대, 검토 기준 목록을 차원 3개 + README 반영으로 갱신
- **doc-sync 스킬 동기화** — 갱신 표의 기능 추가·수정 행에 `README.md` 반영(추가 시 신규 생성 포함)을 갱신 대상으로 명시
- **하네스 문서 동기화** — [[harness]] 구성 요소 표에 차원 스킬 3개 행 추가, 리뷰 루프 트리거 문구를 신규 규칙과 일치시킴; 결정 기록: [[decisions/0006-리뷰-품질-게이트-확대|0006-리뷰-품질-게이트-확대]]

### Added

- **개발 환경 구축 완료** (ADR-0005 구조, Phase 0 스캐폴드)
  - Unity **6000.0.83f1**(6.0 LTS) 샌드박스 `/Sandbox` + UniNet 패키지 `Package/`를 `file:../../Package`로 참조 — sln은 Unity 자동 생성
  - asmdef 2종 착수: `UniNet.Core`(no engine references) · `UniNet.Unity`(바인딩) + 배선 확인용 플레이스홀더. batchmode 컴파일 녹색 (UniNet.Core/Unity.dll 생성 확인)
  - NuGetForUnity 4.5.0(OpenUPM 고정) + 로컬 소스 `unity-nuget/`로 기반 패키지 로드: DRPC 3.2.0 전체 세트 · MessageProtocol 3.0.0(+Core) · Communication RUDP 2.5.1 · 전이(BouncyCastle 2.7.0, LiteNetLib 2.1.4)
  - 소스젠 2종(DRPC.CodeGenerator·MessageProtocol.CodeGenerator) RoslynAnalyzer 라벨로 설치 — 스파이크 1 착수 준비 완료
  - 구조 정정: 패키지를 저장소 루트가 아닌 `Package/` 하위로 분리 (Unity가 패키지 폴더 전체를 임포트 — 재귀 오염 실측, ADR-0005 변경 이력)
  - 배치 운영 노하우: `-disable-assembly-updater -nographics`, 전이 의존성 packages.config 명시, slimRestore=false
  - 버전관리 정책: `Sandbox/Assets/Packages` DLL·전체 Unity `.meta`는 커밋(로컬 소스 절대경로 대신 DLL 커밋으로 재복원 최소화), `Library/`·`Temp/` 등 산출물은 배제 (기존 `**/[Pp]ackages/*`·`*.meta` 무시 패턴은 Sandbox 예외로 해소)
  - 참조 버전 확정: DRPC 3.2.0 / MessageProtocol 3.0.0 / Communication(RUDP) 2.5.1 ([[plan]]·[[architecture]] 반영)
- **개발 환경 구조 확정** — Unity 샌드박스 + UPM 로컬 참조
  - UniNet 저장소 자체를 UPM 패키지로 구성(`package.json` + asmdef), 저장소 내 `/Sandbox` Unity 6 프로젝트가 `file:` 경로로 참조
  - sln은 Unity가 자동 생성, 코어 테스트는 Unity Test Framework EditMode로 시작 — dotnet 이중 파이프라인은 필요 시 재검토
  - 결정 기록: [[decisions/0005-개발-환경-샌드박스-upm|0005-개발-환경-샌드박스-upm]]

## [2026-09-13]

### Added

- **프로젝트 정의·스코프 확정** — 상용 유니티 게임 서버용 네트워크 프레임워크 라이브러리로 정의. 원칙 “사용은 간단하게, 기능은 강력하게”
  - 기능 목표: MonoBehaviour 기준 RPC / 변수 자동 Replicate / UE Network Framework 패리티
  - 기반 스택: MessageProtocol + DRPC(Communication 간접) 적극 재사용, 최소 Unity 6, Unity 서버 빌드(서버 권위)
  - 신규 문서: [[overview]] 전면 갱신, [[architecture]] 초기 설계안, [[roadmap]] UE 패리티 매트릭스(P1~P4)
  - 결정 기록: [[decisions/0002-기반-스택과-스코프-확정|0002-기반-스택과-스코프-확정]]

### Added (하네스)

- **하네스 엔지니어링 도입** — AI 협업 규칙(문서 우선, 능동 질문, OOP/SOLID, 요청 비판적 검토)을 pi 하네스에 구성
  - `Document/` Obsidian Vault (사람+AI 공동 참조, 변경 시 갱신 의무)
  - `AGENTS.md` — 핵심 4원칙과 워크플로 (모든 세션에 자동 로드)
  - `.pi/skills/doc-sync` — 문서 갱신 절차 스킬
  - `.pi/skills/review-until-clean` — 리뷰어 Clean-판정 루프 스킬
  - `.pi/extensions/doc-guard.ts` — 코드 변경 시 문서 미갱신 감지·재촉 훅
  - `.pi/agents/reviewer.md` — ISSUES/CLEAN 판정 리뷰어 (프로젝트 스코프)
  - 자세한 내용: [[harness]], [[decisions/0001-하네스-엔지니어링-도입|0001-하네스-엔지니어링-도입]]

### Added (구현 플랜)

- **구현 진행 플랜 확정** — 얇은 수직 슬라이스 원칙, Phase 0(스파이크)→P1(RPC)→P2(Replicate) 순서, 완료 정의, 설계 난점 3건. [[plan]]
  - ADR-0003: 기반 스택 **패키지 참조 고정** (NuGet + UPM)
  - ADR-0004: MonoBehaviour RPC 계약 **자동 생성** (UniNet 자체 소스젠, 수동 폴백 유지)
