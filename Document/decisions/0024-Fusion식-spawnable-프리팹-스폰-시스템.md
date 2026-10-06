# 0024. Fusion식 spawnable 프리팹 스폰 시스템 — NetworkInstantiate 전면 교체

- **상태**: 승인됨
- **날짜**: 2026-10-06
- **결정자**: DS + pi

## 배경 (Context)

- 기존 동적 스폰(ADR-0009/0014/0017)은 **typeKey 조립 방식** — 스폰 메시지가 첫 NetworkBehaviour 타입 해시(typeKey)를 전달하고 클라는 ① 기본 팩토리(빈 GO+AddComponent) ② `RegisterPrefab<T>` 런타임 카탈로그 ③ ADR-0014 서브 자동 복원(AddComponent)으로 오브젝트를 재조립했다.
- 한계: **한 타입 = 프리팹 1개**(카탈로그 Override 구조) / 서버가 아무 GameObject나 스폰 가능(게이트 없음 — 런타임 템플릿이 관례) / 클라의 실제 프리팹과 무관한 "껍데기+컴포넌트"가 생성됨.
- 조사(같은 세션): Photon PUN2는 프리팹 이름 문자열+Resources 해석, Fusion은 에디트 타임 **spawnable 마킹 + 베이크 테이블**(프리팹 ID 전달, 클라 동일 프리팹 인스턴스화, 양단 프리팹 존재 필수). 사용자 결정 3건:
  1. **프리팹 신원 기반 교체**(typeKey 조립 폐기) — Fusion과 동일 구조
  2. **프리팹만 허용** — 런타임 템플릿 스폰 경로 폐지(런타임 등록 탈출구도 없음)
  3. **마커 컴포넌트 방식** — 프리팹 루트에 붙이는 NetworkSpawnable + 저장 시 각인(ADR-0023 GlobalObjectId 패턴 재사용)

## 결정 (Decision)

1. **`NetworkSpawnable` 마커 컴포넌트** (`Package/Runtime/UniNet.Unity/NetworkSpawnable.cs`) — 프리팹 루트에 부착해 spawnable 마킹. `[SerializeField] ulong _prefabId`(에디트 타임 각인)만 갖는 경량 마커. 런타임 AddComponent는 id가 0 → 거부(에디트 타임 마킹이 유일 경로).
2. **에디터 파이프라인** (`UniNet.Unity.Editor/NetworkSpawnablePostprocessor`):
   - 프리팹 임포트/저장 시 **프리팹 GUID의 FNV-1a 64 해시를 `_prefabId`에 각인**(ADR-0023 패턴 재사용). 루트 외 마커·NetworkBehaviour 없는 프리팹은 에러 로그.
   - **베이크 테이블** `UniNetPrefabTable`(ScriptableObject)을 `Assets/Resources/UniNet/`에 자동 생성·갱신 — 전체 프리팹 경로 스캔(GetAllAssetPaths; `FindAssets("t:NetworkSpawnable")`는 임포트 직후 인덱스가 stale해 0건 반환 — 실측). **메모리 반영은 동기, 디스크 저장은 1틱 지연**(임포트 파이프라인 내 SaveAssets 재진입 방지 + 런타임 Lookup 레이스 방지 — 실측 교정).
3. **런타임 게이트** — `NetworkInstantiate`는 `_prefabId != 0`인 마킹 프리팹만 허용. 미마킹 프리팹·런타임 생성 오브젝트·id 0 마커는 서버 경고 후 null. 스폰 원본이 남는 기존 계약 유지(프리팹 에셋은 그대로).
4. **와이어 변경** — 스폰 메시지(시스템 RPC 3번)에 `prefabId` 8바이트 추가: `[netId][변환 7값][prefabId][subCount][{typeKey, stateLen, state}×N]`. typeKeys는 **클라 검증용 유지**(프리팹 구성 불일치 = 빌드 어긋남 감지). 0.x 호환 깨짐 수용(ADR-0010 전례).
5. **클라 생성 교체** (`UniNetSpawn.Apply`) — prefabId → `UniNetPrefabTable.Lookup`(Resources 베이크 테이블) → **동일 프리팹 Instantiate** → 슬롯 대조(typeKeys) → 등록·전체 상태 적용. **양단 프리팹 에셋 존재 필수 — Fusion과 동일 전제**(미보유 빌드는 "알 수 없는 프리팹 아이디" 에러로 거부).
6. **폐기·제거** — `RegisterPrefab<T>` 공개 API, `UniNetSpawnRegistry`의 팩토리(Factories/Create/Override), ADR-0014 서브 자동 복원(HasSub/AddComponent 경로). `UniNetSpawnRegistry`는 서브 검증용 typeKey 등록만 남김. 제너레이터(0.2.2)도 팩토리 방출 제거 — DLL 동봉 재빌드.
7. **기존 계약 유지** — 서버 권위(클라 호출 경고+null), 서브별 전체 상태·InitialOnly/OwnerOnly 초기 분기, 호스트 재사용(이중 생성 방지), `configure` 콜백(클론 초기화 — ADR-0017), 소유권 배정(명시/라운드로빈), 오브젝트당 255 상한, NetId 규칙(ADR-0023). 신규: `NetworkInstantiate(original, position, rotation, configure)` 4인자 오버로드.
8. **구성 불일치는 자동 복원 대신 거부** — 클라 프리팹이 서버 구성과 다르면(초과·누락·순서) "[UniNet] 스폰 슬롯 불일치" 에러 후 스폰 거부(ADR-0014의 자동 복원 철학 폐기 — 프리팹이 단일 진실원).
9. **고스트 catch-up 방어** — `GetSpawnTransform`이 파괴된 등록 인스턴스(테스트 teardown 잔재 등)에 identity 기본값 반환(드라이버 스윕이 다음 프레임 파괴 전파). 테스트 간 잔재 오염으로 실측 발견.

