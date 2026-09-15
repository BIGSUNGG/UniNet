# changelog — 변경 기록

의미 있는 모든 변경(기능 추가/수정/제거, 규약, 구조, 하네스)을 기록한다.
형식: 날짜 그룹 아래 `### Added / Changed / Removed / Fixed`. 최신 날짜가 위로 오게 관리한다.

## [2026-09-15]

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
