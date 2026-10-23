# Unity 게임 서버용 기능 검토 — 내부 점검과 4개 프레임워크 비교

**결론: UniNet은 기본 넷코드 기능을 상당히 갖췄지만, 현재 v0.22.0을 검증 없이 상용 서버에 바로 채택하기는 권하지 않는다. 새 기능을 늘리기 전에 승인 경계·상태 복구·컬렉션 RPC·관심영역 재진입의 정확성을 먼저 보완해야 한다.** 이는 이번 코드·격리 실행에 근거한 판단이지, 경쟁 제품의 무결성 보증이나 전체 보안 감사 결과가 아니다.

- 검토일: **2026-10-23**. 기준 HEAD: `60368a337b160012b973f7b7be71d0d37a2c8ee9`, 패키지 **0.22.0**, 동봉 생성기 **0.2.17**.
- 범위: Unity 6, GameObject/MonoBehaviour, 서버 권위·셀프호스팅 넷코드. 게임 장르가 지정되지 않았으므로 공통 결함과 장르별 선택 기능을 구분한다.
- 순서: **1차 내부 코드·테스트·격리 실행 → 2차 Mirror/FishNet/Unity NGO/Photon Fusion 공식 자료 대조 → 권고 재평가**.
- 변경: 검토 문서만 변경. 제품 코드·기존 테스트·Unity 게임 자산·버전·태그는 변경하지 않았다. 격리 probe는 무시되는 하네스 산출물이며 배포물/회귀 테스트가 아니다.
- 기존 [[commercial-netcode-audit]]의 CLOSED는 당시 항목과 승인된 계약의 마감이다. **프로젝트 전체의 결함 부재를 뜻하지 않는다.** 본 문서는 별도 후속 발견 사항이며 과거 해결 이력을 소급 변경하지 않는다.
- [[network-framework-comparison]]의 2026-09 표는 역사 자료다. 예측·커스텀 직렬화·컬렉션 델타·씬 전환·재접속·대상 RPC·회전/스케일을 현재 미구현으로 세면 안 된다.

## 1. 이미 갖춘 기능 — 없는 것으로 다시 세지 않는다

소스·생성 경로·기존 테스트를 대조해 다음 기능의 존재를 확인했다. 존재 확인과 모든 시나리오의 정상 동작 보증은 다르다.

- Server/Client/Multicast RPC, 소유자 기본 검사, 옵트인 Validate, 연결당 RPC 호출 제한, 특정 연결/소유자 대상 RPC.
- 필드 델타·RepNotify·OwnerOnly/SkipOwner/InitialOnly·커스텀 직렬화·배열/List 요소 델타.
- 프리팹 기반 스폰/파괴·후발 접속 캐치업·다중 NetworkBehaviour·명시적 소유권 이전·플레이어 매핑.
- 거리/훅/그리드 관련성·우선순위·휴면·30Hz 기본 전송 주기·배칭·예산·비신뢰 전송 옵션.
- 게임 정의 입력/상태 기반 고정 스텝 예측·롤백/재시뮬, 원격 보간, 위치 히스토리, 회전/스케일 동기화.
- 버전 핸드셰이크·게임 접속 승인·킥 사유·하트비트 튜닝·유예 재접속·씬 ready 게이트·통계/에디터 디버거.
- DTLS **선택 설정**과 인증서 검증 확장점. 무설정 연결까지 자동 암호화되는 것은 아니다(`UniNetEndpointOptions.cs:64-76`).

## 2. 1차 검토: 먼저 해결해야 할 동작·보장 공백

### 우선순위와 근거 수준

- **P0**: 인터넷 노출 전 우선 차단할 신뢰 경계 문제.
- **P1**: 해당 공개 기능을 상용 게임에서 쓰기 전에 고칠 정확성·지속 운용 문제.
- **P2**: 사용성/장르/규모에 따라 채택할 기능. 모두 구현하라는 의미가 아니다.
- **실행 확인**: 아래 명시한 격리 범위에서 반례를 실행했다. **정적 확인**: 소스 분기·호출 경로를 확인했으며 Unity 실기 재현과 구분한다.

### FR-01 · P0 · 승인/씬 준비 경계를 우회하는 서버발 게임 RPC

