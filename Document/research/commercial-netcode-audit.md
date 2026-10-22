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
| CON-001 | CLOSED | 시작 기준점의 `AttachConnection`이 버전 확인 없이 소유권·캐치업·이벤트 무조건 실행·게임 RPC 디스패치 무게이트·와이어 7 미선언 무선 수용(조사 `2b8a55d9`) | **v0.6.0 개별 배포 완료(2026-10-20)** — `c2d4c8e` → [Release](https://github.com/BIGSUNGG/UniNet/releases/tag/v0.6.0). 승인 상태·승인 전 게임 데이터 전면 유보·게임 RPC 게이트(생성기 0.2.9 실배포)·미선언 타임아웃 킥. 검증 중 회귀 2건(accept 스레드 Unity API·DLL 미배포) 실측→수정→이력 보존. 회귀 8종·타깝 8/8·EditMode 163/163·PlayMode 29/29·실경로 재현·신규 로컬 Git 설치·Flash r2 CLEAN·main/tag CI 성공·원격 태그/패키지 일치. 원시 증거는 evidence.json. CON-002는 별도 항목 |
| REC-002 | CLOSED | REC-001 조사 중 발견(`a7deed5c`): 자발적 `ClientStop`은 클라가 토큰을 소각(재개 불가가 문서화된 계약)하지만 서버는 승인된 disconnect면 무조건 유예 저장 — '자발적 퇴장' 와이어가 없어 구분 불능. 결과: 클라가 영영 찾을 수 없는 세션이 유예 창 동안 잔존하며 `IsInGraceSession`이 고아 입양을 계속 블로킹 | **v0.9.0 개별 배포 완료(2026-10-21)** — `87a3467` → [Release](https://github.com/BIGSUNGG/UniNet/releases/tag/v0.9.0). 시스템 와이어 15 `IntentionalLeave`(C→S, ADR-0046): ClientStop이 close 직전 통보(전송 Task 250ms 유계 대기+150ms 드레인 — 사용자 승인 계약), 서버는 유예 저장 생략+토큰 툼스톤(UnknownToken)+즉시 재배정. wire 14 확장은 기각(수신 당사자 없음). 리뷰 r9(running ISSUES 3건 문서)→r10 CLEAN. EditMode 210/210·PlayMode 32/32·실루프백 3/3·Arena DIAG PASS·음성 대조 RED 확인·신규 설치 PASS. 원시 증거는 evidence.json 참조 |
| CON-002 | CLOSED | `UniNetEndpointOptions`에는 공통 연결 키만 있고 게임별 자격 증명을 검사하는 접속 승인 확장점이 없음 | **v0.10.0 개별 배포 완료(2026-10-21)** — `ab82e10` → [Release](https://github.com/BIGSUNGG/UniNet/releases/tag/v0.10.0). ADR-0047: 와이어 7 페이로드 확장 `[version][credential]`(조건부 읽기로 구 클라 호환·상한 4096 파싱 전 폐기), 서버 `ConnectionApprover`(`Task<bool>` 콜백 — 메인 시작·비동기 검증), 클라 `ConnectionCredential`, 거부 사유 `ApprovalRejected = 6`(재번호 없음 — 비파괴). 콜백 대기 중 CON-001 유예 상태 재사용, `VersionDeclareTimeoutMs` 총예산 스윕이 hang 콜백 마감. 재접속 재선언도 콜백 1회 통과. 리뷰 r11(ISSUES — Core 디버그 트레이스 잔존)→r12 CLEAN. EditMode 221/221·PlayMode 35/35·실RUDP 승인/거부/초과페이로드 PASS·신규 설치 PASS. 원시 증거는 evidence.json 참조 |
| REC-001 | CLOSED | ADR-0034가 결과 통보 부재 명시. 조사 확정(`a7deed5c`): `ReconnectAsync` true는 연결 수립뿐·`ResumeSession` 반환값 폐기·거절 침묵·경합 2종(해제 감지 전 재접속→영구 신규 플레이어화·동일 토큰 이중 소비) | **v0.7.0 개별 배포 완료(2026-10-20)** — `206fecf` → [Release](https://github.com/BIGSUNGG/UniNet/releases/tag/v0.7.0). `GraceResumeResult` 열거형(파괴)·wire 14 결과 통보·툼스톤(소진=UnknownToken·만료=Expired 정확 종결)·`LastResumeResult`+이벤트·SessionNotFound 디태치 경합 자동 재시도(경합 재부착 수렴). 검증 중 실제 결함 4건(툼스톤 분기순서·스윕 툼스톤 수명0·DIM override·테스트 전제/구독 인스턴스) 수정·이력 보존. 회귀 4종·타깝 12/12·EditMode 167/167·PlayMode 30/30·실RUDP·신규 설치·Flash r1 CLEAN(잔여 리스크 QueueOnMain 실증 해소)·main/tag CI 성공. 원시 증거는 evidence.json. REC-002는 별도 미해결 |
| OWN-001 | CLOSED | 시작 기준점의 `SendCatchup`은 기존 owner 통보 누락(부모 사전 재현 후발 `Owners.Count=0`). 씬 재진입도 누락, 동적 재진입·초기 스폰은 상태 뒤/nonzero 통보 | **v0.4.1 개별 배포 완료(2026-10-20)** — `380b2b4` → [Release](https://github.com/BIGSUNGG/UniNet/releases/tag/v0.4.1). 캐치업/재진입 공유 기준선·초기 스폰의 owner-before-state(0 포함), 기존 와이어/필터 유지. 회귀 8/8·EditMode 146/146·PlayMode 27/27·Arena 실제 RUDP 마커 진단·신규 로컬 Git 설치·최종 Flash r3 CLEAN·main/tag CI PASS, 원격 태그/패키지 버전 일치. 원시 증거는 evidence.json. 아바타 복구·최종 다중 프로세스/공개 태그 신규 설치는 별도 미완 |
| OWN-002 | CLOSED | `SetOwner`/`ResumeSession`은 OwnerUpdate만 전파. 값이 변하지 않은 OwnerOnly/SkipOwner 필드의 새 수신자 초기 상태 재전송이 보이지 않음 | **재현 확정(소스 전수) → v0.11.0 해결 배포 완료(2026-10-21)** — `fcd62aa` → [Release](https://github.com/BIGSUNGG/UniNet/releases/tag/v0.11.0). ADR-0048: `SendOwnershipBaseline`(상태 전용 baseline — 스폰 재전송 없음)을 SetOwner·ResumeSession·ReassignOwnership 3경로에 적용(신규 owner isOwner=true·직전 owner isOwner=false·관련성 게이트·즉시 캐치업 연결 억제). 추가 피해 확정: 이전 owner도 SkipOwner 필드를 기본값으로 봄. 지연 큐 프라이버시 오탐 메모는 flush 시점 그룹 선택으로 재확인·회귀 테스트로 경계 고정. 리뷰 r13(대장 선점 기재 F1 등 5건 지적 — 실제 상태로 정정)→r14 CLEAN. EditMode 230/230(신규 9종)·PlayMode 36/36(신규 1종) 2회 연속(음성 대조는 미실시 — 계획 §5 잔여)·신규 설치 PASS. 원시 증거는 evidence.json 참조 |
| OWN-003 | CLOSED | `NetworkClient.Unregister`은 객체 사전만 제거하고 owner 사전은 남김 | 해결: Unregister가 동일 lock에서 `_owners.Remove(netId)` — 파괴 후 GetOwner=0 회복·누적 제거. 검증: EditMode 234/234 ×2(job `34a0e2d3`·`5d52fe8b`)·PlayMode 40/40 ×2(job `fe631b90`·`bfaefb5c`), 신규 T1~T4(OwnerBaselineTests — `_owners` 반사 단정 포함)+T5~T8(DestroyOwnershipPlayTests — 실RUDP 파괴·10사이클·호스트·퇴장 정합), 리뷰 r15 ISSUES(T3 누적 단정 보강)→r16 CLEAN. 배포: 커밋 `a52e3a3`, 태그 v0.12.0, Release https://github.com/BIGSUNGG/UniNet/releases/tag/v0.12.0 , CI run 39648257676 success(수동 Release·CI 자동 생성 422 중복 경합 → `--failed` 재실행 success), verify-install PASS(log 보존). 계획 `.pi-subagents/artifacts/commercial-netcode/own003-plan.md`, 영수증 `.pi-subagents/artifacts/commercial-netcode/own003-release.json`. 잔여: 음성 대조 미실시(OWN-002와 동일) |
| MOV-001 | CLOSED | 시작 기준점의 `AdoptInput` seq 가드가 임기를 모름 + 소유권 이전 시 스트림 교체 경로 부재. 실기 반례 2종·attach 경합 오인(첫 임기 전멸) 확정 | **v0.5.0 개별 배포 완료(2026-10-20)** — `3c3b4df` → [Release](https://github.com/BIGSUNGG/UniNet/releases/tag/v0.5.0). `OwnershipChanged` 이벤트·`ResetOwnershipStream`(TState 보존)·`_moveEpoch` 임기 규칙(0→비0 입양은 베이이스라인·임기→임기/해제만 범프+리셋)·봉투 `[epoch][len][bytes]`·불일치 조용 기각+카운터. 회귀 9종·타깃 28/28·EditMode 155/155·PlayMode 28/28(실패 이력 보존)·Arena 실제 RUDP 사이클 PASS·신규 로컬 Git 설치·Flash r2 CLEAN·main/tag CI 성공·원격 태그/패키지 버전 일치. 원시 증거는 evidence.json. MOV-002/003은 별도 미해결 |
| MOV-002 | CLOSED | `NetworkTransform.Update`의 SimulationEnabled 게이트는 미승인 히스토리 자체를 정리하지 않음. 재앵커는 PendingCount==0에 의존 | **v0.13.0 개별 배포 완료(2026-10-21)** — 결함 확정(사망→리스폰→재활성화에서 사망 직전 예측 좌표 렌더·죽은 입력 재생). 해결: `INetworkMoveSimulation.DropPendingHistory()` DIM(기본=OnTeleport; MoveSimulation은 큐+링만 소거·권위 TState·최신 입력 보존), 게이트 진입 에지 1회 호출(에지 트리거), 퇴장 에지 재앵커 선실행(스텝 유입 프레임 오염 차단 — r17 ISSUE-1). 재앵커·롤백·ADR-0042 임기 계약 무변경. 검증: RED job `b0d852be`(drop 중화 시 T2/T3 결함 실패)·`0bbdb4dd`(T2c stale-step), EditMode 241/241 ×2(job `17a4efdf`·`cb9288d1`)·PlayMode 41/41 ×2(job `6c6090ac`·`cfb0e695`), 리뷰 r17 ISSUES→r18 CLEAN. 배포: 커밋 `2a77605`, 태그 v0.13.0, Release https://github.com/BIGSUNGG/UniNet/releases/tag/v0.13.0 , CI run 39660695568 success, verify-install PASS. ADR-0049, 계획 `.pi-subagents/artifacts/commercial-netcode/mov002-plan.md`, 영수증 `.pi-subagents/artifacts/commercial-netcode/mov002-release.json`. 잔여: NetworkStatsPlayTests RTT 세션-에이징 플레이크(기존 문서화·diff 무관), 2프로세스 소유자 레그 통합 테스트는 의도적 후속 |
| MOV-003 | CLOSED | `MoveSimulation.TickServer`의 큐 기아 시 마지막 입력 외삽에 입력 만료 정책이 없음 | **v0.14.0 개별 배포 완료(2026-10-21)** — 결함 확정(유예 소유자·전송 중단에서 마지막 입력 무기한 재적용). 해결: 옵트인 `inputExpirySteps`(기본 0=기존 hold-last 바이트 동일 — ADR-0034 패턴), 만료 시 `default(TInput)` 1회 스왑(멱등), 타이머 리셋 4경계(OnTeleport·ResetOwnershipStream·DropPendingHistory·**SetInput** — r19 ISSUE-1). hold-last 기본 유지·decay-to-zero 기각(결정론 계약). 검증: EditMode 248/248 ×2(job `91e29bbd`·`bc2bff5e`)·PlayMode 41/41(job `31770d29`+사전 2회), 리뷰 r19 ISSUES(SetInput 리셋 누락)→r20 CLEAN(T6 결정적 핀). 배포: 커밋 `5556842`, 태그 v0.14.0, Release https://github.com/BIGSUNGG/UniNet/releases/tag/v0.14.0 , CI run 39666784938 success, verify-install PASS. ADR-0050, 계획 `.pi-subagents/artifacts/commercial-netcode/mov003-plan.md`, 영수증 `.pi-subagents/artifacts/commercial-netcode/mov003-release.json`. 잔여: RTT 세션-에이징 플레이크(기존 문서화·diff 무관) |
| TIM-001 | INVESTIGATE | `UniNetTime.ApplyServerTime`이 편도 지연 보정 없이 offset EMA 적용. 감소 보정과 Now 단조 클램프가 시간 진행을 멈출 가능성 | 실제 지연/오프셋 감소에서 시계·보간·히트 시간 오차를 측정하고 테스트 가능한 동기화 계약 정립. 추정 위험을 확정 결함과 혼동하지 않음 |
| TRS-001 | INVESTIGATE | `NetworkTransform`의 지속 복제 필드는 위치 XYZ. `IUniNetSpawnTransform`의 회전은 최초 스폰 값이며 회전·스케일의 지속 동기화 컴포넌트가 보이지 않음 | 상용 회전/스케일/텔레포트/후발 합류 시나리오와 실제 제공 범위 확정, 필요한 지속 변환 동기화 구현 |
| RPC-001 | CLOSED | ClientRpc 송신은 SnapshotConnections 전체 순회. 특정 연결/소유자 대상 생성 API 없음 | **v0.15.0 개별 배포 완료(2026-10-22)** — 해결: 생성기 0.2.14가 모든 `[ClientRpc]`에 동반 `{Name}To(connId, args)`·`{Name}ToOwner(args)` emit(사용자 승인 네이밍 — ADR-0051, 예약 이름 규약 포함). 기존 브로드캐스트 바이트 동일, 와이어 무변경, 필터링은 서버 송신 루프 전용. 검증: EditMode 254/254(job `fe312da4`)·PlayMode 42/42 ×2(job `fa6dd906`·`06f32ce5`), 신규 T1~T7. 컴파일 게이트: 워커 partial-emission CS0759 → 리더 정규 메서드 수정 후 GREEN. 리뷰 r21 CLEAN(N1 예약 이름 → ADR 반영). 배포: 커밋 `1708a53`, 태그 v0.15.0, Release https://github.com/BIGSUNGG/UniNet/releases/tag/v0.15.0 , CI run 39714619174 success, verify-install PASS. 계획 `.pi-subagents/artifacts/commercial-netcode/rpc001-plan.md`, 영수증 `.pi-subagents/artifacts/commercial-netcode/rpc001-release.json`. 잔여: T8(Arena ToOwner 변형) 미구현 — 타깃 커버리지는 T2/T3/T7로 고정 |
| RPC-002 | CLOSED | OwnerUpdate 수신 파싱에 try/catch 없음(형제 7종은 보유). 단일 Replicate도 큐 등록 전 파싱 무방비 (실제 위치 :450·:598 — 대장 표기 드리프트) | **v0.16.0 개별 배포 완료(2026-10-22)** — 해결: 두 핸들러 outer try/catch(배치 패턴 복제, 기존 내부 가드 유지)+`NetworkClient.FaultedPayloadCount`(public, Interlocked — IVT 미부여 어셈블리 CS0122 회피, r22 실증)+LogFault — 메시지 단위 격리·로그 무관 관측(단순 로그 숨김 금지 충족). 검증: EditMode 258/258 ×3(job `a56fbc90`·`9b54eb41`·`f6bfe165`)·PlayMode 42/42 ×2(job `4c6983d2`·`3a18e703`), 신규 OwnerUpdateWireTests T1~T4. 컴파일 게이트: 워커 중괄호 불균형 emission(CS1520/CS0501/CS0535) → 리더 수정+로컬 생성 프로브 균형 검증. 리뷰 r22 CLEAN. 배포: 커밋 `efc8628`, 태그 v0.16.0, Release https://github.com/BIGSUNGG/UniNet/releases/tag/v0.16.0 , CI run 39727024568 success, verify-install PASS. 계획 `.pi-subagents/artifacts/commercial-netcode/rpc002-plan.md`, 영수증 `.pi-subagents/artifacts/commercial-netcode/rpc002-release.json` |
| GEN-001 | CLOSED | `UniNetDispatch.Add` 충돌 핸들러를 Debug.WriteLine 뒤 무음 덮어씀. 생성기 충돌 검사는 어셈블리 내부 한정 | **v0.17.0 개별 배포 완료(2026-10-22)** — 해결: 사용자 구간(ID ≥ 64) 메서드 동일성 멱등 판정(메서드 그룹 델리게이트 오탐 방지) → 실충돌 LogFault(InvalidOperationException: 테이블·ID·기존/신규)+거부(first-wins), mode 불일치 엄격 처리, 예약 구간(<64) 무음 덮어쓰기 유지(와이어 13/14 다중 어셈블리 보존). Debug.WriteLine 제거. 검증: EditMode 262/262 ×2(job `4217fc99`·`c2e9dc7d`)·PlayMode 42/42(job `4ec14995`)+킥 테스트 단독 통과 2회, 신규 UniNetDispatchRegistrationTests T1~T4. 리뷰 r23 CLEAN(1라운드). 배포: 커밋 `eff180d`, 태그 v0.17.0, Release https://github.com/BIGSUNGG/UniNet/releases/tag/v0.17.0 , CI run 39731042772 success, verify-install PASS. 계획 `.pi-subagents/artifacts/commercial-netcode/gen001-plan.md`, 영수증 `.pi-subagents/artifacts/commercial-netcode/gen001-release.json`. 잔여: RTT 플레이크(기존 문서화 — diff 무관) |
| GEN-002 | INVESTIGATE | 생성기가 `namespace + partial class Name`을 출력. 중첩/제네릭 NetworkBehaviour의 오해하기 쉬운 생성 오류 가능 | Unity 타입 제약과 허용 상속 형태를 대조하고 Roslyn 재현. 필요한 지원 또는 정확한 컴파일 진단 |
| GEN-003 | CLOSED | RPC 직접 매개변수는 기본형·string·Message 중심이며 enum은 UNINET002 거부 | **v0.18.0 개별 배포 완료(2026-10-22)** — 해결: 생성기가 enum을 선언된 underlying 정수형(byte~ulong)으로 리졸브해 동일 codec+명시 캐스트 emit — 와이어 바이트 동일(호환). RPC 매개변수·Replicated 필드·컬렉션 원소 전부 커버. 미정의 값 pass-through(D3 — Validate 훅이 사용자 역할). Nullable enum 미지원(문서화). Arena 예시 추가. 검증: EditMode 268/268 ×3(job `d28f24f3`·`6ab1a436`·`911fcf19`)·PlayMode 42/42(job `632e65d5`), 신규 EnumRpcWireTests T1~T6. **함정 발견·문서화**: 테스트 어셈블리 네트워크 픽스처가 생성 어셈블리 전용 `__UniNetRegistration`을 유발해 CS0436 바인딩으로 Fixtures 타입 등록 무효화(25개 실패 실증) — 픽스처는 Tests/Fixtures 어셈블리에(Gen003EnumFixtures.cs 주석). 테스트 asmdef에 DRPC.Client/Server·Communication.Shared 참조 보강(신규 빌드 해소). 리뷰 r24 ISSUES(임시 진단 잔존)→r25 CLEAN. 배포: 커밋 `6513eff`, 태그 v0.18.0, Release https://github.com/BIGSUNGG/UniNet/releases/tag/v0.18.0 , CI run 39746273257 success, verify-install PASS. 계획 `.pi-subagents/artifacts/commercial-netcode/gen003-plan.md`, 영수증 `.pi-subagents/artifacts/commercial-netcode/gen003-release.json`. 잔여: [Message] enum D5 유예, Nullable enum 미지원(문서화) |
| GEN-004 | INVESTIGATE | RPC 직접 Vector/Quaternion/배열 인자에는 필드 Serializer와 같은 확장 경로가 없음. Message envelope 우회는 존재 | 기존 envelope 기능을 누락으로 세지 않되 일반 Unity 값·컬렉션 인자의 직접 지원 필요성과 안전한 크기/타입 계약 검토 |
| LIF-001 | OPEN (해결 구현·검증 완료 — 배포 대기) | `UniNetManager.ServerStopAsync`(실제 :247)에는 동기 ServerStop의 SweepDynamicSpawns가 없음. 시작 실패 롤백 부재. 추가 확정: NetworkBehaviour 등록 플래그 비가역(정지→재시작 후 씬 객체 미재등록) | 해결: ① ServerStopAsync 스윕(동기 동일 위치) ② ServerAsync listen try/catch 롤백 ③ 등록 추적 세대 스탬프 교체(NetworkServer/Client.Generation + Behaviour 세대 필드). 검증: RED job `bff7128f`(스윕 비활성 시 T1/T2 실패), EditMode 268/268(job `6f37c024`)·PlayMode 47/47 ×3(job `8af1ec74`·`015c7ab0`·`60ea66b5`), 신규 ServerRestartSweepPlayTests T1~T5. 리뷰 r26 진행 중. 계획 `.pi-subagents/artifacts/commercial-netcode/lif001-plan.md`. v0.19.0 배포 대기 |
| SCN-001 | CLOSED | 조사 확정(`aa6c5b85`): 게이트 완료 시 `_pendingScene=null` — 후발 접속자는 현재 네트워크 씬을 영구히 못 받고, 씬 객체 baseline은 클라 등록 전 lookup-miss로 조용 폐기(로비에 동적 객체만 남는 실증 경로) | **v0.8.0 개별 배포 완료(2026-10-21)** — `1de0a40` → [Release](https://github.com/BIGSUNGG/UniNet/releases/tag/v0.8.0). `_currentScene` 추적 + RunPostApproval 재전송 — SCN-002/003와 단일 단위로 해결. 검증 상세는 아래 SCN 최신 검증 상태와 evidence.json 참조 |
| SCN-002 | CLOSED | 조사 확정(`aa6c5b85`): SendLoadScene 직후 즉시 캐치업 전송 — 씬 로드/등록 전 도착한 전체상태는 무음 폐기되고 서버 스냅샷은 이미 갱신돼 초기값 재전송 없음(고정값 영구 불일치 실증 경로) | **v0.8.0 개별 배포 완료(2026-10-21)** — per-connection ready 유보. 독립 리뷰 r6~r8 CLEAN, 기존 참여자도 ready 시 재배정·전체상태 복구(접속 이벤트 중복 없음), 마지막 보고의 접속 구독자 예외에도 게이트 평가. 회귀·실제 Notify 검증 — 부모 Unity `SceneTransitionTests` 34/34 EditMode·`CurrentSceneJoinPlayTests` 1/1 PlayMode PASS, 전체 EditMode 202/202·PlayMode 31/31 2회 연속 PASS. 상세는 아래 SCN 최신 검증 상태와 evidence.json 참조 |
| SCN-003 | CLOSED (수용 잔여 별도) | ADR-0045 승인 범위: 해당 연결의 현 wire 11 송신 **전에 도착한** 낡은/위조 보고가 게이트에 반영되는 문제 | **v0.8.0 개별 배포 완료(2026-10-21)** — `SceneLoadSent`+전환 세대 캡처로 송신 전 도착 보고 거부. 새 wire 11 뒤 도착한 이전 동명 씬 보고는 아래 수용 잔여로 분리(범위 내 미해결 결함 아님). 상세는 아래 SCN 최신 검증 상태와 evidence.json 참조 |
| ARENA-001 | OPEN | `ArenaBootstrap.LoadPlayerPrefab`이 UNITY_EDITOR 외 빌드에서 null 반환 | 실제 빌드에서 프리팹을 포함·조회하고 Windows 전용 서버+별도 클라 2개 실행 검증 가능하게 수정 |
| INSTALL-001 | OPEN | `Package/tools/verify-install.sh`는 로컬 repo clone/file URL만 구성 | 최종 공개 UPM 태그를 신규 프로젝트에 설치·컴파일하는 확인 경로 추가. 개발 도구 변경도 리뷰 대상 |

### SCN 최신 검증 상태 (2026-10-21 — 마감)

- **최종 전수 검증·배포 (2026-10-21)**: 클린 에디터 세션에서 전체 EditMode **202/202**(job `93c300840c164d69af45354621316c21`), 전체 PlayMode **31/31 2회 연속**(jobs `bd52731bde084b489cbac929779dc2e2`·`bd2c38fff936414b8e9a5aa04eea609b` — 동일 세션 2회 실행으로 크로스런 포트 재사용 제거 입증). 기능 커밋 `1de0a40` — v0.8.0 태그·[Release](https://github.com/BIGSUNGG/UniNet/releases/tag/v0.8.0)·CI 성공(tag runs `39598549289`/main `39598544339`). 신규 프로젝트 설치·컴파일(로컬 Git URL, 샘플 포함) PASS — 공개 태그 설치 게이트는 INSTALL-001 항목으로 존속. 사용자 자산(Arena.unity·BlackMaterial)은 기능 커밋에서 제외·해시 일치 확인.
- **독립 리뷰 r6~r8 (zai/glm-5.3-flash)**: r6(리뷰5 코드 정적 검토) — 런타임 구현 전부 정확 판정, 문서-코드 불일치 2건 발견(생성기 0.2.11 범핑·신규 공개 API 2종을 "무변경"으로 기록한 7개 문서 + 2인자 오버로드 낡은 XML 주석). r7 — 정정 전수 확인 CLEAN. r8(테스트 인프라 정비 포함) — CLEAN(비차단 advisory 3건은 다음 변경 시 반영 후보).
- **테스트 인프라 정비 (리뷰 6 발견 → 플래너·워커 정비)**: (1) PlayMode 테스트 간 LateScene 누수 — CurrentSceneJoinPlayTests의 Single 전환이 환경 씬을 대체하며 잔존하는 문제: TearDown이 비활성 LateScene을 언로드하고, SceneLoadRegistrationTests의 `[UnitySetUp]` 가드가 잔존(애디티브·활성 씬 모두 — 활성은 `CreateScene` 앵커 후 언로드)을 자가 치유. (2) 20개 픽스처의 static readonly 포트(도메인당 1회 평가 → 이전 실행 잔존 소켓과 재충돌)를 TestPortAllocator(호출마다 신규 포트)로 교체해 크로스런 재사용 제거. 계획 문서: `.pi-subagents/artifacts/commercial-netcode/scn-r6/`(scene-leak-fix-plan·port-allocator-plan). 대상 스위트 최종: `SceneTransitionTests` 34/34(job `ece5ab18f3f34cccb3cc7813ad733e98`)·`CurrentSceneJoinPlayTests` 1/1(job `6c7c7a306ca94ef0826f0d90d68c0860`). SCN-001/002/003 CLOSED.
- **리뷰 5 후속(구현 이력)**: 유보 중 `DestroyObject`가 서버 등록/가시성을 제거한 뒤 통보를 버려, 클라에 살아남은 씬 객체가 영구 잔류할 수 있는 필수 지적을 수정했다. 승인된 유보 연결별 고유 파괴 ID를 보관하고 등록 후 유효 ready 보고에서 여전히 서버에 없는 ID만 소유권/캐치업 기준선 전에 1회 전달한다. 같은 netId 재등록은 낡은 파괴를 보내지 않고 현재 전체상태로 복구하며 disconnect 정리는 메인 큐에 한정한다. 준비된 연결 즉시 송신·미승인 무통보. 공개 API 확장(세대 캡처 2종 신규 — 구 생성 코드용 2인자 오버로드 호환 유지)·와이어 불변·생성기 0.2.11 범핑(이전 문서의 "무변경" 표기는 독립 리뷰 r6에서 정정). EditMode 3케이스 추가(총 34), 순차 Core/EditMode/PlayMode dotnet 빌드·패키지 검증 PASS, headless 신규 **5/5**+기존 **22/22 PASS**, 수정 전 소스는 누락 파괴 단언으로 예상 실패했다. 근거 `.pi-subagents/artifacts/commercial-netcode/scn-r5/`(프로브 초기 using 누락 빌드 실패 로그 포함). 이후 부모 Unity `SceneTransitionTests` **34/34 EditMode PASS**(job `ece5ab18f3f34cccb3cc7813ad733e98`) 및 `CurrentSceneJoinPlayTests` **1/1 PlayMode PASS**(job `6c7c7a306ca94ef0826f0d90d68c0860`).

- 리뷰 3 보정 이후 부모가 `SceneTransitionTests` 26/26 EditMode·`CurrentSceneJoinPlayTests` 1/1 PlayMode, 리뷰 4 코드에서 31/31·1/1 PASS를 실행했다. 리뷰 5 코드에서도 부모가 `SceneTransitionTests` **34/34 EditMode PASS**(job `ece5ab18f3f34cccb3cc7813ad733e98`) 및 `CurrentSceneJoinPlayTests` **1/1 PlayMode PASS**(job `6c7c7a306ca94ef0826f0d90d68c0860`)를 확인했다. 이전 카운트와 worker의 Unity MCP 부재 문장은 과거 이력이다. 이는 대상 suite만 검증했으며 전체 Sandbox suite를 뜻하지 않는다.
- 리뷰 4에서 승인 꼬리 대기 중 중복 wire 11·예외 시 overflow 소유권·송신 ID 선형 병합·예산 압박 중 빈 큐 폐기를 수정했다. EditMode 5케이스 추가(총 31), 순차 Core/EditMode/PlayMode dotnet 빌드·패키지 검증·headless **22/22**·전 소스 음성 대조 3종 PASS. 동일 합성 benchmark는 **127.36→112.14ms**, 양쪽 **22,933,216 bytes**(Unity 성능 개선 증거가 아님). 산출물: `.pi-subagents/artifacts/commercial-netcode/scn-r4/`; 상세 계약/트레이드오프는 [[features/connection-lifecycle]].

### 수용된 범위 밖 잔여 (ADR-0045)

- **ACCEPTED — 동명 씬 in-flight tokenless 보고**: 이미 송신된 이전 보고가 해당 연결의 새 wire 11 송신 뒤 도착하면 구별 불가. 조기 유보 해제·게이트 완료 위험을 수용했으며, 토큰·wire 11/12·공개 시스템 채널 시그니처 변경은 범위 밖이다. SCN-003의 범위 내 미해결 결함으로 세지 않으며, 테스트·리뷰·배포 성공을 뜻하지도 않는다. 타임아웃/취소는 ADR-0035의 게임 책임 계약을 유지한다.

### 오탐·보류 근거

- 예산 연기 델타의 Owner/Others 그룹은 전송 시점의 현재 owner로 선택된다. 그룹 이름이 enqueue 때 정해진다는 이유만으로 이전 owner에게 비공개 필드가 전송된다고 단정할 수 없다. OWN-002 회귀 테스트(입양 baseline+타 연결 OwnerOnly 미누출 단언)로 확인 완료 — 오탐 확정.
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
