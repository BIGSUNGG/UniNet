# commercial-netcode-audit — 상용 셀프호스팅 넷코드 내부 점검

- **상태**: 최초 정적 점검 완료, 기능 수정·실행 재현 진행 중. **완료 판정 아님**.
- **시작 기준점**: `1cee018baf7d20a7ea01480f37eaafe013f04b83` (패키지 `0.3.0`).
- **검증 기반 보수**: `d69739816e9f1d47678ac6d8060a2f4c0055cdf6` — 테스트의 전역 오류 로그 격리만 수정, 런타임 무변경.
- **조사일**: 2026-10-19.
- **범위**: Unity 6/netstandard2.1, Windows 서버·클라이언트, 셀프호스팅·서버 권위 넷코드. 호환성보다 올바른 구조를 우선할 수 있으며 파괴적 변경은 마이그레이션을 제공한다.
- **범위 밖**: 계정·DB·매치메이킹·클라우드 서비스 자체 구축, Windows 외 플랫폼의 실실행 보증, 다른 패러다임 전용 기능의 무조건적 합집합 구현. 네트워크 연결 승인 확장점과 신뢰 경계 자체는 점검 범위다.
- **진행 순서**: 내부 코드·시나리오 점검 → 발견 사항 해결 → 최초 내부 미해결 0개 기준점 기록 → 최소 10종 공식 기능 비교 → 추가 격차 해결. **신규 타사 비교는 아직 착수하지 않았다.** 기존 비교 문서는 과거 자료이며 현재 기능 유무의 증거로 사용하지 않는다.

## 증거와 판정 규칙

- `OPEN`: 소스 또는 실제 실행으로 문제가 확인됨. 해결·재검증·리뷰·필요한 개별 배포 전에는 닫지 않는다.
- `INVESTIGATE`: 구체적 후보이나 재현 또는 계약 대조가 더 필요함. 결함이라고 확정하지도, 해소했다고 취급하지도 않는다.
- `CLOSED`: 해결 근거가 있는 항목. 런타임 기능 변경은 회귀 테스트·Arena 예시·문서·지정 모델 reviewer CLEAN·개별 0.x 배포까지 연결해야 한다.
- 문서상의 기존 제약만으로 상용 기능 공백을 자동 제외하지 않는다. 새로운 범위 제외는 사용자 승인이 필요하다.
- 조사 보고는 탐색 자료다. 메인 에이전트가 실제 코드를 대조하여 과장·추측·오탐을 분리한다. 예: 이동 입력의 낮은 시퀀스 거부는 **기존 시퀀스를 따라잡기까지의 장시간 조작 불능**이지 수학적으로 영구 거부는 아니다.

원시 조사 자료(저장소 루트 기준, 로컬 하네스 산출물):

| 자료 | 경로 |
| --- | --- |
| 시작 상태·사용자 자산 해시·백업 | `.pi-subagents/artifacts/commercial-netcode/baseline/baseline.json` 및 같은 폴더의 `preexisting.patch`, `user-files/` |
| 인접 세션 토큰 예측 실기(비밀 값 미출력) | `.pi-subagents/artifacts/commercial-netcode/baseline/security-token-probe.jsonl` |
| RPC·생성기 조사 | `.pi-subagents/artifacts/28fac812_scout_0_output.md` |
| 복제·스폰 조사 | `.pi-subagents/artifacts/2fcc3b6b_scout_0_output.md` |
| 이동·시간 조사 | `.pi-subagents/artifacts/795a250f_scout_0_output.md` |
| 기준 테스트 보수 worker | `.pi-subagents/artifacts/7f89c2eb_worker_output.md` (원 실행 `1690c8bf`, 재개 `7f89c2eb`; 재사용 출력 별칭 대신 해당 실행의 고정 산출물) |
| 기준 테스트 보수 reviewer | `.pi-subagents/artifacts/226be8e4_reviewer_output.md` (원 실행 `21c02b2e`, 형식 보완 재개 `226be8e4`) |

