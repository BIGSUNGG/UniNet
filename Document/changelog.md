# changelog — 변경 기록

의미 있는 모든 변경(기능 추가/수정/제거, 규약, 구조, 하네스)을 기록한다.
형식: 날짜 그룹 아래 `### Added / Changed / Removed / Fixed`. 최신 날짜가 위로 오게 관리한다.

## [2026-09-22]

### Changed (동적 스폰 API — Spawn 제거, NetworkInstantiate 통합)

- **`UniNetManager.Spawn(instance)` 제거 → `NetworkInstantiate` 3종 오버로드로 통합** (ADR [[0017-동적-스폰-NetworkInstantiate-통합]] — ADR-0009의 Spawn 계약 대체)
  - `NetworkInstantiate(GameObject original)` / `(original, position, rotation)` / `(original, Action<GameObject> configure)` — 원본(프리팹·템플릿) 복제 + netId·소유권 배정 + 전 클라 스폰 전파까지 한 호출, **반환값이 등록된 인스턴스** (원본은 호출측 소유로 남는다)
  - **configure 콜백** — 복제 직후·전파 직전에 클론으로 실행. `Object.Instantiate`가 직렬화 필드(public·[SerializeField])만 복사하므로 private [Replicated] 초기화(InitialOnly 기준선)는 이 콜백에서 세팅한다 ([SerializeField] 강제·실수 시 조용한 상태 유실 방지)
  - `NetworkDestroy`는 유지(파괴 겸 convenience — 등록 해제+전파+로컬 파괴, 클라 고스트 파괴)
  - 가드 — 서버 미실행·NetworkBehaviour 부재 시 경고 후 null 반환. 255 상한 초과 시 인스턴스 폐기(플레이 Destroy/에디트 DestroyImmediate 분기) 후 null 반환
  - **마이그레이션** — 기존 "생성 후 Spawn(instance)" 흐름은 "원본을 만들어 NetworkInstantiate(원본[, configure]) 전달, 반환값 사용, 원본은 호출측 정리"로. 비전파 상태(MovementRule·NetworkCullDistance·NetworkRewindHistory 등)는 스폰 후 클론에 주입해도 무관(기준선 아님). 등록 전용 흐름(풀링 등)은 표현 불가 — 필요시 재추가
  - 콜사이트 전수 마이그레이션 15곳 — ArenaBootstrap·ArenaTwoProcessRunner·TwoProcessRunner·EditMode/PlayMode 테스트 (캐치업 대상 A는 서버 시작 후 스폰으로 순서 조정, MultiTemplate 등 카탈로그 원본은 RegisterPrefab 팩토리 참조로 파괴 금지)
- **검증**: dotnet 빌드 녹색 · **EditMode 60/60** · **PlayMode 15/15** · Unity 6000.0.83f1 배치 EXIT=0

### Added (ServerRpc 소유자 자동 강제 — RequireOwnership 옵트아웃)