**실행 확인(동봉 생성기 송신 partial + 실제 Core + 기록 채널)**.

- `CodeGenerator/Emitter.cs:1230-1259,1290-1305`: ClientRpc/대상 RPC/Multicast 송신 대상은 `Disconnected`만 확인한다. `VersionApproved`·`AwaitingSceneReady` 확인이 없다.
- 같은 파일 `:464-472`의 생성 서버 허브 `UniNetSend`도 일반 송신으로 바로 전달한다. 복제/스폰에 있는 준비 게이트(`NetworkServer.cs:435-451,1614-1616`)를 공유하지 않는다.
- 격리 실행: 버전을 아직 선언하지 않은 연결에 게임 ClientRpc를 호출하면 **`approved=False, gameRpcSends=1`**. 클라이언트에 대상 객체가 없어 실행이 생략되더라도 **페이로드의 송신 자체는 차단되지 않는다**.
- 영향: 비인가 클라이언트에 게임 이벤트/인자 노출, 씬 등록 전 RPC 소실. 접속 승인 콜백의 존재만으로 “승인 전 게임 데이터 없음”이 보장되지 않는다.
- 최소 보완 방향: 서버발 게임 송신의 승인·ready 판정을 공통화하고 방송/대상/멀티캐스트 모두 회귀 확인. 관심영역 수신자 정책은 명시적 타깃의 의도와 구분해 설계한다.

### FR-02 · P1 · 비신뢰 필드 델타에 지속 갱신 중 복구 보장 없음

**실행 확인(실제 Core 스케줄러 + 두 필드 테스트 핸들러 + 1회 드롭 채널)**.

- `NetworkServer.cs:1517-1552`: 전체 서브의 변경이 멈춘 경우에만 0.25초 후 신뢰성 전체상태를 재전송한다. 매번 다른 필드가 바뀌면 이 조건이 성립하지 않는다.
- 생성 핸들러는 비교 후 공통 스냅샷을 전진시킨다(`Emitter.cs:1070-1078`). 다음 델타가 이전에 유실된 **모든 필드**를 포함하는 구조가 아니다.
- 격리 실행: A를 0→1로 바꾼 첫 메시지만 드롭하고 B를 60Hz로 10초 바꾸면 **서버 A=1, 클라 A=0, 클라 B=600**. 전체 변경을 멈추고 기다린 양성 대조에서는 A가 1로 복구된다.
- 영향: 움직이면서 한 번만 변경한 높이/회전/상태가 장기간 어긋날 수 있다. 순차 전송은 순서 보장이지 유실 필드 복구가 아니다. 요소 연산 델타를 비신뢰로 쓰는 경우도 별도 검증이 필요하다.
- 최소 보완 방향: 주기적 기준선/누적 변경 재전송/수신 확인 기반 중 필요한 보장 선택. **모든 스트림을 신뢰성으로 바꾸는 것만이 답은 아니다**. NGO는 움직이는 동안 주기적 axial frame sync를 명시한다[17].

### FR-03 · P1 · RPC의 비어 있지 않은 List와 컬렉션 길이 경계

**실행 확인(현재 동봉 생성기 DLL이 방출한 코덱 + 실제 MessageProtocol)**.

- `Emitter.cs:255-265`: 수신 List는 `new List<T>(길이)`로 **Capacity만** 잡고, `Count`를 루프 상한으로 삼아 인덱스에 쓴다. 처음 Count는 0이므로 원소를 읽지 않는다.
- 격리 실행: `List<int>{11,22,33}` 뒤에 정수 99를 보냈더니 **Count=0, 다음 인자=11, 미소비 12바이트**. 빈 List/null 테스트만으로는 잡히지 않는다.
- `Emitter.cs:156-167`: 송신 길이는 검사 없이 ushort로 변환하지만 원소는 전체 순회한다. 길이 65,536인 int 배열이 거부되지 않고 **길이 0으로 수신**, 262,144바이트가 남는 반례도 확인했다. 길이 3의 배열은 정상인 양성 대조가 있다.
- `Sandbox/Assets/Tests/EditMode/GeneratorTypeSupportTests.cs:101-102,151-152`에는 중첩 필드·비어 있지 않은 컬렉션 RPC 테스트가 Ignore되어 있다. 이 중 컬렉션 테스트는 실제로 필요한 경계다.
- 최소 보완 방향: 수신 길이만큼 Add, 송신 상한 초과 명시 거부, 비어 있지 않은 List와 후속 인자·최대/초과 경계의 실행 회귀 복원. **Replicated List의 FastArray와 RPC List 코덱은 별개**다.

