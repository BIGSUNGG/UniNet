# UniNet UPM 배포 (Git URL)

- **상태**: 확정
- **결정**: 배포 채널 Git URL, 기반 스택 DLL 동봉 (ADR-0021)

## 소비자 설치

```json
"com.ds.uninet": "https://github.com/BIGSUNGG/UniNet.git?path=/Package#v0.24.0"
```

- Unity `6000.0+` 필요. 의존성 전부 패키지 동봉 — 추가 설정 없음.
- 저장소는 **공개** — 소비자 인증 불필요. (사설 전환 시: Unity가 git clone에 로컬 git 자격증명을 사용한다 — 에디터 실행 계정에서 `git clone <url>`이 성공하면 UPM 설치도 성공)

## 매치 배치 모델 — 프로세스당 매치 1개 (2026-11-02 확정)

UniNet의 서버 런타임은 프로세스 전역(`UniNetEnvironment` 정적)이라 **하나의 유니티 서버 프로세스가 하나의 매치/월드**를 담당한다. 이는 확정된 배포 모델이지 제약이 아니다 — Unity 데디케이티드 서버 오케스트레이션(Agones·GameLift·PlayFab MPS 등)이 전제하는 표준 토폴로지와 일치하며, 장애 격리·메모리/GC 회수·동시 재시작이 프로세스 경계에서 가장 확실히 풀린다.

- **1매치 = 1프로세스**: 매치메이커가 매치 생성 시 유니티 서버 빌드 프로세스를 기동하고(포트 할당·환경변수로 매치 파라미터 전달), `AllClientsSceneLoaded`·`ClientDisconnected`·게임 종료 판정으로 매치 종료를 판정해 `ServerStopAsync` 후 프로세스를 종료한다.
- **우아한 종료**: `ServerStopAsync`는 모든 클라이언트에 ServerShutdown 사유 통보→플러시→리슨 해제 순서를 보장한다(ADR-0027) — 오케스트레이터의 종료 신호(SIGTERM) 핸들러에서 호출할 것.
- **헬스체크**: `UniNetEnvironment.PendingMainActions`·`DroppedMainActions`·`server.TrafficStats`·연결 수로 프로세스 건강을 노출할 수 있다(카운터는 Interlocked — 어떤 스레드에서도 읽기 가능).
- **다중 인스턴스(한 프로세스에서 여러 매치)는 미지원** — 필요해지면 연결별 씬·스코프 인스턴스화와 통합 설계로 별도 ADR을 연다(2026-11-02 상용화 검토 참조). 매치당 상태가 작으면 프로세스 기동 비용이 지배적이므로, 실측 전까지 밀도 최적화는 금물이다.

## CI/CD (GitHub Actions — `.github/workflows/upm.yml`)

- **CI (push/PR→main)** — `Package/tools/validate-package.sh` 정적 검증(package.json·DLL 15종+메타·RoslynAnalyzer 라벨·UPM 구조·NuGetForUnity/주소 플레이스홀더 부재), 설치 스크립트 허미틱 회귀, 생성기 소스 빌드와 source-built/동봉 analyzer 양 경로 headless 회귀. Unity 라이선스 불필요 (Unity 테스트·신규 설치는 로컬 배치 게이트 유지)
- **CD (v* 태그 푸시)** — 태그↔package.json 버전↔`Package/CHANGELOG.md` 섹션 정합 검사 통과 시 GitHub Release 자동 생성 (해당 버전 섹션이 릴리스 노트). 태그 자체가 UPM 배포다

## 배포 절차 (태그→푸시뿐)

