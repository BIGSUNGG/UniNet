# v0.23.0 안정화 종합 리뷰 — FR-01~07 구현·검증

- **작성**: 2026-10-26, 주 에이전트(부모) 직접 검토 — pi-crew에 위임하지 않음(목표 계약).
- **범위**: [[research/unity-server-functional-review]] FR-01~07의 해결 구현·검증·문서 동기화. 시작 HEAD `6b30efb`(main, 패키지 0.22.0, 태그 25개) → v0.23.0 배포 준비 상태(태그·릴리스·UPM 배포는 생성하지 않음).
- **수행 방식**: pi-crew executor `zai/glm-5.3` 4단계 구현 + explorer/reviewer `zai/glm-5.3-flash`(모델 대체 없음 — 실행 영수증 확인). 각 단계 후 부모가 실제 빌드·Unity CLI·수정·중간 reviewer CLEAN 게이트(r1→보강→r2 또는 r1 CLEAN+권장 수용)를 수행. 원시 증거: `.pi-subagents/artifacts/v0-23-stabilization/`(무시 경로), `.pi/teams/artifacts/team_20261026…`.

## FR별 해결과 증거

### FR-01 · 승인/씬 준비 전 게임 RPC 즉시 드롭 — 해결

- 구현: `NetworkServer.ServerConnection.CanReceiveGameData`(`!Disconnected && VersionApproved && !AwaitingSceneReady` — 복제/스폰 경로의 기존 3중 게이트와 동일 조건)를 Emitter 방송/`To`/Multicast 3개 송신 루프에 공용 적용. `IsClientSceneReady`(진행 중 전환 보고 집합)을 오용하지 않아 **씬 전환 이력 없는 승인 연결은 정상 송신**(executor 초안 지적 후 정정한 지점).
- 증거: headless(실제 Core+생성 코드 실행) — 미승인 방송·To·멀티캐스트 송신 0·시스템 Welcome 1·승인 후 정상·씬 대기→ready 복귀. Unity `ServerSendGateTests` 3/3(리플레이 없음·로컬 멀티캐스트 보존 포함). EditMode 전수 포함.
- 검토 소견(구조/보안/성능): 단일 공개 판정자로 게이트 일원화(신규 분기 최소)·신뢰 경계 폐쇄(사전 노출 차단, 유보 큐가 아니라 즉시 드롭으로 의미 명확)·순회당 판정은 필드 3개 읽기(부하 무시 가능).

### FR-02 · 이동 중 유실 수렴 — 해결

- 구현: `UnreliableBaselineIntervalSeconds=1.0` 주기 기준선 — Unreliable 서브가 델타를 계속 생산하는 동안 현재 풀 상태를 신뢰 배치로 재전송(기존 DeferredDelta·예산·기아 부스트 경로 재사용). **부모가 in-motion 게이트 추가**(최근 변경이 간격 내인 서브만 — 무활동 서브의 무한 재전송 방지, 퀴에센스 1회로 종결). FastArray는 tag-1 전체 치환만.
- 증거: `ReplicationDeliveryBatchTests` — 유실 델타+A 60Hz 지속 변경 수렴(현재 영원히 어긋나던 반례), FastArray op 유실 최신 전체 치환, 조용한 서브 재전송 1회(부모 추가 회귀). 정지 재전송·휴면 플러시 양성 유지.
- 소견: 수신 ack·와이어 불변이라 계약 위험이 최소이고 비용 상한(가시 서브·연결당 간격마다 풀 상태 1엔트리)이 명시적. NGO axial-frame과 같은 방향 선택.

### FR-03 · RPC 컬렉션 코덱 경계 — 해결

- 구현: 수신 List를 길이만큼 Add(capacity/Count 혼동 수정), 송신 65535 초과 직렬화 전 `ArgumentException`, 디코드 후 `r.Remaining != 0` 트레일링 검증으로 구현 미호출 보장. 추가로 **파서가 지원 불가 인자(예: `List<List<int>>`)를 보고 후에도 emit해 생성기 크래시(CS8785)하며 UNINET002를 삼키던 결함**을 필드 경로와 같은 Report+skip으로 수정(부모 발견·수정).
- 증거: headless — `List{11,22,33}+tail99` 와이어 바이트 정합·복원, 65536 배열/List 명시 거부·65535 인코딩, 잘림·미소비 구현 미호출, UNINET002 음성. Unity `CollectionRpcBoundaryTests` 4/4 + 기존 T4 Ignore 복구(실패 원인이 바로 이 버그였음이 입증).
- 소견: 신뢰 경계(수신 페이로드)의 명시 거부·정렬 검증은 보안 필수 요건. 스칼라·null/empty 와이어 불변 확인.