- **ServerRpc 소유자 자동 강제 — netId 위조로 타 오브젝트 RPC 실행 불가화** (ADR [[0016-ServerRpc-소유자-자동-강제]] · P1 때부터의 알려진 한계 해소 — [[features/monobehaviour-rpc-replicate]] "알려진 한계" 갱신)
  - **속성** — `ServerRpcAttribute.RequireOwnership` 신설 (기본 `true`). 옵트아웃: `[ServerRpc(RequireOwnership = false)]` (NGO RequireOwnership 기본값 true 관례 — ADR-0007 API 스타일)
  - **제너레이터** — 서버 디스패치(`__UniNetServerDispatch_*`) 머리에 발신자-소유자 대조 삽입: `senderConnId != NetworkServer.GetOwner(netId)`이면 구현 미실행 + 경고 1줄(`"[UniNet] ServerRpc 거부 — 비소유 발신: <타입>.<메서드> netId sender owner"`). **로컬 권위 경로(senderConnId 0 — 서버·호스트 직접·오프라인)는 대조 제외**. ClientRpc/MulticastRpc는 대상 아님. ClientRpc 경유의 정상 크로스-클라 ServerRpc는 옵트아웃 속성으로 유지
  - **Core** — `NetworkServer.GetOwner(netId)` 신설 (0 = 미할당). 소유자 미배정 오브젝트로의 네트워크 발신은 거부(안전 기본값 — 등록 직후 창은 메인 큐 FIFO로 실질 노출 없음)
  - **UniNet.CodeGenerator 0.1.4** — nupkg 재배포 (Sandbox Assets/Packages + 로컬 피드. 0.1.3 강제 삽입 + 0.1.4 거부 경고 유량 제한 — 발신자별 최초 1회 + 전역 5초당 1회, 비소유 churn의 로그 플러딩 방어)
  - **in-repo RPC 영향 조사** — Arena(RpcSubmitAim·RpcFire)·NetworkTransform(SubmitMove)은 전부 소유자 전용 의미론이라 무변경 자동 보호. 옵트아웃 시연은 HealthTank.RpcHeal 픽스처 + 기능 문서 예제
  - **마이그레이션 노트** — 기본값 변경으로 기존 비소유 발신 크로스-클라 ServerRpc는 업그레이드 후 거부된다(경고 로그로 식별) — 명시적 `RequireOwnership = false` 필요
- **검증**: dotnet 빌드 5 프로젝트 녹색 · **EditMode 60/60**(`ServerRpcOwnershipTests` 4종 신규 — 소유자 실행·비소유 거부+경고·옵트아웃 실행·로컬 권위 경로) · **PlayMode 15/15**(`ServerRpcOwnershipPlayTests` 신규 — 실제 루프백 왕복 소유자 실행 + 게스트 연결 비소유 발신 거부) · Unity 6000.0.83f1 배치 EXIT=0

## [2026-09-21]

### Added (연결 수명주기 이벤트 — 게임 콜백 위임)

- **연결 수명주기 게이트웨이 API** (ADR [[0015-연결-수명주기-이벤트-게이트웨이]] · 기능 문서 [[features/connection-lifecycle]])
  - **서버** — `NetworkServer.ClientConnected`(환영·소유권 배정·캐치업 이후 발화) / `ClientDisconnected`(**소유권 재배정 이전** 발화 — 게임이 퇴장 연결 소유 오브젝트를 소유자로 식별해 파괴 가능). 전부 메인 스레드
  - **클라** — `UniNetManager.ClientDisconnected`(클라 허브 세션 종료 관측 → 메인 스레드 발화) + `UniNetManager.IsClientConnected` 상태 조회
  - **격리 계약** — 구독자별 try/catch로 첫 예외가 나머지 구독자를 묻지 않고, 마지막 예외 재던짐을 드라이버 메인 펌프 보호가 로그 — 펌프 잔여 작업은 다음 프레임 재개
  - 배경 결함: 해제 알림 부재로 퇴장 플레이어 아바타가 좀비로 잔존(소유권만 재배정) — Arena의 0.5초 폴링 잉여 정리로도 연결-오브젝트 정밀 대응 불가
- **Arena 예제에 수명주기 처리 추가** — ClientDisconnected에서 퇴장 연결 소유 아바타를 `NetworkDestroy`(좀비 소멸 — 폴링 잉여 정리는 백업으로 유지), ClientConnected에서 HUD 표시 + 관리 주기 즉시 실행(스폰 지연 제거). ArenaHud에 수명주기 표시 추가
- **상류 의존 잔여 과제 문서 신설** — [[upstream-blockers]]: 커스텀 NetSerialize·FastArray 2종의 MP 수정 필요 사유·변경 요지·UniNet 측 준비 상태 정리 (roadmap 잔여 행에서 링크. 상류 저장소는 수정하지 않음 — ADR-0003 원칙)
- **검증**: EditMode 55/55(LifecycleEventTests 3종 신규 — 발화 순서·재배정 전 발화·예외 격리) · PlayMode 14/14(LifecycleEventPlayTests 2종 신규 — 실제 세션 이벤트·퇴장 아바타 파괴 전파) · dotnet 빌드 3 프로젝트 녹색