### FR-04 · P1 · 동적 객체의 관심영역 재진입 기준선이 무시될 경로

**정적 확인(서버 송신 → 클라이언트 적용 경로)**.

- 관련성 이탈은 서버 집합에서만 제거하고 클라이언트 객체는 남긴다(`NetworkServer.cs:1893-1903`).
- 재진입 시 동적 객체의 최신 전체상태는 `SendBaseline` → `SendSpawnState`에 담긴다(`NetworkServer.cs:1395-1408`).
- 그런데 `UniNetSpawn.Apply`는 `client.Get(netId) != null`이면 **상태 적용 전에 즉시 return**한다(`UniNetSpawn.cs:19-21`). `states` 적용은 그보다 뒤 `:73-85`다.
- 영향: 한 번 본 적이 있는 동적 객체가 시야 밖에서 체력/장비 등 고정값을 바꾸면 재진입 시 최신 기준선이 무시될 수 있다. 이후 그 값이 다시 바뀌지 않으면 델타로도 복구되지 않는다.
- 최소 보완 방향: “새 인스턴스 생성”과 “기존 인스턴스 기준선 갱신”을 분리하거나 일관된 hide/unspawn 수명주기 제공. **중복 스폰 방지 가드 자체를 단순 제거해서는 안 된다.**

### FR-05 · P1 · 위치가 같으면 TState 차이를 리컨사일하지 않음

**정적 확인 + 실제 MoveSimulation과 컴포넌트의 동일 위치 분기를 조합한 반례**. Unity 컴포넌트 실기 실행은 아니다.

- `NetworkTransform.cs:442-459`: ack 시 예측 위치 오차가 임계 이하이면 `DiscardThrough` 후 종료한다. `_ackState`의 상태 차이를 비교/복원하는 판단 경로가 없다.
- 격리 실행: 위치는 같지만 대시 쿨다운이 다른 상태를 주고 해당 분기를 적용하면 **클라 쿨다운 0.983, 서버 0.000, pending 0**이 된다.
- 영향: 제자리에서 바뀐 쿨다운/속도/이동 모드가 다음 위치 차이를 만들 때까지 남는다. 게임 정의 TState가 즉시 정확히 수렴한다는 계약에는 부족하다.
- 최소 보완 방향: ack 기준 상태도 판단/복원할 계약과 회귀를 추가한다. 현재 `MoveSimulation.Rollback`의 상태 복원 자체는 존재하며, **full CSP&R 전체가 없는 것은 아니다**. Fusion은 권위 스냅샷 상태를 기준으로 재시뮬하는 루프를 설명한다[15].

### FR-06 · P1 · 예산 초과 시 낡은 델타 적체 / 메인 큐 역압 부족

**복제 적체는 실행 확인, 메인 큐는 정적 확인**.

- `NetworkServer.cs:1554-1586,1600-1607,1662-1675`: 새 후보에 기존 `_queue`를 합치고 예산 초과 항목을 다시 추가한다. 지속 과부하에서 같은 객체의 낡은 델타를 합치거나 상한으로 마감하는 정책이 없다.
- 격리 실행: 매 틱 바뀌는 객체 2개, 델타 1개분 예산, 120틱 후 **`DeferredSendCount=120`**. 공정성만으로 생성량>처리량 문제는 해결되지 않는다.
- `UniNetEnvironment.cs:12,68-80`: 메인 큐도 무제한 enqueue + 전량 drain이다. `PendingMainActions`는 관측치이지 한도가 아니다. RPC 제한은 기본 0이며 연결당 호출 개수 제한이다(`NetworkServer.cs:176,848-870`). **DRPC/전송 계층의 모든 큐까지 검사한 것은 아니다**.
- 영향: 오래된 상태 지연 적용·메모리 증가·게임 프레임 점유. 현재 채널/전체 예산을 “하드 자원 상한”으로 해석하면 안 된다.
- 최소 보완 방향: 필드/전체상태는 합칠 수 있어도 컬렉션 연산 델타는 임의로 버리면 안 된다. 최신 전체 기준선으로 대체하는 등 의미를 보존하는 적체 정책과 큐/프레임 처리 예산이 필요하다. FishNet의 입력 큐 상한/폐기 옵션은 비교할 참고 사례이지 모든 큐의 안전 증명은 아니다[6].

