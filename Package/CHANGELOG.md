# UniNet Changelog

Detailed internal history: `Document/changelog.md` in the repository root (Korean).

## 0.2.0

**Spawnable prefab spawning — a rework of dynamic spawns. Breaking.**

### Highlights

- **Mark prefabs as spawnable in the editor** — add a `NetworkSpawnable` marker to a prefab root and save once. The editor stamps a stable id (prefab GUID hash) and bakes a runtime lookup table (`Assets/Resources/UniNet/UniNetPrefabTable.asset`) — fully automatic, nothing to configure.
- **`NetworkInstantiate` now spawns prefabs, and only prefabs** — unmarked prefabs and runtime-created `new GameObject(...)` templates are rejected with a warning. The server decides what can exist on the network, at edit time.
- **Every client instantiates the same prefab** — the spawn message carries the prefab id; each client resolves it through its own baked table and instantiates **its own copy of that exact prefab** (renderer, model, and presets included — no more empty shells). Both ends must ship the same prefab assets.
- **Multiple prefabs per component type** — the old one-prefab-per-type catalog limit is gone. Two prefabs with the same `NetworkBehaviour` spawn side by side.

### Breaking changes & migration

- `UniNetManager.RegisterPrefab<T>(...)` **removed** — delete the calls; the baked table replaces the runtime catalog.
- Runtime template spawning (`new GameObject(...)` + `NetworkInstantiate(template)`) **removed** — mark a prefab instead (edit-time marking is the only path; a marker added at runtime has no stamped id and is rejected).
- Client-side sub auto-restore **removed** — a client prefab whose `NetworkBehaviour` composition differs from the server's is now **rejected** ("spawn slot mismatch" error) instead of silently re-assembled. Keep prefabs identical on both ends.
- Spawn message wire format changed (prefab id added) — update server and clients together.

### Added

- `NetworkInstantiate(original, position, rotation, configure)` overload.
- Ghost catch-up guard — destroyed registered instances no longer throw during late-join catch-up.

### Also included since v0.1.1 (unreleased as 0.1.2)

- **Stable scene-object netIds** — scene `NetworkBehaviour`s get their `netId` stamped into the scene asset at save time (hash of the object's `GlobalObjectId`). Both ends agree even when hierarchies differ at runtime (renames, sibling reorders, added/removed objects, `DontDestroyOnLoad`) or across different build versions. Save each scene once to stamp; unstamped objects keep the legacy hierarchy-path hash behavior.

## 0.1.2

- **씬 오브젝트 netId 안정화** — 씬 저장 시 `NetworkBehaviour`에 GameObject GlobalObjectId 해시를 직렬화 각인 (신규 에디터 어셈블리 `UniNet.Unity.Editor`). 이름 변경·형제 재배치·오브젝트 추가/삭제·DontDestroyOnLoad·빌드 버전 차이에서도 서버-클라 netId 일치. 미각인 오브젝트는 기존 계층 경로 해시로 동작 — 씬을 한 번 저장하면 각인된다 (ADR-0023)

## 0.1.1

- `NetworkInstantiate(original, ownerConnId)` — 명시적 소유자 동적 스폰 (접속 순간 그 접속 소유 토큰 발급). `RegisterDynamicObject(components, ownerConnId)` 선택 인자 동반
- `ReassignOwnership` — 살아 있는 소유자는 유지, 고아(소유자 0·단절)만 라운드로빈 재배정. 재접속·재시작 시 소유권 도용 방지
- `UniNetEnvironment.ServerChanged` — 서버 인스턴스 설정 순간(접속 수락 전, 메인 스레드) 이벤트. 폴링 경쟁 없는 라이프사이클 후크
- 스테일 동적 스폰 잔재 방지 — `IsDynamicSpawn` 마커, 씬 등록 스킵, `ClientStop`/`ServerStop` 시 `SweepDynamicSpawns()` (낡은 netId 재활용 충돌·이중 조인·오소유 읽기 방지)

## 0.1.0

- 초기 패키지 구성 — UniNet.Core / UniNet.Unity 어셈블리
- 기반 스택(DRPC 3.5.0 · MessageProtocol 3.2.0 · Communication 2.7.0 · LiteNetLib 2.1.4 · BouncyCastle 2.7.0) netstandard2.1 DLL 패키지 동봉 (ADR-0021)
- 소스 생성기 3종(DRPC · MessageProtocol · UniNet.CodeGenerator 0.2.1) RoslynAnalyzer 라벨 동봉
- P1~P4 기능: RPC(4종) · [Replicated] 변수 복제(델타) · 동적 스폰/파괴 · 고급 리플리케이션 정책 · NetworkTransform · P4 훅
