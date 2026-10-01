# 0023. 씬 netId 직렬화 안정 아이디 — GlobalObjectId 해시 각인

- **상태**: 승인됨
- **날짜**: 2026-10-01
- **결정자**: DS + pi

## 배경 (Context)

- ADR-0008 이래 씬 오브젝트의 netId는 `FNV-1a 64(씬이름/이름:siblingIndex/...)` 경로 해시 — 양단 무합의 일치라는 강점이 있지만, **런타임 계층 구조가 곧 정체성**이라 다음 상황에서 양단 값이 어긋나고 그 불일치가 조용히 실패로 이어진다 (감지 창구 없음 — ADR-0010 "씬 오브젝트의 증감은 감지 창구가 없다" 한계 승계):
  - 런타임 부모 생성/재배치 — 실행마다 경로 변동
  - DontDestroyOnLoad 이동 — 씬 이름이 "DontDestroyOnLoad"로 바뀌어 경로 자체가 변함
  - 오브젝트 이름 변경·형제 순서 변경·오브젝트 추가/삭제 — 경로 변동
  - 신구 빌드 버전 혼재 — 씬 개편 후 경로 불일치
- 요구: 같은 구조로 로드하지 않았을 때(빌드 버전이 달라도) 씬 오브젝트 netId를 동일하게 맞춘다.
- **핵심 제약 — Unity `GlobalObjectId`는 Editor 전용 API** (공식 문서: "can't be used at application runtime in the Editor's Play mode or in standalone Player builds"). 런타임 재계산 불가 → **에디트 타임에 계산해 직렬화 필드에 각인**해야 한다. Netcode for GameObjects가 `GlobalObjectIdHash`를 에디트 타임 직렬화 필드로 저장하는 것과 동일한 이유다.
- 기각한 대안:
  - **수동 명시적 ID 필드** — 사람이 관리 → 중복 사고·관리 비용. 자동화(GlobalObjectId)가 가능한데 사람이 하는 꼴.
  - **서버 매니페스트 합의** (접속 시 안정 키→netId 테이블 전파, NGO in-scene placement 방식) — 대조 키로 안정 ID가 전제라 독립 대안이 아니라 본 결정 + 검증 계층. 불일치 감지 창구를 주는 가치는 있으나 시스템 메시지 1종·Welcome 흐름 변경이 따르므로 후속 과제로 분리.
  - **씬 오브젝트도 서버 할당 전환** — 클라 로컬 오브젝트와 매핑할 키 문제가 재발하고 "씬 오브젝트는 프리배치" 사용법 모델 붕괴. 스트리밍 월드 요구가 생기면 재검토.

## 결정 (Decision)

1. **씬 오브젝트 netId의 공급원을 교체** — `NetworkBehaviour`에 `[SerializeField] ulong _sceneNetId` 신설. **씬 저장 시점**에 에디터가 GameObject의 `GlobalObjectId`(씬 GUID + local file id + prefab instance id)를 FNV-1a 64로 해시해 각인한다. 런타임은 직렬화 값만 읽는다 (`NetId` getter: `_sceneNetId != 0 → 각인 값, 아니면 기존 경로 해시 폴백`).
2. **각인 주체 — `Package/Runtime/UniNet.Unity.Editor/` 신설 에디터 어셈블리**의 `SceneNetIdPostprocessor`. `EditorSceneManager.sceneSaving` 이벤트에서 저장되는 씬의 모든 NetworkBehaviour GameObject(비활성 포함)를 각인한다. 빌드는 저장된 씬 파일을 직렬화하므로 이 훅 하나로 충분하다 (빌드는 저장 안 된 씬을 못 쓴다).
   - 각인 값은 결정적(같은 GlobalObjectId → 같은 해시)이며 idempotent — 값이 같으면 쓰지 않아 씬 dirty를 만들지 않는다.
   - GlobalObjectId를 만들 수 없는 오브젝트(저장 안 된 신규 씬 등)는 경고 로그 후 0 유지 → 런타임 경로 해시 폴백 (기존 동작 보존).
   - 씬 안에서 해시 중복은 Unity 보증상 불가능하지만, 발생 시 `[UniNet]` 에러 로그로 시끄럽게 — 조용한 덮어쓰기(등록 테이블 last-wins)를 방어.