하네스 파일은 배포 패키지에 넣지 않는다. 최종 기능별 증거대장은 같은 폴더의 `commercial-netcode-evidence.json`에 원시 실행·테스트·배포를 연결한다.

## 전체 런타임 파일 조사 대응표

아래 경로는 모두 `Package/Runtime/` 기준이다. 시작 기준점의 C# 파일 **44개**를 대응한다. `정적`은 코드·호출 경로 조사이며 개별 실기 재현 완료라는 뜻이 아니다. 기존 테스트 전수 통과와 신규 발견 사항의 회귀 테스트는 별개다. 생성기와 Arena·설치 도구는 이 표 외의 관련 조사 대상이다.

| 파일 | 조사 경로·주요 판정 |
| --- | --- |
| `UniNet.Core/AssemblyInfo.cs` | 정적: 테스트 접근 경계 |
| `UniNet.Core/ClientRpcAttribute.cs` | 정적: RPC-001 대상 지정 계약 |
| `UniNet.Core/Delivery.cs` | 정적: 선언값과 와이어 전달 모드 |
| `UniNet.Core/Hosting/Fnv1a.cs` | 정적: GEN-001 식별자 충돌 처리와 대조 |
| `UniNet.Core/Hosting/INetworkMoveSimulation.cs` | 정적: MOV-001~003 입력·상태 수명주기 |
| `UniNet.Core/Hosting/IUniNetClientSender.cs` | 정적: 클라이언트 시스템 송신 경계 |
| `UniNet.Core/Hosting/IUniNetReplicationPolicy.cs` | 정적: 조건·가시성·휴면·주기 정책 |
| `UniNet.Core/Hosting/IUniNetSpawnTransform.cs` | 정적: 스폰 변환과 TRS-001 |
| `UniNet.Core/Hosting/IUniNetSystemChannel.cs` | 정적: 환영·소유권·스폰·씬 시스템 메시지 |
| `UniNet.Core/Hosting/MoveSimulation.cs` | 정적: MOV-001~003, QA-001 |
| `UniNet.Core/Hosting/NetworkClient.cs` | 정적: OWN-001, OWN-003, REC-001 |
| `UniNet.Core/Hosting/NetworkServer.cs` | 정적: SEC-001, CON-001~002, OWN-001~002, REC-001, SCN-001~003 |
| `UniNet.Core/Hosting/PositionHistory.cs` | 정적: 리와인드 경계·보관 상한, 기존 P4 테스트 |
| `UniNet.Core/Hosting/SnapshotBuffer.cs` | 정적: 보간 경계·샘플 용량, 기존 P4 테스트 |
| `UniNet.Core/Hosting/UniNetBatchRegistry.cs` | 정적: 배치 타입별 적용·등록 경계 |
| `UniNet.Core/Hosting/UniNetDeltaApplier.cs` | 정적: 생성 델타 적용 계약 |
| `UniNet.Core/Hosting/UniNetDispatch.cs` | 정적: GEN-001 충돌 덮어쓰기 |
| `UniNet.Core/Hosting/UniNetEndpointOptions.cs` | 정적: CON-001~002 연결·인증·버전 옵션 |
| `UniNet.Core/Hosting/UniNetEnvironment.cs` | 정적: 메인 큐·세션 교체 경계, QA-001 |
| `UniNet.Core/Hosting/UniNetHubFactory.cs` | 정적: 어셈블리별 허브 배선 |
| `UniNet.Core/Hosting/UniNetReplicationHandler.cs` | 정적: 필드 비교·전체/델타·소유 조건 계약 |
| `UniNet.Core/Hosting/UniNetSpawnRegistry.cs` | 정적: 스폰 타입 등록과 슬롯 검증 |
| `UniNet.Core/Hosting/UniNetTime.cs` | 정적: TIM-001 서버 시간 추정 |
| `UniNet.Core/Hosting/UniNetTrafficCounters.cs` | 정적: 페이로드 카운터·Interlocked, 와이어 바이트와 구분 |
| `UniNet.Core/Hosting/UniNetTypeRegistry.cs` | 정적: 타입 해시·다중 어셈블리 등록 |
| `UniNet.Core/MulticastRpcAttribute.cs` | 정적: 멀티캐스트·RPC-001과 역할 구분 |
| `UniNet.Core/ReplicateCondition.cs` | 정적: OwnerOnly·SkipOwner·InitialOnly |
| `UniNet.Core/ReplicatedAttribute.cs` | 정적: 커스텀 코덱·Notify·컬렉션 선언 |
| `UniNet.Core/ServerRpcAttribute.cs` | 정적: 소유자·Validate·전달 모드 |
| `UniNet.Core/UniNetDisconnectReason.cs` | 정적: 종료 사유·연결 계약 |
| `UniNet.Core/UniNetInfo.cs` | 정적: 패키지와 기본 프로토콜 버전 동기화 |
| `UniNet.Unity.Editor/NetworkSpawnablePostprocessor.cs` | 정적: 프리팹 신원·베이크 테이블·임포트 경계 |
| `UniNet.Unity.Editor/SceneNetIdPostprocessor.cs` | 정적: 씬 안정 ID·중복 로드 제약 대조 |
| `UniNet.Unity.Editor/UniNetDebuggerWindow.cs` | 정적: 연결·객체·통계 표시 경로 |
| `UniNet.Unity/AssemblyInfo.cs` | 정적: 어셈블리 테스트 접근 경계 |
| `UniNet.Unity/Net.cs` | 정적: Instantiate/Destroy 위임 |
| `UniNet.Unity/NetworkBehaviour.cs` | 정적: 등록·소유권·서브 슬롯·정책·씬 로드 |
| `UniNet.Unity/NetworkSpawnable.cs` | 정적: 프리팹 표시·신원 |
| `UniNet.Unity/NetworkTransform.cs` | 정적: MOV-001~003, TIM-001, TRS-001 |
| `UniNet.Unity/UniNetBlobEnvelope.cs` | 정적: 이동 입력·상태 Message envelope |
| `UniNet.Unity/UniNetBootstrap.cs` | 정적: 잔존 wiring-check 컴포넌트, 버전 조회 전용 |
| `UniNet.Unity/UniNetManager.cs` | 정적: CON-001, REC-001, SCN-001~003, LIF-001 |
| `UniNet.Unity/UniNetPrefabTable.cs` | 정적: 실제 프리팹 복원·슬롯 대조 |
| `UniNet.Unity/UniNetSpawn.cs` | 정적: 클라이언트 생성·전체 상태·파괴·OWN-003 |

