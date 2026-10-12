# 네트워크 통계 (트래픽 카운터·RTT·큐 깊이)

- **상태**: 구현됨
- **최초 작성**: 2026-10-12
- **마지막 갱신**: 2026-10-12

## 개요

네트워크 사용량과 지연을 관측하는 조회 API — 서버/클라 송수신 **페이로드 바이트·패킷 카운터**, 클라 측정 **RTT**(EMA), 커넥션별 RTT(서버 보관), 예산 지연 큐·메인 큐 **깊이**. 결정 배경: [[0029-네트워크-통계-API]].

**측정 계약(중요)**: 카운터는 **UniNet 페이로드 바이트**(RPC 파라미터 직렬화분)다. 와이어 프레이밍(메서드 헤더)·전송 오버헤드(UDP/RUDP 프레이밍·ACK·재전송·DTLS)는 포함되지 않는다 — "게임이 얼마나 상태를 실어 나르는가"의 애플리케이션 관점 측정이며, 소켓 레벨 총바이트와는 상수 배수 차이가 있다.

## 설계

- **`UniNetTrafficCounters`** (`Package/Runtime/UniNet.Core/Hosting/UniNetTrafficCounters.cs`) — `BytesSent`·`BytesReceived`·`PacketsSent`·`PacketsReceived` (long, Interlocked). 송신은 생성 허브 Send* 전 경로, 수신은 생성 허브 수신 핸들러·디스패치 전 경로에서 계상 (제너레이터 0.2.4 방출)
- **`NetworkServer.TrafficStats`** / **`NetworkClient.TrafficStats`** — 각 단의 카운터 인스턴스
- **RTT 프로브(와이어 8/9)** — 클라 드라이버 1초 주기 핑 `[프로브 원점(Stopwatch 도메인 초)][현재 RTT ms]` → 서버 허브 에코(원점 반사) + `ServerConnection.RttMs` 갱신(클라 보고값) → 클라 `NetworkClient.RoundTripTimeMs` EMA(α=0.25, 첫 왕복 전 -1). Stopwatch 도메인은 디스패치 스레드에서도 안전(Unity 시계는 메인 스레드 전용)
- **`NetworkServer.DeferredSendCount`** — P3 예산 초과로 이번 틱에 못 보내고 대기 중인 델타 수
- **`UniNetEnvironment.PendingMainActions`** — 메인 펌프 대기열 깊이 (드라이버 Update 처리량 관찰)

## 사용 예

```csharp
// 서버 (권위) — Arena 예제(ArenaBootstrap.PublishNetStats) 참고: 5초 주기 HUD+로그
var st = server.TrafficStats;
Debug.Log($"↑{st.BytesSent / 1024.0:F1}KB/{st.PacketsSent}pkt ↓{st.BytesReceived / 1024.0:F1}KB/{st.PacketsReceived}pkt");
foreach (var conn in server.SnapshotConnections())
    Debug.Log($"conn {conn.ConnId} RTT {conn.RttMs:F0}ms");   // 클라 보고값 — 관측용(권위 판정 금지)
Debug.Log($"예산 지연 큐 {server.DeferredSendCount}");

// 클라
Debug.Log($"RTT {UniNetEnvironment.Client.RoundTripTimeMs:F0}ms · ↓{UniNetEnvironment.Client.TrafficStats.BytesReceived}B");
```

## 동작 상세

- 카운터 계상 시점: 송신 — `SendRPC` 호출 직전(페이로드 확정 후) / 수신 — 핸들러 진입(페이로드 파싱 전). 크래시·유실로 소실된 송신도 계상된다(시도 기준)
- RTT EMA는 클램프(음수 여행 불가)하며 서버 보관값은 다음 핑까지 최대 1초 지연
- 테스트 채널(RecordingChannel) 경로는 계측되지 않는다 — 카운터는 생성 허브(실세션) 경로 전용. EditMode는 카운터 객체·큐 깊이 단위 검증, 실측은 PlayMode 루프백

## 테스트 / 검증

- EditMode `NetworkStatsTests` 3종 — 카운터 누적·예산 지연 큐 깊이(2델타/예산 8B로 두 번째 지연 검증 — 기아 방지 강제전송 규칙 포함)·메인 큐 깊이 적립/드레인
- PlayMode `NetworkStatsPlayTests` 1종 — 실세션에서 양단 카운터 증가(버전 선언·핑·리플리케이션·TimeSync 포함) + 클라 RTT 측정(≥0, 루프백 <500ms) + 서버 커넥션 RTT 보고(핑 피기백 대기)
- 2026-10-12: EditMode 90/90 · PlayMode 23/23 (MCP 에디터)

## 변경 이력

- 2026-10-12 — 최초 구현 (ADR-0029, 제너레이터 0.2.4)
