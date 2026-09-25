# research/network-framework-comparison — 네트워크 프레임워크 비교

> UniNet과 주요 게임 네트워킹 프레임워크의 기능·특징 비교. 조사 기준: 2026-09 (공식 문서 기준).
> UniNet 기준점: [[overview]] · 구현 범위: [[roadmap]] (P1~P4 완료)
>
> **2026-09-24 갱신**: 직렬화 2종은 MP 상류 수정 없이 UniNet 단독 구현으로 전환됨 (ADR [[0020-직렬화-배열-델타-유니넷-단독-구현]]). 커스텀 직렬화/양자화는 **구현 완료**([[features/custom-netserialize|custom-netserialize]]), 배열 델타는 UniNet 단독 설계 기반 구현 진행 중([[features/fastarray-delta|fastarray-delta]]). 본문의 "🔶U 상류 대기" 표기는 조사 시점(2026-09-22) 기준 서술이며 현재 상태는 이 노트가 우선한다.

## 범례

- ✅ 내장 / 공식 지원
- 🔶 부분 지원 (훅·옵트인·조건부·서드파티 필요 등)
- ❌ 없음 (공식 문서 기준 미제공)
- 🔶U UniNet 단독 불가 — MessageProtocol 상류 수정 대기 ([[upstream-blockers]])

## 비교 대상 요약

| 프레임워크 | 한 줄 정의 | UniNet과의 관계 |
| --- | --- | --- |
| **UniNet** (이 프로젝트) | MonoBehaviour 기준 RPC·변수 리플리케이션, UE NF 패리티 목표, Unity 서버 빌드 전용서버, 셀프호스팅 | — |
| **UE Network Framework** | 언리얼 엔진 내장 클라이언트-서버 복제 (Actor replication, RepGraph, Iris) | 기능 패리티 목표(참조선) — C++/엔진 결속형 |
| **Photon Fusion 2** | 유료 클로즈드 SDK. Server/Host/Shared 3토폴로지, 틱 시뮬, CSP&R·래그컴펜 내장, Photon Cloud 또는 자체 호스팅 | 가장 가까운 상용 경쟁자 |
| **Unity NGO 2.x** | 1st-party 무료 GameObject 넷코드. 서버 권위 NetworkVariable, 분산 권위(Unity 6), full 예측 없음(anticipation만) | 무료 직접 경쟁자 |
| **Netcode for Entities (DOTS)** | 1st-party 무료 ECS 넷코드. 고스트 스냅샷·full 예측·대규모 스케일, 진입장벽 높음 | 다른 패러다임(DOTS)의 상위 스케일 |
| **FishNet** | 무료(Pro 유료) GameObject 넷코드. Prediction v2·대역폭 최적화 강점 | 무료 직접 경쟁자 |
| **Mirror** | MIT 무료 OSS. 서버 권위 SyncVar/Command/ClientRpc, 예측은 실험적 | 무료 직접 경쟁자(보수형) |
| **Nakama / Colyseus / DarkRift 2** | 서버 로직 우선(authoritative backend/room) 프레임워크 — 상태 리플리케이션은 얕음 | 다른 계층(백엔드), 보완재 |
| **LiteNetLib / Riptide / ENet** | 전송 계층 라이브러리만 제공 | 다른 계층(트랜스포트) — UniNet은 자체 RUDP+DTLS 보유 |

## 표 A — 리플리케이션·RPC 핵심 기능