### FR-04 · AOI 재진입 기준선 — 해결

- 구현: `UniNetSpawn.Apply`의 중복 스폰 가드를 제거하지 않고 '기존 인스턴스 기준선 갱신' 분기(`RefreshExistingInstance` — 트랜스폼+서브별 전체 상태·무재생성·SubId·typeKeys 검증·불일치 거부·RepNotify 첫 스폰 동일 계약). 호스트 중복은 서버 원본 불변.
- 증거: `AoiReentryBaselineTests` 3/3(이탈 중 값 변경→재진입 갱신·무재생성·슬롯 유지, 호스트 무간섭, 슬롯 불일치 거부). 기존 Relevancy/OwnerBaseline/SpawnablePrefab 회귀 통과.
- 소견: "가드 제거"가 아니라 분기 확장이라 중복 스폰 방지·owner-before-state 계약 보존. 씬 객체 무영향 확인.

### FR-05 · 위치 무관 권위 TState 리컨사일 — 해결

- 구현: 히스토리 링에 상태 스냅샷 열 추가, `INetworkMoveSimulation.ReconcileState`(DIM 기본 no-op — 커스텀 구현 무변경)가 해당 seq 예측 상태와 서버 echo를 바이트 정확 비교, 불일치 시에만 기존 `Rollback` 재생 의미 그대로 수행.
- 증거: `MoveSimulationTests` 21/21(신규 4종 — 권위 복원·일치 무비용·재생 후 정정 궤적·링 밖), `NetworkTransformComponentTests` 11/11(실제 컴포넌트 — 재현 보정·무재시뮬 대조·렌더 기준점 무변경). **reviewer r1이 dangling if(일치 ack에서 Y/Z 기준점 무조건 덮음 — 실제 렌더 결함)를 발견** → 중괄호 수리+전용 단언 추가.
- 소견: 일치 ack 비용 = 조회+직렬화 1회+비교(재생 0). 손실 압축 상태 코덱의 와이어 정밀도 한계는 문서화(movement-prediction·SKILL).

### FR-06 · 유한 적체·명시적 초과 — 해결

- 구현: 연기 큐 (NetId, SubId)당 최신 권위 전체 상태 병합(실패 시 둘 다 유지 폴백), 상한 1024 도달 초과 후보 강제 송신(`DeferredForcedSendCount` — 실제 전송 시만 계수), 메인 큐 상한 65536(초과 드롭+LogFault+`DroppedMainActions` — throw 대신 디스패치 스레드 보호), 수신 드롭 관측 `DroppedReplicateEntries`. **부모가 `QueueHoldsKey` 순서 불변식 추가** — 큐에 병합 full이 남은 키의 신규 op 델타(절대 인덱스)가 먼저 나가면 수신자 Insert 범위 예외·항목 유실이 됨을 틱별 진단으로 확인·차단.
- 증거: `DeferredBacklogTests` 4/4(120틱 과부하 키 수 바운딩·최신 수렴, FastArray 최신 전체 치환, 상한 강제 송신 명시성, 메인 큐 상한 관측·전량 드레인). SceneTransitionTests 39/39(계수 단언을 병합 계약으로 갱신 — 의미 단언 원본 유지).
- 소견: 사용자 승인 정책("상태 수렴+명시적 실패") 준수 — 조용한 누락 경로 없음(강제 송신 또는 LogFault+카운터). `UniNetEnvironment.cs`에 `using System.Threading` 누락(제품 컴파일 오류)을 headless가 포착·수리한 사례가 회귀 인프라의 효과를 입증.

### FR-07 · 생성기 소스/CI 게이트 — 해결

- 구현: `UniNetCodeGenerator.cs` 병합 잔재 제거로 source-output pipeline 복구(덤프 부수효과 없음). headless 회귀 호스트 `Tests/UniNet.CodeGenerator.Tests`(net10.0, 프레임워크 없음 — 실제 Core 소스+동봉 MessageProtocol DLL+최소 shim을 `RunGeneratorsAndUpdateCompilation`으로 진단·컴파일·적재 실행). CI validate에 setup-dotnet+소스 빌드+양경로 회귀 필수 연결(update-dlls.sh 로컬 전용 유지).
- 증거: 빌드 종료 0. headless **19/19 — source-built·동봉 양경로 동일 DLL**(0.2.18, sha `3ba4a4f3…`). 동봉 analyzer가 이제 소스에서 재현 가능.
- 소견: 65535/65536·잘림·미소비·승인 게이트·UNINET002 음성을 실제 실행으로 검증하는 최소 인프라. CI 원격 실행은 push 후 최초(태그 금지 유지).

