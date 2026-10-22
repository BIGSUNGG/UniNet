#!/usr/bin/env bash
# 공개 태그 푸시 후 실행하는 게이트 — 태그 재지정 금지 규약상 수정은 항상 새 태그로 배포할 것.
# 빈 Unity 프로젝트에 공개 GitHub URL(태그 고정)로 com.ds.uninet을 설치해 임포트·컴파일을 재현하는 검증.
# 사용: Package/tools/verify-install-public.sh [TAG=Package/package.json version]
# 결과: 콘솔 PASS/FAIL + 로그 경로.
#  - 실행 전 git ls-remote로 원격 태그 존재를 확인하고, manifest에 소비자 계약 URL을 주입한다.
#  - 임포트 후 PackageCache에서 샘플(BasicsSample.cs)을 추출해 재임포트 — 태그가 배포한 내용 그대로 컴파일 검증.
#  - packages-lock.json에 com.ds.uninet이 git 소스(github.com/BIGSUNGG/UniNet)로 기록됐는지 단언(file:// 폴백 오판 차단).
# 주의: 네트워크(원격 태그 조회·git URL 설치)·Unity 경로 의존은 verify-install.sh와 동일. PackageCache 폴더명(com.ds.uninet@*) 형식에 의존.
set -euo pipefail

UNITY="C:/Program Files/Unity/Hub/Editor/6000.0.83f1/Editor/Unity.exe"
PKG_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
REPO_ROOT="$(cd "$PKG_ROOT/.." && pwd)"
REPO_URL="https://github.com/BIGSUNGG/UniNet.git"