### Fixed (리뷰 라운드 1 — 수명주기 이벤트 결함 7건)

- **[블로커] 해제 경로 구독자 예외가 소유권 재배정을 영구 생략** — `DetachConnection` 메인 큐 람다에서 `ClientDisconnected` 구독자가 예외를 던지면 재던짐이 같은 람다의 잔여 문장인 `ReassignOwnership()`을 건너뛰어 죽은 연결이 소유자로 고착(OwnerOnly 델타 수신자 상실) → 발화부를 `try/finally`로 감싸 재던짐·펌프 로깅 계약은 유지하면서 재배정은 예외와 무관하게 항상 실행. 해제 경로 예외 테스트 추가(`해제_경로에서도_구독자_예외가_소유권_재배정을_막지_않는다`)
- **클라 플래그 레이스 제거** — `await connect` 재개 전 네트워크 스레드에서 허브 `Disconnected`가 관측되면 가드가 이벤트를 삼키고 `IsClientConnected`가 true로 고착 가능 → 플래그 확정을 허브 구독 직후로 이동(스레딩 가정 무관 안전), connect 실패 경로에서 플래그 리셋 후 재던짐. 추가로 `ClientAsync` 시작 시 `SetClientSender(null)`로 이전 세션 허브의 지연 관측분이 새 세션 wiring에서 통과하지 못게 차단
- **거짓 ClientDisconnected 방지** — 이전 세션 허브의 지연 `Disconnected`가 재접속된 새 세션을 건드릴 수 있어 허브 동일성 필터(`ReferenceEquals(허브, ClientSender)`) 도입. 허브 래핑 람다 2분기 중복도 로컬 함수 1개로 정리
- **XML 문서 정정** — `UniNetManager.ClientDisconnected` 주석의 “(자발 ClientStop 포함)”은 사실이 아님(ClientStop이 먼저 플래그를 끊어 가드가 발화를 흡수) → “자발 ClientStop은 상태만 즉시 해제하고 이벤트는 발화하지 않는다”로 정정, 기능 문서도 정렬
- **구독자 예외 디버깅성 개선** — `RaiseLifecycle`의 `throw last` → `ExceptionDispatchInfo.Capture(last).Throw()`로 원본 스택 트레이스 보존(어떤 구독자가 던졌는지 추적 가능)
- **이벤트 스톰 가이드 문서화** — 접속·해제는 연결당 1회씩 메인 스레드 발화 — 대량 churn 시 콜백 비용 비례, 콜백에서 전 씬 스캔 대신 connId→오브젝트 맵 유지 권장 ([[features/connection-lifecycle]])
- **검증 (수정 후)**: dotnet 빌드 5 프로젝트 녹색 · EditMode 56/56 (해제 예외 테스트 1종 추가) · PlayMode 14/14 (Unity 6000.0.83f1 배치)

## [2026-09-20]

### Added (P4 훅 — 시간 동기화·인터폴레이션·리와인드·그리드 가시성)

