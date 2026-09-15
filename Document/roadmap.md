# roadmap — 언리얼 Network Framework 패리티 매트릭스

UniNet의 기능 목표(핵심 기능 3, [[overview]])을 언리얼 엔진 Network Framework와 대비해 정리한 로드맵.
"모든 기능"을 단계적 구현 풀셋으로 관리한다. 우선순위는 핵심 기능 나열 순서(1 RPC → 2 Replicate → 3 패리티)를 따른다.

## 단계 정의

| 단계 | 범위 | 상태 |
| --- | --- | --- |
| **P1** | MonoBehaviour 기준 RPC (핵심 기능 1) | **구현됨** (2026-09-14, [[0008-구현-아키텍처]]) |
| **P2** | 변수 자동 Replicate — 기본 (핵심 기능 2) | **기본 구현됨** (델타·RepNotify·소유권·초기 전송 — 스폰/파괴 동기화·조건부·InitialOnly 잔여) |
| **P3** | Replicate 고급 — 가시성·우선순위·최적화 | 미착수 |
| **P4** | 고급 — 예측·래그컴펜세이션·커스텀 드라이버 | 미착수 |

## 매트릭스

매핑 방식 — **DRPC**: 기존 DRPC 기능 재사용 · **MP**: MessageProtocol 직렬화 · **UniNet**: 자체 구현 · **훅**: 엔진 통합 수준 기능의 상응물(유저 코드 확장점)

### RPC·호출 (P1)

| UE 기능 | 설명 | 매핑 | 단계 |
| --- | --- | --- | --- |
| Server RPC | 클라이언트→서버 호출 | DRPC (IServerProcedureDeclarations) | P1 |
| Client RPC | 서버→특정 클라이언트 호출 | DRPC (양방향 계약) | P1 |
| NetMulticast | 접속 전체 브로드캐스트 | UniNet (대상 선별 + DRPC) | P1 |
| Reliable/Unreliable 지정 | 호출별 신뢰성 | DRPC (전달 모드 5종) | P1 |
| 오브젝트 단위 RPC | 월드 오브젝트 대상 호출 | UniNet (네트워크 ID 관리) | P1 |

### 변수 리플리케이션 — 기본 (P2)

| UE 기능 | 설명 | 매핑 | 단계 |
| --- | --- | --- | --- |
| Property Replication | 멤버 변수 자동 동기화 | UniNet + MP | P2 |
| 조건부 리플리케이션 | COND_OwnerOnly 등 조건 | UniNet | P2 |
| RepNotify | 값 변경 시 클라이언트 콜백 | UniNet | P2 |
| 델타 직렬화 | 변경분만 전송 | UniNet + MP | P2 |
| 스폰/파괴 동기화 | 동적 오브젝트 생성·제거 반영 | UniNet + DRPC | P2 |
| 소유권(Ownership) | 연결-오브젝트 소유 관계 | UniNet | P2 |
| 네트워크 역할 | Authority/AutonomousProxy/SimulatedProxy 개념 | UniNet | P2 |
| 초기 전송(InitialOnly) | 첫 동기화 시 전체 전송 | UniNet | P2 |

### 리플리케이션 — 고급 (P3)

| UE 기능 | 설명 | 매핑 | 단계 |
| --- | --- | --- | --- |
| Relevancy/가시성 | 거리·조건 기반 대상 선별 (NetCullDistance 등) | UniNet | P3 |
| Priority/스타베이션 방지 | 대역폭 할당 우선순위 | UniNet | P3 |
| Dormancy | 유휴 오브젝트 리플리케이션 중단 | UniNet | P3 |
| NetUpdateFrequency | 오브젝트별 전송 주기 | UniNet | P3 |
| 커스텀 NetSerialize | QuantizedVector 등 사용자 직렬화 | MP | P3 |
| FastArray 직렬화 | 배열 델타 동기화 | UniNet + MP | P3 |
| RepGraph 스타일 커스터마이징 | 그리드 공간 분할 가시성 | UniNet | P4 |

### 연결·보안 (P1~P3 분산)

| UE 기능 | 설명 | 매핑 | 단계 |
| --- | --- | --- | --- |
| 연결 수명주기·핸드셰이크 | 접속/종료·핸드셰이크 (재접속·하트비트는 앱 계층 구현) | Communication + DRPC + UniNet | P1 |
| 전송 신뢰성 계층 | RUDP, 5가지 전달 모드 | Communication + DRPC | P1 |
| 패킷 암호화 | DTLS 1.2 | Communication | P1 |
| 채널 우선순위 큐 | 유형별 대역폭 관리 | UniNet (리플리케이션 드라이버 레벨) | P3 |

### 엔진 통합 수준 — 상응 훅 제공 (P4)

| UE 기능 | 설명 | UniNet 방식 | 단계 |
| --- | --- | --- | --- |
| 클라이언트 예측·서버 리와인드 | 엔진 이동 예측과 롤백 | 훅 — 인터폴레이션 버퍼·리와인드 스냅샷 API | P4 |
| 래그 컴펜세이션 | 히트스캔 히스토리 판정 | 훅 — 위치 히스토리 질의 API | P4 |
| 틱 스케줄링 통합 | 엔진 루프와 네트워크 업데이트 동기 | 훅 — 전송 주기·적용 시점 제어 | P2~P4 |

## 관리 규칙

- 구현이 착수되면 단계/항목에 상태를 표시하고 [[changelog]]에 기록한다
- 매트릭스 항목 추가·제외는 ADR로 결정을 남긴다
- UE 기능 명칭은 이해를 위한 참조이며, UniNet API 명칭은 Unity 관례를 따라 별도 정한다
