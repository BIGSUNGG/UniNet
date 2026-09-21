# examples/arena-shooter — Sandbox 아레나 슈팅 예시 게임

- **상태**: 구현됨 (Sandbox — UniNet 구현 기능(P1 전체 + P2 전체) 활용 데모)
- **작성**: 2026-09-17
- **위치**: `Sandbox/Assets/Scripts/Arena/` · 씬 `Sandbox/Assets/Scenes/Arena.unity`

## 개요

UniNet의 구현된 기능 전부를 활용하는 **탑다운 2~4인 슈팅 아레나** 예시 게임. 에셋 없이 Unity 기본 도형(Plane·Cube·Sphere)과 간단한 FX만으로 캐릭터·맵·이펙트를 구성한다. 서버 권위 모델을 그대로 따른다 — 입력은 ServerRpc로 서버에 제출하고, 시뮬레이션(이동·충돌·발사·피해)은 서버만 수행하며, 결과가 조건부 리플리케이션으로 각 클라에 동기화된다.

- **맵**: 36×36 바닥 + 4면 경계 벽 + 중앙 기둥 4개 (규격은 `ArenaConfig` 단일 진실 공급원 — 씬 비주얼과 서버 판정이 같은 값을 쓴다)
- **플레이어**: 접속 수만큼 서버가 동적 스폰. WASD 이동, 마우스 조준, 좌클릭 발사. 탄약 8발(자동 재생), HP 100, 사망 시 2초 후 랜덤 스폰 지점 리스폰
- **총알**: 발사자 색을 따르는 구. 명중 시 25 피해(4발마다 치명타 2배). 피격·사망 FX는 전원이 로컬 재생
- **승부**: 킬 수를 점수로 집계 — HUD 점수 팝업 + 킬피드

파일 구성:

| 파일 | 역할 |
| --- | --- |
| `ArenaConfig.cs` | 시뮬레이션 상수·스폰 지점·기둥 규격 (단일 진실 공급원) |
| `ArenaRole.cs` | 역할 판별 — 인스펙터 강제값 → MPPM 태그 → MPPM 토폴로지 |
| `ArenaBootstrap.cs` | 역할별 UniNet 시작 + 서버의 접속 수 ↔ 플레이어 수 동기화 |
| `ArenaPlayer.cs` | RPC 3종·[Replicated] 조건 4종·RepNotify·서버 권위 시뮬레이션·로컬 입력·비주얼 |
| `ArenaBullet.cs` | 총알 — 동적 스폰/파괴·InitialOnly 시드·선분 스윕 명중 판정 |
| `ArenaFx.cs` | 로컬 FX (네트워크 오브젝트 아님 — MulticastRpc 구현이 전원에서 호출) |
| `ArenaHud.cs` | OnGUI HUD — 내 상태·네임플레이트·킬피드·서버 통계 |
| `ArenaCameraRig.cs` | 탑다운 카메라 — 클라는 내 플레이어 추적, 서버는 전체 고정 |
| `ArenaTwoProcessRunner.cs` | 2-프로세스 배치 검증기 (아래 자동 검증 참조) |

## 실행 가이드 (MPPM — 메인=서버, 가상 플레이어=클라)

1. **Sandbox 프로젝트 열기** — `Assets/Scenes/Arena.unity` 열기 (Build Settings에 등록되어 있어 MPPM 가상 플레이어도 같은 씬으로 시작한다)
2. **MPPM 창 열기** — `Window > Multiplayer > Multiplayer Play Mode`. 이 저장소의 로컬 구성(`Library/VP/SystemData.json` — 커밋 불가 로컬 상태)에는 가상 플레이어가 이미 구성되어 있다. 새 머신/새 클론에서는 창에서 **가상 플레이어 2개를 추가**한다 (태그 없이 두면 자동으로 클라이언트 역할)
3. **메인 에디터 Play** — `[Arena] 부트스트랩 시작 role=Server port=7777` 로그가 뜬다. 메인 에디터는 서버(관전 시점)로 동작한다
4. **가상 플레이어 2개 Play** — MPPM 창의 가상 플레이어 카드별 Play 버튼으로 클론 에디터 2개를 띄우고, 각 클론에서 Play를 누른다. `[Arena] 부트스트랩 시작 role=Client`가 뜨고 서버(메인)에 접속한다. 접속 순서대로 서버가 플레이어를 스폰하고, 각 클라는 `IsOwner`인 내 아바타를 조작한다
5. **조작** — WASD 이동 · 마우스 조준 · 좌클릭 발사