### FR-07 · P1 · 생성기 소스 재빌드와 배포 검증의 공백

**빌드 오류 확인**. 런타임 기능 누락과는 별도의 출고 신뢰성 문제다.

- `CodeGenerator/UniNetCodeGenerator.cs:54`에 깨진 문자열/`catch` 잔재가 있다. 현재 소스를 포함한 격리 컴파일은 **CS1010·CS1002**로 실패한다. 제품 소스는 수정하지 않았다.
- 따라서 이후 probe는 소스를 몰래 고친 사본 대신 **현재 배포 패키지의 생성기 DLL**을 참조했다. 그 DLL의 코덱에서도 FR-01/03이 재현된다. 소비자 임포트가 성공하더라도 소스에서 동일 산출물을 다시 만들 수 있다는 뜻은 아니다.
- `.github/workflows/upm.yml:14-25`의 validate job은 패키지 정적 검사만 실행한다. 생성기 빌드·생성 코드 컴파일·Unity 런타임 테스트가 CI 필수 게이트가 아니다.
- 최소 보완 방향: 생성기 소스 빌드 + 진단/생성물 컴파일 + 비어 있지 않은 코덱 왕복을 최소 CI에 포함. Ignore 2개를 통과로 집계하지 않는다. 대규모 CI 프레임워크 신설보다 기존 검사에 필요한 실행 게이트를 붙이는 편이 작다.

## 3. 2차 검토: 유사 프로젝트를 보고 달라진 판단

### 비교 대상의 역할

| 대상 | 참고 이유 | 비교 한계 |
| --- | --- | --- |
| **Mirror** | 서버 권위 GameObject, observer 기반 RPC·동일 서버 내 additive 인스턴스·커스텀 spawn/unspawn[1], [2], [3] | 이 문서가 Mirror 전체 기능/보안을 재감사한 것은 아님 |
| **FishNet** | observer 생명주기·연결별 씬·예측 큐 정책·풀링의 실용적 API[4], [5], [6], [7] | Pro/무료 전체 기능을 동일하게 가정하지 않음. 이 문서에서 별도 lag compensation 제공 여부는 미평가 |
| **Unity NGO 2.4.4** | 공식 parenting·객체 참조·풀링 확장점·비신뢰 축 복구 문서[8], [9], [16], [17] | 확인한 **고정 판본**이며 최신 버전 전반의 판정이 아님. 해당 문서는 full rollback CSP&R이 아니라 anticipation이라고 명시[10] |
| **Photon Fusion 2** | 공통 tick·권위 상태 재시뮬·히트박스 이력·scene/provider 수명주기[12], [13], [14], [15] | 서버/호스트/Shared를 구분. 내장 hitbox lag compensation은 Server/Host 전용[12] |

### 기능별 대조와 재평가