## 통합 검증 요약

- **EditMode 전수 연속 2회 310 통과/0 실패/1 스킵**(스킵 1 = 범위 외 의도적 T2 Ignore). **PlayMode 전수 47/47**. 집중: ServerSendGate 3/3·CollectionRpcBoundary 4/4·AoiReentry 3/3·ReplicationDeliveryBatch 10/10·MoveSimulation 21/21·ComponentTests 11/11·DeferredBacklog 4/4·SceneTransition 39/39·DestroyOwnership 4/4·ArenaRoundtrip 2/2.
- **기존 결함 판별**(기준점 HEAD worktree 대조): 점프 착지 프레임 상한 — HEAD에서 동일 실패(사전 존재 하네스 예산 결함, 6000 상향 수리 후 통과). 파괴 정리 1회 실패 — 기준점·재실행 통과(타이밍 플레이크, 근본 원인 미특정 기록).
- **기존 사용자 자산 61파일 바이트 무변경**(baseline.json SHA 대조). Sandbox 신규 파일은 전부 회귀 fixture/테스트.
- 원시 증거 경로: `.pi-subagents/artifacts/v0-23-stabilization/{constraints,phase1..4,integration-verification}.md`·`Sandbox/Logs/unity-tests-*`·`.pi/teams/artifacts/team_20261026*`.

## 중간 reviewer 기록 (glm-5.3-flash, 읽기 전용)

| 단계 | r1 | 보강 | r2 |
| --- | --- | --- | --- |
| FR-01/03/07 | ISSUES 1(파서 changelog 누락) | changelog bullet | **CLEAN** |
| FR-02/04 | ISSUES 1+3(README·문서 정합) | README 2불릿·이력·서술 정정(1건 수용거부 사유 기록) | **CLEAN** |
| FR-05 | ISSUES 1(dangling if — 실제 결함)+2 | 중괄호·단언 2개·한계 문서·중복 제거 | **CLEAN**(표기 '클린') |
| FR-06 | **CLEAN**+권장 3 | 폴백 주석/문서 정밀화·카운터 실전송 계수·죽은 조건 제거 | (CLEAN 유지) |

## 부모가 executor 산출에 가한 수정 (전량 공개)

1단계: csproj XML `--`(MSB4025)·`WithNullableContextOptions`·crypto 참조·shim 2멤버·negative 패스 Core 포함·오버로드 명시·`ChannelProxy` unsealed·길이 공식·List 인자. **파서 Report+return(제품)**. 2단계: **in-motion 게이트(제품)**·using 누락·idle 회귀 추가·문서 4건. 3단계: 스텁 삭제·테스트 오기 단언·**dangling if(제품, reviewer 발견)**·한계 문서·중복 제거. 4단계: **using System.Threading(제품 컴파일 오류)**·**QueueHoldsKey 순서 불변식(제품, 진단 확인)**·자결적 유실·계약 갱신·권장 3건. 통합: 점프 프레임 상향(하네스). 버전: 0.23.0/0.2.18·문서 6종.

## 잔여 한계 (범위 내 미해결 지적 없음)

- 원격 CI·2-프로세스 실기 왕복(run2proc)·장시간 부하·실제 인터넷 지연/손실·DTLS 실연결 — 이번 미실행. CCU/SLO 판정 불가.
- 파괴 정리 플레이크 근본 원인 미특정(재현율 1/3회, 관찰 대상 기록).
- 병합 실패 폴백의 cap 우회(병리 경로, LogFault 가시화 — reviewer 권장으로 문서 정밀화만 수행).
- 손실 압축 상태 코덱의 ReconcileState 비교 한계(와이어 정밀도 — 문서화 완료).
- 장르별 선택 기능(sender context·observer RPC 범위·풀링·객체 참조·parenting·씬 인스턴스·hitbox/tick) — 목표에서 제외된 후속 과제([[research/unity-server-functional-review]] §3).

## 관련 문서

[[changelog]] · [[migration-v0-23]] · [[features/replication-batching-delivery]] · [[features/replication-p3-policy]] · [[features/movement-prediction]] · [[research/unity-server-functional-review]] · [[overview]]
