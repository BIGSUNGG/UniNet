# changelog — 변경 기록

의미 있는 모든 변경(기능 추가/수정/제거, 규약, 구조, 하네스)을 기록한다.
형식: 날짜 그룹 아래 `### Added / Changed / Removed / Fixed`. 최신 날짜가 위로 오게 관리한다.

## [2026-10-20]

### Added (이동 소유권 전이 스트림 리셋·epoch — MOV-001, 패키지 0.5.0)

- **소유권 전이 후 새 소유자의 이동 입력 스트림 즉시 채택** (MOV-001 ·[[0042-이동-소유권-전이-스트림-리셋-에포크]]·[[features/movement-prediction|movement-prediction]]) — `MoveSimulation.AdoptInput`의 시퀀스 가드가 임기를 몰라 소유권 이전 시 새 소유자 seq 1이 구 seq 아래로 기각(probe-1), 이상적 리셋 후에도 이전 임기 지연 패킷 seq 900이 재오염(probe-2)했다. 부모 2회 실기 반례(`.pi-subagents/artifacts/commercial-netcode/mov001-pre-fix-probe{,-2}.json`)
- **근본 수정 3계층** — ① `NetworkClient.OwnershipChanged(netId, prev, next)` Core 이벤트: 값이 실제로 바뀔 때만 발화(첫 배정·동일값 재전파 무시), 디스패치 스레드 발화 — 구독자 `QueueOnMain` 마셜·미등록 netId 널가드 계약 ② `INetworkMoveSimulation.ResetOwnershipStream()`: 큐·히스토리·`_lastSeenSeq`·`LastAdoptedSeq` 클리어, **권위 TState·최신 입력 보존**(`OnTeleport`와 계약 차이 — 텔레포트는 상태 리셋, 전이는 이어받기) ③ 이동 소유권 epoch: `NetworkTransform`에 `[Replicated(OwnerOnly, Notify)] uint _moveEpoch`, 서버는 임기→임기 이전 시에만 범프+리셋 짝(0→비0 입양은 타이밍 독립 베이스라인 — 무범프·무리셋), 봉투 `UniNetBlobEnvelope.Data`에 `[uint epoch][int len][byte[] 입력]` 접두 — **RPC 시그니처·시스템 메시지 불변·생성기 재빌드 불필요**. 서버 epoch 불일치 조용 기각 + `MoveEpochMismatches` 카운터. 클라는 소유권 획득 이벤트·OwnerOnly epoch 수신 양경로에서 세션 재바인딩(`_clientSeq=0`·accumulator·히스토리 클리어·권위 좌표 재앵커, 예측 상태는 첫 ack 롤백 수렴). 호스트 권위 인스턴스는 이벤트 재설정 스킵(이중 리셋 가드). epoch 델타는 놓친 이벤트의 자가치유 백스톱
- **테스트** — EditMode `MoveSimulationTests` 3종(리셋 후 seq1 채택·`OnTeleport` 대용 불가(probe-1)·권위 TState 보존)·`NetworkTransformComponentTests` 5종(probe-2 재현 — 실제 `SetOwner` A→B→A + 프레임 분리로 구 epoch 기각·새 스트림 채택, 값 불변 무범프, wire-2 실제 생성 수신기 재생 재바인딩, 호스트 이중 리셋 방지, 다중 객체 무간섭)·`OwnerBaselineTests` 1종(이벤트 값변경 전용·미등록 안전). PlayMode `MovementOwnershipPlayTests`(사이드 포트 +9·HostAsync 실RUDP·소유권 사이클: 해제→재배정 입양 재획득→구 epoch 기각→새 스트림 채택·ack 재개 실API 단얜)
- **파괴 변경 (마이그레이션)** — `INetworkMoveSimulation` 직접 구현자는 `ResetOwnershipStream` 추가 필수. 이동 봉투를 직접 인/디코딩하는 커스텀 경로는 epoch 접두 반영(입장당 +8바이트). `MoveSimulation<TInput,TState>`·`NetworkTransform` 조합 사용자는 무변경. 기본 ProtocolVersion 핸드셰이크로 0.4.x↔0.5.0 불일치 거부 — 양단 함께 업데이트

### Fixed (후발 접속 소유권 기준선 — OWN-001, 패키지 0.4.1)

- **캐치업·가시성 진입 경로의 객체별 소유권 기준선** (OWN-001 ·[[research/commercial-netcode-audit|commercial-netcode-audit]]·[[features/monobehaviour-rpc-replicate|monobehaviour-rpc-replicate]]·[[features/replication-p3-policy|replication-p3-policy]]) — `SendCatchup`은 상태/스폰만 보내 소유권 통보가 없었고, `ReassignOwnership`은 살아 있는 기존 소유자를 KEEP하므로 후발 클라의 소유자 캐시가 0으로 남아 `IsOwner`·소유자 분기가 왜곡됐다. 씬 가시성 재진입 경로에도 통보가 없었다(동적 재진입은 0이 아닐 때만 스폰 뒤 전송)
- **근본 수정** — 두 진입 경로가 동일한 `SendBaseline`(소유권 통보 → 스폰/전체 상태 순서)을 공유. 초기 `BroadcastSpawn`도 동일 순서로 정렬(상태→통보 2순회를 단일 순회의 통보→스폰으로 — 상태 적용과 owner 수신 사이 프레임이 끼면 초기 스폰 Notify의 owner 관측이 틀어지는 형제 경로). 기존 OwnerUpdate 와이어(2번)·`NetworkClient.ApplyOwner` 캐시 재사용으로 **와이어/생성기 무변경**. 소유자 0도 명시적 기준선으로 스테일 메타데이터를 덮는다. 관련성 필터는 그대로 — 비관련 객체는 기준선에서 제외되고 전 연결 브로드캐스트 없음. 수신 순서 검증: OwnerUpdate 수신기는 디스패치 스레드에서 즉시 캐시 갱신, 스폰/리플리케이션 적용은 메인 큐이므로 상태 적용·RepNotify 시점에 소유자가 이미 정확
- **테스트** — `OwnerBaselineTests` 8종 신규(초기 스폰 순서·씬/동적 후발 KEEP·가시성 재진입 씬+동적·소유자 0 스테일 덮기·다중 서브 객체별 기준선·실제 생성 수신기 재생으로 캐시/IsOwner/RepNotify 순서). `RecordingChannel`에 교차 메시지 순서 로그 추가. Arena 진단 `Diag/OWN-001 후발 소유권 기준선`(실서버/클라 RUDP·사이드 포트·단일 프로세스 마커 기반 — 유예 KEEP 경로 재현, 다중 프로세스는 별도 단계. 재진입 관측은 타임드 틱 + 진단용 스테일 캐시 주입 → 실제 와이어 복원). 수정 전 실패 확인: 부모 사전 재현(`.pi-subagents/artifacts/commercial-netcode/own001-pre-fix-probe.json` — 후발 `Owners.Count=0`)
- **패키지 0.4.1** — wire 무변경으로 생성기 재빌드 불필요. 기본 ProtocolVersion 핸드셰이크는 패키지 버전 비교이므로 양단 함께 업데이트(0.4.0↔0.4.1 불일치 거부). 감사 OWN-001은 부모 실기·CLEAN·배포 전까지 OPEN 유지

### Changed (MOV-001 개별 배포 확인)