| 기능 축 | 공식 자료로 확인한 참고점 | 현재 UniNet | 재평가 |
| --- | --- | --- | --- |
| 오브젝트 RPC / 특정 대상 | Mirror Command/ClientRpc/TargetRpc[1], NGO RpcTarget[11] | 3종 + To/ToOwner 존재 | 추가 구현이 아니라 FR-01 경계부터 보강 |
| 서버가 아는 실제 발신자 | Mirror가 optional sender를 주입[1]; NGO는 send/receive context 제공[11] | 생성 dispatch만 senderConnId를 알고 `_Validate`/`_Implementation`에는 게임 인자만 전달(`Emitter.cs:1204-1225`) | **P2 높음**: RequireOwnership=false 상호작용·채팅·상점 등에 신뢰할 호출자 context 필요. 클라 인자 connId는 인증 근거가 될 수 없음. 자기 소유 Player RPC로 우회 가능 |
| 비신뢰 델타 복구 | NGO는 움직이는 동안 초당 축 기준선으로 eventual consistency 명시[17] | 전체 서브 정지 후에만 복구 | **FR-02 우선도 상승**. 모든 패킷의 신뢰성보다 “유실 이후 수렴”이 핵심 |
| observer와 메시지 범위 | Mirror ClientRpc는 observer에만[1]; FishNet 비observer는 메시지/콜백과 객체 활성도 차단[4] | 복제 관련성 필터 존재, 방송 RPC에는 관련성 필터 없음(`Emitter.cs:1230-1240,1290-1305`) | AOI를 보안/은닉 경계로 쓰려면 **추가 P1 계약** 필요. “이미 본 정보를 회수”하는 것은 불가능하며 이후 노출 차단을 뜻함 |
| 관심영역 이탈·재진입 | FishNet은 씬 객체 비활성·동적 객체 미생성 정책 설명[4] | 이탈 시 마지막 객체 유지 + FR-04 | **P2 hide/활성 콜백**과 **P1 재진입 기준선 적용**을 구분. 모든 제품이 이탈 즉시 Destroy한다고 일반화하지 않음 |
| additive / 연결별 인스턴스 | Mirror scene+physics 격리[2]; FishNet global/connection scenes·stacking[5] | 네트워크 LoadScene은 전 연결 단일 이름·클라 Single 로드(`NetworkServer.cs:923-938`, `UniNetManager.cs:86-90`) | **P2, 인스턴스 던전/다중 매치에는 P1**. Unity에서 수동 additive 로드/자동 등록이 가능하다는 것과 네트워크 씬 관리 지원은 다름 |
| 동일 씬 재로드/다중 인스턴스 | Fusion은 load Version으로 동일 씬의 새 로드를 구분하고 additive 최대 8개를 문서화[14] | 씬 에셋별 각인 ID, 네트워크 씬 인스턴스 ID 없음(`NetworkBehaviour.cs:17-41`) | 다중 인스턴스면 식별자·ready·언로드 정리까지 필요. 현재 ID를 단순 경로 해시라고 설명하면 오탐 |
| 네트워크 풀링 확장점 | Mirror spawn/unspawn delegate[3], FishNet pool[7], NGO prefab handler[9], Fusion provider[13] | 서버/클라 Instantiate/Destroy 경로 고정(`UniNetManager.cs:319-391`, `UniNetSpawn.cs:55,99-110`) | **P2 우선 후보**. 기본 풀 전체가 아니라 acquire/release+상태 초기화 확장점부터. 4사 모두 기본 자동 풀링이라고 말하면 틀림 |
| 네트워크 parenting | NGO는 parent와 transform을 현재/후발 클라에 복제[8] | parent ID/attach 메시지 없음; 월드 position/rotation·localScale 복제 | **P2**: 탑승/장비 부착 게임이면 필요. 고정 프리팹 자식 계층 보존과 런타임 부착 동기화는 별개 |
| 안전한 객체/컴포넌트 참조 | NGO reference wrapper + TryGet, component index[16] | NetId/SubId·Get 기반 수동 조회는 가능, 전용 직렬화 참조 계약 없음 | **P2**: 작은 값 타입+기존 조회 재사용이면 충분. 새 객체 레지스트리 불필요. 파괴/미스폰/후발 수신 실패 계약 필요 |
| 예측의 범위 | Fusion은 공통 tick의 권위 state에서 재시뮬[15], FishNet prediction manager[6] | 이동별 고정 스텝 CSP&R은 있음, FR-05·전역 network tick 없음 | **이동 예측 미구현이라는 옛 결론 철회**. 상태 전용 수렴·입력/스냅샷 tick 귀속을 보강하고, 전역 물리 rollback은 게임 요구가 생길 때 |
| 히트 판정 리와인드 | Fusion hitbox별 position/rotation·raycast/overlap·subtick[12] | `PositionHistory.cs:12-90`는 XYZ 이력, `NetworkBehaviour.cs:111-140`는 위치 조회 훅 | **P2, 정밀 FPS에는 P1**: 회전한 히트박스/자세/shape 이력·쿼리 없음. 원형 판정 아레나에 풀 물리 rollback을 일괄 추가할 필요 없음 |
| 시간 정합 | Fusion의 명시적 tick과 재시뮬[15] | `UniNetTime.cs:24-26,86-104`는 RTT 보정 없는 수신 offset; 이동 렌더도 도착 시각 샘플(`NetworkTransform.cs:419-432`) | 정밀 FPS에서는 편도 지연·서버 스냅샷 시점·보간 지연을 함께 측정. **시간 동기화가 없다는 판정은 아님** |