## 기능 문제 대장

파일 경로는 저장소 루트 기준이며 표의 줄 번호는 시작 기준점 기준이다. 수정 후에는 기능별 증거대장의 커밋과 회귀 테스트를 기준으로 확인한다.

| ID | 상태 | 문제·증거 | 필요한 확인/해결 |
| --- | --- | --- | --- |
| QA-001 | CLOSED | 잘린 이동 입력 테스트가 전역 LogFault의 실제 예외 로그를 미기대. 기준 EditMode job `b0e1e2fd3ace401382514bfc7a88b3e2`에서 재현 | 테스트 보수 커밋 `d697398`; targeted 1/1·EditMode 132/132·PlayMode 27/27, 지정 모델 reviewer CLEAN. 런타임·배포 버전 무변경 |
| SEC-001 | CLOSED | 시작 기준점의 `NetworkServer.cs:76-79`는 증가 ulong 토큰, `ResumeSession:601-619`은 bearer 토큰으로 재부착. 실제 Unity에서 인접 토큰 예측 가능 `true` 확인(비밀 미출력) | **개별 배포 완료(2026-10-20)**: ADR-0041, RNG 256비트 string·48바이트 상한·연결 검증·조용한 거절/수신 카운터·마스킹. 회귀 10/10·EditMode 138/138·PlayMode 27/27·Arena 실제 RUDP 마커 진단 PASS, 소스 리뷰 r3/문서 확인 CLEAN, 정적 검증·커밋된 로컬 Git 신규 Unity 설치 PASS. `ca13ee7` → [v0.4.0](https://github.com/BIGSUNGG/UniNet/releases/tag/v0.4.0), main/tag CI success·원격 태그/패키지 버전 일치 확인. 원시 증거는 evidence.json 참조. 아바타 복구·최종 3프로세스·최종 공개 태그 신규 설치는 별도 미완 항목 |
| CON-001 | OPEN | `NetworkServer.AttachConnection:361-383`이 버전 수용 전에 소유권·전체 상태·접속 이벤트 실행. `AllowsServerRpc:463-485`에도 핸드셰이크 완료 게이트 없음. 커스텀 ProtocolVersion은 SDK 버전 비교를 대체함 | 버전 미선언·불일치·타임아웃 상태에서 게임 데이터/RPC 차단과 승인 수명주기를 정립. 앱 식별자가 같아도 다른 SDK wire를 구분할 독립 검증 필요(SEC-001의 Arena 식별자 상향은 해당 데모의 마이그레이션일 뿐 이 게이트 구현이 아님) |
| CON-002 | INVESTIGATE | `UniNetEndpointOptions`에는 공통 연결 키만 있고 게임별 자격 증명을 검사하는 접속 승인 확장점이 없음 | 계정 서비스 구축은 제외하되 게임 제공 인증/승인 콜백이 데이터 노출 전에 실행되는 연결 계약 필요성·API 확정 |
| REC-001 | OPEN | ADR-0034가 빠른 재접속 실패 창·결과 통보 부재를 명시. `UniNetManager.ReconnectAsync:91-97`은 재부착 결과와 무관하게 연결 후 true 반환 | 재개 승인/거절 결과, 기존 연결 감지 전 재개 경합, 토큰 수명·만료·새 연결 유효성·게임 아바타 보존을 실제 세션으로 검증 |
| OWN-001 | CLOSED | 시작 기준점의 `SendCatchup`은 기존 owner 통보 누락(부모 사전 재현 후발 `Owners.Count=0`). 씬 재진입도 누락, 동적 재진입·초기 스폰은 상태 뒤/nonzero 통보 | **v0.4.1 개별 배포 완료(2026-10-20)** — `380b2b4` → [Release](https://github.com/BIGSUNGG/UniNet/releases/tag/v0.4.1). 캐치업/재진입 공유 기준선·초기 스폰의 owner-before-state(0 포함), 기존 와이어/필터 유지. 회귀 8/8·EditMode 146/146·PlayMode 27/27·Arena 실제 RUDP 마커 진단·신규 로컬 Git 설치·최종 Flash r3 CLEAN·main/tag CI PASS, 원격 태그/패키지 버전 일치. 원시 증거는 evidence.json. 아바타 복구·최종 다중 프로세스/공개 태그 신규 설치는 별도 미완 |
| OWN-002 | INVESTIGATE | `SetOwner`/`ResumeSession`은 OwnerUpdate만 전파. 값이 변하지 않은 OwnerOnly/SkipOwner 필드의 새 수신자 초기 상태 재전송이 보이지 않음 | 소유권 이전 직후 비변경 조건 필드의 값·Notify·비공개 데이터 경계를 재현 |
| OWN-003 | OPEN | `NetworkClient.Unregister:63-66`은 객체 사전만 제거하고 owner 사전은 남김 | 파괴 후 GetOwner=0, 반복 스폰/파괴의 owner 상태 정리·Arena 퇴장 검증 |
| MOV-001 | INVESTIGATE | `MoveSimulation.AdoptInput:179`은 기존 `_lastSeenSeq` 이하 거부. 소유권 이전 시 서버/새 소유자의 입력·예측 상태를 교체하는 경로가 없음 | 큰 기존 seq 이후 새 소유자 seq=1 입력의 장시간 거부·마지막 입력 잔존을 재현. 이전/회수/재접속의 reset 계약과 상태 복구 |
| MOV-002 | INVESTIGATE | `NetworkTransform.Update`의 SimulationEnabled 게이트는 미승인 히스토리 자체를 정리하지 않음. 재앵커는 PendingCount==0에 의존 | pending>0인 사망→원격 리스폰→재활성화에서 오래된 예측 좌표 렌더 여부 검증 |
| MOV-003 | INVESTIGATE | `MoveSimulation.TickServer`의 큐 기아 시 마지막 입력 외삽에 입력 만료 정책이 없음 | 접속 유지 중 전송 중단·유예 소유자 상태에서 의도치 않은 무기한 움직임 재현, 게임 정의 입력의 안전한 만료 계약 검토 |
| TIM-001 | INVESTIGATE | `UniNetTime.ApplyServerTime`이 편도 지연 보정 없이 offset EMA 적용. 감소 보정과 Now 단조 클램프가 시간 진행을 멈출 가능성 | 실제 지연/오프셋 감소에서 시계·보간·히트 시간 오차를 측정하고 테스트 가능한 동기화 계약 정립. 추정 위험을 확정 결함과 혼동하지 않음 |
| TRS-001 | INVESTIGATE | `NetworkTransform`의 지속 복제 필드는 위치 XYZ. `IUniNetSpawnTransform`의 회전은 최초 스폰 값이며 회전·스케일의 지속 동기화 컴포넌트가 보이지 않음 | 상용 회전/스케일/텔레포트/후발 합류 시나리오와 실제 제공 범위 확정, 필요한 지속 변환 동기화 구현 |
| RPC-001 | OPEN | `CodeGenerator/Emitter.cs:899-944` ClientRpc·MulticastRpc 송신은 SnapshotConnections 전체 순회. 특정 연결/소유자 대상 생성 API 없음 | 소유자 전용 및 특정 연결 응답을 서버 권위로 지원하고 타 클라 미수신을 회귀·Arena에서 단언 |
| RPC-002 | OPEN | `Emitter.cs:408-420` OwnerUpdate 수신 파싱에 try/catch 없음. 단일 Replicate 경로도 큐 등록 전 파싱은 무방비 | 잘린 시스템·단일 복제 페이로드 격리, 정상 후속 메시지 처리. 단순 로그 숨김 금지 |
| GEN-001 | OPEN | `UniNetDispatch.Add:60-64` 충돌 핸들러를 Debug.WriteLine 뒤 덮어씀. 제너레이터 충돌 검사는 어셈블리 내부에 한정 | 같은 핸들러의 멱등 재등록과 다른 어셈블리의 실충돌을 구분하고 안전하게 진단·거부 |
| GEN-002 | INVESTIGATE | 생성기가 `namespace + partial class Name`을 출력. 중첩/제네릭 NetworkBehaviour의 오해하기 쉬운 생성 오류 가능 | Unity 타입 제약과 허용 상속 형태를 대조하고 Roslyn 재현. 필요한 지원 또는 정확한 컴파일 진단 |
| GEN-003 | OPEN | RPC 직접 매개변수는 기본형·string·Message 중심이며 enum은 UNINET002 거부 | enum 매개변수의 실사용 지원·정확한 underlying codec·잘못된 값 검증·Arena 예시 |
| GEN-004 | INVESTIGATE | RPC 직접 Vector/Quaternion/배열 인자에는 필드 Serializer와 같은 확장 경로가 없음. Message envelope 우회는 존재 | 기존 envelope 기능을 누락으로 세지 않되 일반 Unity 값·컬렉션 인자의 직접 지원 필요성과 안전한 크기/타입 계약 검토 |
| LIF-001 | OPEN | `UniNetManager.ServerStopAsync:197-211`에는 동기 ServerStop의 SweepDynamicSpawns가 없음. 시작 실패 시 환경 롤백도 별도 검증 필요 | 전용 서버 async 정지→동일 프로세스 재시작의 오래된 동적 객체·ID 충돌을 재현하고 종료/실패 경로 대칭 정리 |
| SCN-001 | OPEN | `CheckSceneGate:565-573`이 완료 뒤 `_pendingScene=null`로 지움. AttachConnection은 진행 중 씬만 재전송 | 씬 전환 완료 후 후발 접속자에게 현재 네트워크 씬을 전달하고 ready/캐치업 순서 검증 |
| SCN-002 | INVESTIGATE | 씬 객체의 전체 상태가 클라 등록 전에 오면 lookup 실패로 폐기될 수 있고, 나중 등록에 전체 상태 재요청 경로가 보이지 않음 | 로비에서 접속→서버 게임 씬 상태 수신→뒤늦은 로드 조합의 고정값 불일치 재현 |
| SCN-003 | INVESTIGATE | 씬 ready는 이름 문자열로만 구분, 실패·시간 초과/같은 씬 반복 전환에서 낡은 보고 구분은 미검증 | 전환 식별·실패 복구·기한·완료 이벤트 격리를 실제 사용 흐름으로 검증 |
| ARENA-001 | OPEN | `ArenaBootstrap.LoadPlayerPrefab`이 UNITY_EDITOR 외 빌드에서 null 반환 | 실제 빌드에서 프리팹을 포함·조회하고 Windows 전용 서버+별도 클라 2개 실행 검증 가능하게 수정 |
| INSTALL-001 | OPEN | `Package/tools/verify-install.sh`는 로컬 repo clone/file URL만 구성 | 최종 공개 UPM 태그를 신규 프로젝트에 설치·컴파일하는 확인 경로 추가. 개발 도구 변경도 리뷰 대상 |

### 오탐·보류 근거

- 예산 연기 델타의 Owner/Others 그룹은 전송 시점의 현재 owner로 선택된다. 그룹 이름이 enqueue 때 정해진다는 이유만으로 이전 owner에게 비공개 필드가 전송된다고 단정할 수 없다. 필요하면 OWN-002의 복합 회귀로 확인한다.
- 동적 가시성 복귀에는 이미 OwnerUpdate가 있으므로 OWN-001을 그 경로까지 확장한 주장은 정정했다.
- O(n) 128개 샘플 탐색, 커넥션별 재인코딩, 박싱은 측정 없는 기능 결함으로 등록하지 않았다. 기능·보안 검증을 성능 이유로 생략하지는 않는다.
- DTLS는 인증서 설정으로 활성화되는 옵션이다. 문서의 ‘항상 기본 암호화’ 표현만 믿고 일반 연결의 암호화를 보장하지 않는다. 실제 보안 사용법은 연결 승인/세션 점검과 함께 대조한다.

## 기준 검증 기록

| 검증 | 원시 식별자/결과 |
| --- | --- |
| 수정 전 EditMode | Unity MCP job `b0e1e2fd3ace401382514bfc7a88b3e2`: 132개 완료, QA-001 실패 |
| 수정 후 targeted | job `9e1859170206428f92a552cb6f87ecb0`: 1/1 통과, 실패·스킵 0 |
| 수정 후 EditMode | job `b9c3a0b1963749ee9ec965e6b9a4b758`: 132/132 통과, 실패·스킵 0 |
| 수정 후 PlayMode | job `daab8138afee4c8ab3545c9e2433c804`: 27/27 통과, 실패·스킵 0 |
| C# LSP | MoveSimulationTests.cs primary confirmed clean (초기 timeout은 성공으로 세지 않음) |
| 리뷰 | zai/glm-5.3-flash 원 실행 `21c02b2e`·형식 보완 `226be8e4`: 최종 첫 줄 CLEAN |
| 사용자 자산 | Arena.unity·BlackMaterial.mat·BlackMaterial.mat.meta의 SHA-256이 시작 백업과 일치 |

## 최초 내부 ‘미해결 없음’ 기준점

**미설정.** 위 OPEN/INVESTIGATE 항목이 남아 있으며 신규 경쟁 프레임워크 비교를 시작할 조건을 충족하지 않았다.

## 관련 문서

- [[../overview|overview]] · [[../roadmap|roadmap]] · [[../deployment|deployment]]
- [[../features/connection-lifecycle|connection-lifecycle]] · [[../features/movement-prediction|movement-prediction]]
- [[../examples/arena-shooter|arena-shooter]] · [[network-framework-comparison]]
