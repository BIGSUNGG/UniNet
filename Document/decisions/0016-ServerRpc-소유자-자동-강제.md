# 0016. ServerRpc 소유자 자동 강제 (RequireOwnership 옵트아웃)

- **상태**: 승인됨
- **날짜**: 2026-09-21
- **결정자**: 사용자 (자동 강제 + 옵트아웃 방식 선택)

## 배경 (Context)

P1 구현 때부터 문서화된 알려진 한계([[0008-구현-아키텍처]], [[features/monobehaviour-rpc-replicate]] "알려진 한계"): 서버 수신 핸들은 페이로드의 netId만으로 대상 오브젝트를 찾아 `_Implementation`을 실행한다. 악의적 클라가 **다른 오브젝트의 netId로 페이로드를 조립**하면 피해자 오브젝트의 ServerRpc(예: 타 플레이어 아바타의 이동 입력)를 실행할 수 있었다. `_Validate` 후크는 인자만 검증 가능해 발신자를 검증하지 못한다.

완화 플러밍은 이미 마련돼 있었다 — 연결별 허브가 발신 connId를 주입해 디스패치까지 전달(`senderConnId`, ADR-0008 리뷰 라운드 반영분). 남은 것은 소유자 대조를 어디에 두느냐였다. 선택지는 ① 게임이 `_Validate`에서 자체 대조(도구만 제공) ② 제너레이터가 자동 강제(안전 기본값, 속성으로 옵트아웃) ③ 문서 가이드만. 사용자가 ②를 승인했다.

## 결정 (Decision)

**제너레이터가 서버 디스패치에 소유자 대조를 자동 삽입한다. 기본은 강제, 속성 하나로 옵트아웃한다.**

- `ServerRpcAttribute.RequireOwnership` 신설 — 기본 `true`. 옵트아웃: `[ServerRpc(RequireOwnership = false)]` (Netcode for GameObjects의 `RequireOwnership` 기본값 true 관례와 동일 — ADR-0007의 Mirror/Netcode류 API 스타일)
- 생성 디스패치(`__UniNetServerDispatch_*`) 머리에서 `senderConnId != NetworkServer.GetOwner(netId)`이면 구현을 실행하지 않고 경고 1줄을 남긴다 (`"[UniNet] ServerRpc 거부 — 비소유 발신: <타입>.<메서드> netId sender owner"`) — 조용한 파손이 아니라 식별 가능한 거부
- **로컬 권위 경로 제외** — `senderConnId == 0`(서버·호스트 직접 호출, 오프라인 실행)은 대조 없이 실행한다. 검사 대상은 네트워크로 도달한 발신뿐이다. 실제 연결 ID는 1부터 단조 부여되므로 0과 충돌하지 않는다
- ClientRpc/MulticastRpc(서버→클라 방향)는 대상 아니다
- 소유자 조회를 위해 `NetworkServer.GetOwner(netId)` (0 = 미할당) 신설
- 호스트는 서버 권위 직접 경로(senderConnId 0)로 실행되어 영향 없음. 소유자 미배정(0) 오브젝트에 대한 네트워크 발신은 거부된다 — 등록 직후~첫 재배정 전의 창은 메인 큐 FIFO 순서(AttachConnection 예약이 RPC 디스패치보다 먼저 실행)로 실질 노출되지 않는다
- **로컬 권위 0-면제의 전제** — DRPC는 허브 생성 직후 `OnConnected`를 발화해 첫 요청 디스패치 전에 연결별 허브에 connId를 부여한다(상류 계약). 이 전제가 깨지면 `senderConnId == 0` 면제가 비소유 우회 경로가 되므로, DRPC 업그레이드 시 발화 순서를 재확인한다
- 생성 거부 경고는 **유량 제한**된다 — `NetworkServer.ReportServerRpcRejection(senderConnId)`(발신자별 최초 1회 + 전역 5초당 최대 1회, Core 무로그 계약 유지 — 판단만 반환)이 true일 때만 로그. 비소유 ServerRpc churn 시에도 로그 I/O가 입력에 비례하지 않는다 (리뷰 라운드 3 반영)
- 제너레이터 0.1.2 → **0.1.3**(강제 삽입) → **0.1.4**(로그 유량 제한) (nupkg 재배포 — Sandbox `Assets/Packages` + 로컬 피드)

**마이그레이션 노트** — 기본값이 "모든 클라 허용"에서 "소유자만"으로 바뀌었다. 기존 코드 중 비소유 클라가 호출하던 크로스-클라 ServerRpc는 업그레이드 후 거부된다(경고 로그로 식별 가능). 정상 크로스-클라 호출(전 클라 보고류)은 명시적 `[ServerRpc(RequireOwnership = false)]`를 붙인다. 본 저장소 in-repo RPC 전수 확인 결과: Arena(RpcSubmitAim·RpcFire)·NetworkTransform(SubmitMove)은 모두 소유자 전용 의미론이라 변경 불필요 — 옵트아웃 패턴은 HealthTank.RpcHeal 픽스처와 기능 문서 예제로 시연한다.

## 결과 (Consequences)

- **긍정**: 게임 코드 0줄로 서버 권위 신뢰 경계가 기본 닫힌다 — netId 위조로 타 오브젝트 ServerRpc 실행 불가. 거부가 경고 로그로 관측된다. 옵트아웃이 RPC 단위라 정상 크로스-클라 패턴도 유지된다
- **부담 / 리스크**: 기본값 변경의 호환성(기존 비소유 호출 거부 — 위 마이그레이션 노트). 소유자 미배정 오브젝트로의 네트워크 발신 거부(의도된 안전 기본값). 발신자-소유자 대조가 핫패스 디스패치마다 사전 조회 1회 추가(사전 조회 — cold에 준하는 비용)
- **폐기 방법**: `ServerRpcAttribute.RequireOwnership` 기본값을 false로 바꾸거나, 제너레이터의 대조 삽입 블록(`Emitter.cs`의 `rpc.RequireOwnership` 분기)을 제거하면 된다 — 단일 지점
- **검증**: EditMode `ServerRpcOwnershipTests` 4종(소유자 실행·비소유 거부+경고·옵트아웃 실행·로컬 권위 경로) + PlayMode `ServerRpcOwnershipPlayTests`(실제 루프백 왕복 소유자 실행 + 게스트 연결 비소유 거부). 세부 수치는 [[changelog]] 참조