### 비교 후의 최종 우선순위

1. **출고 차단/핵심 보장**: FR-01 → FR-02/03/04/05. 새 API 개수보다 권한 경계와 상태 수렴을 먼저 확보한다.
2. **함께 검증할 운영 경계**: FR-06/07. 지속 예산 초과·느린 클라·잘림/초과 입력·승인 거절/로딩 중 RPC·비어 있지 않은 List·패킷 1회 유실을 실제 경로로 회귀화한다.
3. **범용 확장 후보**: 실제 발신자 context → 네트워크 acquire/release 확장점 → 객체 참조·parenting. 필요한 게임 API를 작게 확장한다.
4. **장르별**: 인스턴스 게임은 연결별/additive 씬과 생명주기, 정밀 FPS는 hitbox history/tick/보간 지연, 물리 게임은 Rigidbody/충돌 예측을 별도 요구로 잡는다.

**비교가 바꾼 핵심 판단**: “P1~P4 기본 기능이 없다”가 아니라 **있는 기능을 조합했을 때의 수명주기·유실 복구 보장이 약하다**. 풀링은 공통된 유용한 확장점이지만, 모든 타사 기능의 합집합을 구현할 이유는 없다.

## 4. 게임 유형별 사용 판단과 불필요한 확장 방지

| 사용 형태 | 현재 판단 / 추가 조건 |
| --- | --- |
| 소규모 단일 씬·단일 매치·간단한 이동/원형 히트 판정 | 기본 기능은 충분한 편. FR-01~07의 해당 경로를 해결·실제 서버/클라로 검증한 뒤 채택 판단. “4인까지 안전” 같은 CCU 보증은 하지 않음 |
| 고빈도 스폰 액션/슈팅 | 위 조건 + acquire/release 확장점의 필요성을 프로파일링. 풀링 없는 것만으로 프레임 드롭을 실측했다고 말하지 않음 |
| 정밀 FPS·차량·물리 상호작용 | 위치 훅만으로 완성형 hitbox rewind/물리 예측이라고 보기 어렵다. 상태 전용 수렴·tick 시간축·히트박스·충돌별 요구가 먼저 |
| 한 프로세스의 여러 방/인스턴스 던전 | 현재 네트워크 씬 관리로 바로 지원한다고 할 수 없음. scene instance/connection membership/격리 필요. 단일 매치 프로세스 여러 개 배치하는 방식과 구분 |
| 오픈월드·다중 서버/존 이동 | 존 핸드오프·서버 간 세션/상태 이전은 현 단일 프로세스 전역 환경(`UniNetEnvironment.cs:8-28`) 밖의 설계 과제. 같은 게임을 한 서버에서 돌리는 데 무조건 필요한 기능은 아님 |

- **계정/DB/매치메이킹/배치·오토스케일/릴레이 서비스 자체 구축**: 상위 서비스 영역. 연결 승인·접속 주소·메타데이터 확장점과 서비스 제품 전체를 혼동하지 않는다. 직접 접속 가능한 전용서버에 NAT 통과/호스트 마이그레이션을 일괄 필수화하지 않는다.
- **애니메이션/음성/리플레이/관전/마이그레이션/Dictionary·Set 전용 델타**: 사용 계약을 정한 뒤 필요시 추가. 현재 Replicated/RPC·List로 충분한 게임까지 의무 기능으로 세지 않는다.
- **보안 운영 전제**: 실제 서비스 자격 증명·재개 토큰은 DTLS와 적절한 인증서 검증을 구성해 보호해야 한다. `ConnectionKey`나 암호학적 토큰 생성만으로 전송 기밀성이 생기지 않는다. 이번에는 DTLS 실연결·공격 부하를 시험하지 않았다.

## 5. 검증 증거와 한계

### 이번 실행

원시 산출물 루트: `.pi-subagents/artifacts/functional-review-2026-10-23/` (Git 무시, 로컬 검토 증거).