- **P4 훅 구현 — UniNet 저장소만 수정해서 가능한 항목 완결** (ADR [[0012-P4-훅-시간동기화-인터폴레이션-리와인드-그리드]] · 기능 문서 [[features/replication-p4-hooks]])
  - **UniNetTime** — 서버 권위 단조 시계 (UE ReplicatedWorldTimeSeconds 상응). 서버=로컬 시계, 클라=로컬+EMA(α=0.25) 오프셋, 단조 보장(역행 클램프). 서버·호스트는 ApplyServerTime 무시 — 기록·질의 시계 불일치 결함 방어
  - **TimeSync 와이어** — 시스템 메시지 methodId 5 (`[serverTime: double]`): IUniNetSystemChannel.SendTimeSync + 생성 클라 허브 수신 핸들 + 드라이버 1초 주기 브로드캐스트. 수신은 메인 큐 우회(Unity 시계 메인 전용). UniNet.CodeGenerator 0.1.2
  - **SnapshotBuffer<T>** — 클라 인터폴레이션 버퍼: 시간 역전 스냅샷 폐기·범위 밖 끝 값 홀드·선형 보간 (틱 스케줄링 "적용 시점 제어" 훅)
  - **PositionHistory + NetworkRewindHistory/GetHistoryPosition** — 래그컴펜세이션 훅: 서버 옵트인 위치 히스토리(링 128) + 과거 시점 질의(선형 보간·끝 홀드)
  - **SetVisibilityGrid(cellSize, visibleRadius)** — RepGraph 스타일 그리드 공간 분할 가시성: 셀 멤버십 양자화 판정, P3 계약(관련성 훅 AND·페일오픈·재진입 기준선 복구) 유지
- **검증**: EditMode 52/52(UniNetSpawnApplyTests 4종 신규 — 2서브 복원·역방향 순서·미등록 거부·단일 회귀 + NetworkTransformComponent 3종 관찰자 실사용)·PlayMode 12/12(리모트 버퍼 적재 회귀 테스트 포함)
- **스폰 서브 자동 복원 — 멀티 컴포넌트 스폰 슬롯 불일치 런타임 에러 근본 해결** (ADR [[0014-스폰-서브-자동-복원]] · NetworkTransform 도입 후 발견)
  - **원인**: 클라 스폰이 첫 서브 타입의 기본 팩토리(단일 컴포넌트)로만 생성 — RequireComponent로 결합된 멀티 컴포넌트 오브젝트(ArenaPlayer + NetworkTransform)가 서버 2서브/클라 1서브로 어긋남
  - **수정**: UniNetSpawn.Apply가 스폰 메시지의 typeKeys 순서대로 누락 NetworkBehaviour 서브를 자동 복원(AddComponent) — UniNetSpawnRegistry에 typeKey→Type 역조회 추가, 미등록 타입 키는 스폰 거부+부분 생성 폐기
  - **계약 완화**: 멀티 컴포넌트 오브젝트의 RegisterPrefab 필수 → 선택 (커스텀 비주얼/구성용)

### Fixed (라이브 재현 — 클라→다른 클라 이동 동기화 단절)

- **NetworkTransform의 [Replicated] 위치 3필드에 RepNotify(OnNetX/Y/ZChanged) 미연결** — 필드 갱신은 되지만 Notify 미호출로 리모트 인터폴레이션 버퍼가 영구 비어 다른 클라 화면에 이동이 반영되지 않음 → 3필드 Notify 연결로 해소. 아울러 NetworkTransform.SubmitMove가 클라에서 로컬 예측 입력(_inX)을 갱신하지 않던 누락도 수정(예측 즉시 반영 복원)
  - **검증**: EditMode 52/52(UniNetSpawnApplyTests 4종 신규 — 2서브 복원·역방향 순서·미등록 거부·단일 회귀 + MultiComponentTests 자동 복원 계약 갱신)·PlayMode 12/12
