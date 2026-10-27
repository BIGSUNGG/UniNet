# 00-INDEX — UniNet 문서 지도

> **AI 세션 시작 규칙**: 작업 시작 전 이 문서와 [[overview]]를 먼저 읽는다.
> 작업 영역과 관련된 문서를 아래에서 찾아 읽은 후 시작한다. 문서 갱신은 `doc-sync` 스킬을 따른다.
> **사람 참고 규칙**: Obsidian으로 이 폴더(`Document/`)를 Vault로 열어 사용한다.

## 핵심 문서

| 문서 | 내용 | 갱신 시점 |
| --- | --- | --- |
| [[overview]] | 프로젝트 정의·목표·기술 스택 | 방향이 바뀔 때 |
| [[architecture]] | 시스템 구조·모듈·의존성 규칙 | 구조가 바뀔 때 |
| [[conventions]] | 코딩 규약 (OOP/SOLID, 네이밍, 문서 규약) | 규약이 정해지거나 바뀔 때 |
| [[roadmap]] | UE Network Framework 패리티 매트릭스·단계 | 기능 상태가 바뀔 때 |
| [[plan]] | 구현 진행 플랜 (Phase 0~4·완료 정의·난점) | 단계 진입·완료 시 |
| [[changelog]] | 모든 변경의 기록 (기능 단위) | 모든 의미 있는 변경 시 |
| [[harness]] | AI 하네스 구성 (AGENTS.md, 스킬, 훅, 에이전트) | 하네스를 바꿀 때 |

## 영역별 문서

### features/ — 기능 문서

- [[features/monobehaviour-rpc-replicate|monobehaviour-rpc-replicate]] — MonoBehaviour RPC 3종 + [Replicated]/RepNotify + 동적 스폰/파괴·조건부 (P1 전체 + P2 전체, 구현됨)
- [[features/connection-lifecycle|connection-lifecycle]] — 연결 수명주기 이벤트: 서버 접속/해제 이벤트·클라 해제 콜백·상태 조회 (구현됨)
- [[features/replication-p3-policy|replication-p3-policy]] — 리플리케이션 고급 정책: 가시성·우선순위·휴면·전송 주기·채널 예산 (P3, 구현됨)
- [[features/replication-batching-delivery|replication-batching-delivery]] — 복제 배칭과 오브젝트별 전송 신뢰성: 틱별 신뢰성별 배치(wire 13)·Unreliable 스트림·정지 재전송 (구현됨 — ADR-0039)
- [[features/replication-p4-hooks|replication-p4-hooks]] — P4 훅: UniNetTime·SnapshotBuffer·PositionHistory 리와인드·그리드 가시성 (구현됨)
- [[features/movement-prediction|movement-prediction]] — 이동 예측: 게임 정의 입력·상태 구조체·고정 스텝 CSP&R·점프/대시/구르기/텔레포트 (구현됨 — ADR-0013·0037·0038)
- [[features/custom-netserialize|custom-netserialize]] — 커스텀 필드 직렬화: [Replicated(Serializer)] 정적 Write/Read 계약·양자화 (구현됨 — 제너레이터 0.1.9, ADR-0020)
- [[features/network-statistics|network-statistics]] — 네트워크 통계: 트래픽 카운터(페이로드 바이트)·RTT 프로브·큐 깊이 (구현됨 — 제너레이터 0.2.4, ADR-0029)
- [[features/fastarray-delta|fastarray-delta]] — 배열/리스트 요소 델타 동기화: 옵 인코딩·섀도 diff (구현됨 — 제너레이터 0.2.1, ADR-0020)

### examples/ — 예시 게임 문서

- [[examples/arena-shooter|arena-shooter]] — Sandbox 아레나 슈팅 예시 게임 (구현 기능 전부 활용·MPPM 실행 가이드·기능 매트릭스·2-프로세스 검증, 구현됨)

### research/ — 비교·조사 문서

- [[research/unity-server-functional-review|unity-server-functional-review]] — v0.22.0 최신 기능 검토: 승인 경계·유실 복구·컬렉션 RPC 등의 격리 반례, Mirror/FishNet/NGO/Fusion 공식 본문 비교와 장르별 우선순위 (2026-10-23 작성, **FR-01~07은 2026-10-26 v0.23.0에서 해결 — [[research/v0-23-stabilization-review]]·[[migration-v0-23]] 참조**)
- [[research/v0-23-stabilization-review|v0-23-stabilization-review]] — v0.23.0 안정화 구현·검증 종합 리뷰: FR-01~07 각 수정·실행 증거·구조/보안/성능 검토·잔여 한계 (2026-10-26, 부모 직접 검토)
- [[research/commercial-netcode-audit|commercial-netcode-audit]] — 기존 상용 넷코드 내부 감사·런타임 44개 파일 대응표·기능 문제 대장 (2026-10-22 기존 항목 CLOSED 기준점). 후속 발견 사항은 위 기능 검토 참조. 기능별 원시 증거·검증·배포 연결은 `research/commercial-netcode-evidence.json`
- [[research/network-framework-comparison|network-framework-comparison]] — UE NF·Photon Fusion 2·NGO·N4E·FishNet·Mirror 비교 역사 자료 (2026-09 조사, 현재 기능 판정에는 위 2026-10-23 검토 우선)

### upstream-blockers — 상류 의존 과제

- [[upstream-blockers]] — DRPC/MessageProtocol/Communication 저장소 수정이 필요한 잔여 과제 (2026-09-24: 직렬화 2종의 UniNet 단독 구현 전환으로 폐기 완료 — ADR-0020 참조. 잔여 의존 과제 없음)

