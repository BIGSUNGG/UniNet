# UniNet UPM 배포 (Git URL)

- **상태**: 확정
- **결정**: 배포 채널 Git URL, 기반 스택 DLL 동봉 (ADR-0021)

## 소비자 설치

```json
"com.ds.uninet": "https://github.com/BIGSUNGG/UniNet.git?path=/Package#v0.1.0"
```

- Unity `6000.0+` 필요. 의존성 전부 패키지 동봉 — 추가 설정 없음.
- 저장소는 **공개** — 소비자 인증 불필요. (사설 전환 시: Unity가 git clone에 로컬 git 자격증명을 사용한다 — 에디터 실행 계정에서 `git clone <url>`이 성공하면 UPM 설치도 성공)

## CI/CD (GitHub Actions — `.github/workflows/upm.yml`)

- **CI (push/PR→main)** — `Package/tools/validate-package.sh` 정적 검증: package.json 유효성·DLL 15종+메타·RoslynAnalyzer 라벨·UPM 표준 구조·NuGetForUnity 부재·주소 플레이스홀더 부재. Unity 라이선스 불필요 (Unity 테스트는 로컬 배치·verify-install.sh 게이트 유지)
- **CD (v* 태그 푸시)** — 태그↔package.json 버전↔`Package/CHANGELOG.md` 섹션 정합 검사 통과 시 GitHub Release 자동 생성 (해당 버전 섹션이 릴리스 노트). 태그 자체가 UPM 배포다

## 배포 절차 (태그→푸시뿐)

최초 설정은 완료됨 (2026-09-28): 저장소 [BIGSUNGG/UniNet](https://github.com/BIGSUNGG/UniNet) 공개 운영, 플레이스홀더 치환·CI/CD 구축 완료.

매 배포:

1. `Package/package.json`의 `version` 상향 + `Package/CHANGELOG.md` `## X.Y.Z` 섹션 추가 + `Document/changelog.md` 기록
2. 커밋 → 태그 (`git tag vX.Y.Z`) → 푸시 (`git push && git push --tags`) → CI 검증 통과 시 Release 자동 생성
3. 태그 재지정 금지 — 소비자 `packages-lock.json`이 해시로 고정하므로 수정 배포는 항상 새 태그로

## 기반 스택 버전 갱신 (개발 시)

```bash
Package/tools/update-dlls.sh          # C:/Projects/DS/unity-nuget의 nupkg에서 DLL 추출·복사 + UniNet.CodeGenerator 빌드
```

- 버전은 스크립트 상단에서 고정한다. 복사 후 Unity 리프레시 → 테스트 → 커밋.
- `Sandbox/NuGet.config`(로컬 소스)은 UniNet.CodeGenerator 빌드용 개발 도구로 유지 — 소비자와 무관.

## 검증

- 배치 검증: `Sandbox`에서 EditMode/PlayMode 전수 테스트 통과
- 신규 프로젝트 재현: `Package/tools/verify-install.sh` — 빈 Unity 프로젝트에 로컬 git URL로 설치 → 컴파일 에러 0 확인 (file://+`.git` 접미사로 git 전송 경로 실측)
- 로컬 정적 검증: `Package/tools/validate-package.sh [--tag vX.Y.Z]` (CI와 동일 스크립트)

## 잔여 리스크

- GitHub https 경로의 태그 설치는 CI가 아닌 실제 소비자 프로젝트에서 1회 확인할 것 (로컬은 file://로 실측 완료).