| 산출물 | 의미 |
| --- | --- |
| `internal-scout.md` | 읽기 전용 내부 조사. 부모가 대조하여 잘못된 경로 해시 일반화·성능 단정·씬 스폰 유실 추정은 본 보고서 확정 항목에서 제외 |
| `probe/Probe.csproj`, `probe/Program.cs` | 현재 Core 소스를 링크하고 **동봉 생성기 DLL**을 읽어 실행하는 .NET 10 격리 probe. 새 패키지 의존성 설치 없음 |
| `probe-source-build-initial.log` | 생성기 **현재 소스** 포함 컴파일의 CS1010/CS1002. 제품 코드 빌드 실패를 숨기지 않음 |
| `probe-harness-missing-debug.log` | 별도 생성 partial 프로브의 로그 stub 부족으로 발생한 **하네스 초기 오류**, 이후 보완. 제품 오류와 구분 |
| `generator-dump-side-effect.patch` | 최초 생성기 실행이 추적 중인 `.genprobe/unity-dump.g.cs`를 덤프 출력으로 변경한 이력. 시작 시 그 파일은 HEAD와 같았으며 정확히 복원했다. 최종 probe는 동봉 Parser/Emitter 직접 호출로 이 부수 효과를 피한다 |
| `probe-run.log` | List/길이 경계·승인 전 송신·유실 필드·적체·상태 전용 분기의 반례 출력. 종료 0은 **반례 재현 성공**이며 제품 정상 PASS가 아님 |
| `probe/bin/Release/net10.0/emitted-codec.cs.txt`, `shipped-generator-output.cs.txt` | 동봉 DLL에서 얻은 실제 생성 코덱/출력. 원격 서버·Unity 네이티브 동작을 실행한 것은 아님 |
| `sources.json`, `citation-check.json`, `source-evaluation.json` | 조회한 공식 출처 목록·구조적 인용 검증·출처 평가. 이 검사만으로 주장 의미의 참/거짓이 자동 증명되지는 않음 |

재실행: `dotnet run --project .pi-subagents/artifacts/functional-review-2026-10-23/probe/Probe.csproj --configuration Release`. 프로브는 로컬의 기존 Roslyn 4.3 캐시를 사용한다. 이 산출물 디렉터리는 배포 패키지에 포함되지 않는다.

### 명시적 한계 / 미확인

1. **Unity EditMode/PlayMode 전수·새 3프로세스·DTLS·실제 인터넷 지연/손실 실험은 이번에 하지 않았다.** 기존 감사의 통과 기록을 이번 재검증 결과로 재사용하지 않는다. FR-04는 정적, FR-05는 Core+동일 분기 반례로 한정한다.
2. 외부는 **공식 본문** 비교다. Mirror/FishNet 최신 커밋·Fusion 내부 구현·NGO 최신 버전 전체를 실행 감사하지 않았다. NGO는 문서 제목이 명시한 **2.4.4** 기준이며, 일부 공식 페이지는 갱신 시점이 오래됐다.
3. CCU·tick p95/p99·장시간 메모리·손실/지터 하 예측 품질의 상용 SLO를 입증하지 않았다. 이번 120틱 적체 재현과 소규모 기존 Arena 통과로 “수백 명 가능/불가능”을 단정하지 않는다.
4. 조회 한도: **검색 8개 쿼리 시도(마지막 Mirror 검색 1개 공급자 오류), 본문 18개 URL 시도 중 17개 유효**. Fusion의 옛 `/manual/network-simulation-loop` 경로는 본문 근거가 없어서 제외하고 `/concepts-and-patterns/`로 교체했다. 사용량/비용은 검색·본문 도구가 반환하지 않아 미계측이며 **무료로 간주하지 않는다**.
5. 내부/외부 근거가 충분해지는 지점에서 종료한 제한된 기능 검토다. 무한 재검색·모든 경쟁사·모든 기능에 대한 전수 완결을 주장하지 않는다. 미검증 후보를 “결함 확정”이나 새 범위 제외 결정으로 바꾸지 않았다.

## 6. 근거 간 충돌과 정정