### decisions/ — 아키텍처 결정 기록 (ADR)

- [[0001-하네스-엔지니어링-도입]] — AI 협업 하네스와 문서 규칙 도입
- [[0002-기반-스택과-스코프-확정]] — 상용 유니티 게임 서버 라이브러리로 정의, DRPC/MessageProtocol 재사용, Unity 6, Unity 서버 빌드
- [[0003-패키지-참조-고정]] — 기반 스택을 NuGet+UPM 패키지 참조로 고정
- [[0004-계약-자동-생성]] — MonoBehaviour RPC 계약 자동 생성 (UniNet 소스젠, 수동 폴백)
- [[0005-개발-환경-샌드박스-upm]] — 개발 환경 확정: Unity 샌드박스 + UPM 로컬 참조 (저장소 내 `/Sandbox`)
- [[0006-리뷰-품질-게이트-확대]] — 리뷰 품질 게이트 확대: 트리거 확대·차원 스킬 3종(구조/보안/속도)·README 갱신 규칙
- [[0007-사용법-우선-api-확정]] — 사용법 우선 개발 + 공개 API 스타일 확정 (Mirror/Netcode류 속성; 변경 이력 — partial 재구조화로 ADR-0008 승계)
- [[0008-구현-아키텍처]] — P1 RPC + Replicate 기본 구현: 소스젠(로슬린 4.3) 직접 배선·다중 어셈블리·씬경로 netId·라운드로빈 소유권·검증 증거
- [[0009-동적-스폰-조건부-리플리케이션]] — P2 완결: 명시적 Spawn/NetworkDestroy·타입 카탈로그·캐치업·ReplicateCondition(OwnerOnly/SkipOwner/InitialOnly)·호스트 권위 원본 보존
- [[0010-2층-식별자-다중-컴포넌트]] — 다중 NetworkBehaviour 지원: GameObject 단위 netId + SubId 슬롯·다중 서브 스폰·슬롯 대조
- [[0011-P3-리플리케이션-고급-정책]] — P3 완결: IUniNetReplicationPolicy·틱 이중 모드·가시성 계약·기아 보정 우선순위·채널 예산·휴면 2상태·MP 의존 2종 P4 이관
- [[0012-P4-훅-시간동기화-인터폴레이션-리와인드-그리드]] — P4 완결: UniNetTime(TimeSync 와이어 5번)·SnapshotBuffer·PositionHistory 리와인드·그리드 가시성·아레나 예측 전환
- [[0013-NetworkTransform-컴포넌트]] — NetworkTransform 컴포넌트: 이동 예측의 라이브러리 승격(조합 채택 — 제너레이터 리프 스캔 한계)·MovementRule 주입·제너레이터 겹리(0.1.2)
- [[0014-스폰-서브-자동-복원]] — 멀티 컴포넌트 스폰 슬롯 불일치 근본 해결: 클라 서브 자동 복원(typeKeys)·RegisterPrefab 필수 → 선택 완화
- [[0015-연결-수명주기-이벤트-게이트웨이]] — 연결 수명주기 이벤트: 게임 콜백 위임 채택·발화 순서 계약(재배정 전/캐치업 후)·구독자 격리
- [[0016-ServerRpc-소유자-자동-강제]] — ServerRpc 발신자-소유자 자동 대조(보안 기본값)·RequireOwnership 옵트아웃·마이그레이션 노트
- [[0017-동적-스폰-NetworkInstantiate-통합]] — Spawn(등록 전용) 제거·NetworkInstantiate(복제 겸함) 단독 API·configure 콜백(비직렬화 InitialOnly 초기화)
- [[0018-ServerRpc-Validate-옵트인]] — `_Validate` 자동 감지 제거·`[ServerRpc(Validate = true)]` 옵트인·불일치 진단(UNINET011 에러·UNINET012 경고)·마이그레이션 노트
- [[0019-코드-주석-영어화-사용자-관점-규약]] — 모든 코드 주석 영어 + 라이브러리 사용자 관점 XML doc·why 중심 인라인 규약 (문자열 리터럴은 대상 아님)
- [[0020-직렬화-배열-델타-유니넷-단독-구현]] — 직렬화 2종(커스텀 NetSerialize·FastArray)의 UniNet 단독 구현 전환 — "MP 수정 전제" 기존 판정 철회 (승인됨 — 2026-09-24 구현 완료)
- [[0021-DLL-캡슐-UPM-전환]] — 기반 스택 DLL·소스젠 패키지 동봉(개발=배포 형태 통일, NuGetForUnity 철거) + Git URL 배포 확정
- [[0022-사용자-가시-변경-skills-동기화-규칙]] — 사용자 가시 변경 시 `skills/uninet/SKILL.md` 같은 세션 반영 의무화 (적용 지점 AGENTS.md 한 곳)
- [[0023-씬-netId-직렬화-안정-아이디]] — 씬 오브젝트 netId를 에디트 타임 GlobalObjectId 해시 직렬화 각인으로 교체 — 이름·순서·계층 변경·DontDestroyOnLoad·빌드 버전 차이에서 양단 일치 (경로 해시는 폴백)
- [[0024-Fusion식-spawnable-프리팹-스폰-시스템]] — NetworkInstantiate 전면 교체: NetworkSpawnable 마킹·프리팹 GUID 해시 각인·베이크 테이블·클라 동일 프리팹 인스턴스화 (RegisterPrefab·기본 팩토리·자동 복원 폐기)
- [[0025-접속-후-씬-로드-재등록]] — 접속 후 로드되는 씬의 오브젝트 자동 재등록: 드라이버 sceneLoaded 훅·멱등 스킵·QueueOnMain 지연 실행(리슨서버 교착 수리 포함)
- [[0026-그리드-가시성-cull0-계약-정합]] — 그리드 가시성 cull=0 항상 관련 계약 복원: 그리드 멤버십 판정 cull>0 한정·Arena 뷰어 주입 제거
- [[0027-킥-연결-종료-사유]] — 서버 강제 퇴장 DisconnectClient·와이어 시스템 메시지 6 사유 전달·클라 ClientDisconnected 사유 인자(Kicked/ServerShutdown/ConnectionLost)
- [[0028-프로토콜-빌드-버전-핸드셰이크]] — 프로토콜 버전 선언-비교(와이어 7)·Welcome 서버 버전 필드·불일치 VersionMismatch 거부
- [[0029-네트워크-통계-API]] — 트래픽 카운터(페이로드 바이트 계약)·자체 RTT 프로브(와이어 8/9)·큐 깊이 관측
- [[0030-명시적-소유권-이전-API]] — SetOwner(netId, connId) 소유권 이전(전 연결 OwnerUpdate 전파·0=방치후 입양)
- [[0031-플레이어-오브젝트-연결-매핑]] — connId↔플레이어 오브젝트 매핑(SetPlayerObject/GetPlayerObject)·퇴장 일괄 정리 DestroyObjectsOwnedBy
- [[0032-커넥션별-ServerRpc-레이트리밋]] — ServerRpcRateLimitPerSecond 고정 창 게이트·초과 드롭+집계·플러드 방어
- [[0033-하트비트-타임아웃-튜닝-노출]] — UniNetEndpointOptions.DisconnectTimeoutMs(상류 DS_RPC 3.6.1 전달 추가·기반 스택 3.6.x 갱신)
- [[0034-재접속-세션-보존-재부착]] — SessionGraceSeconds 유예 세션·Welcome 토큰·ResumeRequest(와이어 10) 재부착·ReconnectAsync
- [[0035-씬-전환-동기화]] — LoadScene 브로드캐스트(와이어 11)·클라 보고(와이어 12)·AllClientsSceneLoaded 준비 게이트
- [[0036-에디터-디버깅-툴]] — UniNetDebuggerWindow 연결·RTT·통계·오브젝트·소유자 표시
- [[0037-예측-full-CSPnR-내장]] — NetworkTransform.FullReconciliation(옵트인) 롤백+재시뮬 예측 리컨사일
- [[0038-상태-기반-이동-예측-일반화]] — MoveSimulation<TInput,TState> 게임 정의 입력·상태·고정 스텝·상태 에코 롤백(무상태 계약 대체)
- [[0039-복제-배칭과-신뢰성-분리]] — 틱별 신뢰성별 배치(wire 13)·오브젝트별 전송 신뢰성(Unreliable=Sequenced)·정지 재전송·휴면 플러시 신뢰 강제
- [[0040-복제-주기-기본값-30Hz]] — NetworkUpdateFrequencyHz 기본 0(무제한)→30. 0 설정으로 매 틱 옵트인
- [[0041-세션-토큰-암호학적-난수-전환]] — SEC-001: 세션 토큰을 256비트 암호학적 난수 opaque Base64로 전환(와이어 1/10 string화·파싱 전 상한·재개 3조건 소비·추측/오류로 세션 미소진·비밀 미출력)
- [[0042-이동-소유권-전이-스트림-리셋-에포크]] — MOV-001: NetworkClient.OwnershipChanged 값변경 이벤트·MoveSimulation.ResetOwnershipStream(TState 보존)·이동 소유권 epoch(봉투 접두·불일치 기각)로 소유권 전이 후 새 스트림 즉시 채택 (0.5.0)
- [[0043-연결-승인-수명주기-버전-선언-전-게임-데이터-유보]] — CON-001: 버전 선언 수용 전 소유권·캐치업·복제·게임 RPC 유보, 승인 시 기존 순서로 해제, 미선언 타임아웃 킥 (0.6.0, 제너레이터 0.2.9)
- [[0044-재개-결과-통보와-자동-재시도]] — REC-001: ResumeSession 결과 열거형·wire 14 재개 결과 통보·소비/만료 토큰 툼스톤·SessionNotFound 자동 재시도(0.5s 간격) (0.7.0, 제너레이터 0.2.10)
- [[0045-씬-전환-잔여-현재-씬-전달-레디-보고-전-캐치업-유보-낡은-보고-차단]] — SCN-001/002/003: 현재 씬 추적·레디 보고 전 캐치업 유보·낡은 보고 차단 (0.8.0, 와이어 불변·생성기 0.2.11 세대 캡처)
- [[0046-자발-퇴장-통보-와이어15-IntentionalLeave-유예-세션-즉시-폐기]] — REC-002: 시스템 와이어 15 `IntentionalLeave`(C→S) 신설 — ClientStop이 close 직전 통보, 서버는 유예 저장 생략+토큰 툼스톤+즉시 재배정. ClientStop 유계 플러시 창(전송 Task 250ms+드레인 150ms). 구 클라 호환 (0.9.0)
- [[0047-게임-접속-승인-콜백-와이어7-자격증명]] — CON-002: 와이어 7 페이로드 확장 `[version][credential]`(조건부 읽기로 구 클라 호환)·`UniNetEndpointOptions.ConnectionApprover/ConnectionCredential`·`UniNetDisconnectReason.ApprovalRejected = 6`·콜백 대기 중 CON-001 유예 상태 재사용·`VersionDeclareTimeoutMs` 총예산 재사용 (0.10.0)
- [[0048-소유권-이전-조건-필드-baseline-재전송]] — OWN-002: 소유권 이전 3경로(SetOwner/ResumeSession/ReassignOwnership)에 상태 전용 baseline 재전송(신규 owner isOwner=true·직전 owner false·관련성 게이트·즉시 캐치업 억제) — 신규 owner의 OwnerOnly/InitialOnly 누락과 이전 owner의 SkipOwner 기본값 고착 해소, RepNotify 올바른 prev 발화 (0.11.0)