## 결과 (Consequences)

- **긍정**: 한 타입에 프리팹 N개(1:1 제약 해소 — 검증 테스트로 증명) / 스폰 가능 대상의 에디트 타임 통제(게이트) / 클라가 **실제 프리팹**(렌더러·모델·프리셋 포함)로 생성 — 카탈로그 등록 코드 불필요 / 프리팹 구성 불일치 감지(빌드 어긋남 조기 발견) / 프리팹 ID 각인이 GUID 기반이라 재배치·이름 변경에 불변.
- **부담 / 리스크**:
  - **양단 프리팹 에셋 존재 필수** — 클라 빌드가 프리팹을 누락하면 해당 스폰 거부(런타임 에러로 감지). Fusion과 동일한 운영 계약.
  - 마킹→베이크→배포 파이프라인이 생김(신규 프리팹은 저장 후 테이블 베이크 필요 — 포스트프로세서 자동).
  - 기존 코드 마이그레이션: `RegisterPrefab` 호출 삭제 + 템플릿 생성 코드 → 마킹 프리팹 참조로 교체.
  - `UniNetPrefabTable`은 프로젝트별 자동 생성(Resources 폴더) — 생성 시점까지 Lookup은 빈 테이블 동작.
- **폐기 방법**: 스폰 식별 모델 변경 시 새 ADR로 대체 (ADR-0009 결정 1·ADR-0014 전체·ADR-0017의 스폰 API 파트를 본 ADR로 대체).

## 검증 증거 (2026-10-06)

- 배치 컴파일 녹색: error CS 0건 (`Sandbox-impl-a24-edit4.log` 등)
- **EditMode 80/80**: `Sandbox/tests-editmode-a24.xml` — 신규 `SpawnablePrefabTests` 4종(미마킹·런타임·id 0 마커 거부 + 스폰 0건 / 각인 마커 거부 / **같은 타입 프리팹 2종 동시 스폰 — 1:1 제약 해소 증명** / 베이크 테이블 해석) + `MultiComponentTests` 갱신(프리팹 경로 스폰·구성 불일치 거부) + 기존 전수
- **PlayMode 17/17**: `Sandbox/tests-playmode-a24.xml` (고스트 catch-up 방어 적용 후 — 1회 실패는 방어 전 잔재 오염)
- **2-프로세스 왕복 PASS**: `Builds/2proc-*.log` — 재작성된 TwoProcessRunner가 프리팹 기반 스폰으로 `DYNAMIC-SPAWN-DESTROY PASS`·`MULTI-COMPONENT PASS`·`SERIALIZATION PASS`, 양단 score=89 일치, 프리팹 Lookup 실패 0, 양단 테이블 자동 베이크(4개)
- 제거 확인: `grep -rn "RegisterPrefab\|RestoreSubs" Package/Runtime` 0건

## 알려진 한계·후속 과제

- 테스트 프리팹 5종(Sandbox Fixtures 4 + Arena 1)은 배치 베이커(`TestPrefabBaker`·`ArenaPrefabBaker`)로 생성해 커밋 — 테스트 프리팹 구성 변경 시 재베이크 필요.
- 풀링·Addressables/런타임 프리팹 로딩은 범위 밖(후속).
- 테이블 베이크의 전체 경로 스캔은 O(전체 에셋) — 수천 에셋 프로젝트에서 임포트 빈도가 높아지면 인덱스 캐시 재검토(`ponytail:` 표기).
- PlayMode 테스트 teardown이 HostStop 없이 오브젝트만 파괴하는 관행 — 고스트 방어(결정 9)로 완화됐으나 teardown 정리는 후속.