| 기능 | UniNet | UE NF | Fusion 2 | NGO 2.x | N4E | FishNet | Mirror |
| --- | :-: | :-: | :-: | :-: | :-: | :-: | :-: |
| RPC (서버/클라/멀티캐스트) | ✅ 3종 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ (+TargetRpc) |
| 호출별 신뢰/비신뢰 지정 | ✅ 전달모드 5종(DRPC) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| 오브젝트 단위 RPC | ✅ netId+SubId(다중 컴포넌트) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| 변수 자동 리플리케이션 | ✅ `[Replicated]` | ✅ `UPROPERTY(Replicated)` | ✅ `[Networked]` | ✅ `NetworkVariable` | ✅ `[GhostField]` | ✅ `[Sync]` | ✅ `SyncVar` |
| 변경 콜백 (RepNotify류) | ✅ Notify(이전값 전달) | ✅ OnRep | ✅ OnChanged | ✅ OnValueChanged | ✅ | ✅ | ✅ hook |
| 조건부 리플리케이션 | ✅ OwnerOnly·SkipOwner·InitialOnly 플래그 | ✅ COND_* 다종 | 🔶 권한자·AOI 중심 | 🔶 쓰기 권한 중심 | 🔶 쓰기 권한·필터 | 🔶 | 🔶 OnSerialize 오버라이드 |
| 델타 전송 | ✅ 필드 델타 | ✅ (+양자화) | ✅ | 🔶 변수는 변경 시 전체(NetworkList만 연산 델타) | 🔶 양자화 기반 | 🔶 옵션 | ✅ dirty bit |
| 동적 스폰/파괴 + 후발 캐치업 | ✅ NetworkInstantiate/NetworkDestroy | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| 소유권·네트워크 역할 | ✅ (라운드로빈 최소 정책) | ✅ | ✅ Input/State Authority | ✅ | ✅ | ✅ | ✅ |
| 가시성/관심 관리 | ✅ 컬거리+커스텀 훅+그리드 | ✅ NetCullDistance+RepGraph/Iris | ✅ AOI 그리드+스케줄링 | 🔶 CheckObjectVisibility 델리게이트 | 🔶 필터 시스템 | ✅ Observer 조건(Distance 등) | ✅ 거리/그리드/씬 컴포넌트 |
| 우선순위 + 기아 보정 | ✅ UE GetNetPriority 공식 채택 | ✅ | 🔶 2.1 전송 우선순위 | ❌ | ✅ 우선순위 슬롯 | ❌ | ❌ |
| 휴면 (Dormancy) | ✅ 2상태+Flush | ✅ 다단계 | ❌ AOI·전송 제어로 대체 | ❌ | ❌ | ❌ | ❌ |
| 오브젝트별 전송 주기 | ✅ NetworkUpdateFrequencyHz | ✅ NetUpdateFrequency | ✅ | 🔶 틱레이트 전역 | 🔶 틱 기반 | 🔶 기본 전송률 | 🔶 syncInterval |
| 채널 대역폭 예산 | ✅ 유형별 예산 | 🔶 RepGraph 내부 | ❌ (문서 기준 미제공) | ❌ | ❌ | ❌ | ❌ |
| 커스텀 직렬화/양자화 | 🔶U 상류 대기 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| 배열/컬렉션 델타 | 🔶U FastArray류 상류 대기 | ✅ FFastArraySerializer | ✅ | ✅ NetworkList | ✅ | ✅ | ✅ SyncList류 |

## 표 B — 예측·래그컴펜세이션·시간

| 기능 | UniNet | UE NF | Fusion 2 | NGO 2.x | N4E | FishNet | Mirror |
| --- | :-: | :-: | :-: | :-: | :-: | :-: | :-: |
| Full CSP&R (예측+롤백+재시뮬) | 🔶 훅+NetworkTransform (사용자 조립 — 아레나 예측 실증) | ✅ CharacterMovement 내장 | ✅ Server/Host 모드 | ❌ anticipation만 (롤백-재생 없음) | ✅ | ✅ Prediction v2 | 🔶 실험적 (프로덕션 비권장 공식 문서) |
| 래그컴펜세이션 (서버 리와인드) | 🔶 훅 — PositionHistory+GetHistoryPosition (아레나 히트스캔 실증) | 🔶 샘플 수준 (Lyra SSR 등 서드파티 보편) | ✅ Server/Host 전용 | ❌ | ❌ 직접 구현 | ✅ | ✅ LagCompensator |
| 시간 동기화 | ✅ UniNetTime (서버 권위 단조 시계) | ✅ | ✅ | ✅ NetworkTime | ✅ | ✅ | ✅ |
| 인터폴레이션 버퍼 | ✅ SnapshotBuffer | ✅ | ✅ | ✅ NetworkTransform | ✅ | ✅ | ✅ Snapshot Interpolation |