- **리뷰 라운드 2 반영 (권장 1건 → 수용 + 플레이모드 안정화)**: RefreshGrid의 "연결별 집합 재사용"이 실제로는 작동하지 않았음(`_gridRelevant.Clear()`로 딕셔너리째 비워 매 틱 재할당) → 딕셔너리 유지 + Clear 후 재기입으로 수정, 끊긴 연결 키는 DetachConnection에서 제거. 리와인드 테스트도 프레임 타이밍 무관한 결정론 시점 단언으로 강화(before=이동 전 0 / after+여유=최신 10 — 홀드 계약 활용)
- **리뷰 라운드 3 반영 (권장 3건 → 전부 수용)**: ① 누락됐던 ADR-0013(NetworkTransform 컴포넌트) 문서 신규 작성 + 00-INDEX decisions 목록 보강 ② UniNetSpawnApplyTests의 미사용 헬퍼(SpawnAndRegisterServerSide) 제거 ③ 슬롯 불일치 에러 메시지의 부정확한 원인 예시(비네트워크 컴포넌트)를 실제 잔여 원인(프리팹 초과 서브·컴포넌트 순서 어긋남)으로 정정
- **리뷰 라운드 1 반영 (권장 5건 → 전부 수용)**: ① TimeSync 와이어 도달 관찰자(ReceiveCount) 추가 + 호스트 모드 테스트를 와이어 도달 단언으로 강화 ② RefreshGrid 버킷 리스트·연결 집합 재사용 풀링(틱당 GC 압력 제거) ③ PositionHistory.Record 시간 역전·중복 폐기로 SnapshotBuffer와 계약 정렬 ④ 미사용 HasRewindHistory 제거 + RewindSampleCount를 리와인드 테스트에서 실사용 ⑤ BulletChannelBudgetPerTickBytes → PlayerChannelBudgetPerTickBytes 개명(실제 대상 정렬)

### Changed (Sandbox 아레나 — P4 게임플레이 전환)

- **클라 이동 예측** — 이동 규칙을 ArenaMovement.Step 단일 공급원으로 추출(서버·예측 공유), 소유 클라 입력 즉시 적용 + 서버 상태 수신 시 ReconcileAxis(스냅/소프트) 조정, 리모트는 SnapshotBuffer로 InterpolationDelay(0.12s) 뒤 렌더
- **발사를 히트스캔+래그컴펜세이션으로 전환** — RpcFire에 발신자 조준 시각(hitTime) 추가, 서버가 대상을 리와인드(클램프 [Now−1s, Now])해 선분-원 판정, 트레이서 MulticastRpc 신설(ArenaFx.Tracer), ArenaBullet 제거(동적 스폰/파괴 커버리지는 플레이어 스폰·파괴가 담당)
- **기존 테스트 갱신** — ArenaRoundtripTests(트레이서 단언·TryFire 시그니처)·ArenaTwoProcessRunner(동일) — PlayMode 10/10 그린
- **README·roadmap·plan·00-INDEX·architecture·examples 문서 갱신**

### Removed (Sandbox 아레나 정리)

- **ArenaBullet 제거** — 히트스캔 전환으로 투사체 클래스 삭제 (총알 전용 P3 상수는 ArenaConfig에 유지 — 채널 예산 대상은 ArenaPlayer로 변경)

## [2026-09-18]

### Added (P3 완결 — 리플리케이션 고급 정책)

- **P3 리플리케이션 고급 5종 구현 — UniNet 저장소만 수정해서 완결** (ADR [[0011-P3-리플리케이션-고급-정책]] · 기능 문서 [[features/replication-p3-policy]])
  - **정책 인터페이스 `IUniNetReplicationPolicy`** (UniNet.Core) — NetworkBehaviour가 구현. UE 대응: NetworkPriority(NetPriority)·NetworkUpdateFrequencyHz(NetUpdateFrequency)·NetworkCullDistance(NetCullDistance)·NetworkDormant+FlushNetworkDormancy(NetDormancy 2상태 단순화)·IsNetworkRelevant(IsNetRelevantFor 훅). 제너레이터·와이어 포맷 무변경
  - **틱 이중 모드** — 1-arg 즉시 모드(P2 호환 100% 보존·batchmode 검증기 경로) / 2-arg 시간 모드(드라이버가 Time.unscaledTimeAsDouble 제공)
  - **가시성(Relevancy)** — 거리 컬(NetworkCullDistance + SetViewerPosition 뷰어 위치, 미설정 연결은 페일오픈) + 관련성 훅. 씬 오브젝트 첫 평가는 조용한 시드(P2 후발 등록 계약 유지), 재진입·동적 첫 평가는 스폰/전체 상태 기준선 복구, 어느 연결에도 비관련이면 비교 생략. 비관련 전환 시 델타만 중단(전파 파괴 미지원 — 명시적 계약)
  - **우선순위·스타베이션 방지** — 전역 예산 ReplicationBudgetPerTickBytes(0=무제한) + 병합 큐 + 기아 보정(UE GetNetPriority 공식 `priority × (1 + 대기초/0.1)`) + 예산 초과 대델타 강제 전송. PriorityTests 6종(이전 세션 스펙) 구현 완료
  - **채널 우선순위 큐(유형별 대역폭 관리)** — SetReplicationChannelBudget(Type, bytes) — 유형 초과분만 연기, 타 유형은 계속 흐름
  - **휴면(Dormancy)** — NetworkDormant 중 델타 비교 생략(스냅샷 동결), FlushNetworkDormancy 시 누적 변경분 일괄 전송
  - **전송 주기(NetUpdateFrequency)** — NetworkUpdateFrequencyHz 미도달 틱 비교 생략, 도달 틱에 최신 변경분(최신값 승)
