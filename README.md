# UniNet

상용 유니티 게임 서버용 네트워크 프레임워크 라이브러리. Unity `MonoBehaviour`를 기준으로
RPC 호출과 변수 리플리케이션을 제공하고, 언리얼 Network Framework의 기능 수준을 목표로 한다.

> 원칙 — **"사용은 간단하게, 기능은 강력하게"**

## 저장소 구조

| 경로 | 내용 |
| --- | --- |
| `Package/` | UniNet UPM 패키지 (`com.ds.uninet`) — 라이브러리 본체 |
| `Sandbox/` | Unity 6000.0.83f1(6.0 LTS) 샌드박스 프로젝트 — 스파이크·데모·테스트 (폐기 가능) |
| `Document/` | Obsidian Vault — 정의·구조·플랜·결정 기록 (SSoT) |

## 개발 환경

- 샌드박스가 패키지를 로컬 참조한다: `Sandbox/Packages/manifest.json` → `"com.ds.uninet": "file:../../Package"`
- 기반 스택(DRPC·MessageProtocol·Communication)은 NuGetForUnity 4.5.0 + 로컬 소스 `../unity-nuget/`로 로드
- sln/csproj는 Unity가 asmdef 기준 자동 생성 (직접 작성하지 않는다)
- 배치 검증: `-batchmode -quit -nographics -disable-assembly-updater`

문서: [Document/00-INDEX.md](Document/00-INDEX.md) · 환경 결정: [Document/decisions/0005-개발-환경-샌드박스-upm.md](Document/decisions/0005-개발-환경-샌드박스-upm.md)
