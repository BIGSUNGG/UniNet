# Basics 샘플

RPC + 변수 Replicate의 최소 동작 예제.

1. Package Manager → UniNet → Samples → **Import**
2. 빈 GameObject 2개 생성: `BasicsBootstrap`, `BasicsSample` 컴포넌트 각각 부착
3. Play → 콘솔에 `[Basics] host ready` 출력 확인
4. **Space** 키 → `RpcHit(10)` 서버 전송 → HP 리플리케이션·FX 로그 확인 (`HP 100 -> 90`, `hit fx -10`)

전용 서버/클라 분할: `BasicsBootstrap.Start()`의 `HostAsync(7777)`을 `ServerAsync(7777)` / `ClientAsync("127.0.0.1", 7777)`로 교체.