TAG="${1:-}"
if [ -z "$TAG" ]; then
	TAG="$(grep -o '"version": *"[^"]*"' "$PKG_ROOT/package.json" | head -1 | cut -d'"' -f4 || true)"
fi
if [ -z "$TAG" ]; then
	echo "FAIL: TAG 인자가 없고 package.json version 파싱도 실패 — 태그를 인자로 지정할 것"
	exit 1
fi
[[ "$TAG" != v* ]] && TAG="v$TAG"   # repo tags are v-prefixed (package.json version is bare) — normalizes both the default and an explicit bare arg (r30)

WORK="$(mktemp -d -t uninet-verify-public-XXXX)"
WIN_WORK="$(cygpath -m "$WORK")" # Unity(managed)가 이해하는 Windows 경로
FRESH="$WORK/FreshProject"       # 소비자 역할 (빈 프로젝트)
trap 'echo "작업 디렉터리 유지: $WORK"' EXIT

echo "== 원격 태그 확인: $TAG @ $REPO_URL"
if ! REMOTE_TAG="$(git ls-remote --tags "$REPO_URL" "refs/tags/$TAG")" || [ -z "$REMOTE_TAG" ]; then
	echo "FAIL: 원격에 태그 없음: $TAG — 태그 푸시(→Release) 후 실행할 것"
	exit 1
fi
TAG_HASH="$(printf '%s\n' "$REMOTE_TAG" | cut -f1)"
COMMIT_HASH="$(git ls-remote "$REPO_URL" "refs/tags/$TAG^{}" | cut -f1 || true)" # 주석 태그면 커밋 해시 별도 조회
echo "태그: $TAG  해시: $TAG_HASH${COMMIT_HASH:+ (커밋: $COMMIT_HASH)}"

echo "== 빈 프로젝트 구성"
mkdir -p "$FRESH/Packages" "$FRESH/Assets"
cp -r "$REPO_ROOT/Sandbox/ProjectSettings" "$FRESH/ProjectSettings"
# clone 대신 소비자 계약 URL(공개 git URL+태그)을 그대로 주입
cat >"$FRESH/Packages/manifest.json" <<EOF
{
  "dependencies": {
    "com.ds.uninet": "${REPO_URL}?path=/Package#${TAG}"
  }
}
EOF

run_unity() { # $1 = 로그 경로
	"$UNITY" -batchmode -quit -nographics -projectPath "$WIN_WORK/FreshProject" -logFile "$1"
}

check_log() { # $1 = 로그 경로 — 컴파일 에러·패키지 해석 판정 (exit 코드는 호출부, 설치 흔적은 설치 로그에서만)
	local LOG="$1"
	if grep -qE "error CS[0-9]+" "$LOG"; then
		echo "FAIL: 컴파일 에러"
		grep -E "error CS[0-9]+" "$LOG" | head -5
		FAIL=1
	fi
	if grep -q "Cannot resolve\|invalid dependencies\|failed to resolve" "$LOG"; then
		echo "FAIL: 패키지 해석 실패"
		grep -iE "cannot resolve|invalid dependencies|failed to resolve" "$LOG" | head -3
		FAIL=1
	fi
}

LOG2="" # 샘플 추출 실패 시 미생성 — set -u 대비

echo "== Unity 배치 임포트 1/2 — 공개 URL 설치 (수 분 소요)"
LOG1="$WORK/unity-install.log"
EXIT=0
run_unity "$LOG1" || EXIT=$?

echo "== 결과 판정 (설치 임포트)"
FAIL=0
if [ $EXIT -ne 0 ]; then
	echo "FAIL: Unity exit $EXIT"
	FAIL=1
fi
if ! grep -q "com.ds.uninet" "$LOG1"; then
	echo "FAIL: com.ds.uninet 흔적 없음 (설치 안 됨?)"
	FAIL=1
fi
check_log "$LOG1"

echo "== PackageCache에서 샘플 추출 → 재임포트 (태그가 배포한 내용 그대로 검증)"
SAMPLE_SRC=""
for f in "$FRESH"/Library/PackageCache/com.ds.uninet@*/Samples~/Basics/BasicsSample.cs; do
	if [ -f "$f" ]; then
		SAMPLE_SRC="$f"
		break
	fi
done
if [ -z "$SAMPLE_SRC" ]; then
	echo "FAIL: PackageCache에 샘플 없음 (com.ds.uninet@*/Samples~/Basics/BasicsSample.cs)"
	FAIL=1
else
	cp "$SAMPLE_SRC" "$FRESH/Assets/"
	echo "== Unity 배치 임포트 2/2 — 샘플 컴파일 (수 분 소요)"
	LOG2="$WORK/unity-sample.log"
	EXIT=0
	run_unity "$LOG2" || EXIT=$?
	echo "== 결과 판정 (샘플 재임포트)"
	if [ $EXIT -ne 0 ]; then
		echo "FAIL: Unity exit $EXIT"
		FAIL=1
	fi
	check_log "$LOG2"
fi

echo "== packages-lock 단언 (git 소스 기록 확인)"
LOCK="$FRESH/Packages/packages-lock.json"
if [ ! -f "$LOCK" ]; then
	echo "FAIL: packages-lock.json 없음"
	FAIL=1
else
	LOCK_ENTRY="$(grep -A10 '"com.ds.uninet"' "$LOCK" || true)"
	if [ -z "$LOCK_ENTRY" ]; then
		echo "FAIL: packages-lock.json에 com.ds.uninet 기록 없음"
		FAIL=1
	elif printf '%s\n' "$LOCK_ENTRY" | grep -q "file://"; then
		echo "FAIL: packages-lock.json이 file:// 소스로 기록됨 — 공개 URL 설치 아님"
		FAIL=1
	elif ! printf '%s\n' "$LOCK_ENTRY" | grep -q "github.com/BIGSUNGG/UniNet"; then
		echo "FAIL: packages-lock.json 소스가 github.com/BIGSUNGG/UniNet 아님"
		FAIL=1
	elif ! printf '%s\n' "$LOCK_ENTRY" | grep -q '"source": *"git"'; then
		echo "FAIL: packages-lock.json source 타입이 git 아님"
		FAIL=1
	else
		LOCK_HASH="$(printf '%s\n' "$LOCK_ENTRY" | grep -oE '"hash": *"[^"]+"' | head -1 || true)"
		echo "LOCK: $LOCK_HASH"
	fi
fi

if [ $FAIL -eq 0 ]; then
	echo "PASS: 공개 태그 $TAG 설치·컴파일 성공 (샘플 포함) — 로그: $LOG1, $LOG2"
else
	echo "상세 로그: $LOG1${LOG2:+ $LOG2}"
	exit 1
fi
