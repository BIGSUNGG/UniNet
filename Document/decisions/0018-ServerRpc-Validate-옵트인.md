# 0018. ServerRpc 검증 훅 옵트인 (Validate — 자동 감지 제거)

- **상태**: 승인됨
- **날짜**: 2026-09-22
- **결정자**: 사용자 (ServerRpc 한정 옵트인 + 불일치 진단 정책 선택)

## 배경 (Context)

P1부터 제너레이터는 ServerRpc·ClientRpc·MulticastRpc를 불문하고 `<RPC>_Validate`라는 이름의 메서드(같은 매개변수 + `Task<bool>` 반환)가 존재하면 **자동으로 감지해** 서버 디스패치에 검증 훅을 연결했다. 이름 규약만으로 동작이 결정되는 암묵적 계약이었다.

문제는 두 가지다. 첫째, `_Validate` 메서드를 작성해도 실행되지 않는 상황(예: RPC 이름 리팩터링으로 이름 규약이 깨짐)이 사용자 눈에 보이지 않았다. 둘째, "이 RPC에 검증이 붙는가"가 메서드 존재 여부라는 우연이 아니라 **선언 지점의 명시적 선택**이어야 한다 — UE의 `WithValidation` UFUNCTION 지정자처럼 옵트인이 API 표면에 드러나는 편이 사용이 간단하고 기능은 강력하다는 프로젝트 원칙과 맞는다. 제거의 대상이 되는 자동 감지는 ClientRpc/MulticastRpc에서는 애초에 훅이 생성되지 않아(서버 디스패치에만 삽입) 실질 의미가 ServerRpc에 있었다.

## 결정 (Decision)

**`_Validate` 자동 감지를 제거하고, `[ServerRpc(Validate = true)]`로 옵트인한 RPC에만 검증 훅을 연결한다.**

- `ServerRpcAttribute.Validate` 신설 — 기본 `false`. 옵트인: `[ServerRpc(Validate = true)]`
- 훅 계약은 현행 유지 — `<RPC>_Validate`는 RPC와 **같은 매개변수 + `Task<bool>` 반환**. 서버 디스패치(`__UniNetServerDispatch_*`)에서 소유자 강제(ADR-0016) 통과 직후 `await`되며, `false`면 `_Implementation`을 실행하지 않는다
- **불일치 진단 정책**:
  - `Validate = true`인데 `_Validate` 메서드가 없음 → **컴파일 에러** (UNINET011, `_Implementation` 누락 UNINET004와 같은 패턴)
  - `Validate = true`인데 `_Validate` 시그니처 불일치 → **컴파일 에러** (기존 UNINET005 유지)
  - ServerRpc에 `Validate` 플래그 없이 `_Validate` 메서드만 존재 → **경고** (UNINET012 신설 — 옵트인 누락 실수를 조용히 묻히지 않는다)
- **ClientRpc/MulticastRpc는 대상 아님** — 검증 훅은 서버 권위 경로에만 의미가 있고 두 속성에는 `Validate` 속성을 추가하지 않는다. 둘에 붙은 `_Validate` 메서드는 일반 private 메서드로 아무 진단도 내지 않는다 (진단이 필요해지면 그때 추가)
- 제너레이터 0.1.4 → **0.1.5** (nupkg 재배포 — Sandbox `Assets/Packages` + 로컬 피드 `C:/Projects/DS/unity-nuget`)

**마이그레이션 노트** — 자동 감지 의존 코드는 업그레이드 후 `_Validate`가 실행되지 않는다(UNINET012 경고로 식별). 본 저장소 in-repo `_Validate` 5곳(NetworkTransform.RpcSubmitMove·Arena.RpcSubmitAim·Arena.RpcFire·VerifyPlayer.RpcPing·VerifyPlayer.RpcDeliver)을 전부 `[ServerRpc(Validate = true)]`로 옵트인해 마이그레이션했다 — 라이브러리 사용자도 동일한 한 줄 추가가 전부다.

## 결과 (Consequences)

- "이 ServerRpc에 검증이 있다"가 RPC 선언의 속성 인자로 드러난다 — 선언부만 읽고 신뢰 경계 동작을 파악할 수 있다
- 이름 우연이 아닌 명시적 계약이 되어, RPC 이름 변경 시 검증 유실이 컴파일 에러(UNINET011)로 잡힌다
- 플래그 없는 `_Validate` 잔존은 경고(UNINET012)로 노출된다 — 옵트인 누락·메서드 명명 실수 모두 컴파일 타임 신호를 받는다
- ClientRpc/MulticastRpc의 `_Validate`는 진단 없는 일반 메서드가 된다 — 서버 권위 검증이 아닌 목적의 사설 헬퍼와 충돌하지 않는 대신, 옵트인을 잊어도 경고가 없다. 필요 시 확장 (YAGNI)
- 검증: dotnet 빌드 녹색 · Unity 리프레시 컴파일 에러 0 · **EditMode 60/60** · **PlayMode 15/15** (HostRoundtripTests의 `_Validate` 거부 경로 포함)