## 표 C — 배포·운영·생태계

| 항목 | UniNet | UE NF | Fusion 2 | NGO 2.x | Mirror/FishNet | Nakama/Colyseus/DarkRift |
| --- | :-: | :-: | :-: | :-: | :-: | :-: |
| 서버 모델 | Unity 서버 빌드 (전용/호스트) | UE 전용서버 | Server/Host/Shared | 서버/호스트/분산 권위 | 서버/호스트 | 서버가 주체 (모듈/룸) |
| 자체 호스팅 | ✅ 기본 전제 | ✅ | ✅ (또는 Photon Cloud) | ✅ (Relay 선택) | ✅ | ✅ |
| 클라우드 릴레이/호스팅 | ❌ | ❌ | ✅ Photon Cloud | ✅ Unity Relay | ❌ | △ (Nakama Heroic Cloud 등) |
| NAT 통과 | ❌ 직접 구현 필요 | 🔶 플랫폼별 | ✅ | ✅ (Relay) | 🔶 플러그인 | 서버 직접 접속 전제 |
| 씬 관리 | ❌ 수동 스폰 | ✅ Seamless travel | 🔶 | ✅ 내장 | ✅ 내장 | — |
| 매치메이킹·계정·백엔드 | ❌ | 🔶 플랫폼별 | 🔶 로비 | 🔶 Services 별도 | ❌ | ✅ 풀백엔드 (Nakama) |
| 재접속·하트비트 | ❌ 앱 계층 (roadmap 명시) | 🔶 | 🔶 | 🔶 | 🔶 | 🔶 |
| 전송 암호화 | ✅ DTLS 1.2 기본 | 🔶 인터페이스 제공·직접 구현 | ✅ DTLS 옵션 | 🔶 Relay 경유 시 | ❌ (wss 웹소켓만) | 서버 구성별 |
| 라이선스/비용 | 자체 소유 (기반 패키지 고정) | 엔진 로열티 | 유료 CCU 과금 | 무료 SDK+서비스 과금 | 무료 (MIT/무료+Pro) | OSS (호스팅 유료 옵션) |
| 소스 접근 | ✅ 라이브러리 전체 | ✅ 엔진 OSS | ❌ 클라 SDK 클로즈드 | ✅ | ✅ | ✅ |
| 문서·커뮤니티·실전 사례 | ❌ 신생 (자체 문서만) | ✅ 방대 | ✅ 방대 | ✅ 방대 | ✅ | ✅ |

## UniNet에만 있는 것

1. **UE급 리플리케이션 정책 세트가 무료·셀프호스팅·MonoBehaviour GameObject 모델로 한 묶음 기본 탑재** — 거리 컬+커스텀 훅+그리드 가시성, 우선순위+기아 보정(UE 공식 채택), 휴면+Flush, 오브젝트별 전송 주기, 유형별 채널 예산. Mirror·NGO는 거리 가시성 수준, FishNet은 관찰자 조건만, Fusion은 유료·클로즈드 SDK, UE는 엔진 전체를 요구.
2. **클라·서버 동일 API + 서버도 Unity 서버 빌드 하나의 스택** — 전용서버에서 MonoBehaviour 그대로 실행. 자체 호스팅이 기본 전제라 CCU 과금·벤더 락인 없음.
3. **보안 기본값 설계** — DTLS 1.2 기본 암호화, ServerRpc 발신자-소유자 자동 강제(netId 위조 거부), 검증 훅 옵트인+컴파일 진단. 타 프레임워크는 대부분 "사용자가 직접 검증하라"는 문서 수준.
4. **DRPC 인터페이스 계약 + 로슬린 소스젠의 컴파일 타임 검증** — UNINET0xx 진단으로 계약 오류를 빌드에서 차단 (Mirror는 IL weaving, NGO는 런타임 속성+제한적 소스젠).
5. **UE NF 패리티를 공개 매트릭스로 관리하며 실제 P1~P4 완주** — 패리티 목표 자체를 명시적으로 갖는 유일한 Unity 라이브러리.