- **검증**: EditMode 38/38(P3 신규 12종 — Relevancy 4·Dormancy 3·UpdateFrequency 3·ChannelBudget 2 + Priority 6)·PlayMode 8/8(드라이버 시간 모드 전환 회귀 + 파괴 엔트리 틱 방어 신규 1종)
- **리뷰 라운드 1 반영 (ISSUES 5건 → 전부 수용)**: ① 파괴된 엔트리가 같은 프레임 틱 스냅샷에 남아 IsRelevant의 transform 접근으로 MissingReferenceException → 드라이버 스윕이 살아있는 엔트리만 틱에 전달 ② DetachConnection의 `_everVisible` 정리 누락(재접속마다 HashSet 누수 — 리소스 소진) → 정리 블록에 Remove 추가 ③ 전송 루프의 항목×연결 IsRelevant 재평가 제거 — 인큐 시점 관련 비트마스크 스냅샷(하위 64 연결, 초과분은 전송 시점 폴백) ④ 즉시 모드 문서-코드 불일치(예산이 적용되던 것) → 예산·유형 예산을 timed 게이팅으로 문서 계약과 일치 ⑤ 휴면 진입 직전 연기된 델타가 휴면 중에도 전송되던 것 → 휴면 중 연기분 보류(드롭 시 스냅샷 갱신으로 유실 — 깨우면 전송)
- **리뷰 라운드 2 반영 (권장 1건 → 수용)**: `DestroyObject`가 연결별 가시성 맵(`_visible`·`_everVisible`)에서 파괴 netId를 제거하지 않아 동적 스폰/파괴 churn마다 잔여물이 누적 → 제거 추가. 씬 리로드 재등록 시 신선한 기준선 보장 부수 효과. DetachConnection 블록 들여쓰기 정정

### Changed (Sandbox 아레나 예시 — P3 사용 예시 통합)

- **아레나에 P3 정책 사용 예시 추가** — 총알(NetworkUpdateFrequencyHz 30·NetworkCullDistance 24)·플레이어(NetworkPriority 2·사망 시 휴면 진입→리스폰 FlushNetworkDormancy 일괄 전파)·부트스트랩(연결별 SetViewerPosition 소유 플레이어 좌표 주입·총알 SetReplicationChannelBudget 512B/tick) — 상수는 ArenaConfig 단일 진실 공급원
- **README** — P3 사용법 섹션 추가. 기능 문서·roadmap·plan·00-INDEX 갱신

### Removed (Sandbox 정리)

- **아레나와 무관한 Usage 예시 7종 제거** — Player·Projectile·Weapon·GadgetCarrier·UsageBootstrap·CriticalHitMsg·DamageMsg (씬 참조 없음 확인 후 삭제 — API 사용법은 README 코드 예시로 대체)

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
