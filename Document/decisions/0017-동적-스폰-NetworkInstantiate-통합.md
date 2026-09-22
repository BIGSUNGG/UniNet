# 0017. 동적 스폰 NetworkInstantiate 통합

- **상태**: 승인됨
- **날짜**: 2026-09-22
- **결정자**: 사용자 (NetworkInstantiate 단독·복제 겸함·configure 콜백 방식 선택)

## 배경 (Context)

ADR-0009가 정립한 동적 스폰 API는 `Spawn(instance)` — 호출측이 `Instantiate`/생성을 마친 인스턴스를 넘기면 등록(netId·소유권·전파)만 수행하는 **등록 전용** 계약이었다. `NetworkDestroy`는 등록 해제+전파+로컬 파괴를 겸하는 convenience였다.

사용자 제안으로 재검토했다: ① `RegisterObject`/`UnregisterObject` 네이밍(내부 어휘와 일치), ② `UnregisterObject`는 씬에서 GO를 제거하지 않고 RPC/동기화에서만 제외하는 의미 변경. 논의 결과 채택된 방향은 달랐다:

- **네이밍** — 생태계 표준 동사(Spawn)보다 실제 기계론(복제·등록)을 드러내는 쪽. 다만 `RegisterObject`가 아니라 **`NetworkInstantiate`/`NetworkDestroy` 페어링** (파괴 쪽은 현행 유지).
- **의미** — 등록 해제만 남기는 비파괴 API는 채택하지 않고, 반대로 **스폰 쪽에 복제를 흡수**해 한 호출로 끝낸다. Mirror `Unspawn`류 비대칭(서버 GO 유지·클라 파괴)이나 좀비 상태(NetId 잔존 컴포넌트) 관리 부담을 안게 되므로.

복제 흡수 설계에서 발견된 제약: `Object.Instantiate`는 **직렬화 복사**라 public·`[SerializeField]` 필드만 따라온다. 평범한 private `[Replicated]` 필드(InitialOnly 기준선의 주류 패턴 — ArenaPlayer `_displayName` 등)는 템플릿에서 클론으로 복사되지 않아 스폰 기준선이 조용히 깨진다.

## 결정 (Decision)

**`Spawn(instance)`를 제거하고 `NetworkInstantiate`를 단독 API로 삼는다 — 원본(프리팹·템플릿) 복제 + 등록 + 전 클라 스폰 전파를 한 호출로 수행하고, 등록된 인스턴스를 반환한다.**

- 오버로드 3종 — `NetworkInstantiate(GameObject original)` / `(original, Vector3 position, Quaternion rotation)` / `(original, Action<GameObject> configure)`. 구성은 하나의 코어(복제 → configure → 등록 → 전파)로 통일
- **configure 콜백** — 복제 직후·전파 직전에 클론으로 실행. private `[Replicated]` 초기화(InitialOnly 기준선 등 비직렬화 값)는 여기서 세팅한다. `[SerializeField]` 필드 강제(실수 시 조용한 상태 유실) 대신 채택
- 비전파 상태(델리게이트·서버 동작 플래그 — MovementRule·NetworkCullDistance·NetworkRewindHistory 등)는 기준선에 실리지 않으므로 스폰 후 클론에 주입해도 무관
- `NetworkDestroy`는 유지(파괴 겸 convenience — 등록 해제+전파+로컬 파괴). 클라 고스트는 파괴(현행 SendDestroy 경로)
- 원본(템플릿)은 호출측 소유로 남는다 — 반환값을 쓰고 원본은 호출측에서 정리. `RegisterPrefab` 카탈로그 원본은 팩토리가 참조하므로 **파괴 금지**
- 가드 — 서버 미실행·NetworkBehaviour 부재 시 경고 후 null 반환(복제 없음). 컴포넌트 255 상한 초과 시 인스턴스 폐기(플레이 `Destroy`/에디트 `DestroyImmediate` 분기) 후 null 반환

**마이그레이션 노트** — 기존 "생성 후 `Spawn(instance)`" 흐름은 ① 원본을 만들어 public 필드를 세팅 → ② `NetworkInstantiate(원본[, configure])` → ③ 반환값(등록 인스턴스) 사용 → ④ 원본 정리로 바뀐다. 기존 인스턴스 등록(풀링 재사용 등)은 이 API로 표현할 수 없다 — 필요해지면 별도 API로 재추가한다.

## 결과 (Consequences)

- **한 줄 스폰** — 프리팹 흐름(상용 게임의 주류)이 `NetworkInstantiate(prefab, pos, rot)` 한 줄로 끝난다. 생성-등록 순서 실수(등록 전 상태 누락)가 구조적으로 줄어든다
- **이름이 기계론과 일치** — Instantiate 겸함이 이름에 드러나고 `NetworkDestroy`와 대칭을 이룬다
- **등록 전용 흐름 소멸** — 풀링·씬 배치 오브젝트의 후발 등록은 표현 불가(필요시 재추가). 템플릿 원본 정리 책임이 호출측에 있다
- **직렬화 복사 의미론이 공개 계약이 됐다** — 비직렬화 초기화는 configure 콜백(또는 스폰 후 주입)이라는 사용 규칙을 문서·예제로 지속 안내해야 한다
- 검증 — EditMode 60/60·PlayMode 15/15 (콜사이트 15곳 전수 마이그레이션: ArenaBootstrap·ArenaTwoProcessRunner·TwoProcessRunner·EditMode/PlayMode 테스트)