## UniNet에 없는 것 (타사에는 있는 것)

1. **프레임워크 내장 Full CSP&R** — 예측·롤백·재시뮬은 훅(SnapshotBuffer·UniNetTime)+NetworkTransform 제공, 게임이 조립. Fusion/N4E/FishNet은 내장. 최대 체감 차.
2. **커스텀 NetSerialize/양자화 + 배열 델타(FastArray류)** — roadmap 유일 미완(상류 MP 수정 대기). 실전에서 배열 동기화 필수라 가장 시급한 열위.
3. **릴레이·NAT 통과·매치메이킹·계정 등 인프라 전반** — Photon Cloud·Unity Services 영역. 셀프호스팅 전제로 의도된 공백이나, 운영 부담은 사용자 몫.
4. **씬 관리·호스트 마이그레이션·재접속·하트비트** — NGO/Mirror/Fusion에는 있음. 전용서버 전제상 일부(호스트 마이그레이션)는 불필요할 수 있으나 재접속·하트비트는 문서화된 앱 계층 과제.
5. **플랫폼 실증** — WebGL/모바일 지원 미검증 (RUDP+DTLS 기반 특성상 웹은 불확실).
6. **생태계** — 커뮤니티·실전 배포 사례·독립 벤치마크 0. 검증 규모: EditMode 60+PlayMode 15+2-프로세스 RUDP.
7. **DOTS급 대규모 스케일** — 수천 엔티티는 N4E 영역 (UniNet은 GameObject 틱 구조).

## 종합 장단점

**강점**

- 기능 밀도: P3 정책 세트(가시성·우선순위·휴면·주기·채널 예산)는 무료 GameObject 프레임워크 중 유일한 완전 세트
- 보안 기본값(암호화·소유자 강제·계약 검증)이 출고 상태로 켜져 있음
- 비용·종속성 통제: 자체 소유 소스, 패키지 고정, 과금 모델 없음
- 단일 Unity 스택: 코드·에셋·디버깅 환경 공유, 서버가 Unity 서버 빌드

**약점**

- 성숗도·생태계 0: 실전 사례·커뮤니티·벤치마크 부재, 신뢰는 자체 테스트뿐
- 예측이 조립식: full CSP&R이 필요하면 사용자 작업량이 Fusion/FishNet 대비 큼
- 상류 의존 2종: 커스텀 직렬화·배열 델타가 MessageProtocol 수정 전까지 막힘
- 주변 인프라 직접 구축: 릴레이·매치메이킹·재접속 등 운영 기능 공백

## 시사점 (roadmap 반영 후보)

1. **FastArray/커스텀 NetSerialize 상류 해소 최우선** — 배열 동기화는 실전 필수 기능이며 현재 유일한 roadmap 미완.
2. **풀 CSP&R 라이브러리 컴포넌트화 검토** — NetworkTransform 라인 확장(예측 리컨사일 표준 컴포넌트)으로 Fusion/FishNet 대비 최대 열위 해소 가능.
3. **포지셔닝 문서화** — 이 비교표 기준으로 "UE 패리티 무료 셀프호스팅" 위치를 README에 반영할 가치.

## 조사 출처 (2026-09 기준 공식 문서)

- UE: docs.unrealengine.com (Networking Overview·Actor Relevancy·Replication Graph), dev.epicgames.com (Iris)
- Fusion 2: doc.photonengine.com/fusion/v2 (fusion-choose·interest-management·lag-compensation·time-synchronization·whats-new-2-1)
- NGO 2.x: docs.unity3d.com/Packages/com.unity.netcode.gameobjects@2.13 (distributed-authority·client-anticipation·object-visibility)
- N4E: docs.unity3d.com/Packages/com.unity.netcode@7.0 (prediction-n4e·ghost-snapshots)
- FishNet: fish-networking.gitbook.io (features·prediction v2·network-observer·observermanager)
- Mirror: mirror-networking.gitbook.io (synchronization·remote-actions·client-side-prediction·snapshot-interpolation)
- Nakama/Colyseus/DarkRift: colyseus.io, Unity netcode 리서치 보고서(2024), itechguides 비교(2026)