- **v0.5.0 게시 완료** — 구현 `3c3b4df`·[GitHub Release](https://github.com/BIGSUNGG/UniNet/releases/tag/v0.5.0), main/tag CI success·원격 태그와 패키지 0.5.0 일치 확인. 회귀 9종·타깃 28/28·EditMode 155/155·PlayMode 28/28·Arena 실제 RUDP 소유권 사이클·신규 로컬 Git 설치/샘플 컴파일 PASS, Flash r1 권장(할당) 수용 후 r2 CLEAN. 컴파일 오류 3종·봉투 길이접두 누락 4실패·attach 경합 재현 1실패 등 수정 이력도 원시 보존후 전부 교정했다. MOV-001 CLOSED, 사용자 자산 해시 보존·커밋 제외 확인. `INetworkMoveSimulation` 커스텀 구현자 마이그레이션(ResetOwnershipStream 추가 의무)·봉투 레이아웃 변경은 Package/CHANGELOG 0.5.0 참조.

### Changed (OWN-001 개별 배포 확인)

- **v0.4.1 게시 완료** — 구현 `380b2b4`·[GitHub Release](https://github.com/BIGSUNGG/UniNet/releases/tag/v0.4.1), main/tag CI success·원격 태그와 패키지 버전 0.4.1 일치 확인. 회귀 8/8·EditMode 146/146·PlayMode 27/27·Arena RUDP 진단·최종 Flash r3 CLEAN·커밋된 로컬 Git URL 신규 Unity 설치/샘플 컴파일 PASS. 초기 0/0 수집·테스트 helper 2실패·기존 통보 횟수 1실패도 원시 이력에 보존하고 모두 교정·전수 재실행했다. OWN-001 CLOSED, 사용자 자산 3개 해시 보존·커밋 제외 확인. 최종 전용 서버+클라 2개·공개 태그 설치는 아직 별도 미완이다.

### Changed (Windows 전용 서버 빌드 모듈 설치)

- 사용자 승인 후 공식 Unity Hub CLI로 현재 Unity 6000.0.83f1의 `windows-server` 모듈 설치. Hub 선택 상태·서버 variation 12개·Win64 Mono 실행 파일/DLL 실체와 해시 확인, 지정 GLM-5.3-Flash 환경 리뷰 CLEAN. 에디터 종료·씬 저장 없이 기존 Arena/BlackMaterial 자산 해시 보존. 설치 원시 로그/전후 상태/리뷰는 증거대장의 `environmentChanges` 참조. 모듈 설치는 전용 서버 빌드·다중 프로세스 통신 검증 완료를 뜻하지 않는다.

### Changed (SEC-001 개별 배포 확인)

- **v0.4.0 게시 완료** — 구현 `ca13ee7`, [GitHub Release](https://github.com/BIGSUNGG/UniNet/releases/tag/v0.4.0), main/tag CI success·원격 태그와 패키지 0.4.0 일치 확인. 지정 모델 소스/추가 문서 리뷰 CLEAN, 회귀 10/10·EditMode 138/138·PlayMode 27/27·Arena 실제 RUDP 마커 진단·정적 검증에 더해 커밋된 로컬 Git URL의 신규 Unity 설치/샘플 컴파일 PASS. SEC-001 CLOSED로 전환하고 증거대장에 커밋·릴리스·CI·설치 원시 로그 연결. 최종 공개 태그 설치와 전용 서버+별도 클라 2개 검증은 아직 미완이다. 기존 Arena/BlackMaterial 사용자 자산은 커밋에서 제외하고 바이트 해시 보존 확인.

## [2026-10-19]

### Added (세션 토큰 암호학적 난수 전환 — SEC-001, 패키지 0.4.0)

- **세션 토큰을 256비트 암호학적 난수 opaque Base64로 전환** ([[0041-세션-토큰-암호학적-난수-전환]]·[[features/connection-lifecycle|connection-lifecycle]]) — 기존 시드+1 `ulong` 발급은 인접 토큰 예측으로 타인 유예 세션 소유권 탈취 가능(SEC-001 실기 확인). `RandomNumberGenerator` 32바이트 → Base64 44자(`SessionTokenBytes`/`MaxSessionTokenChars`). 비활성 = 빈 문자열. 발급은 활성/유예 토큰과 무충돌·스레드 안전(`_gate` 통일)
- **`ResumeSession` 강화** — 토큰 일치·만료 전·새 연결 활성 3조건 통과 시에만 세션 소비. 잘못된/추측/만료/미발급 토큰·잘못된 connId는 거부만 하고 세션 미소진. 만료 제거·고아 재배정은 `SweepGraceSessions` 담당
- **와이어 1(Welcome)·10(ResumeRequest) 토큰 `string`화 + 재개 파싱 전 페이로드 상한** — 위조 길이 접두의 대형 문자열 할당 차단. 토큰 원문은 로그·예외 금지, 에디터 디버거는 존재 표시(`issued (44 ch)`)로 마스킹
- **타입 전파** — `IssueSessionToken`/`ServerConnection.SessionToken`/`NetworkClient.SessionToken`/`IUniNetSystemChannel.SendWelcome` → `string`. 생성기 0.2.8(Emitter Welcome/Resume 직렬화·조건). 테스트 채널 8곳 시그니처 동반
- **테스트** — `ReconnectGraceTests` 8종(기존 4종 갱신 + 토큰 형식/유일성·추측 거부·connId 오류 미소진·중복 재개 거부 신규 4종). Arena 진단 `Diag/SEC-001 위조 토큰 재개 거부`(실서버/클라 API·토큰 미출력)
- **문서·검증** — features/connection-lifecycle·ADR-0041·overview·README·skills/uninet/SKILL.md·00-INDEX·arena-shooter·deployment·audit·evidence.json 동기화. 회귀 **10/10**, EditMode **138/138**, PlayMode **27/27**, Arena 실제 RUDP 마커 진단 **DIAG PASS**, 소스 리뷰 **CLEAN**·패키지 정적 검증 PASS. 개별 게시 확인 전 SEC-001은 OPEN 유지. `ulong`→`string` 마이그레이션은 Package/CHANGELOG.md 0.4.0 참조. 설치 pin `#v0.4.0`, Arena 앱 식별자 `arena-2-uninet-0.4`; 커스텀 ProtocolVersion은 SDK/wire 변경 시 양단 함께 올려야 함. 0.3.0은 미배포 개발 이력으로 기록하고, 개요의 이미 해소된 상류 차단 과제·과거 테스트 수를 현재 점검 상태와 구분

### Added (상용 넷코드 내부 점검 — 진행 중)

- **상용 셀프호스팅 넷코드 점검 대장** — [[research/commercial-netcode-audit]]에 런타임 C# 44개 파일의 조사 대응표, 코드로 확인된 문제와 추가 재현 후보, 기준 테스트 결과, 최초 내부 완료 후 타사 비교라는 순서를 기록. `research/commercial-netcode-evidence.json`에 지정 모델의 실제 실행 메타데이터·테스트 원시 결과·기능별 배포를 연결하는 증거대장 신설. 아직 기능 미해결 항목이 남아 있으며 완료 또는 배포를 주장하지 않음

### Fixed (SEC-001 검토 r1 수정 — 미커밋 구현에 대한 fix pass)

- **와이어 10 재개 상한 정정** — MessageProtocol 문자열은 int32 4바이트 길이 접두 + UTF8이다(부모 실측 48바이트/44자). 기존 `1+44=45` 상한은 정상 재개 요청 전면 거절 버그. `MaxResumeRequestPayloadBytes=48`로 정정·상한 초과·불량 입력(잘림·위조 접두 — 리더가 선언 길이를 버퍼 대조 후 슬라이스하므로 위조 접두는 할당이 아닌 예외)은 **조용히 폐기**(미인증 경로의 메시지마다 예외 기록 = 공격자 제어 콘솔 폭격). ADR-0041·Package/CHANGELOG 1바이트 서술 동시 정정
- **디버거 클라 token 표시줄 괄호 누락 컴파일 오류 수리** + 전체 파일 괄호 균형 대조
- **Arena 진단 재설계** — 기존 데모 런타임을 명시적 정상 정지(고아 7777 리스너 방지)·재진입 가드 추가, 소유 대상을 비동적 마커(실제 서버 레지스트리 등록)로 전환 — ClientStop 동적 스폰 소거가 아바타를 파괴해 유예 단언이 깨지는 기존 LIF/REC 갭 회피. "마커 기반이지 아바타 복구 검증 아님" 명시
- **`ResumeRequestWireTests` 2종 신설** — 실제 MessageBufferWriter 페이로드를 생성 허브 `__ResumeRequest_Requested`(reflection)로 투입·PumpMain까지: 정상 48바이트 재개 성립·잘림/초과(49B)/위조 접두(100만 자 선언) 무소진·이후 정상 재개 성공 (core-direct 8종이 못 잡던 와이어 계층 회귀 방지)
- **Package/CHANGELOG 0.3.0 미배포 개발 이력 보충** (공개 태그는 0.2.2까지 — 0.3.0 Release로 오기 금지)·evidence.json 검토 r1 fix-pass 기록·audit 진행 근거 갱신

### Fixed (테스트 격리 보수 — 런타임 무변경)

- **EditMode `MoveSimulationTests.잘못된_페이로드는_버려지고_예외가_새지_않는다` 전수 실행 실패 수리** — 불량 페이로드 격리 계약(입력 폐기·예외 격리·오류 관측)은 엔진이 예외를 `UniNetEnvironment.LogFault`로 관측시키는 설계인데, `LogFault`는 프로세스 전역이라 이전 테스트가 `UniNetManager` 정적 초기화자(= `Debug.LogException` 배선)를 실행한 뒤면 테스트 러너가 그 예외 로그 자체를 실패로 처리(`Unhandled log message: '[Exception] EndOfStreamException...'`). 테스트가 이전 `LogFault` delegate를 보존→임시 capture로 교체→`finally` 복원하고, 잘린 입력에서 `EndOfStreamException` 관측 1건·null 페이로드 무관측·`LastAdoptedSeq==0`을 명시 단언하도록 수정 — 동작 계약을 숨기지 않고 검증. 런타임·공개 API·기능 문서 무변경 (테스트만 변경)

## [2026-10-13]

### Added (복제 주기 기본값 30Hz — /goal 후속, ADR-0040)

- **`NetworkUpdateFrequencyHz` 기본값 0 → 30** ([[0040-복제-주기-기본값-30Hz]]) — 정책을 설정하지 않는 프로젝트도 틱 레이트 전체 복제 대신 30Hz 스트리밍(스텝 병합)을 기본으로. `0` 설정으로 매 틱 전송 옵트인. 즉시 모드(P2 호환 경로)는 주기 무시 계약 유지. Arena의 명시적 30Hz는 기본값과 일치(데모 문서화 목적 유지)
- **테스트** — `UpdateFrequencyTests`에 기본값 30Hz 검증 추가(미도달 틱 보류·도달 틱 최신값), 주기 0 옵트인 테스트 명시적 설정으로 전환. EditMode 132/132
- **문서** — ADR-0040·features/replication-p3-policy·README·skills/uninet/SKILL.md

### Added (복제 배칭과 오브젝트별 전송 신뢰성 분리 — /goal "1~3까지 진행")

- **오브젝트별 복제 신뢰성 정책** ([[0039-복제-배칭과-신뢰성-분리]]·[[features/replication-batching-delivery]]) — `NetworkBehaviour.NetworkReplicationDelivery` (`Delivery.ReliableOrdered` 기본 / `Unreliable`). `Unreliable`은 와이어에서 Sequenced(유실 허용·순서 보장)로 전송 — `NetworkTransform`이 기본 적용
- **복제 틱 배칭 (wire 13)** — 커넥션별·틱별 신뢰/비신뢰 각 1개 배치(최대 2메시지). 생성 허브에 `SendReplicateBatch`·배치 수신자 추가, `UniNetBatchRegistry`(methodId→applier)로 어떤 어셈블리가 수신해도 전체 타입 적용. 배치 상한 1000B (Sequenced 단편화 불가 보호)
- **정지 시 신뢰 재전송** — Unreliable 오브젝트가 0.25s 이상 조용해지면 풀 상태를 신뢰 배치로 1회 재전송 (마지막 델타 유실로 클라가 영구 정지하는 구멍 봉합). **휴면 플러시 신뢰 강제** — `FlushNetworkDormancy` 깨어난 첫 델타는 신뢰로
- **Arena** — 플레이어 복제 주기 30Hz (NetworkTransform·ArenaPlayer 슬롯 모두) — 60Hz 시뮬 스텝 2개가 델타 1개로 병합. 서버→클라 메시지 수 이동 시 ~240/s → 커넥션당 틱당 최대 2개로 절감
- **테스트** — EditMode `ReplicationDeliveryBatchTests` 7종 (배치 묶음·신뢰성 라우팅·정지 재전송·플러시 강제·배치 상한 분할). 기존 테스트 채널에 배치 언팩 추가. EditMode 131/131·PlayMode 27/27
- **문서** — ADR-0039·features/replication-batching-delivery·README·skills/uninet/SKILL.md·00-INDEX·arena-shooter

### Changed (파괴적 — 와이어 포맷)

- **`UniNetInfo.Version` 0.3.0** — 와이어 포맷 변경(배치 메시지 + 신뢰성 라우팅). 구버전 혼속은 ADR-0028 핸드셰이크가 VersionMismatch로 거부. `package.json` 동기
- 복제 틱 전송 경로가 개별 `SendReplicate`에서 신뢰성별 배치(`SendReplicateBatch`)로 전환 — `IUniNetSystemChannel` 구현체 7곳(테스트 채널) 동반 갱신. 스폰·캐치업·OwnerUpdate 등 이벤트 경로는 기존 신뢰 개별 전송 유지

### Added (상태 기반 이동 예측 일반화 — /goal "이동 동기화 확장성")

- **`MoveSimulation<TInput, TState>` 엔진** ([[0038-상태-기반-이동-예측-일반화]]·[[features/movement-prediction|movement-prediction]]) — 게임 정의 입력·상태 구조체 + 순수 스텝 규칙(ADR-0020 코덱 계약) 기반 고정 스텝(기본 60Hz) 시뮬레이션. 서버 입력 큐(용량 8·단조 검증·불량 페이로드 기각)·소유 클라 히스토리 링(128)·롤백(서버 상태 blob 복원 → 미승인 입력 재생)
- **점프·대시·구르기·텔레포트 지원** — 속도·쿨다운 같은 이동 상태가 롤백에서 정확 복원(무상태 재생 반례 단언 테스트). 텔레포트는 `SetNetworkPosition` 상태 리셋+재앵커
- **와이어** — 입력 blob `[Message] UniNetBlobEnvelope`(UniNet.Unity 소속 — MP 소스젠 애널라이저 스코프)로 `RpcSubmitMove(seq, envelope)` 스트림·상태 blob은 OwnerOnly 복제(인스턴스 교체=전체 치환). 제너레이터 무변경
- **Arena** — 점프(Space)·대시(Shift, 쿨다운)·텔레포트(T) 실구현. 이동 입력 스트림 전환(기존 변경시 전송→고정 스텝 스트림)
- **테스트** — EditMode `MoveSimulationTests` 7종 + `NetworkTransformComponentTests` 재작성 4종, PlayMode Arena `호스트_점프_대시_텔레포트_상태_시뮬레이션` + `NetworkTransformPlayTests` 재작성 3종. EditMode 118/118·PlayMode 27/27
- **문서** — ADR-0038·features/movement-prediction(ADR-0013·0037 문서 갭 해소)·README·skills/uninet/SKILL.md·00-INDEX·arena-shooter

### Removed (파괴적 — 무상태 이동 계약 철회)

- `NetworkTransform.MovementStep`/`MovementRule`/`SubmitMove`/`PredictOwner`/`SoftRate`/`FullReconciliation` — `MoveSimulation` 주입 계약으로 대체(소비자 Arena·테스트 전부 동반 마이그레이션). 재시뮬 dt `Time.deltaTime` 근사도 고정 스텝으로 해소(ADR-0037 부담 사항 종결)
- 구 테스트 `FullReconciliationTests`·`FullReconciliationRollbackTests` — 엔진 테스트로 계승·삭제

### Fixed (리모트 렌더링 — 텔레포트 슬라이드·한 박자 지연)

- **원인 ① 텔레포트가 미끄러짐** — SnapshotBuffer.TrySample이 인접 두 샘플을 선형 보간하므로 텔레포트 샘플이 들어오면 이전 위치↔목적지를 샘플 간격(~16ms)에 걸쳐 통과하며 렌더 → 수정: 축별 마지막 샘플 대비 점프가 max(SnapThreshold, 3×변위EMA) 초과면 해당 축 버퍼 클리어 후 새 샘플 단독 홀드(불연속은 스냅으로 렌더, 보간 통과 없음). 변위 EMA 항은 고속 이동의 병합 샘플(한 델타에 2스텝 분) 오판 방지 — 리뷰 라운드 1
- **원인 ② 한 박자 느림** — 리모트 렌더가 Now − InterpolationDelay(0.12s 고정)로 그려져 밀집 스트림에서도 항상 최대 지연 적용 → 수정: InterpolationDelay를 최대치로 재정의하고 도착 간격 EMA 기반 적응 지연(3×갭, 하한 0.03s, 상한=InterpolationDelay) — 60Hz 스트림에서 유효 지연 ~0.05s로 수축. 도착 추적은 3축 전체 노티파이가 박자(수직 전용 이동 포함), 동일 델타 내 중복 발화는 억제 — 리뷰 라운드 1
- **원인 ③(추가 발견) 복제전용 리모트 미인터폴레이션** — Simulation==null 리모트가 원시 권위 좌표를 렌더(구 코드는 항상 인터폴레이션) → Update 분기 재구성으로 리모트는 항상 인터폴레이션
- **테스트** — EditMode `NetworkTransformRemoteRenderTests` 6종(텔레포트 스냅+재개·적응 지연 수축/최대치 유지·복제전용 리모트 보간·고속 병합 샘플 비오판·수직 전용 지연 수축). EditMode 124/124·PlayMode 27/27

## [2026-10-12]

### Added (예측 full CSP&R 내장 — /list 항목 12)

- **NetworkTransform.FullReconciliation** ([[0037-예측-full-CSPnR-내장]]) — 옵트인(true 시) 소유 클라 리컨사일이 소프트/스냅 대신 롤백+재시뮬레이션: 입력 시퀀싱(seq++)·서버 에코(_ackSeq OwnerOnly Replicated)·입력 히스토리 링버퍼(128)·불일치 시 서버 위치로 롤백 후 미승인 입력 재시뮬·승인분 히스토리 폐기. 기존 소프트/스냅은 기본값(false) 유지
- **테스트** — EditMode `FullReconciliationTests` 2종(리컨사일 축 스냅/소프트 경계·롤백 임계 제곱 거리 판정) + `FullReconciliationRollbackTests` 2종(리플렉션 OnAckSeqChanged 롤백 경로·텔레포트 재앵커). e2e는 Arena PlayMode(FullReconciliation=true 전환)
- **Arena 전환** — ArenaPlayer._nt.FullReconciliation = true 설정 (롤백+재시뮬로 이동 예측 전환)
- **문서** — ADR-0037·README·skills/uninet/SKILL.md·00-INDEX

### Added (에디터 디버깅 툴 — /list 항목 11)

- **UniNetDebuggerWindow** ([[0036-에디터-디버깅-툴]]) — Window → UniNet → Network Debugger. Play Mode 세션 실시간 표시: 연결 목록(connId·RTT·플레이어·씬 준비·레이트 드롭·세션 토큰)·트래픽 통계(ADR-0029)·등록 오브젝트(netId·타입·소유자·동적)·서버 설정·클라 상태. 읽기 전용·자동 리프레시
- **문서** — ADR-0036·README·skills/uninet/SKILL.md·00-INDEX

### Added (씬 전환 동기화 — /list 항목 10)

- **서버 주도 씬 전환** ([[0035-씬-전환-동기화]]) — `NetworkServer.LoadScene(sceneName)` 전 연결 브로드캐스트(와이어 11)·클라 보고(와이어 12) 시 준비 표시·전원 준비 시 `AllClientsSceneLoaded` 이벤트 발화. 연결 해제 시 게이트 재판정(마지막 연결 퇴장으로 게이트 완성 가능). 후발 접속자도 진행 중 전환에 참여(캐치업 시 SendLoadScene 재발송). 제너레이터 0.2.7
- **클라 자동 흐름** — 와이어 11 수신 → `UniNetManager.OnSceneTransitionRequested` → SceneManager.LoadScene → ADR-0025 재등록 → 드라이버 sceneLoaded 콜백에서 `ReportSceneLoaded` 자동 전송
- **테스트** — EditMode `SceneTransitionTests` 4종(브로드캐스트·전원 보고 게이트 발화·이씬 보고 무시·후발 접속자 재발송)
- **문서** — ADR-0035·README·skills/uninet·00-INDEX

### Added (재접속 세션 보존+재부착 — /list 항목 9)

- **유예 세션·재부착** ([[0034-재접속-세션-보존-재부착]]) — `NetworkServer.SessionGraceSeconds`(기본 0=비활성) 설정 시 연결 해제 후 소유 오브젝트+플레이어 매핑을 토큰 키 유예 세션으로 보존(ReassignOwnership이 유예 소유자 스킵·드라이버 Update가 SweepGraceSessions 만료 정리). Welcome에 세션 토큰(증가 ulong) 탑재·클라는 SessionToken 보관
- **재부착 와이어 10** — 클라 재접속 시 생성 허브가 자동 ResumeRequest(토큰) 전송 → 서버 `ResumeSession(token, newConnId)`이 소유 오브젝트를 새 connId로 이전·OwnerUpdate 전파·매핑 복원 (제너레이터 0.2.6)
- **클라 API** — `UniNetManager.ReconnectAsync(address, port, options)` — 이전 토큰 계승 ClientAsync. ClientAsync 자체도 토큰 계승하므로 일반 재호출도 자동 재개
- **테스트** — EditMode `ReconnectGraceTests` 4종(유예 보존·재부착 이전·만료 후 입양·유예 비활성 즉시 재배정)
- **문서** — ADR-0034·README·skills/uninet/SKILL.md·00-INDEX

### Added (하트비트 타임아웃 튜닝 노출 — /list 항목 8)

- **`UniNetEndpointOptions.DisconnectTimeoutMs`** ([[0033-하트비트-타임아웃-튜닝-노출]]) — 유지(하프오픈) 타임아웃 튜닝(0=전송 기본 5000ms·양수=적용). **상류 릴리스 경로(사용자 선택 A)**: DRPC 공개 API에 전달 경로가 없어 DS_RPC 3.6.1에 `RpcEndpointOptions.DisconnectTimeoutMs`·`CreateTransportOptions(disconnectTimeoutMs)` 전달 추가(로컬 피드 패킹 — nuget.org 3.6.0에는 없는 기능)
- **기반 스택 동반 갱신** — DRPC 3.5.0→3.6.1·Communication 2.7.0→2.7.1·MessageProtocol 3.2.0→3.2.1(형제 nupkg nuget.org→로컬 피드 확보)·update-dlls.sh 핀 갱신·DLL 재추출
- **테스트** — EditMode `HeartbeatOptionsTests` 2종(전달 계약 UniNet→DRPC→RUDP 전송 옵션 12345 왕복·기본 0=전송 기본 5000ms 유지). 전체: EditMode 102/102·PlayMode 25/25 — 기반 스택 갱신 후 전 스위트 회귀 없음
- **문서** — ADR-0033·README·skills/uninet/SKILL.md 설정 예시·00-INDEX. DS_RPC 저장소 문서(changelog·README·skills/drpc) 동기

### Added (RPC 레이트리밋 — /list 항목 7)

- **커넥션별 ServerRpc 레이트리밋** ([[0032-커넥션별-ServerRpc-레이트리밋]]) — `NetworkServer.ServerRpcRateLimitPerSecond`(호출/초, 0=무제한 기본). 생성 디스패치 람다가 핸들러 실행 전 고정 1초 창 게이트(제너레이터 0.2.5) — 초과 드롭(빈 응답)·경고 로그는 ADR-0016 정적 스로틀 재사용. 상태는 연결 레지스트리와 분리된 ConcurrentDictionary(디스패치 스레드 무경합·Stopwatch 시계)로 연결 분리 시 해제
- **통계 노출** — `RateLimitedServerRpcDrops`(전체)·`GetRateLimitedServerRpcDrops(connId)`
- **테스트** — EditMode `RpcRateLimitTests` 3종(무제한 기본·플러드 드롭 집계(13발/한도3 → 드롭10)·창 갱신)·PlayMode `RpcRateLimitPlayTests` 1종(동시 프레임 40발 플러드 → 실행 ≤한도·드롭 30+ 집계 — 생성 디스패치 경로 e2e)
- **Sandbox 예시** — Arena가 `ArenaConfig.ServerRpcRateLimitPerSecond`(120 — 정상 피크 약 20/s의 여유 상한) 설정
- **문서** — ADR-0032·monobehaviour-rpc-replicate·README·skills/uninet/SKILL.md·00-INDEX

### Added (플레이어 오브젝트·연결 매핑 — /list 항목 6)

- **connId↔플레이어 오브젝트 매핑 API** ([[0031-플레이어-오브젝트-연결-매핑]]) — `NetworkServer.SetPlayerObject(connId, netId)`(0=해제)·`ClearPlayerObject`·`GetPlayerObject`·`GetPlayerConnection`(역조회). 아바타 스폰 시 등록, 연결 분리 시 자동 해제. 선언적 계약 — SetOwner 소유권 이전이 매핑을 이동시키지 않음
- **퇴장 일괄 정리 헬퍼 2층** — Core `NetworkServer.DestroyObjectsOwnedBy`(네트워크 파괴만 — 등록 해제·와이어·가시성) + Unity `UniNetManager.DestroyObjectsOwnedBy`(로컬 GO 파괴 포함 — 게임 호출 대상). 서버 측 엔트리 기반으로 해당 연결 소유 동적 오브젝트만 정확히 파괴(씬 스캔 제거·씬 오브젝트 제외). 매핑은 ClientDisconnected 발화 후 해제(핸들러 내 GetPlayerObject 읽기 가능 — 순서 계약)
- **테스트** — EditMode `PlayerMappingTests` 4종(등록/조회/역조회·분리 자동 해제·명시 해제 2경로·소유 동적만 선택 파괴+전파)
- **Sandbox 전환** — Arena가 아바타 스폰 시 매핑 등록·퇴장 정리를 `DestroyObjectsOwnedBy`로 전환(FindObjectsByType 스캔 제거)
- **문서** — ADR-0031·connection-lifecycle 갱신·README·skills/uninet/SKILL.md·00-INDEX

### Added (명시적 소유권 이전 API — /list 항목 5)

- **`NetworkServer.SetOwner(netId, connId)`** ([[0030-명시적-소유권-이전-API]]) — 살아있는 오브젝트의 소유권을 서버가 다른 연결로 이전(아이템 줍기·탈것·점령 등 상용 패턴). 기존 OwnerUpdate 와이어(시스템 메시지 2)로 전 연결 전파 — 신규 와이어·제너레이터 변경 없음. connId 0 = 방치(다음 재배정 패스가 고아 입양), 명시 소유권은 조인/리브 관계없이 유지(기존 KEEP 정책 승계). 미지 netId → false
- **테스트** — EditMode `OwnershipTransferTests` 3종(이전+전파·미지 false·방치→재배정 입양)·PlayMode `OwnershipTransferPlayTests` 1종(실세션 IsOwner 해제/재취득 왕복)·2-프로세스 `OWNERSHIP-TRANSFER PASS`(세션 중 해제·재부여 전파 관찰 — 클라 샘플링이 춤 관찰까지 대기하도록 수정)
- **Sandbox 예시** — Arena K키 소유권 순환 데모(첫 아바타 소유자를 다음 연결로 이전 + HUD 안내)·2Proc 러너 소유권 춤 단계
- **문서** — ADR-0030·monobehaviour-rpc-replicate 소유권 서술 갱신·README·skills/uninet/SKILL.md·00-INDEX

### Added (네트워크 통계 API — /list 항목 4)

- **트래픽 카운터·RTT·큐 깊이 조회 API** ([[0029-네트워크-통계-API]]) — `UniNetTrafficCounters`(송수신 페이로드 바이트·패킷, Interlocked)를 생성 허브 전 송수신 경로에 계측(제너레이터 0.2.4). `NetworkServer.TrafficStats`·`NetworkClient.TrafficStats`. 측정 계약: RPC 파라미터 페이로드 바이트(와이어 프레이밍·전송 오버헤드 제외 — 애플리케이션 대역폭 관점)
- **자체 RTT 프로브(와이어 8/9)** — 클라 1초 주기 핑 → 서버 에코 + `ServerConnection.RttMs`(클라 보고 피기백). 클라 `NetworkClient.RoundTripTimeMs` EMA(α=0.25). Stopwatch 도메인 채택 — 디스패치 스레드에서 Unity 시계 예외 발생 실증 후 교체(메인 스레드 전용 제약 회피)
- **큐 깊이 관측** — `NetworkServer.DeferredSendCount`(P3 예산 지연 큐)·`UniNetEnvironment.PendingMainActions`(메인 펌프 대기열)
- **테스트** — EditMode `NetworkStatsTests` 3종(카운터 누적·예산 지연(2델타/8B — 기아 방지 강제전송 규칙 반영)·큐 깊이)·PlayMode `NetworkStatsPlayTests` 1종(실세션 양단 카운터 증가·RTT 측정·서버 커넥션 RTT 보고)
- **Sandbox 예시** — Arena 5초 주기 통계 라인(ArenaHud.LastNetStats HUD 표시 + 서버 로그 — 송수신 KB/pkt·지연큐·RTT)
- **문서** — ADR-0029·기능 문서 network-statistics.md 신규·README·skills/uninet/SKILL.md·00-INDEX

### Added (프로토콜/빌드 버전 핸드셰이크 — /list 항목 3)

- **버전 선언-비교 핸드셰이크** ([[0028-프로토콜-빌드-버전-핸드셰이크]]) — `UniNetEndpointOptions.ProtocolVersion`(기본 = 패키지 버전, 게임 빌드 버전 오버라이드)을 클라가 접속 직후 자동 선언(와이어 시스템 메시지 7)하고 서버가 `NetworkServer.ExpectedProtocolVersion`과 비교. 불일치 클라는 `VersionMismatch` 사유 킥으로 거부(ADR-0027 인프라 재사용 — 거부 사유가 클라에 전달돼 강제 업데이트 UX 분기 가능). Welcome에 서버 버전 필드 추가(`NetworkClient.ServerProtocolVersion`). 제너레이터 0.2.3
- **테스트** — EditMode `VersionHandshakeTests` 3종(불일치 거부·일치 수용·검사 비활성)·PlayMode `VersionMismatchPlayTests` 2종(실세션 불일치 거부·커스텀 일치 접속+서버 버전 수신)·2-프로세스 `VERSION-MISMATCH PASS`(킥 후 잘못된 버전 재접속 거부 — 기존 전 단계 PASS 유지)
- **Sandbox 예시** — Arena가 `ArenaConfig.ProtocolVersion`("arena-1")을 양단 옵션으로 선언(빌드 버전 사용 예시)·2Proc 러너 버전 불일치 단계·2Proc 프로젝트 동기화
- **문서** — ADR-0028 신규·connection-lifecycle 갱신·README·skills/uninet/SKILL.md 갱신·`UniNetInfo.Version` 0.2.2로 정합(package.json 동기, 핸드셰이크 기본값으로 기능적 의미 부여)

### Added (킥·연결 종료 사유 — /list 항목 2)

- **서버 강제 퇴장 API·와이어 종료사유 전달** ([[0027-킥-연결-종료-사유]]) — `NetworkServer.DisconnectClient(connId, reason)` (사유 공지를 와이어에 플러시한 뒤 150ms 유예 후 전송 폐쇄 — SendAsync는 큐 등록만이라 유실됨을 실증해 SendAndFlush+유예로 해결). `UniNetDisconnectReason` enum(None/ClientStopped/ServerShutdown/Kicked/ConnectionLost)·와이어 시스템 메시지 6 신설(제너레이터 0.2.2)
- **클라 종료 사유 노출(파괴적 변경)** — `UniNetManager.ClientDisconnected`가 `Action<UniNetDisconnectReason>`로 서명 변경. 서버 종료/네트워크 유실/킥 구분. `ServerStopAsync`는 전 연결에 ServerShutdown 공지 후 리스너 폐기. 자발 `ClientStop` 미발화 계약 유지
- **테스트** — EditMode `DisconnectNoticeTests` 3종·PlayMode `DisconnectReasonPlayTests` 2종·LifecycleEventPlayTests 서버 정지 사유 검증 추가·2-프로세스 `KICK-REASON PASS`(킥 왕복 — 기존 3종 PASS 유지, 양단 score=89)
- **Sandbox 예시** — Arena 클라 HUD에 퇴장 사유 표시(킥/서버종료/유실 구분 안내)·2Proc 프로젝트 Fixtures 동기화
- **문서** — ADR-0027 신규·connection-lifecycle 기능 문서 갱신·README·skills/uninet/SKILL.md 갱신(마이그레이션: 구독자 서명 변경 필수)

### Fixed (그리드 가시성 cull=0 계약 정합 — /list 항목 1)

- **클라→서버→다른 클라 이동 동기화 단절 근본 해소** ([[0026-그리드-가시성-cull0-계약-정합]]) — 그리드 가시성 활성 시 컬 거리 미설정(cull=0) 오브젝트도 그리드 반경 밖이면 연결별로 컬되던 것(P3 계약 "cull 0 = 항상 관련" 위반)을 복원. `IsRelevant` 그리드 분기가 cull>0 오브젝트에만 멤버십 판정을 적용(자체 컬 거리와 AND)하고, cull=0은 전 모드에서 항상 관련
- **Arena 예제 정리** — ArenaBootstrap의 뷰어 위치 주입 제거(소규모 아레나는 컬 거리 설정 오브젝트가 없어 데드 웨이트였음). SetVisibilityGrid 호출은 선행 세션에서 이미 제거된 상태. P4 전환으로 총알이 제거된 뒤 사용처가 없던 고아 상수 4종(BulletSpeed·BulletLifetime·BulletUpdateFrequencyHz·BulletCullDistance)도 정리
- **테스트 결정성 수리(사전 존재 결함)** — 에디터에서 Enter Play Mode 도메인 리로드 비활성 시 EditMode와 PlayMode 스위트가 한 도메인을 공유해 ADR-0016 정적 로그 스로틀 상태가 스위트 간 유출되고, 소유권 거부 로그 예측 테스트가 재현성 있게 실패하던 것을 확인 후 수리. `ResetRejectionLogThrottleForTests()` internal 훅(제품 동작 무변경)을 추가하고 양 테스트 SetUp에서 호출. 배치 모드(별도 프로세스)는 본래 무관
- **회귀 테스트 신규** — `그리드_활성_시_cull_미설정_오브젝트는_반경_밖이어도_항상_관련이다`. 기존 그리드 컬 테스트는 cull>0 픽스처로 갱신(그리드 판정 대상 계약 반영)

## [2026-10-08]

### Fixed (씬 로드 재등록 지연 실행 — v0.2.2)

- **0.2.1 훅의 리슨서버 교착 수리** ([[0025-접속-후-씬-로드-재등록]] 개정) — `sceneLoaded` 콜백 안의 즉시 등록이 «펌프 → 디스패치 → LoadScene → sceneLoaded → 등록(락·스냅샷)» 재진입을 만들어 클라이언트 메인 스레드를 동결시키던 것(ck-darkness 3-EXE 결정적 재현)을, `QueueOnMain` 지연 실행(다음 프레임 펌프 배수)으로 수리. 소비자 체감 변화 없음(씬레디 게이트 계약 유지)

### Fixed (접속 후 씬 로드 재등록)

- **접속 후 로드되는 씬의 오브젝트 자동 재등록** (ADR [[0025-접속-후-씬-로드-재등록]]) — 씬 오브젝트 등록이 `ServerAsync`/`ClientAsync` 시점 1회만 돼, 로비 접속 → 전투 씬 로드 흐름에서 늦게 로드된 씬의 NetworkBehaviour 가 양단 미등록(→ `IsServer=false` → RPC 송신 조용한 no-op)으로 남던 것을 수리. 드라이버에 `sceneLoaded` 재등록 훅(프로세스 수명 1회 구독), 재등록은 등록 플래그로 멱등 스킵(스냅샷 초기화 재실행 차단). 소비 사고: ck-darkness 씬 귀속 연출(구역·고치·낙석) 전체 미전달 회귀
- **내부 API**: `RegisterAllToServer/Client` 반환값 신규(신규 등록 수) · 신규 로그 `[UniNet] 씬 로드 네트 등록 — {씬}: 서버 +N, 클라 +M`

### Added (테스트)

- **`SceneLoadRegistrationTests`** (PlayMode) — 호스트 접속 후 LateScene 애디티브 로드 → 양단 등록·멀티캐스트 왕복·재등록 스킵 검증. 테스트 씬 자산 `LateScene.unity` + 빌드세팅 등록은 배치 베이커 `LateSceneBaker`(`-executeMethod UniNet.Tests.LateSceneBaker.Bake`) 로 생성

### 검증

- PlayMode 18/18 (`Builds/tests-playmode-a25b.xml`) · EditMode 80/80 (`Builds/tests-editmode-a25.xml`) — 배치 모드

## [2026-10-06]

### Deployed (v0.2.0)

- **v0.2.0 태그 배포** — 커밋 1e2c697·태그 푸시 → CI 2종 success → GitHub Release [UniNet v0.2.0](https://github.com/BIGSUNGG/UniNet/releases/tag/v0.2.0) 자동 생성. 릴리스 노트는 `Package/CHANGELOG.md` 0.2.0 섹션을 **영어·소비자 관점으로 재작성**(Highlights·Breaking changes & migration·0.1.2 미배포분 포함). README·deployment.md 설치 태그 v0.2.0 갱신

### Changed (스폰 시스템 전면 교체 — 파괴적)

- **Fusion식 spawnable 프리팹 스폰 시스템** (ADR [[0024-Fusion식-spawnable-프리팹-스폰-시스템]]) — `NetworkInstantiate`가 `NetworkSpawnable` 마커(프리팹 루트 부착, 에디터 저장 시 프리팹 GUID 해시 ID 각인)가 붙은 프리팹만 스폰. 스폰 메시지에 프리팹 ID 추가, 클라는 베이크 테이블(`UniNetPrefabTable`, Resources 자동 생성)로 자기 프리팹 사본을 인스턴스화 — **양단 프리팹 에셋 존재 필수**. 한 타입 프리팹 N개 허용
- **폐기**: `RegisterPrefab<T>` 공개 API·소스젱 기본 팩토리·ADR-0014 서브 자동 복원 (구성 불일치는 자동 복원 대신 거부)·런타임 템플릿 스폰 경로. 제너레이터 팩토리 방출 제거(0.2.0)
- **신규**: `NetworkInstantiate(original, position, rotation, configure)` 4인자 오버로드·고스트 catch-up 방어(파괴 인스턴스 transform 접근 방어)
- 호출처 전수 재작성: Arena(프리팹 에셋 베이커 포함)·TwoProcessRunner·PlayMode/EditMode 테스트 — 테스트 프리팹 5종 배치 베이커로 생성·커밋

### 검증

- EditMode 80/80 (신규 SpawnablePrefabTests 4종 — 게이트 거부·프리팹 2종 동시 스폰·테이블 해석) · PlayMode 17/17 · 2-proc 왕복 PASS 3종(DYNAMIC-SPAWN-DESTROY·MULTI-COMPONENT·SERIALIZATION, 양단 score=89 일치)

## [2026-10-01]

### Changed (씬 netId 정체성)

- **씬 오브젝트 netId 직렬화 안정 아이디로 교체** (ADR [[0023-씬-netId-직렬화-안정-아이디]]) — 에디터 씬 저장 시 NetworkBehaviour에 GameObject GlobalObjectId(씬 GUID+localId)의 FNV-1a 64 해시를 직렬화 각인. 기존 경로 해시는 미각인 오브젝트 폴백으로 강등. 이름 변경·형제 재배치·오브젝트 추가/삭제·DontDestroyOnLoad 이동·신구 빌드 버전 혼재에서도 양단 netId 일치. 와이어 포맷 불변(무합의 유지). 신규 에디터 어셈블리 `UniNet.Unity.Editor`(SceneNetIdPostprocessor) + `NetworkBehaviour._sceneNetId` 직렬화 필드
- **소유권 라운드로빈 테스트 기대값 갱신** — `소유권_라운드로빈_정책이_연결순서대로_배정된다`가 KEEP 소유권 정책 도입 때 미갱신된 기대값(8)을 현재 계약(4 — 재배정 2건×2연결, 생존 소유자 미재배정)으로 수정. 사전 존재 실패(base선 검증 — 본 변경 무관)

### Added (테스트)

- `SceneNetIdTests` 4종 — 직렬화 아이디 우선·폴백 해시·동적 선점·첫-읽기 고정 계약 (EditMode 80/80·PlayMode 17/17)

### 발견 (사전 존재 이슈 — 해소)

- **2-프로세스 검증 프로젝트 고장 → 복구·왕복 재검증 PASS** — `Builds/2ProcServer`·`2ProcClient`의 소스젠 출력 중복(CS0101×144) 근본 원인: ADR-0021 이전 NuGetForUnity 캐시의 제너레이터 DLL 3종(UniNet.CodeGenerator 0.2.1·DRPC.CodeGenerator 3.5.0·MessageProtocol.CodeGenerator 3.2.0)이 잔존해 패키지 동봉분과 **이중 로드** → 출력 2벌 생성. 중복 analyzer 3종 삭제로 복구, 왕복 재확보 (양단 score=89 일치·클라 PASS 3종). 재발 방지: 제너레이터는 패키지 동봉분 단일 공급원만 로드
- **소유권 라운드로빈 테스트 기대값 갱신** (위 Changed에서 처리)과 PlayMode `SubmitMove...` 파괴 타이밍 flaky — 단독/전체 재실행 통과, 재현 불규칙 (안정화 후속 과제 — ADR-0023)

## [2026-09-30]

### Changed (하네스 — Skills 동기화 규칙)

- **AGENTS.md 핵심 원칙 1에 Skills 갱신 bullet 추가** (ADR [[0022-사용자-가시-변경-skills-동기화-규칙]]) — 사용자 가시 변경(공개 API·동작 계약·UNINET0xx 진단·런타임 로그·설치 방법) 시 `skills/uninet/SKILL.md`를 같은 세션에서 반영하도록 의무화. 내부 리팩토링·테스트 등 스킬 무관 변경은 제외. [[harness]] 핵심 원칙에도 반영. doc-sync·reviewer·doc-guard 훅은 미변경 (사용자 결정 — AGENTS.md 한 곳만)

### Added (에이전트용 사용자 매뉴얼 스킬)

- **`skills/uninet/SKILL.md` 에이전트 스킬 신설** — 라이브러리 소비자(다른 프로젝트에서 UniNet을 쓰는 개발자·AI 에이전트) 관점의 영어 사용 매뉴얼. 표준 agent-skill 형식(YAML frontmatter `name: uninet` + 트리거 description)으로 pi·Claude Code·Codex 호환. 구성: Overview(정체성·설치·멘탈 모델·퀵스타트) / Core Usage(RPC 3종·Replicated/RepNotify/조건부·동적 스폰·소유권·수명주기) / Wrong Usage, Exceptions & Errors(UNINET001~015 진단 전수표 + 런타임 경고/오류 11종 + 조용히 틀어지는 오용 패턴 9종) / Advanced(대역폭 제어·커스텀 직렬화·FastArray·예측/리와인드·보안/전송 튜닝·스레딩/격리 모델) / Known Limitations. 원천: README·features 6종·제너레이터 진단 정의(UNINET001~015)·런타임 로그 원문(`[UniNet]` 접두어 한국어 메시지 그대로 수록 — grep 가능)
- **README.md에 스킬 안내 추가** — Documentation 표에 `skills/uninet/SKILL.md` 행 신설 (소비자 저장소 설치 안내 포함)

## [2026-09-29]

### Changed (README·Description 영어 재작성)

- **README.md 전면 영어 재작성** — `Features`·`Quick Start` 필수 목차 신설 + Requirements·Usage·Sample·Repository layout·Documentation·Release 구성(DS_Communication·DS_MessageProtocol·DS_RPC README 문체 기준). 구현 기능 전수 명시 — features 6건·roadmap 구현 완료 항목(P1~P4)·v0.1.1 소유권 API(명시적 소유자 스폰·ReassignOwnership·ServerChanged)까지 대응, 임베디드 누락 0건. 배포 소비자 관점 재구성, 상대 링크 전건 유효성 검증
- **Package/package.json 사용자 가시 텍스트 영어화** — `description`(서버 권위 프레임워크 소개+기능 요약)·`samples[].description` 재작성. displayName·버전 등 나머지 필드 무변경, 유효 JSON 유지

### Added (v0.1.1 — 명시적 소유권 스폰)

- **명시적 소유자 동적 스폰** — `NetworkInstantiate(original, ownerConnId)` · `NetworkServer.RegisterDynamicObject(components, ownerConnId=0)`. 소비자(ck-darkness)의 토큰 소유권 게이트(스푸핑 방지 불변식) 지원 — 접속 순간 그 접속 소유 스폰, 재접속·재시작에도 소유권 유지
- **`UniNetEnvironment.ServerChanged`** — 서버 인스턴스 설정 시점(접속 수락 전) 메인 스레드 이벤트. `NetSession` 같은 씬 컴포넌트의 폴링 없는 라이프사이클 후크

### Fixed (v0.1.1 — 소유권·세션 잔재)

- **`ReassignOwnership` 고아 정책** — 살아 있는 소유자는 그대로 유지, 고아(소유자 0·단절 접속)만 라운드로빈 재배정. 기존 전체 재배정은 재접속 시 살아 있는 토큰 소유권을 훔쳤다
- **스테일 동적 스폰 잔재** — `NetworkBehaviour.IsDynamicSpawn` 마커로 씬 등록 스킵(낡은 판 잔재가 새 세션 동적 netId와 충돌), `ClientStop`/`ServerStop`에서 `SweepDynamicSpawns()` 파괴 (이중 조인·오소유 읽기 방지)
- 코드는 ck-darkness 프로젝트에서 실전 검증된 임베디드 패키지 작업분을 역이식해 패키지 배포 채널로 통합

## [2026-09-28]

### Changed (UPM Git URL 배포 준비 — ADR-0021)

- **기반 스택 DLL 캡슐 UPM 전환** (ADR [[0021-DLL-캡슐-UPM-전환]] — 배포 채널 Git URL 확정)
  - **DLL 동봉** — `Package/Runtime/Dependencies/` lib DLL 12종 (DRPC 3.5.0 · MessageProtocol 3.2.0 · Communication 2.7.0 · LiteNetLib 2.1.4 · BouncyCastle 2.7.0), `Package/Runtime/UniNet.Unity/Analyzers/` 소스젠 3종 (DRPC·MessageProtocol·UniNet.CodeGenerator 0.2.1) RoslynAnalyzer 라벨·전 플랫폼 비활성. 소비자 git URL 한 줄 설치 — 추가 설정·피드 불필요
  - **NuGetForUnity 철거** — Sandbox manifest·packages.config·Assets/NuGet.config·Assets/Packages·Packages/NuGet·`.locked` 아티팩트 제거. 개발 형태 = 배포 형태 통일 (루트 `Sandbox/NuGet.config`는 제너레이터 빌드용 유지)
  - **UPM 표준 구조** — `Samples~/Basics`(최소 동작 샘플), `Documentation~/index.md`(퀵스타트), `LICENSE.md`, `CHANGELOG.md`, package.json samples 등록. README 설치 섹션·개발 환경 갱신
  - **도구** — `Package/tools/update-dlls.sh`(기반 스택 버전 갱신: nupkg 추출+제너레이터 빌드), `Package/tools/verify-install.sh`(신규 프로젝트 임포트·컴파일·샘플 검증), `Package/tools/validate-package.sh`(패키지 정적 검증 — CI 공용). 절차 문서 [[deployment]] 신규

### Added (GitHub Actions CI/CD + v0.1.0 배포)

- **`.github/workflows/upm.yml`** — CI( push/PR→main 정적 검증: package.json·DLL 15종+메타·RoslynAnalyzer 라벨·UPM 표준 구조·NuGetForUnity 부재·플레이스홀더 부재) + CD(`v*` 태그 시 버전 정합 검사 통과면 GitHub Release 자동 생성 — CHANGELOG 해당 섹션이 릴리스 노트). Unity 라이선스 불필요(정적 검증만, 사용자 확정), Unity 테스트는 로컬 배치 게이트 유지
- **배포 저장소 확정·공개** — [BIGSUNGG/UniNet](https://github.com/BIGSUNGG/UniNet) (ADR-0021 배경 갱신: 사설→공개). 저장소 주소 플레이스홀더 전부 치환 (README·Document·Documentation~)
- **`Package/CHANGELOG.md` `## 0.1.0` 섹션** — 태그 정합 검사용 버전 섹션 추가
- **검증**: validate-package.sh 태그 모드 PASS + 음성(불일치 태그 감지) · CI/CD v0.1.0 실행·Release 생성 실측 · **실제 소비자 URL(GitHub https + #v0.1.0) 신규 프로젝트 설치·컴파일 PASS** · 첫 CI 적발 이슈: .gitignore `*~` 규칙이 `Samples~`/`Documentation~` 추적 제외 → 예외 규칙 추가 수정
  - **검증**: Unity 컴파일 에러 0 · **EditMode 76/76** · **PlayMode 17/17** · 신규 프로젝트 임포트 PASS

## [2026-09-24]

### Added (직렬화 사용 예시 — Sandbox)

- **`Sandbox/Assets/Scripts/Usage/SerializationUsage.cs` 신규** — ADR-0020 양 기능 사용법 예제 2종: `QuantizedWeapon`(양자화 Vector3 조준점 + 선택 Equals 억제 + float→byte 충전량) · `InventoryRig`(List 요소 델타 — Loot/Drop/Swap/DropAll 뮤테이터가 각각 Insert/RemoveAt/Set/Clear 오페 되는 것을 서버 측 코드로 시연 + T[] 커스텀 요소 직렬화)
- **`Sandbox/Assets/Editor/SerializationUsageDemo.cs` 신규** — 에디터 메뉴 `UniNet ▸ Usage ▸ 직렬화 사용 예시 (호스트 루프백)` — 에디트 모드에서 호스트 루프백을 띠우고 6초간 양 기능 동기화를 콘솔에 관찰 (에디트 모드 허브 등록은 리플렉션 일괄 등록 — CS0433 다중 어셈블리 충돌 회피)
- **검증**: Unity 컴파일 에러 0 · 메뉴 실행 실측 — 양자화 델타(12.34 동기 + 같은 버킷 12.349 억제로 노티 무증가)·`[101,202,303]→[999,303]` 오프 리플레이·prev 얕은 복사·slots T[] 복원 콘솔 확인 · **EditMode 76/76 회귀 없음**

### Added (FastArray 구현 — ADR-0020 기능 2/2, 목표 완결)

- **`T[]`/`List<T>` [Replicated] 필드의 요소 단위 델타 동기화 구현** (ADR [[0020-직렬화-배열-델타-유니넷-단독-구현]] 기능 2 — 기능 문서 [[features/fastarray-delta|fastarray-delta]])
  - **와이어** — 델터: `[tag=0][opCount:u16]{[op:u8][idx:u16][elem]}*` (옵: Set/Insert/RemoveAt/Clear) · 전체(스폰/캐치업): `[tag=1][count:u32][elem]*`. 요소 직렬화 규칙은 스칼라 필드와 동일(기본형·string·[Message]·Serializer 지정 — 컬렉션에서 Serializer는 요소 단위 적용). 참조형 요소는 null 플래그로 왕복
  - **diff** — 공통 prefix/suffix 제외 후 중간 구간: 동일 길이면 Set 배치, 다르면 Remove+Insert (append는 순수 Insert) · Clear 단일 옵 · ReliableOrdered 전제 단순 리플레이(시퀀싱 불필요) · 요소 65,535 상한 초과 시 예외(틱 격리로 서브오브젝트만 스킵)
  - **제너레이터 0.1.9 → 0.2.1** — Parser 컬렉션 감지·요소 검증(진단 UNINET014/015 신설) + Emitter 필드별 생성 헬퍼 5종(dirty/write/writeFull/apply/copy) · `FieldDisplay` 수정(`global::string[]` CS1001 방지). nupkg 재배포 + Sandbox 스왑
  - **검증**: dotnet 빌드 녹색 · GenDump 진단 0 + UNINET014/015 음성 케이스 발화 확인 · 생성 코드 독립 컴파일 구문 검증 · Unity 컴파일 에러 0 · **EditMode 76/76** (기존 68 + 신규 8 — 서버/클라 인스턴스 분리 패턴) · **PlayMode 17/17** (기존 16 + 신규 1) · **2-프로세스 실기 PASS** (양자화 위치+요소 델타 동시 — `Sandbox-2proc-*.log` `[UNINET-2PROC] SERIALIZATION PASS`, 기존 검증 4종 회귀 없음)
- **ADR-0020 승인됨 전환** — roadmap 잔여 2행 → 구현 ✅ 갱신, upstream-blockers MP 2건 폐기 표기, 연구 문서 갱신 노트 정합 — 목표 계약의 doc-sync 항목 전부 완료

### Added (커스텀 NetSerialize 구현 — ADR-0020 기능 1/2)

- **`[Replicated(Serializer = typeof(T))]` 사용자 정의 필드 직렬화 구현** (ADR [[0020-직렬화-배열-델타-유니넷-단독-구현]] 기능 1 — 기능 문서 [[features/custom-netserialize|custom-netserialize]])
  - **속성** — `ReplicatedAttribute.Serializer` (Type, 선택). 정적 `Write(ref MessageBufferWriter, in T)` + `static T Read(ref MessageBufferReader)` 계약(선택 `Equals(in T, in T)` — 양자화 동일값 재전송 억제). 커스텀 직렬화기 지정 필드는 기본형·[Message] 외 타입(예: `Vector3`)도 허용 — Unity 타입 리플리케이션 해금
  - **제너레이터 0.1.5 → 0.1.9** — 서명 검증(오버로드·베이스 체인 전수 스캔, 진단 UNINET013 신설) + 생성 코드 분기 3곳(델타 비교·쓰기, 전체 쓰기, 읽기). 필드별 고유 변수(`__s{index}`)로 커스텀 Equals 필드 N개 공존 지원. nupkg 재배포 (Sandbox `Assets/Packages` + 로컬 피드 `C:/Projects/DS/unity-nuget`)
  - **서버 가용성 격리** — `UniNetEnvironment.LogFault` 훅 신설(Core는 UnityEngine 무참조 유지 — Unity 계층이 부트스트랩에서 연결). 리플리케이션 틱·WriteFull 경로(스폰/캐치업/가시성)에서 직렬화기 예외 시 해당 서브오브젝트만 건너뛴다(스냅샷 미갱신 → 다음 틱 재시도)
  - **검증**: dotnet 빌드 녹색 · GenDump 분기·음성케이스(UNINET013) 확인 · Unity 컴파일 에러 0 · **EditMode 68/68** (기존 60 + 신규 8: 왕복·양자화 델타·버킷 생략·Equals 기본 경로·WriteFull·RepNotify prev·듀얼 Equals 필드·operator== 없는 구조체 CS0019 회귀) · **PlayMode 16/16** (기존 15 + 신규 1: 루프백 RUDP 양자화 왕복·버킷 억제) · reviewer 4라운드 총 16건 이슈 수정 반영 (반드시 5·권장 11)
- **테스트 픽스처** — `Sandbox/Assets/Tests/Fixtures/QuantizedTank.cs` (커스텀 Equals 직렬화기 2종 공존 + 오버로드 허브(미끼 오버로드·상속 Read) — 검증기 오탐 회귀 방지)

### Added (직렬화 2종 UniNet 단독 구현 — 설계 문서)

- **커스텀 NetSerialize·FastArray의 MP 무수정 구현 전환 설계** (ADR [[0020-직렬화-배열-델타-유니넷-단독-구현]] 제안됨 — 승인 대기)
  - **기존 판정 철회 근거**: [[upstream-blockers]]의 "MP 코덱 계층 수정 전제"는 현 구조와 불일치 — 제너레이터가 필드별 직렬화 배선을 직접 생성(Emitter 3개 생성 지점), MP/DRPC는 `WriteBytes` 순수 바이트 전송층, `MessageBufferWriter`/`Reader` 프리미티브는 public으로 사용자 직렬화기 접근 가능. 기존 판정은 MP 네이티브 코덱 등록 방식에만 성립하는 전제였음
  - **[[features/custom-netserialize|features/custom-netserialize]] 신규** (설계중) — `[Replicated(Serializer = typeof(T))]` 정적 `Write(ref writer, in T)`/`Read(ref reader)` 쌍 계약·양자화·Unity 타입 해금·진단 UNINET013
  - **[[features/fastarray-delta|features/fastarray-delta]] 신규** (설계중) — `T[]`/`List<T>` 요소 단위 옵 인코딩(Set/Insert/RemoveAt/Clear)·섀도 diff·ReliableOrdered 전제 리플레이·진단 UNINET014/015
  - **미확정 사항 4건** 문서화 (선택 Equals 계약·RepNotify prev 방식·인덱스-옵 vs 안정 ID·RPC 파라미터 확장 순서) — 목표 계약 체결 시 권고안 전부 확정 (Equals 포함·얕은 복사·인덱스-옵·RPC v1 제외)
  - 문서 변경 — 설계 확정 단계 기록 (구현은 위 Added 항목 참조)

## [2026-09-22]

### Added (네트워크 프레임워크 비교 연구 문서)

- **주요 네트워킹 프레임워크 비교 보고서 신설** — `Document/research/network-framework-comparison.md`
  - **비교 대상 9계열**: UE Network Framework(패리티 목표)·Photon Fusion 2·Unity NGO 2.x·Netcode for Entities·FishNet·Mirror·Nakama/Colyseus/DarkRift·트랜스포트 계층(LiteNetLib 등)
  - **기능 매트릭스 3표**: 리플리케이션·RPC 핵심 17항목 / 예측·래그컴펜세이션·시간 4항목 / 배포·운영·생태계 10항목 (공식 문서 기준, 2026-09)
  - **UniNet에만 있는 것 5종** — UE급 P3 정책 세트(가시성·우선순위+기아 보정·휴면·주기·채널 예산)의 무료 셀프호스팅 탑재·단일 Unity 스택·보안 기본값(DTLS·소유자 강제)·계약 소스젠·공개 패리티 로드맵
  - **없는 것 7종** — 내장 Full CSP&R(훅만 제공)·커스텀 직렬화/배열 델타(상류 대기)·릴레이/매치메이킹 인프라·씬 관리/재접속·플랫폼 실증·생태계·DOTS급 스케일
  - **시사점**: FastArray 상류 해소 최우선 → 풀 CSP&R 컴포넌트화 검토 → 포지셔닝 문서화
  - 코드 무변경 — 문서만 추가 (리뷰 게이트 대상 아님)
- **00-INDEX에 research/ 영역 신설**

### Changed (코드 주석 전체 영어화 — 사용자 관점 규약 확립)

- **저장소 전체 코드 주석을 영어로 전환 + 라이브러리 사용자 관점 재작성** (ADR [[0019-코드-주석-영어화-사용자-관점-규약]] · 규약 [[conventions]])
  - **대상 78개 파일** — Package(UniNet.Core·UniNet.Unity 공개 API XML doc) · CodeGenerator(생성기 주석 + 생성 코드에 방출되는 주석 템플릿 `Emitter.cs`) · Sandbox(Arena 예제·Tests·에디터 툴). XML doc은 첫 문장 what + 사용자 계약(실행 주체·기본값·제약·호출 시점), 인라인은 why 중심으로 재작성
  - **경계** — 코드 로직·식별자·문자열 리터럴 무변경 (로그·예외 메시지의 한국어는 주석이 아니므로 유지). 생성물(obj/·.gendump-fixture.cs)은 제외 — 제너레이터 재실행 시 영어로 재생성
  - **검증**: 주석 제거 정규화 비교로 코드 무변경 입증(HEAD 대비 77파일 일치, Emitter는 생성-주석 템플릿 문자열만 변경) · dotnet 빌드 녹색 · Unity 컴파일 에러 0 · EditMode 60/60 · **reviewer 판정 CLEAN** — 주석-코드 대조 전수 확인(틀린 주석 0·정보 손실 0), 문자열 리터럴 집합 불변 확인
- **주석 규약 확립** — conventions에 주석 언어(영어)·관점(공개 API는 사용자 계약, 인라인은 why) 규약 추가 (ADR-0019)

### Changed (ServerRpc 검증 훅 — 자동 감지 제거, Validate 옵트인)

- **`_Validate` 자동 감지 제거 → `[ServerRpc(Validate = true)]` 옵트인** (ADR [[0018-ServerRpc-Validate-옵트인]])
  - **속성** — `ServerRpcAttribute.Validate` 신설 (기본 `false`). 훅 계약 현행 유지: `<RPC>_Validate`는 RPC와 같은 매개변수 + `Task<bool>` 반환, 서버 디스패치에서 소유자 강제(ADR-0016) 통과 직후 `await` — `false`면 `_Implementation` 미실행
  - **진단** — `Validate = true` + `_Validate` 누락 → 컴파일 에러(UNINET011 신설) · 시그니처 불일치 → 컴파일 에러(UNINET005 유지) · ServerRpc에 플래그 없이 `_Validate` 존재 → 경고(UNINET012 신설 — 옵트인 누락 실수 노출)
  - **ClientRpc/MulticastRpc 대상 아님** — 두 속성에 `Validate` 없음, 검증 훅은 서버 권위 경로에만 의미. 붙은 `_Validate`는 일반 메서드 취급(진단 없음)
  - **제너레이터 0.1.4 → 0.1.5** — nupkg 재배포 (Sandbox `Assets/Packages` + 로컬 피드 `C:/Projects/DS/unity-nuget`)
  - **마이그레이션** — in-repo `_Validate` 5곳 전부 옵트인: NetworkTransform.RpcSubmitMove · Arena.RpcSubmitAim · Arena.RpcFire · VerifyPlayer.RpcPing · VerifyPlayer.RpcDeliver
- **검증**: dotnet 빌드 녹색 · Unity 리프레시 컴파일 에러 0 (UNINET 진단 0 — 옵트인 5곳 훅 정상 연결) · **EditMode 60/60** · **PlayMode 15/15** (HostRoundtripTests `_Validate` 거부 경로 포함)

### Added (Net 정적 파사드 — 무한정자 스폰/파괴 호출)

- **`UniNet.Unity.Net` 정적 클래스** — `NetworkInstantiate`(3종 오버로드)·`NetworkDestroy`의 전달 전용 진입점. 파일 상단 `using static UniNet.Unity.Net;` 한 줄로 일반 `Instantiate`/`Destroy`처럼 한정자 없이 호출한다 (구현은 UniNetManager에 그대로 — 전달만). 사용례: ArenaBootstrap(라이브 에디터 루프백 검증)
- **검증**: 에디터 리프레시 컴파일 에러 0 · **EditMode 60/60** · **PlayMode 15/15** (라이브 에디터 Test Runner)

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