최초 설정은 완료됨 (2026-09-28): 저장소 [BIGSUNGG/UniNet](https://github.com/BIGSUNGG/UniNet) 공개 운영, 플레이스홀더 치환·CI/CD 구축 완료.

매 배포:

1. `Package/package.json`의 `version` 상향 + `Package/CHANGELOG.md` `## X.Y.Z` 섹션 추가 + `Document/changelog.md` 기록
2. 커밋 → 새 태그 (`git tag vX.Y.Z`) → main과 해당 태그만 명시적으로 푸시 (`git push origin main && git push origin vX.Y.Z`) → CI 검증 통과 시 Release 자동 생성
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
- 공개 태그 게이트 (배포 절차 2의 태그 푸시→Release **후** 실행): `Package/tools/verify-install-public.sh [TAG=package.json version]` — `git ls-remote`로 원격 태그 존재 확인 → 소비자 계약 URL(`https://github.com/BIGSUNGG/UniNet.git?path=/Package#TAG`)로 빈 프로젝트 설치 → PackageCache에서 샘플 추출·재임포트 컴파일 → `packages-lock.json`이 git 소스(github.com/BIGSUNGG/UniNet)로 git 소스로 기록됐는지 단언(해시는 로그 에코). PASS면 배포 완료. 태그 재지정 금지 — 수정 배포는 항상 새 태그
- 로컬 정적 검증: `Package/tools/validate-package.sh [--tag vX.Y.Z]` (CI와 동일 스크립트)

## v0.23.0 배포 검증·영수증 (2026-10-29)

- 제품·테스트 기준 커밋: `7272d73964965a63e7ef23a1ee2a1f405810ffe9`. 기존 A/B/C/D 리뷰 원문의 첫 줄 CLEAN·실행 종료 metadata·동결 diff와 실제 커밋의 일치를 재확인했다. 사용자 선택에 따라 추가 reviewer는 호출하지 않았다.
- 새 실행: EditMode **330통과/0실패/1기존 Ignore**(도구 종합 판정 `uncertain` — 전체 통과로 바꾸지 않음), PlayMode **50/50**, 생성기 source-built/동봉 각각 **49/49**, 설치 스크립트 허미틱 **4/4**, 태그 정적 검증 및 로컬 Git 신규 설치·샘플 컴파일 PASS.
- 원시 증거: `.pi-glla/releases/v0.23.0-20261029/`의 `local-gates.json`·`existing-clean-verification.json`·실행 로그/XML. 루트 LSP의 Roslyn 참조 미해결은 실제 csproj 빌드 오류와 구분하며, 직접 빌드는 오류 0개(기존 NU5128 경고 1개)다.
- README·패키지 문서·소비자 SKILL 설치 URL을 `#v0.23.0`으로 통일했다. 이번 세션의 제품 코드·동봉 DLL 변경은 없다. 기존 `.gitignore`·하네스 타임스탬프 변경은 커밋에서 제외한다.
- 후속 D02(`AllClientsSceneLoaded` 구독자 격리)·D03(`PumpMain` 호출당 처리 예산)는 별도 큐에 남으며 이번 릴리스에는 포함하지 않는다. 기본 핸드셰이크 버전 상승에 대한 양단 업데이트 안내는 [[migration-v0-23]] 참조.
- **게시 완료**: `v0.23.0` → `1d124ec0d639e4e44dabba0b924e146dd8583ac1`(제품 기준 이후 설치 안내만 수정한 커밋), [GitHub Release](https://github.com/BIGSUNGG/UniNet/releases/tag/v0.23.0) 게시 시각 `2026-10-29T08:10:11Z`. [main CI](https://github.com/BIGSUNGG/UniNet/actions/runs/40840129785)·[tag CI](https://github.com/BIGSUNGG/UniNet/actions/runs/40840133181) 모두 success, 원격 패키지 버전 `0.23.0` 확인. 태그는 재지정하지 않았다.
- **공개 설치 완료**: `bash Package/tools/verify-install-public.sh v0.23.0` PASS — 빈 프로젝트가 실제 공개 URL로 설치한 패키지와 그 PackageCache의 Basics 샘플을 컴파일했다. `packages-lock.json`의 `source=git`·정확한 `#v0.23.0` URL·`hash=1d124ec0d639e4e44dabba0b924e146dd8583ac1`을 추가 단언했다. `public-install-verification.json`·`install-public-*.log`·CI JSON/원문 로그를 위 증거 경로에 보존했다.
- 세션 시작 대비 Sandbox 추적 파일과 기존 사용자 변경 **334경로 SHA256 동일**. 별도 docs-only 커밋으로 이 영수증을 main에 기록하며, `v0.23.0`의 패키지 내용은 그대로 유지한다.

## 잔여 리스크

- 없음 (2026-09-29 해소) — GitHub https + `#v0.1.0` 태그 설치를 실제 소비자 URL로 배치 검증: 신규 프로젝트 클론·컴파일 에러 0·샘플 포함 PASS
