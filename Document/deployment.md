# UniNet UPM 배포 (Git URL)

- **상태**: 확정
- **결정**: 배포 채널 Git URL, 기반 스택 DLL 동봉 (ADR-0021)

## 소비자 설치

```json
"com.ds.uninet": "https://github.com/OWNER/UniNet.git?path=/Package#v0.1.0"
```

- Unity `6000.0+` 필요. 의존성 전부 패키지 동봉 — 추가 설정 없음.
- 사설 저장소: Unity는 git clone에 로컬 git 자격증명(credential helper / SSH)을 사용한다. 에디터 실행 계정에서 `git clone <url>`이 성공하면 UPM 설치도 성공한다.

## 배포 절차 (설정 이후엔 태그→푸시뿐)

최초 1회 — 사용자 설정:

1. GitHub에 **사설** 저장소 생성 (예: `OWNER/UniNet`), 이 로컬 저장소를 remote로 등록·푸시
2. `Document/deployment.md`·`README.md`·`Package/Documentation~/index.md`의 `OWNER/UniNet` 플레이스홀더를 실제 주소로 치환 (아래 명령)

   ```bash
   grep -rl "OWNER/UniNet" README.md Document Package | xargs sed -i 's|OWNER/UniNet|<실제owner>/UniNet|g'
   ```

매 배포:

1. `Package/package.json`의 `version` 상향 + `Package/CHANGELOG.md` 항목 추가 + `Document/changelog.md` 기록
2. 커밋 → 태그 (`git tag vX.Y.Z`) → 푸시 (`git push && git push --tags`)

소비자는 `#vX.Y.Z` 태그로 고정 설치하며, 태그 재지정만으로 롤백된다.

## 기반 스택 버전 갱신 (개발 시)

```bash
Package/tools/update-dlls.sh          # C:/Projects/DS/unity-nuget의 nupkg에서 DLL 추출·복사 + UniNet.CodeGenerator 빌드
```

- 버전은 스크립트 상단에서 고정한다. 복사 후 Unity 리프레시 → 테스트 → 커밋.
- `Sandbox/NuGet.config`(로컬 소스)은 UniNet.CodeGenerator 빌드용 개발 도구로 유지 — 소비자와 무관.

## 검증

- 배치 검증: `Sandbox`에서 EditMode/PlayMode 전수 테스트 통과
- 신규 프로젝트 재현: `Package/tools/verify-install.sh` — 빈 Unity 프로젝트에 로컬 git URL로 설치 → 컴파일 에러 0 확인

## 잔여 리스크

- **Git URL 해석 경로**: 로컬 검증은 `file://` git URL(로컬 클론)로 시뮬레이션한다. GitHub https + 인증 경로는 최초 배포 시 실제 저장소에서 1회 확인한다.
- `Library/PackageCache`의 git 패키지 잠금은 `packages-lock.json`에 해시로 기록된다 — 태그 재배포(동일 태그 재지정) 시 소비자가 캐시 삭제·재해석해야 할 수 있다. 매 배포는 새 태그로.