3. **무합의 원칙 유지** — 프로토콜·와이어 포맷 변경 0. netId **값**만 바뀌며 ADR-0010의 "포맷 변경(기존 구현 간 호환 깨짐)은 0.x로 수용"으로 흡수. 서버·클라는 여전히 각자 로컬 직렬화 값을 비교 없이 사용한다.
4. **각인 전제 계약** — NetworkBehaviour를 배치한 씬은 **한 번은 에디터에서 저장**되어야 한다 (저장 전 오브젝트는 GlobalObjectId의 scene GUID가 비어 있어 각인 불가 — Unity 제약). 미각인 오브젝트는 경로 해시로 동작하므로 기존 테스트·런타임 생성 오브젝트는 무영향.
5. **NetId 첫-읽기 고정 계약 명시** — `NetId` getter는 첫 읽기에서 값을 고정한다(기존 캐시 동작 유지). 각인은 씬 저장(런타임 시작 전)에 끝나므로 등록 시점에는 항상 반영되어 있다.

## 결과 (Consequences)

- **긍정**: 정체성이 직렬화 데이터에 귀속 — 이름 변경·형제 재배치·오브젝트 추가/삭제·DontDestroyOnLoad 이동·신구 빌드 혼재 모두에서 양단 netId 일치. 프리팹 인스턴스는 instance id가 포함되어 인스턴스별로 구분. 구조 차이에 대한 이전 세션의 "무보증"이 "직렬화된 씬 에셋 기준 보증"으로 강화.
- **부담 / 리스크**:
  - 씬 에셋에 각인 필드가 생김 — 씬 diff에 `_sceneNetId` 행 추가(1회).
  - **같은 씬을 additive로 중복 로드하면 정의상 동일 ID** — 어느 안정 키로도 해결 불가(경로 해시도 동일 한계). 문서 계약으로 유지: 해당 시나리오는 동적 스폰으로 전환.
  - 씬 간 오브젝트 이동 시 ID 변경 — 양단이 같은 씬 에셋을 쓰면 여전히 상호 일치하므로 문제 없음. 씬 에셋을 프로젝트 간 복제해 GUID가 분기되면 양단 불일치 (버전 관리로 방지).
  - 불일치 자체를 감지하는 창구는 여전히 없음 — 매니페스트 대조(위 기각 대안 2)는 후속 과제.
- **폐기 방법**: netId 소스 변경 시 새 ADR로 대체 (ADR-0008 결정 4의 "netId = 씬 경로 해시"를 본 ADR로 대체).

## 검증 증거 (2026-10-01)

- 배치 컴파일 녹색: `Sandbox-impl-a1.log` (error CS 0건)
- EditMode 80/80: `Sandbox/tests-editmode-a1.xml` — 신규 `SceneNetIdTests` 4종(직렬화 아이디 우선·폴백·동적 선점·첫-읽기 고정) + 기존 전수 회귀
- PlayMode 17/17: `Sandbox/tests-playmode-a1.xml` (NetworkTransform 타이밍 취약 테스트 1회 실패 후 단독·전체 재실행 통과 — 사전 존재 flaky, 본 변경 무관)
- **2-프로세스 왕복 PASS** — `Builds/2proc-server.log`·`2proc-client.log` (`SERVER-DONE`/`CLIENT-DONE` score=89 양단 일치, 클라 PASS 3종: DYNAMIC-SPAWN-DESTROY·MULTI-COMPONENT·SERIALIZATION). 검증 중 발견한 사전 존재 고장(소스젠 출력 2벌 CS0101×144)의 근본 원인: 2Proc 프로젝트에 ADR-0021 이전 NuGetForUnity 캐시의 제너레이터 DLL 3종(UniNet.CodeGenerator 0.2.1·DRPC.CodeGenerator 3.5.0·MessageProtocol.CodeGenerator 3.2.0)이 잔존해 패키지 동봉분(`Package/Runtime/UniNet.Unity/Analyzers/`)과 **이중 로드**되어 출력이 2벌 생성된 것 — 중복 analyzer 3종 삭제로 복구. 재발 방지: 제너레이터는 단일 공급원(패키지 동봉분)만 analyzer로 로드할 것

## 알려진 한계·후속 과제

- 접속 시 양단 netId 집합 대조(불일치 감지 창구) — 시스템 메시지 1종으로 후속 검토.
- `NetworkTransform` PlayMode 타이밍 취약성(파괴 경합) — 재현 불규칙, 테스트 안정화 후속 과제.
- 2-프로세스 검증 프로젝트 고장 — 원인 규명 완료(NuGetForUnity 캐시 잔존 제너레이터 DLL 3종의 이중 로드)·복구·왕복 재검증 통과(본 ADR 검증 증거 참조). 잔여: 2Proc 프로젝트 자체는 untracked 로컬 상태 — 세팅 절차의 문서화는 별도 과제.