> 툴바 참고 — 에디터 툴바의 인스턴스 런처(MPPM 아이콘)는 MPPM 설치 후 에디터 세션을 **재시작**해야 표시되는 케이스가 있다. 표시되지 않아도 MPPM 창의 카드별 Play 버튼으로 동일하게 실행된다.

역할 판별 규칙(`ArenaRoleResolver`):

| 조건 | 역할 |
| --- | --- |
| 태그 `UniNetHost` | Host (서버+클라 1인 — 빠른 단독 데모) |
| 태그 `UniNetServer` | Server |
| 태그 `UniNetClient` | Client |
| 태그 없음 + 메인 에디터 | Server |
| 태그 없음 + 가상 플레이어 | Client |

부트스트랩 인스펙터에서 `Role`을 강제 지정할 수도 있다. 서버는 접속이 늘면 플레이어를 스폰하고 줄면 잉여 플레이어를 파괴한다. 소유권은 라이브러리의 라운드로빈 최소 정책이 배정하므로, 게임은 "누가 무엇을 소유하는가"를 가정하지 않고 클라이언트는 `IsOwner`로 내 아바타를 찾는다 (이름·색은 아바타 고유값).

## 기능 사용 매트릭스 (P1+P2+P3+P4 → 게임 내 위치)

### P1 — RPC

| 기능 | 게임 내 사용 | 위치 |
| --- | --- | --- |
| ServerRpc | `RpcSubmitMove` / `RpcSubmitAim` / `RpcFire` — 이동·조준·발사 요청 | ArenaPlayer |
| ServerRpc 검증 후크(`_Validate`) | 이동 8방향 클램프·조각 각도 유효성·발사 단위벡터 검증 | ArenaPlayer |
| ClientRpc | `RpcShowKillFeed` — 킬피드 전파 (서버는 미실행) | ArenaPlayer |
| MulticastRpc | `RpcPlayFireFx`·`RpcPlayHitFx`(비신뢰) / `RpcPlayDeathFx`(신뢰) — 전원 로컬 FX | ArenaPlayer |
| 신뢰성 지정(Delivery) | FX 비신뢰(유실 허용)·사망/킬피드 신뢰 — 유실 허용 이벤트와 필수 이벤트 구분 | ArenaPlayer |
| 오브젝트 단위 RPC | 총알·플레이어 각각이 독립 netId 오브젝트로 RPC/리플리케이션 | ArenaBullet·ArenaPlayer |
| RPC 매개변수 신뢰 경계 | 서버가 입력 값 범위를 `_Validate`로 검증(클램프) | ArenaPlayer |

### P2 — 변수 리플리케이션

| 기능 | 게임 내 사용 | 위치 |
| --- | --- | --- |
| [Replicated] 기본 | `_x·_y`(위치) `_hp` `_score` — 전 클라 항상 동기화 | ArenaPlayer·ArenaBullet |
| 조건부 — OwnerOnly | `_ammo` 내 탄약 — 소유 클라만 수신 (검증기가 타인 미수신까지 관찰) | ArenaPlayer |
| 조건부 — SkipOwner | `_aimYaw` 내 조준각 — 나는 로컬 렌더라 남에게만 전송 | ArenaPlayer |
| 조건부 — InitialOnly | `_displayName`·`_colorSeed`(이름·색) / `_seed`(총알 치명타 판정) — 스폰 시 1회만 | ArenaPlayer·ArenaBullet |
| RepNotify | `OnHpChanged`(피격 플래시) `OnScoreChanged`(점수 팝업) `OnAmmoChanged` | ArenaPlayer |
| 델타 전송 | 변경 필드만 전송 — 위치·HP·탄약·점수 전부 (라이브러리 자동) | — |
| 동적 스폰 | 총알 발사·플레이어 접속 — `Instantiate → UniNetManager.Spawn` | ArenaPlayer·ArenaBootstrap |
| 동적 파괴 | 총알 히트/수명 만료 — `UniNetManager.NetworkDestroy` | ArenaBullet |
| 소유권(IsOwner) | 입력 처리 게이트·내 아바타 탐색(카메라·HUD) | ArenaPlayer·ArenaHud·ArenaCameraRig |
| 네트워크 역할(IsServer/IsClient) | 시뮬레이션은 서버만, 클라는 복제 좌표 추종 | ArenaPlayer·ArenaBullet |
| 후발 접속 캐치업 | 중간에 합류한 클라가 기존 플레이어를 전체 상태로 수신 | 라이브러리 (게임은 자동 활용) |

