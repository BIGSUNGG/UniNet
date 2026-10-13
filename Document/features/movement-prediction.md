# movement-prediction (이동 예측·상태 기반 CSP&R)

- **상태**: 구현됨
- **최초 작성**: 2026-10-13
- **마지막 갱신**: 2026-10-13

## 개요

`NetworkTransform` 컴포넌트 + `MoveSimulation<TInput,TState>` 엔진 — 위치 복제, 소유 클라 예측(롤백+재시뮬), 리모트 인터폴레이션을 제공한다. 게임은 **자신의 입력·상태 구조체**와 순수 이동 규칙을 주입해 점프(중력·수직 속도)·대시/구르기(쿨다운·시간 창)·텔레포트(상태 리셋) 같은 상태 기반 이동을 정확한 CSP&R(Client-Side Prediction & Reconciliation)로 구현한다.

## 요구사항 / 목표

- 상태(속도·쿨다운 등)를 필요로 하는 이동 규칙도 롤백 재시뮬에서 정확히 수렴
- 서버 권위 유지 — 입력은 서버가 채택·검증, 위치는 서버가 복제
- 게임 코드는 구조체 2개 + 정적 함수 5개만 작성 (직렬화·시퀀싱·롤백은 프레임워크)
- 기반 스택(DRPC·MessageProtocol·Communication)·UniNet 제너레이터 무변경

## 설계

- **`MoveSimulation<TInput, TState>`** (`Package/Runtime/UniNet.Core/Hosting/MoveSimulation.cs`) — 순수 C# 엔진. 고정 스텝 시뮬레이션, 서버 입력 큐(용량 8), 소유 클라 히스토리 링(128 엔트리, seq+입력값+예측 위치), 롤백(서버 상태 blob 복원 → 미승인 입력 재생). `INetworkMoveSimulation` 인터페이스로 컴포넌트에 주입.
- **`NetworkTransform`** (`Package/Runtime/UniNet.Unity/NetworkTransform.cs`) — 프레임 어큐뮬레이터로 고정 스텝 구동(서버=권위, 소유 클라=예측+`RpcSubmitMove` 스트림), `(ackSeq, _ackState blob)` OwnerOnly 복제(필드 선언 순서 = 적용 순서), 롤백 트리거(`RollbackThresholdSq`), 텔레포트 재앵커, 리모트 `SnapshotBuffer` 인터폴레이션. `Simulation == null`이면 위치 복제 전용.
- **`UniNetBlobEnvelope`** (`Package/Runtime/UniNet.Unity/UniNetBlobEnvelope.cs`) — `[Message]` 와이어 봉투(byte[] Data). RPC 입력 파라미터·상태 복제 양쪽에 사용. **UniNet.Unity 소속** — MP 소스젠이 애널라이저 폴더 기준 하위 어셈블리에만 적용되므로.
- 코덱 계약은 [[features/custom-netserialize|custom-netserialize]]의 정적 Write/Read와 같은 형태.
- 관련 ADR: [[0013-NetworkTransform-컴포넌트]](컴포넌트화) · [[0037-예측-full-CSPnR-내장]](무상태 롤백) · [[0038-상태-기반-이동-예측-일반화]](현재 계약)

## 동작 상세

```csharp
// 게임 — 구조체 + 5개 정적 함수
public struct MoveInput  { public sbyte Dx, Dz; public bool Jump, Dash; }
public struct MoveState  { public float Vy, DashTimer, DashCooldown; public sbyte DashDx, DashDz; }

public static void Step(ref MoveState st, ref float x, ref float y, ref float z, in MoveInput i, float dt)
{
    // 순수 함수: (상태, 위치, 입력, dt)만 사용 — Time.*·난수 금지 (결정론 계약)
    // 점프 엣지·대시 트리거도 상태 기반으로 여기서 판정
}

public static void WriteInput(ref MessageBufferWriter w, in MoveInput i) { ... }
public static MoveInput ReadInput(ref MessageBufferReader r) { ... }
public static void WriteState(ref MessageBufferWriter w, in MoveState s) { ... }
public static MoveState ReadState(ref MessageBufferReader r) { ... }

// 컴포넌트 주입 + 매 프레임 입력
_nt.Simulation = new MoveSimulation<MoveInput, MoveState>(
    Step, WriteInput, ReadInput, WriteState, ReadState, stepsPerSecond: 60f);
_sim = (MoveSimulation<MoveInput, MoveState>)_nt.Simulation;
_sim.SetInput(new MoveInput { Dx = dx, Dz = dz, Jump = Input.GetKey(KeyCode.Space) });
```

- **고정 스텝** — 양단 같은 dt(기본 60Hz)로 스텝 → 재시뮬 결정론. 소유 클라 렌더는 직전 두 예측 스텝 사이 보간.
- **입력 스트림** — 소유 클라가 매 스텝 `(seq, blob)` 전송(상시 — 대기 중에도 중력 진행 때문). 서버는 스텝마다 큐에서 1개 채택, 비면 최신 외삽. 스테일/재생·오버사이즈·불량 blob 기각(신뢰 경계).
- **롤백** — ack 수신 시 예측 위치 vs 서버 위치 비교, 임계 초과면 서버 상태 blob 복원(속도·쿨다운 포함) 후 미승인 입력 재생. ack가 히스토리 창(~2.1s) 밖이면 하드 스냅.
- **텔레포트/리스폰** — 서버 `SetNetworkPosition`이 상태 초기화+재앵커. 대기 입력이 있으면 롤백 경로로 수렴.
- **리모트** — 기존과 동일: 위치 델타 + `Now − InterpolationDelay` 렌더.

## 테스트 / 검증

- EditMode — `MoveSimulationTests` 7종(와이어 왕복·**점프 궤적 롤백 상태 복원 — 무상태 재생 반례 단언**·대시 쿨다운 복원·히스토리 폐기·텔레포트 초기화·서버 큐 규율·불량 페이로드 기각) + `NetworkTransformComponentTests` 4종
- PlayMode — Arena `호스트_점프_대시_텔레포트_상태_시뮬레이션`(점프 완주·대시 버스트·쿨다운 억제·텔레포트) + `NetworkTransformPlayTests` 3종
- 실제 클라 예측 왕복(호스트가 아닌 별도 클라)은 2-프로세스 검증기로 수동 확인

## 변경 이력

- 2026-10-13 — 최초 작성 (ADR-0038 — ADR-0013 컴포넌트·ADR-0037 무상태 롤백을 상태 기반 일반화로 계승·문서 갭 해소)