### deployment — 배포

- [[deployment]] — UPM Git URL 배포 절차(태그→푸시)·소비자 설치·기반 스택 갱신 스크립트·신규 프로젝트 검증

### _templates/ — 문서 템플릿

- `_templates/feature.md` — 기능 문서 템플릿
- `_templates/adr.md` — ADR 템플릿

## 최근 변경 (자세한 것은 [[changelog]])

- 2026-10-26 — **v0.23.0 FR-01~07 안정화** (FR 검토 7건 전부 해결·배포 준비 — 태그/릴리스 제외): 승인/씬 준비 전 게임 RPC 즉시 드롭(`CanReceiveGameData`), Unreliable 이동 중 1.0s 주기 기준선·AOI 재진입 기존 인스턴스 갱신, RPC 컬렉션 코덱 경계(수신 List 복원·65535 초과 명시 거부·미소비 검증), 위치 무관 권위 TState 리컨사일(`ReconcileState` DIM), 연기 큐 최신 전체 병합·상한 강제 송신·메인 큐 상한, 생성기 소스 빌드 복구·headless 회귀 CI 게이트(0.2.18). EditMode 연속 2회 310/0/1·PlayMode 47/47·headless 양경로 19/19·사용자 자산 61파일 무변경. [[migration-v0-23]]·[[research/v0-23-stabilization-review]] 신규. 구현은 pi-crew executor(glm-5.3)+탐색/reviewer(glm-5.3-flash) 4단계·중간 reviewer CLEAN 4회.
- 2026-10-26 — **Unity 작업 수단 일원화 — Unity CLI** (ADR-0055·[[harness]] "Unity 작업 수단" 절 신설. Sandbox·Builds 2Proc의 에디터 연동 패키지 매니페스트·잠금·캐시와 pi 하네스의 연동 서버 등록 제거. Unity 작업은 배치 모드 CLI(`-runTests`·`run2proc.sh`·`UniNetSolutionGenerator`)로 수행·증거는 종료코드·로그·NUnit XML. `AGENTS.md` 프로젝트 사실에도 반영)
- 2026-10-23 — **상용 Unity 서버용 기능 재검토**: v0.22.0 코드·동봉 생성기·격리 실행에서 승인 전 게임 RPC 송신·유실 필드 미복구·RPC List/길이 경계·예산 적체를 확인하고, 정적 재진입/상태 리컨사일 경로·생성기 소스 빌드 오류를 구분해 기록. 4개 프레임워크 공식 본문 17개로 추가 비교. [[research/unity-server-functional-review]] — 구현 변경·결함 해결·신규 배포 아님.
- 2026-10-21 — **OWN-002 v0.11.0 해결 — 소유권 이전 시 조건 필드 baseline 재전송** (ADR-0048·재현 확정: 이전 3경로가 OwnerUpdate만 전파해 신규 owner는 비변경 OwnerOnly/InitialOnly를 영구 기본값으로 보고 이전 owner도 SkipOwner 기본값 고착 — 감사 항목이 가정하지 않은 추가 피해. `SendOwnershipBaseline` 상태 전용 baseline(스폰 재전송 없음·지연 델타 무효화 선행·관련성 게이트·즉시 캐치업 연결 억제)을 3경로에 적용. 와이어·생성기 무변경. **검증·배포 완료**: EditMode 230/230(신규 9종 — Notify·SkipOwner·InitialOnly·예산 연기 무효화·ResumeSession·입양 경계·동적 스폰 미재전송 등)·PlayMode 36/36(실루프백 재입양 baseline) 2회 연속·음성 대조·리뷰 r13 CLEAN(zai/glm-5.3-flash)·커밋 `fcd62aa` → [v0.11.0 Release](https://github.com/BIGSUNGG/UniNet/releases/tag/v0.11.0)·CI 성공·신규 설치 PASS — OWN-002 CLOSED, 지연 큐 프라이버시 오탐 메모 확인 처리. 리뷰 r13(대장 선점 기재 등 5건 지적)→r14 CLEAN)
- 2026-10-21 — **CON-002 v0.10.0 구현 — 게임 접속 승인 콜백 확장점** (ADR-0047·[[features/connection-lifecycle]] 갱신. 클라 옵션 `ConnectionCredential`이 와이어 7 선언에 자격 증명 동봉(페이로드 `[version][credential]` 확장 — 구 클라 잔여 0 호환·상한 4096바이트 파싱 전 폐기), 서버 옵션 `ConnectionApprover`(`Task<bool>` 콜백, 메인 시작·비동기 검증)이 승인/거부를 게임에 위임. 거부는 기존 킥 경로+`ApprovalRejected = 6`(열거값 명시·재번호 없음 — 비파괴). 콜백 대기 중 CON-001 유예 상태 재사용(게임 데이터 전면 유보·RPC 게이트), `VersionDeclareTimeoutMs` 총예산 스윕이 hang 콜백 마감. 재접속 재선언도 콜백 1회 통과. 생성기 0.2.13. **검증·배포 완료**: EditMode 221/221(신규 11종)·PlayMode 35/35(신규 3종)·실RUDP 승인/거부/초과페이로드 PASS·리뷰 r11(ISSUES — Core 디버그 트레이스 잔존 등)→r12 CLEAN(zai/glm-5.3-flash)·커밋 `ab82e10` → [v0.10.0 Release](https://github.com/BIGSUNGG/UniNet/releases/tag/v0.10.0)·CI 성공(첫 태그는 Package/CHANGELOG 0.10.0 누락으로 실패 — 섹션 추가 후 재태그 `2c3095c`)·신규 설치 PASS — CON-002 CLOSED)
- 2026-10-21 — **REC-002 v0.9.0 구현 — 자발 퇴장 통보와 유예 즉시 폐기** (ADR-0046·[[features/connection-lifecycle]] 갱신. 자발적 `ClientStop`은 재개 토큰을 소각하므로 유예 저장은 고아 세션만 남겼다 — ClientStop이 close 직전 시스템 **와이어 15 `IntentionalLeave`(C→S)**를 발사하고 서버는 유예 저장 생략+토큰 툼스톤(`UnknownToken`)+소유권 즉시 재배정으로 후발 클라의 즉시 입양을 허용. 전달 신뢰성: 전송 Task 250ms 유계 대기+150ms 드레인 창(사용자 승인 계약 — 미적용 시 실루프백 간헐 유실 관측). 구 클라 호환(미통보=기존 유예). 생성기 0.2.12. **검증·배포 완료**: EditMode 210/210·PlayMode 32/32·실루프백 3/3 연속·Arena 진단 PASS·음성 대조 RED 확인·리뷰 r9→r10 CLEAN(zai/glm-5.3-flash)·커밋 `87a3467` → [v0.9.0 Release](https://github.com/BIGSUNGG/UniNet/releases/tag/v0.9.0)·CI 성공·신규 설치 PASS — REC-002 CLOSED)

- 2026-10-20 — **SCN 리뷰 5: 유보된 파괴 전달 복구** — 승인된 씬 유보 연결의 파괴 ID를 보관, 등록 후 유효 ready에서 여전히 부재한 ID만 소유권/캐치업 기준선 전에 1회 전달. 같은 netId 재등록은 캐치업으로 대체, disconnect는 메인 큐에서 폐기. EditMode +3케이스(총 34, 부모 Unity 34/34 EditMode·1/1 PlayMode PASS)·순차 dotnet 빌드/패키지 검증·headless 5+기존 22종/음성 대조 PASS. 필수 독립 reviewer CLEAN·배포는 미완료, 커밋/릴리스 없음. [[features/connection-lifecycle]]·[[research/commercial-netcode-audit]] 참조.

- 2026-10-20 — **SCN-001/002/003 v0.8.0 구현 — 씬 전환 잔여** (ADR-0045·[[features/connection-lifecycle]] 갱신. `_currentScene` 추적으로 완료된 전환의 씬이 후발 접속자에게 전달, `AwaitingSceneReady`로 보고 전 게임 데이터 유보(전체상태 폐기 창 소멸·유보된 attach 꼬리는 보고 시 해제·CON-001 2단계 순서), `SceneLoadSent`+전환 세대 캡처로 송신 전 도착한 낡은 보고만 거부(송신 뒤 도착한 이전 동명 보고는 ADR-0045 수용 한계). 와이어 불변·생성기 0.2.11(디스패치 수신 시점 세대 캡처, 신규 공개 API 2종 — 구 생성 코드 2인자 오버로드 호환). EditMode +7종 및 리뷰 후속 3종(값 적용 재생 단언 포함)·PlayMode 실RUDP 1종. 리뷰 5 후속 Unity `SceneTransitionTests` 34/34 EditMode·`CurrentSceneJoinPlayTests` 1/1 PlayMode PASS. **2026-10-21 마감**: 독립 리뷰 r6~r8 CLEAN(zai/glm-5.3-flash), 리뷰 6에서 발견한 PlayMode 스위트 결함 정비(TestPortAllocator 포트 플레이키 근본 해결 + LateScene 누수 정리), 전체 EditMode 202/202·PlayMode 31/31 2회 연속 PASS, 커밋 `1de0a40` → [v0.8.0 Release](https://github.com/BIGSUNGG/UniNet/releases/tag/v0.8.0)·CI 성공, 신규 설치 PASS — SCN-001/002/003 CLOSED)

- 2026-10-20 — **REC-001 v0.7.0 구현 — 재개 결과 통보와 자동 재시도** (ADR-0044·[[features/connection-lifecycle]] 갱신. `ResumeSession`이 `GraceResumeResult` 열거형 반환(파괴), wire 14로 모든 종결 사유를 요청 연결에 통보(ok/reason/retryableRemainingMs), 소비·스윕만료 토큰 툼스톤으로 이중 소비 패자 UnknownToken·만료 Expired 정확 종결, 클라 `LastResumeResult`+`ResumeResultReceived` 이벤트, SessionNotFound 디태치 경합 자동 재시도(0.5s 간격·창 마감까지 — 경합이 재부착으로 수敛). 제너레이터 0.2.10(해시 검증 배포). **검증·배포 완료**: 회귀 4종·타깝 12/12·EditMode 167/167·PlayMode 30/30·실RUDP·신규 설치·Flash r1 CLEAN·v0.7.0 Release·main/tag CI 성공 — 구현 `206fecf`, REC-001 CLOSED. 검증 중 결함 4건(툼스톤 분기순서·스윕 툼스톤 수명0·DIM override·테스트 전제/구독 인스턴스) 수정·이력 보존)

- 2026-10-20 — **CON-001 v0.6.0 구현 — 연결 승인 수명주기** (ADR-0043·[[features/connection-lifecycle]] 갱신. 미선언 연결은 Welcome만 받고 승인(와이어 7 수용)까지 게임 데이터 전면 유보 — 소유권 재배정·캐치업·복제·스폰·ClientConnected·씬 게이트·생성 허브 게임 ServerRpc 게이트(IsVersionApproved·UnapprovedServerRpcDrops 카운터, 제너레이터 0.2.9). 승인 시 기존 순서로 1회 해제(멱등), 불일치 기존 킥 유지, 미선언 타임아웃 VersionDeclareTimeoutMs(기본 10s·0=비활성) 스윕 킥. 빈 기대버전은 현행 완전 호환. EditMode 7종+기존 100 attach 지점 선언 정합화·PlayMode 실RUDP 1종. **검증·배포 완료**: 회귀 8종(스레드 안전 포함)·타깝 8/8·EditMode 163/163·PlayMode 29/29·실경로 재현·신규 설치·Flash r2 CLEAN·v0.6.0 Release·main/tag CI 성공 — 구현 `c2d4c8e`, CON-001 CLOSED. 검증 중 발견된 회귀 2건(accept 스레드 Unity API 호출로 attach 전면 중단·생성기 DLL 미배포)은 수정·원시 이력 보존)

- 2026-10-20 — **MOV-001 v0.5.0 구현 — 이동 소유권 전이 스트림 리셋·epoch** (ADR-0042·[[features/movement-prediction]] 갱신. `NetworkClient.OwnershipChanged` 값변경 이벤트(디스패치 스레드→QueueOnMain)·`MoveSimulation.ResetOwnershipStream`(권위 TState 보존)·`NetworkTransform` 이동 epoch([Replicated(OwnerOnly)]·봉투 `[uint epoch][int len][byte[]]` 접두·RPC 시그니처·시스템 메시지 불변·생성기 재빌드 불필요)·불일치 조용 기각+`MoveEpochMismatches` 카운터·클라 양경로 재바인딩·호스트 이중 리셋 가드. 파괴 변경 2종(인터페이스 메서드·봉투 레이아웃). **검증·배포 완료**: 회귀 9종·타깃 28/28·EditMode 155/155·PlayMode 28/28·Arena 실RUDP 사이클·신규 설치·Flash r2 CLEAN·v0.5.0 Release·main/tag CI 성공 — 구현 `3c3b4df`, MOV-001 CLOSED)

- 2026-10-20 — **OWN-001 v0.4.1 개별 배포 완료** (캐치업/가시성 재진입/초기 스폰의 owner-before-state·0 포함·와이어 무변경. 회귀 8/8·EditMode 146/146·PlayMode 27/27·Arena RUDP·신규 로컬 Git 설치 PASS, 최종 Flash CLEAN·main/tag CI 성공. 구현 `380b2b4`, 증거대장 연결·OWN-001 CLOSED)
- 2026-10-20 — **SEC-001 v0.4.0 개별 배포 완료** (구현 `ca13ee7`·소스/문서 CLEAN·신규 로컬 Git 설치 PASS·원격 태그/Release·main/tag CI 성공 확인. 증거대장 연결, SEC-001 CLOSED. 상용 전체 점검은 계속 진행 중)
- 2026-10-19 — **세션 토큰 암호학적 난수 전환** (SEC-001 — ADR-0041·패키지 0.4.0 파괴적. 토큰 `ulong`→opaque Base64 44자·재개 3조건 소비·추측/오류 미소진·파싱 전 상한·디버거 마스킹. 재개 회귀 10/10·EditMode 138/138·PlayMode 27/27·Arena 진단 PASS·소스/문서 리뷰 CLEAN. 개별 배포 확인 전 OPEN 유지)
- 2026-10-19 — **상용 넷코드 내부 점검 대장** ([[research/commercial-netcode-audit]] — 런타임 44파일 대응표·기능 문제 27건·증거대장 evidence.json. 진행 중)

- 2026-10-13 — **복제 주기 기본값 30Hz** (ADR-0040 — NetworkUpdateFrequencyHz 기본 0→30, 0=옵트인. 기존 UpdateFrequencyTests 갱신+기본값 테스트 추가. EditMode 132/132)
- 2026-10-13 — **복제 배칭과 신뢰성 분리** (ADR-0039 — 틱별 신뢰성별 배치 wire 13·오브젝트별 Delivery(Unreliable=Sequenced)·정지 재전송·휴면 플러시 신뢰 강제. Arena 30Hz. UniNet 0.3.0(와이어 변경). EditMode 131/131·PlayMode 27/27)

- 2026-10-13 — **상태 기반 이동 예측 일반화** (ADR-0038 — MoveSimulation<TInput,TState> 게임 정의 입력·상태 구조체·고정 스텝 시뮬·상태 에코 롤백. 점프·대시·텔레포트 Arena 실구현. 무상태 MovementRule/SubmitMove 파괴적 대체. 리모트 렌더 개선(텔레포트 스냅·적응 지연) 동반. EditMode 124/124·PlayMode 27/27)
- 2026-10-12 — **하트비트 타임아웃 튜닝 노출** (ADR-0033 — DisconnectTimeoutMs 옵션·상류 DS_RPC 3.6.1 전달 추가(사용자 선택 A)·기반 스택 DRPC 3.6.1·Communication 2.7.1·MessageProtocol 3.2.1 갱신. EditMode 102/102·PlayMode 25/25)
- 2026-10-12 — **RPC 레이트리밋** (ADR-0032 — ServerRpcRateLimitPerSecond 고정 창 게이트·초과 드롭 집계. 제너레이터 0.2.5. EditMode 100/100·PlayMode 25/25)
- 2026-10-12 — **플레이어 오브젝트·연결 매핑** (ADR-0031 — SetPlayerObject/GetPlayerObject/GetPlayerConnection·DestroyObjectsOwnedBy 퇴장 정리. Arena 전환. EditMode 97/97·PlayMode 24/24)
- 2026-10-12 — **명시적 소유권 이전 API** (ADR-0030 — SetOwner·OwnerUpdate 전파·0=방치후 입양. EditMode 93/93·PlayMode 24/24·2-프로세스 소유권 이전 왕복)
- 2026-10-12 — **네트워크 통계 API** (ADR-0029 — 트래픽 카운터·자체 RTT 프로브(와이어 8/9)·큐 깊이. 제너레이터 0.2.4. EditMode 90/90·PlayMode 23/23)
- 2026-10-12 — **프로토콜/빌드 버전 핸드셰이크** (ADR-0028 — ProtocolVersion 옵션·와이어 7 선언·Welcome 서버 버전·불일치 VersionMismatch 거부. EditMode 87/87·PlayMode 22/22·2-프로세스 VERSION-MISMATCH PASS)
- 2026-10-12 — **킥·연결 종료 사유** (ADR-0027 — DisconnectClient API·와이어 사유 전달·클라 이벤트 사유 인자로 서명 변경. EditMode 84/84·PlayMode 20/20·2-프로세스 KICK-REASON PASS)
- 2026-10-12 — **그리드 가시성 cull=0 계약 정합** (ADR-0026 — 클라→서버→다른 클라 이동 동기화 단절 해소. 그리드 멤버십 판정 cull>0 한정·Arena 뷰어 주입 제거·회귀 테스트 추가)
- 2026-10-06 — **Fusion식 spawnable 프리팹 스폰 시스템** (ADR-0024 — NetworkInstantiate 전면 교체·마커 컴포넌트·베이크 테이블·클라 동일 프리팹 인스턴스화. EditMode 80/80·PlayMode 17/17·2-proc PASS 3종)

- 2026-10-01 — **씬 netId 직렬화 안정 아이디** (ADR-0023 — 에디터 씬 저장 시 GlobalObjectId 해시 각인·신규 에디터 어셈블리 UniNet.Unity.Editor. EditMode 80/80·PlayMode 17/17·2-프로세스 왕복 PASS — 2Proc 프로젝트의 NuGet 캐시 잔존 제너레이터 DLL 이중 로드 고장도 복구)

- 2026-09-30 — **Skills 동기화 규칙 신설** (ADR-0022 — AGENTS.md 핵심 원칙 1에 사용자 가시 변경 시 `skills/uninet/SKILL.md` 반영 bullet 추가) + **에이전트용 사용자 매뉴얼 스킬 신설** (`skills/uninet/SKILL.md` — 소비자 관점 영어 매뉴얼, UNINET001~015 진단 전수표·런타임 로그 원문·고급 튜닝 수록)
- 2026-09-28 — **UPM Git URL 배포 준비 완료** (ADR-0021 — 기반 스택 DLL·소스젠 3종 패키지 동봉, NuGetForUnity 철거·개발=배포 통일. UPM 표준 구조(Samples~/Documentation~/CHANGELOG). update-dlls.sh·verify-install.sh. EditMode 76/76·PlayMode 17/17·신규 프로젝트 임포트 PASS)
- 2026-09-28 — **GitHub Actions CI/CD + v0.1.0 배포** (정적 검증 CI + 태그 시 Release 자동 생성 CD · 저장소 [BIGSUNGG/UniNet](https://github.com/BIGSUNGG/UniNet) 공개 확정 · 플레이스홀더 치환 · validate-package.sh)

- 2026-09-24 — **FastArray 구현 — 목표 완결** (ADR-0020 기능 2/2 — T[]/List<T> 인덱스-옵 요소 델타·진단 UNINET014/015(라운드 6 권장 3건 수정 — OOM 가드·fault 백오프·INDEX 정합). EditMode 76/76·PlayMode 17/17·**2-프로세스 실기 PASS**(양 기능 동시 검증). ADR-0020 승인됨 전환·roadmap 잔여 0·upstream-blockers MP 2건 폐기)
- 2026-09-24 — **커스텀 NetSerialize 구현** (ADR-0020 기능 1/2 — `[Replicated(Serializer)]` 정적 Write/Read+Equals 계약·Vector3 등 타입 해금·서버 틱 예외 격리(LogFault)·제너레이터 0.1.9·진단 UNINET013. EditMode 68/68·PlayMode 16/16·reviewer 4라운드 16건 이슈 수정)
- 2026-09-24 — **직렬화 2종 UniNet 단독 구현 전환 — 설계 문서** (ADR-0020 제안: "MP 수정 불가" 기존 판정 철회 — 제너레이터 필드 배선 구조 검증. features/custom-netserialize + features/fastarray-delta 설계 문서 신규)
- 2026-09-22 — **네트워크 프레임워크 비교 연구 문서 신설** (research/network-framework-comparison — UE NF·Fusion 2·NGO·N4E·FishNet·Mirror 기능 매트릭스 + UniNet 유무·장단점)
- 2026-09-22 — **코드 주석 전체 영어화 + 사용자 관점 재작성** (ADR-0019: 주석 언어·관점 규약 확립 — 78개 파일, 코드 무변경 입증, reviewer CLEAN)
- 2026-09-22 — **ServerRpc 검증 훅 옵트인화** (ADR-0018: `_Validate` 자동 감지 제거·`[ServerRpc(Validate = true)]` 옵트인 — UNINET011 에러·UNINET012 경고 신설. 제너레이터 0.1.5. in-repo `_Validate` 5곳 마이그레이션. EditMode 60/60·PlayMode 15/15)
- 2026-09-22 — **ServerRpc 소유자 자동 강제** (ADR-0016: 비소유 발신 거부 + RequireOwnership 옵트아웃 — netId 위조로 타 오브젝트 RPC 실행 불가화. 제너레이터 0.1.3. EditMode 60/60·PlayMode 15/15)
- 2026-09-22 — **동적 스폰 API 교체** (ADR-0017: Spawn 제거·NetworkInstantiate(복제 겸함·configure 콜백) 통합·NetworkDestroy 유지. EditMode 60/60·PlayMode 15/15)
- 2026-09-21 — **연결 수명주기 이벤트 — 게임 콜백 위임** (ADR-0015: 서버 접속/해제 이벤트·클라 해제 콜백·상태 조회 + Arena 퇴장 처리 + 상류 의존 잔여 과제 문서 신설. EditMode 56/56·PlayMode 14/14)
- 2026-09-20 — **NetworkTransform 컴포넌트 + 스폰 서브 자동 복원** (ADR-0013·0014: 이동 예측 라이브러리 컴포넌트화 + 멀티 컴포넌트 스폰 슬롯 불일치 근본 해결. EditMode 52/52·PlayMode 12/12)
- 2026-09-20 — **P4 훅 완결 — 시간 동기화·인터폴레이션·리와인드·그리드 가시성 구현** (ADR-0012: UniNet 단독 구현분 전부. EditMode 45/45·PlayMode 10/10·아레나 예측/히트스캔 전환)
- 2026-09-18 — **P3 완결 — 리플리케이션 고급 정책 구현** (ADR-0011: 가시성·우선순위·휴면·전송 주기·채널 예산 — UniNet 단독 구현분 5종. EditMode 38/38·PlayMode 7/7·아레나 예시 통합)
- 2026-09-18 — **Sandbox Usage 예시 7종 제거** (아레나와 무관한 사용법 예제 정리 — README 코드 예시로 대체)
- 2026-09-17 — **수명주기 종료 API 추가 — RUDP 포트 잔존 바인딩 실패 근본 해소** (ServerStopAsync/ServerStop/ClientStop/HostStopAsync/HostStop — LifecycleStopTests 3종·PlayMode 7/7×2회·EditMode 20/20 통과, UniNet.CodeGenerator 0.1.1)
- 2026-09-17 — **Sandbox 아레나 슈팅 예시 게임 구현** (examples/arena-shooter: 구현 기능 P1+P2 전부 활용 — MPPM 설치·메인=서버/가상 플레이어=클라 토폴로지·Arena 씬·PlayMode 테스트 통과·2-프로세스 클라 2 접속 검증 PASS)

- 2026-09-16 — **다중 NetworkBehaviour 지원 — 2층 식별자(netId+SubId) 구현** (ADR-0010: 다중 서브 스폰·subId RPC 라우팅·슬롯 대조. EditMode 19종·PlayMode 3종·2-프로세스 MULTI-COMPONENT PASS)
- 2026-09-16 — **P2 완결 — 동적 스폰/파괴·조건부 리플리케이션·InitialOnly 구현 완료** (ADR-0009: 명시적 Spawn/NetworkDestroy API·타입 카탈로그·후발 접속 캐치업·호스트 권위 원본 보존. EditMode 13종·PlayMode 2종·2-프로세스 DYNAMIC-SPAWN-DESTROY PASS)
- 2026-09-14 — **P1 RPC + P2 Replicate 기본 구현 완료** (ADR-0008: 소스젠·런타임·2-프로세스 RUDP 왕복 검증 포함. 첫 기능 문서 등재)
- 2026-09-14 — 사용법 우선 API 확정 (ADR-0007: 공개 API 스타일 Mirror/Netcode류·Sandbox 사용법 예제·Package API 스텁 · 변경 이력 — RepNotify·MulticastRpc 확장 후 partial 재구조화)
- 2026-09-14 — 기반 패키지 최신화: DRPC 3.5.0 / MessageProtocol 3.2.0 / Communication(RUDP) 2.7.0 (ADR-0005 변경 이력)
- 2026-09-14 — 리뷰 품질 게이트 확대 (ADR-0006: 트리거 확대·차원 스킬 3종·README 갱신 규칙)
- 2026-09-14 — 개발 환경 구축 완료 (ADR-0005 변경 이력: Package/ 분리·6000.0.83f1·NuGetForUnity 4.5.0·기반 패키지 버전 확정)
- 2026-09-14 — 개발 환경 확정 (ADR-0005: Unity 샌드박스 + UPM 로컬 참조, 저장소 내 `/Sandbox`)
- 2026-09-13 — 구현 플랜 확정 (plan·ADR-0003 패키지 참조·ADR-0004 계약 자동 생성)
- 2026-09-13 — 프로젝트 정의·기반 스택·스코프 확정 (overview·architecture·roadmap·ADR-0002)
- 2026-09-13 — 하네스 엔지니어링 도입 (문서 Vault + AGENTS.md + 스킬 + 훅 + 리뷰어)