### P3 — 리플리케이션 고급 정책

| 기능 | 게임 내 사용 | 위치 |
| --- | --- | --- |
| 가시성 컬 거리(NetCullDistance) | 총알 궤적 — 반경 24 밖 연결에는 전송하지 않고, 접근하면 관련 전환 틱에 스폰 | ArenaBullet (`NetworkCullDistance`) |
| 뷰어 위치(SetViewerPosition) | 각 연결의 소유 플레이어 좌표를 컬 판정 기준점으로 서버에 주입 | ArenaBootstrap `ManagePlayers` |
| 관련성 훅(IsNetRelevantFor) | 라이브러리 기능 — 아레나는 거리 컬만 사용 (커스텀 훅은 테스트 픽스처 FilteredRelay가 검증) | — |
| 우선순위(NetPriority) | 플레이어 상태를 우선순위 2로 — 대역폭 부족 시 총알(기본 1)보다 먼저 전송 | ArenaPlayer `InitServerState` |
| 전송 주기(NetUpdateFrequency) | 총알 궤적 30Hz 스로틀 — 미도달 변경분은 최신값으로 합쳐짐 | ArenaBullet `Init` |
| 휴면(Dormancy) | 사망 확정 델타 전송 후 리스폰까지 휴면(비교·전송 중단) → 리스폰 시 `FlushNetworkDormancy`로 상태 일괄 전파 | ArenaPlayer `ServerTick`·`Respawn` |
| 채널 우선순위 큐(유형별 예산) | 플레이어 유형 틱 예산 512B — 유형별 대역폭 관리 예시 (P4 전환으로 총알이 제거되어 대상이 플레이어) | ArenaBootstrap (Start) |
| 전역 틱 예산 | 기본 0(무제한) 유지 — 데모는 유형별 예산만 시연 | — |

### P4 — 훅 (시간 동기화·예측·래그 컴펜세이션)

| 기능 | 게임 내 사용 | 위치 |
| --- | --- | --- |
| TimeSync 시간 동기화 | 드라이버 1초 주기 브로드캐스트 — 클라가 서버 시각 역을 유지 | 라이브러리 (UniNetTime — 게임은 자동 활용) |
| 클라 이동 예측 | 소유 클라가 입력을 즉시 적용해 이동, 서버 상태 수신 시 ReconcileAxis로 조정 | ArenaPlayer.Update + ArenaMovement |
| 인터폴레이션 버퍼 | 리모트 플레이어를 Now − 0.12s 시점으로 렌더 (지터 흡수·적용 시점 제어) | ArenaPlayer (SnapshotBuffer) |
| 히트스캔 + 래그 컴펜세이션 | 발사 시 발신자 조준 시각(hitTime) 전송 — 서버가 대상을 리와인드(클램프 [Now−1s, Now])해 선분 판정 | ArenaPlayer.RpcFire |
| 리와인드 히스토리 | 플레이어 위치를 서버 틱마다 기록 (옵트인) | ArenaPlayer (NetworkRewindHistory) |
| 이동 규칙 단일 공급원 | 서버 권위 시뮬레이션과 클라 예측이 ArenaMovement.Step 공유 (예측 오차 상수화 방지) | ArenaMovement |

## 자동 검증

### PlayMode 테스트 (Test Runner)

`UniNet.Tests.ArenaRoundtripTests.호스트_아레나_왕복_스폰_RPC_리플리케이션_킬플로우` — 실제 루프백 RUDP로 이동 ServerRpc → 서버 이동 → 피해·RepNotify → 발사·탄약(OwnerOnly) → 총알 동적 스폰 → 명중·사망·킬 크레딧 → ClientRpc 킬피드 → 총알 파괴 동기화 → 리스폰을 단언한다. 대기는 프레임 수 기반 — 에디터 스로틀링으로 `Time.deltaTime`이 실제 시간과 왜곡되는 환경에서도 강건하다.