- **문서의 전항 CLOSED vs 이번 반례**: 과거 항목의 계약·범위와 이번 조합 시나리오가 다르다. 기존 해결을 취소한 것으로 세거나 현재 무결성으로 확대하지 않는다.
- **full CSP&R 없음 vs 현재 코드**: 현재 MoveSimulation에는 실제 롤백/상태 복원/재시뮬이 있다. 남은 문제는 상태 전용 차이를 트리거하지 않는 컴포넌트 경계다. NGO anticipation과 같은 것으로 축소하지 않는다[10], [15].
- **DTLS 기본 암호화 vs 현재 옵션**: `ServerCertificate`를 설정해야 켜지는 선택 기능이다. 예전 비교의 보안 우월 주장은 근거로 쓰지 않는다.
- **배칭/주기/예산 존재 vs 실제 보장**: 이는 유실 복구·안정된 최대 메모리·매 틱 최대 2패킷까지 자동 보장하지 않는다. 배치가 크면 중간 flush도 있으므로 “연결당 항상 2개 이하” 역시 일반화하면 안 된다(`NetworkServer.cs:1634-1635`).

## 공식 출처

모두 본문을 조회한 1차 자료다. 아래 번호는 본문 및 표의 인용과 연결된다.

[1] Mirror Remote Actions — <https://mirror-networking.gitbook.io/docs/manual/guides/communications/remote-actions>
[2] Mirror Scene Interest Management — <https://mirror-networking.gitbook.io/docs/manual/interest-management/scene>
[3] Mirror Custom Spawn Functions — <https://mirror-networking.gitbook.io/docs/manual/guides/gameobjects/custom-spawnfunctions>
[4] FishNet Observer System — <https://fish-networking.gitbook.io/docs/guides/features/observers>
[5] FishNet Loading Scenes — <https://fish-networking.gitbook.io/docs/guides/features/scene-management/loading-scenes>
[6] FishNet PredictionManager — <https://fish-networking.gitbook.io/docs/fishnet-building-blocks/components/managers/predictionmanager>
[7] FishNet Object Pooling — <https://fish-networking.gitbook.io/docs/guides/features/networked-gameobjects-and-scripts/spawning/object-pooling>
[8] NGO NetworkObject parenting — <https://docs.unity3d.com/Packages/com.unity.netcode.gameobjects@2.4/manual/advanced-topics/networkobject-parenting.html>
[9] NGO Object pooling — <https://docs.unity3d.com/Packages/com.unity.netcode.gameobjects@2.4/manual/advanced-topics/object-pooling.html>
[10] NGO Client anticipation — <https://docs.unity3d.com/Packages/com.unity.netcode.gameobjects@2.4/manual/advanced-topics/client-anticipation.html>
[11] NGO RPC params — <https://docs.unity3d.com/Packages/com.unity.netcode.gameobjects@2.4/manual/advanced-topics/message-system/rpc-params.html>
[12] Fusion Lag Compensation — <https://doc.photonengine.com/fusion/v2/manual/advanced/lag-compensation>
[13] Fusion Advanced Spawning — <https://doc.photonengine.com/fusion/v2/manual/advanced/advanced-spawning>
[14] Fusion Scene Loading — <https://doc.photonengine.com/fusion/v2/manual/scene-loading>
[15] Fusion Network Simulation Loop — <https://doc.photonengine.com/fusion/v2/concepts-and-patterns/network-simulation-loop>
[16] NGO NetworkObject serialization — <https://docs.unity3d.com/Packages/com.unity.netcode.gameobjects@2.4/manual/advanced-topics/serialization/networkobject-serialization.html>
[17] NGO NetworkTransform — <https://docs.unity3d.com/Packages/com.unity.netcode.gameobjects@2.4/manual/components/networktransform.html>

## 관련 문서 / 변경 이력

- [[../overview|overview]] · [[../roadmap|roadmap]] · [[commercial-netcode-audit]] · [[network-framework-comparison]]
- 2026-10-23 — v0.22.0 내부 기능 검토·격리 반례와 4개 프레임워크 공식 본문 비교. 구현/수정/배포 완료를 의미하지 않는 검토 결과로 기록. 라이브러리 동작·공개 API를 변경하지 않았으므로 README/소비자 SKILL은 이번 문서 변경 대상에서 제외.
