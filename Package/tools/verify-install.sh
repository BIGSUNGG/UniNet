#!/usr/bin/env bash
# 신규(빈) Unity 프로젝트에 로컬 git URL로 com.ds.uninet을 설치해 임포트·컴파일을 재현하는 검증.
# 사용: Package/tools/verify-install.sh [저장소경로=자동]
# 결과: 콘솔 PASS/FAIL + 로그 경로. 샘플(BasicsSample.cs)도 같이 컴파일해 배포물 그대로를 검증한다.
# 주의: 커밋된 상태를 clone해 검증한다 — 배포 전 커밋 이후에 실행할 것.
set -euo pipefail

UNITY="C:/Program Files/Unity/Hub/Editor/6000.0.83f1/Editor/Unity.exe"
PKG_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
REPO="${1:-$(cd "$PKG_ROOT/.." && pwd)}"

WORK="$(mktemp -d -t uninet-verify-XXXX)"
WIN_WORK="$(cygpath -m "$WORK")"          # Unity(managed)가 이해하는 Windows 경로
SIM="$WORK/upm-repo-sim.git"      # GitHub 역할 (커밋된 저장소 상태) — .git 접미사로 git URL로 인식시킨다
FRESH="$WORK/FreshProject"    # 소비자 역할 (빈 프로젝트)
trap 'echo "작업 디렉터리 유지: $WORK"' EXIT

echo "== 저장소 클론 시뮬레이션: $REPO"
git clone -q "$REPO" "$SIM"

echo "== 빈 프로젝트 구성"
mkdir -p "$FRESH/Packages" "$FRESH/Assets"
cp -r "$REPO/Sandbox/ProjectSettings" "$FRESH/ProjectSettings"
cat > "$FRESH/Packages/manifest.json" <<EOF
{
  "dependencies": {
    "com.ds.uninet": "file://${WIN_WORK}/upm-repo-sim.git?path=/Package"
  }
}
EOF
# 샘플 소스도 소비자 Assets에 넣어 컴파일 검증 (Samples~는 Unity 임포트 대상이 아니므로)
cp "$PKG_ROOT/Samples~/Basics/BasicsSample.cs" "$FRESH/Assets/"

echo "== Unity 배치 임포트 (수 분 소요)"
LOG="$WORK/unity.log"
"$UNITY" -batchmode -quit -nographics -projectPath "$WIN_WORK/FreshProject" -logFile "$LOG"
EXIT=$?

echo "== 결과 판정"
FAIL=0
if [ $EXIT -ne 0 ]; then echo "FAIL: Unity exit $EXIT"; FAIL=1; fi
if grep -qE "error CS[0-9]+" "$LOG"; then
  echo "FAIL: 컴파일 에러"; grep -E "error CS[0-9]+" "$LOG" | head -5; FAIL=1
fi
if grep -q "Cannot resolve\|invalid dependencies\|failed to resolve" "$LOG"; then
  echo "FAIL: 패키지 해석 실패"; grep -iE "cannot resolve|invalid dependencies|failed to resolve" "$LOG" | head -3; FAIL=1
fi
if ! grep -q "com.ds.uninet" "$LOG"; then
  echo "FAIL: com.ds.uninet 흔적 없음 (설치 안 됨?)"; FAIL=1
fi

if [ $FAIL -eq 0 ]; then
  echo "PASS: 신규 프로젝트 임포트·컴파일 성공 (샘플 포함) — 로그: $LOG"
else
  echo "상세 로그: $LOG"
  exit 1
fi
