# movement-prediction (이동 예측·상태 기반 CSP&R)

- **상태**: 구현됨
- **최초 작성**: 2026-10-13
- **마지막 갱신**: 2026-10-20 (소유권 전이 스트림 리셋·epoch — ADR-0042)

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
- 관련 ADR: [[0013-NetworkTransform-컴포넌트]](컴포넌트화) · [[0037-예측-full-CSPnR-내장]](무상태 롤백) · [[0038-상태-기반-이동-예측-일반화]](현재 계약) · [[0042-이동-소유권-전이-스트림-리셋-에포크]](소유권 전이·epoch)

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
- **소유권 전이 (ADR-0042)** — 임기→임기 이전(`SetOwner` 직접 이전·해제)이면: 서버는 `NetworkTransform`의 이동 epoch(`[Replicated(OwnerOnly)] uint _moveEpoch`)을 증가시키고 `MoveSimulation.ResetOwnershipStream()`과 짝 실행한다(큐·히스토리·`_lastSeenSeq`·`LastAdoptedSeq` 클리어 — **권위 TState·최신 입력 보존**, `OnTeleport`와 계약이 다르다). **0→비0 입양(초기 스폰·해제 후 재배정)은 베이스라인 — 무범프·무리셋**: 초기 스폰은 스트림이 처녀상태, 해제 후 입양은 해제 시점 범프가 이미 경계를 막았고 0 소유 기간엔 소유권 게이트가 발신자를 차단한다(타이밍 독립 규칙). 새 소유자 클라는 `NetworkClient.OwnershipChanged` 이벤트(디스패치 스레드 — `QueueOnMain` 마셜 필수)와 OwnerOnly epoch 수신 양경로에서 자기 세션을 재바인딩한다(`_clientSeq=0`·accumulator·히스토리 클리어·권위 좌표 재앵커 — 예측 상태는 첫 ack 롤백으로 수렴). 이동 입력 봉투 `UniNetBlobEnvelope.Data`는 `[uint epoch][int length][byte[] 입력]` 레이아웃 — 서버는 epoch 불일치 입력을 조용히 기각(`NetworkTransform.MoveEpochMismatches` 카운터 관측)해 이전 임기의 전송 중 패킷 재오염(probe-2)을 차단한다. 호스트(권위 인스턴스)는 이벤트 재설정을 스킵한다. 한 프레임 안의 A→B→A는 서버 관측상 무변화라 범프가 없다 — 새 스트림이 구 최대 시퀀스를 넘으면 자가치유(ADR-0042 한계 문서 참조).
- **사망 게이트 시 미승인 예측 히스토리 소거 (ADR-0049)** — `SimulationEnabled`를 `false`로 만드는 순간(사망·비활성 진입 에지) `NetworkTransform`이 `INetworkMoveSimulation.DropPendingHistory()`를 1회 호출한다. `MoveSimulation` 구현은 큐+히스토리 링만 비우고 **권위 TState·최신 입력은 보존**한다(인터페이스 DIM 기본값은 `OnTeleport()` 전체 소거 — 사용자 구현체가 선택). 효과: 사망 중 도착 ack는 하드 스냅으로 권위 좌표 자기정렬, 재활성화 시 pending==0이라 기존 텔레포트 재앵커가 리스폰 좌표를 채택(사망 전 예측 좌표 렌더·죽은 입력 재생 소멸). 에지 트리거라 게이트 유지 중엔 재소거하지 않는다. **퇴장 에지(재활성화)에서는 재앵커가 스텝보다 먼저 선실행**된다 — 스텝이 유입되는 프레임에서도 죽은 시대 좌표 오염 없이 리스폰 좌표에서 예측이 재개된다(r17 ISSUE-1 — RED job `0bbdb4dd`). 재앵커 로직 자체(조건·스냅)·롤백·ADR-0042 임기 계약은 무변경 — MOV-002.
- **리모트** — 위치 델타 + 인터폴레이션. 유효 렌더 지연은 도착 간격 EMA 기반 적응(3×갭, 하한 0.03s, 상한=`InterpolationDelay`) — 밀집 스트림에서 자동 수축해 체감 지연을 줄인다. 축별 점프가 `max(SnapThreshold, 3×변위EMA)`를 넘으면(텔레포트) 해당 축 버퍼를 클리어해 불연속을 스냅으로 렌더한다(보간 통과 없음·고속 병합 샘플 비오판 방지).

## 테스트 / 검증

- EditMode — `MoveSimulationTests` 7종(와이어 왕복·**점프 궤적 롤백 상태 복원 — 무상태 재생 반례 단언**·대시 쿨다운 복원·히스토리 폐기·텔레포트 초기화·서버 큐 규율·불량 페이로드 기각) + `NetworkTransformComponentTests` 4종
- PlayMode — Arena `호스트_점프_대시_텔레포트_상태_시뮬레이션`(점프 완주·대시 버스트·쿨다운 억제·텔레포트) + `NetworkTransformPlayTests` 3종
- EditMode — `NetworkTransformRemoteRenderTests` 6종(리모트 텔레포트 스냅·적응 지연 수축/최대치 유지·복제전용 리모트 보간·고속 병합 샘플 비오판·수직 전용 지연 수축)
- EditMode (ADR-0042) — `MoveSimulationTests` 3종(리셋 후 새 owner seq1 채택·`OnTeleport` 리셋 대용 불가(probe-1)·권위 TState 보존) + `NetworkTransformComponentTests` 5종(probe-2 재현 — 이전 임기 지연 seq+epoch 불일치 기각 후 새 스트림 채택·소유자 값 불변 시 범프 없음·실제 생성 OwnerUpdate 수신기(wire-2) 경유 재바인딩·호스트 이중 리셋 방지·다중 객체 무간섭) + `OwnerBaselineTests` 1종(ApplyOwner 이벤트 값변경 시만 발화·미등록 netId 안전)
- PlayMode (ADR-0042) — `MovementOwnershipPlayTests` 실RUDP 소유권 사이클(해제→재배정 입양 재획득→구 epoch 기각→새 스트림 채택·ack 재개)
- 실제 클라 예측 왕복(호스트가 아닌 별도 클라)은 2-프로세스 검증기로 수동 확인

## 변경 이력

- 2026-10-21 — MOV-002(ADR-0049) — 사망 게이트 진입 에지에서 미승인 예측 히스토리 소거(0.13.0)

- 2026-10-13 — 최초 작성 (ADR-0038 — ADR-0013 컴포넌트·ADR-0037 무상태 롤백을 상태 기반 일반화로 계승·문서 갭 해소)
- 2026-10-13 — 리모트 렌더링 개선: 텔레포트 불연속 스냅(보간 통과 제거)·적응 인터폴레이션 지연·복제전용 리모트 보간 수리
- 2026-10-20 — 소유권 전이 스트림 리셋·epoch (ADR-0042 — MOV-001): `NetworkClient.OwnershipChanged` 이벤트·`MoveSimulation.ResetOwnershipStream`(TState 보존)·이동 epoch([Replicated(OwnerOnly)] uint·봉투 접두·불일치 조용 기각+카운터)·클라 양경로 재바인딩·호스트 이중 리셋 가드