### 2-프로세스 검증 (클라 2 접속 — 교차 클라 기능)

호스트(서버+클라A) + 별도 프로세스 클라이언트(B)로 실제 프로세스 간 RUDP 왕복을 검증한다. batchmode에서는 `RuntimeInitializeOnLoadMethod`가 실행되지 않으므로 러너가 생성 등록을 명시 호출한다. **프로젝트 사본 2개가 필요하다** (같은 폴더를 두 Unity 프로세스가 열 수 없음):

```bash
# 1) 사본 2개 생성 (Library 포함 — 재임포트 회피 / Temp·Logs 제외)
robocopy "C:\Projects\DS\UniNet\Sandbox" "C:\Projects\DS\tmp-arena-host" /E /XD Temp Logs obj /MT:16
robocopy "C:\Projects\DS\UniNet\Sandbox" "C:\Projects\DS\tmp-arena-client" /E /XD Temp Logs obj /MT:16
# 2) 사본의 로컬 패키지 참조 경로 수정 (Packages/manifest.json)
#    com.ds.uninet: "file:../../Package" → "file:../../UniNet/Package"
# 3) 실행 — host 먼저, 초기화(수십 초) 후 client
Unity.exe -batchmode -quit -projectPath C:\Projects\DS\tmp-arena-host -executeMethod Arena.ArenaTwoProcessRunner.Run --arena-role=host -logFile host.log
Unity.exe -batchmode -quit -projectPath C:\Projects\DS\tmp-arena-client -executeMethod Arena.ArenaTwoProcessRunner.Run --arena-role=client -logFile client.log
```

성공 판정 마커(`[ARENA-2PROC]`):

- `HOST-TWO-CLIENTS` → `HOST-DONE` — 클라 2 접속·동적 스폰·클라A 탄약 감소·권위 피해·킬피드 전송
- `CLIENT-SPAWNED-2` — 동적 플레이어 2 스폰 전파 + 소유권 분배(라운드로빈) + InitialOnly 이름 전파
- `CLIENT-SKIP-OWNER-AIM OK` — SkipOwner 조각: non-owner(B)만 조각각 수신
- `CLIENT-OWNER-ONLY-AMMO OK` — OwnerOnly 탄약: non-owner(B)는 감소를 수신하지 않음
- `CLIENT-BULLET-SPAWNED` → `CLIENT-DAMAGE-REPLICATED` — 총알 스폰 전파 + 피해 리플리케이션 + RepNotify
- `CLIENT-KILLFEED OK` / `CLIENT-MULTICAST-FX OK` — ClientRpc 킬피드·MulticastRpc FX
- `CLIENT-DONE`

2026-09-17 실행 결과: `HOST-DONE ammoA=7 aimA=45 hpB=0 killfeed=Alpha ▶ Bravo` + `CLIENT-DONE` (전 단계 OK).

## 알려진 한계 (라이브러리 후속 과제)

- **발신자 식별 미전달** — 생성된 `_Implementation`에는 발신 연결 ID가 전달되지 않아, 서버가 "이 RPC를 보낸 연결이 이 오브젝트의 소유자인가"를 검증할 수 없다 (다른 플레이어의 netId로 ServerRpc를 호출하는 위조 가능). 값 범위 검증(`_Validate`)만 게임 차원에서 수행 중. 발신자 컨텍스트 전달은 라이브러리(P1 후속) 과제
- **이동 보간** — 리모트는 SnapshotBuffer 인터폴레이션(0.12s 지연), 소유 클라는 예측+ReconcileAxis(스냅/소프트). 입력 ack 기반 재적용 큐는 미구현 (P4 훅 단순형 계약)
- **위치 리플리케이션 순회** — 서버 틱 기반 전체 오브젝트 순회(라이브러리 현재 구조). 그리드 가시성(P4)으로 판정은 양자화 가속되지만 순회 자체는 유지 — 오브젝트 수가 매우 커지면 버킷 역방향 조회로 확장 가능
- **관전 시 로컬 입력 없음** — 서버 역할 화면은 통계·전체 시점만 표시한다

## 관련 문서

- 라이브러리 기능 상세: [[features/monobehaviour-rpc-replicate]]
- 기능 로드맵: [[roadmap]] · 구현 아키텍처: ADR [[0008-구현-아키텍처]]·[[0009-동적-스폰-조건부-리플리케이션]]
