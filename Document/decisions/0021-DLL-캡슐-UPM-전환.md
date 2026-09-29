# 0021. 기반 스택 DLL 캡슐 UPM 전환 — Git URL 배포 확정

- **상태**: 승인됨
- **날짜**: 2026-09-28
- **결정자**: 사용자 (프로젝트 소유자)
- **대체**: [[0003-패키지-참조-고정]]의 NuGetForUnity 연결 방식, [[0005-개발-환경-샌드박스-upm]]의 NuGetForUnity 채택 분량

## 배경 (Context)

- UPM 배포 준비에서 소비자가 기반 스택(DRPC·MessageProtocol·Communication)을 어떻게 받을지가 블로커였다.
- 기존: NuGetForUnity + DS 로컬 NuGet 소스(`C:/Projects/DS/unity-nuget`) — 소비자 머신 경로에 의존해 배포 상태에서 재현 불가.
- 후보: 사설 NuGet 피드 / netstandard2.1 DLL 패키지 동봉 / com.ds.* UPM 재포장.
- 배포 채널: GitHub **Git URL** (`…/UniNet.git?path=/Package`) 확정. (2026-09-28: 저장소 공개 전환 — [BIGSUNGG/UniNet](https://github.com/BIGSUNGG/UniNet), 소비자 인증 불필요)

## 결정 (Decision)

1. **DLL 캡슐 UPM**: 기반 스택 netstandard2.1 DLL(및 소스 생성기 3종)을 `Package/` 안에 Git 커밋로 동봉한다. 소비자 추가 설정 제로 — git URL 한 줄로 전부 설치된다.
2. **개발 형태 = 배포 형태**: Sandbox도 패키지 내 동봉 DLL을 소비한다. NuGetForUnity·packages.config·NuGet.config(Assets)·`.locked` 아티팩트 철거. (`Sandbox/NuGet.config` 루트는 UniNet.CodeGenerator 빌드용 개발 도구로 유지)
3. 배치 배포: 버전 태그 → 푸시. 절차·소비자 설치·갱신 스크립트: [[deployment]]

## 구현 (2026-09-28)

- `Package/Runtime/Dependencies/` — lib DLL 12종 (DRPC 3.5.0 · MessageProtocol 3.2.0 · Communication 2.7.0 · LiteNetLib 2.1.4 · BouncyCastle 2.7.0)
- `Package/Runtime/UniNet.Unity/Analyzers/` — 소스 생성기 3종 (DRPC·MessageProtocol·UniNet.CodeGenerator 0.2.1), RoslynAnalyzer 라벨·전 플랫폼 비활성
- 갱신 스크립트 `Package/tools/update-dlls.sh`, 신규 프로젝트 검증 `Package/tools/verify-install.sh`
- 검증: EditMode 76/76 · PlayMode 17/17 통과, 신규 프로젝트 임포트·컴파일(샘플 포함) PASS

## 결과 (Consequences)

- **긍정**: 소비자 경험 "사용은 간단하게" 극대화 — 피드·자격증명 없음. 개발/배포 드리프트 원천 차단.
- **부담/리스크**: 기반 스택 업데이트마다 DLL 재동봉·커밋 필요(스크립트로 자동화). DLL 바이너리가 Git 히스토리에 축적된다(~5MB/스냅샷, netstandard2.1 한정 — 수용).
- **폐기 방법**: 사설 NuGet 피드 도입 시 이 ADR을 대체하고 [[0003-패키지-참조-고정]]·[[0005-개발-환경-샌드박스-upm]]·README·[[deployment]] 갱신.
