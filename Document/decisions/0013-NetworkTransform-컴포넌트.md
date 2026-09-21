# 0013. NetworkTransform 컴포넌트 — 이동 예측의 라이브러리 승격 (조합 채택)

- **상태**: 승인됨
- **날짜**: 2026-09-20
- **결정자**: DS + pi

## 배경 (Context)

- P4 훅(ADR-0012)이 시계·버퍼·히스토리 **프리미티브**만 라이브러리화한 결과, 예측 루프·조정(Reconcile)·리모트 보간 렌더의 **조립 코드가 게임 예제(ArenaPlayer)에 남았다**. 사용자 지적: "이동 예측 기능을 UniNet에서 컴포넌트 형식으로 지원해야지 왜 Arena 예제 코드에서 구현해?" — 예측 루프·조정·보간은 게임마다 반복되는 네트워킹 공통 관심사이며, UE CharacterMovementComponent·Netcode NetworkTransform이 라이브러리 차원에서 제공하는 이유와 같다.
- 처음 검토된 "NetworkTransform 추상 컴포넌트(상속)" 형태는 구현 불가능함이 확인됐다 — 제너레이터가 **리프 타입 기준**으로 멤버를 스캔(`GetMembers()`는 선언 멤버만 반환)하므로, 기반 클래스의 `[Replicated]` 필드가 파생 타입의 리플리케이션 핸들에 포함되지 않는다.

## 결정 (Decision)

1. **구체 컴포넌트 + 규칙 주입 (조합)** — `NetworkTransform : NetworkBehaviour` 봉인 컴포넌트를 같은 오브젝트의 **별도 슬롯(SubId)**으로 결합한다 (ADR-0010 멀티컴포넌트). 상속 대신:
   - `MovementStep` 델리게이트 (`MovementRule`) — 게임 이동 규칙 주입 (서버 권위·예측이 공유)
   - `SimulationEnabled` — 시뮬레이션 게이트 (사망·이동 불가 상태; 서버·예측 동시 정지)
   - 게임은 `SubmitMove`로 입력만 제공한다 (예측 즉시 반영 + ServerRpc 전송 내장)
2. **컴포넌트 책임**: 위치 복제(`[Replicated] px/py/pz` + RepNotify)·소유 클라 예측(즉시 적용)·조정(축 단위 스냅/소프트 — 임계값·비율은 컴포넌트 필드로 조정 가능)·리모트 인터폴레이션(SnapshotBuffer + InterpolationDelay)·Awake에서 스폰 트랜스폽 흡수.
3. **게임 측 남는 것**: 이동 규칙 구현(ArenaMovement.Step을 어댑터로 주입), 입력 캡처(키보드·마우스 → SubmitMove), 게임 이벤트(사망 시 SimulationEnabled=false, 리스폰 시 SetNetworkPosition+true).
4. **제너레이터 겹리 (0.1.2)** — NetworkTransform이 UniNet.Unity 어셈블리의 첫 NetworkBehaviour 파생이 되면서 생성 등록 클래스가 게임 어셈블리 것과 이름 충돌(CS0433). 라이브러리 어셈블리(UniNet.Core/UniNet.Unity)의 생성 코드를 `UniNet.Generated.Hosting` 네임스페이스로 겹리하고 RuntimeInitializeOnLoadMethod 자체 발화 등록으로 전환했다.
5. **테스트 안정화** — 플레이 모드 종료 후에도 RUDP 리스너 소켓이 에디터 프로세스에 잔존하는 환경 특성 때문에 PlayMode 테스트 포트를 실행별 랜덤(파일별 오프셋 블록)으로 전환했다.

## 결과 (Consequences)

- **긍정**: 예측·조정·보간이 게임 코드에서 소멸 — ArenaPlayer는 이동 규칙 주입 1줄로 예측을 얻는다. 이동 규칙 단일 공급원(StepNet 어댑터)으로 서버·예측 규칙 불일치가 구조적으로 불가능하다. 제너레이터 무변경.
- **부담 / 리스크**: 위치만 지원(회전·스케일 미포함 — 필요 시 확장). MovementRule 미주입 시 위치 복제만 동작(정지). 멀티 컴포넌트 오브젝트의 클라 스폰 슬롯 불일치가 후속 발견 → ADR-0014(스폰 서브 자동 복원)로 해소.
- **폐기 방법**: 컴포넌트 계약 변경 시 새 ADR로 대체.

## 검증 증거 (2026-09-20)

- EditMode 48/48 (NetworkTransformComponentTests 3종 신규 — ReconcileAxis 스냅/소프트/무변화)
- PlayMode 12/12 (NetworkTransformPlayTests 2종 신규 — SubmitMove 기반 RPC 루프백 이동·SetNetworkPosition 위치 복제)
- 아레나 재전환 후 기존 ArenaRoundtrip·2-프로세스 흐름 유지 확인

## 알려진 한계·후속 과제

- 위치 복제만 지원(회전은 게임 필드로 처리) — 필요 시 축 확장
- 예측 조정은 스냅/소프트 단순형 — 입력 ack 기반 재적용 큐 미구현
- 멀티 컴포넌트 스폰 슬롯 불일치 → ADR-0014로 해소
