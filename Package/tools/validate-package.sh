#!/usr/bin/env bash
# com.ds.uninet 패키지 정적 검증 — CI(.github/workflows/upm.yml)와 로컬 공용.
# 사용: validate-package.sh [--tag vX.Y.Z]
#   --tag  태그 배포 모드 — package.json 버전·CHANGELOG 섹션 정합까지 검사 (CD에서 사용)
set -uo pipefail

PKG_ROOT="$(cd "$(dirname "$0")/.." && pwd)" # Package/
REPO="$(cd "$PKG_ROOT/.." && pwd)"
FAIL=0
say() { echo "$*"; }
bad() {
	echo "FAIL: $*"
	FAIL=1
}
PY="$(command -v python3 || command -v python)"

# --- package.json ---
PJ="$PKG_ROOT/package.json"
[ -f "$PJ" ] || {
	echo "FAIL: package.json 없음"
	exit 1
}
say "== package.json"
"$PY" - "$PJ" <<'EOF'
import json, sys
p = json.load(open(sys.argv[1], encoding="utf-8"))
ok = True
if p.get("name") != "com.ds.uninet": print("FAIL: name != com.ds.uninet"); ok = False
v = str(p.get("version", ""))
if len(v.split(".")) != 3: print(f"FAIL: version '{v}' not semver"); ok = False
if "unity" not in p: print("FAIL: unity 필드 없음"); ok = False
if not p.get("samples"): print("FAIL: samples 없음"); ok = False
sys.exit(0 if ok else 1)
EOF
if [ $? -ne 0 ]; then FAIL=1; fi

# --- DLL 동봉 (ADR-0021) ---
say "== DLL 동봉"
LIB_COUNT=0
LIB_META=0
for f in "$PKG_ROOT/Runtime/Dependencies/"*.dll; do
	[ -e "$f" ] || continue
	LIB_COUNT=$((LIB_COUNT + 1))
	[ -f "$f.meta" ] || LIB_META=$((LIB_META + 1))
done
[ "$LIB_COUNT" -eq 12 ] || bad "lib DLL ${LIB_COUNT}종 (기대 12)"
[ "$LIB_META" -eq 0 ] || bad "lib DLL 중 .meta 없는 것 ${LIB_META}개"
GEN_COUNT=0
for f in "$PKG_ROOT/Runtime/UniNet.Unity/Analyzers/"*.dll; do
	[ -e "$f" ] || continue
	GEN_COUNT=$((GEN_COUNT + 1))
	[ -f "$f.meta" ] || bad "$f .meta 없음"
	grep -q "RoslynAnalyzer" "$f.meta" || bad "$(basename "$f") RoslynAnalyzer 라벨 없음"
done
[ "$GEN_COUNT" -eq 3 ] || bad "소스젠 ${GEN_COUNT}종 (기대 3)"

# --- UPM 표준 구조 ---
say "== UPM 표준 구조"
for f in "Samples~/Basics/BasicsSample.cs" "Documentation~/index.md" "LICENSE.md" "CHANGELOG.md"; do
	[ -f "$PKG_ROOT/$f" ] || bad "$f 없음"
done

# --- NuGetForUnity 회귀 가드 (ADR-0021 — 철거 유지) ---
say "== NuGetForUnity 부재"
if grep -qi "nugetforunity" "$REPO/Sandbox/Packages/manifest.json"; then
	bad "Sandbox manifest에 nugetforunity 잔존"
fi

# --- 플레이스홀더 잔존 가드 ---
say "== 주소 플레이스홀더"
if grep -rq "[O]WNER/UniNet" "$REPO/README.md" "$REPO/Document" "$PKG_ROOT/Documentation~" 2>/dev/null; then
	bad "OWNER/UniNet 플레이스홀더 잔존"
fi

# --- 태그 정합 (--tag) ---
TAG="${2:-}"
if [ "${1:-}" = "--tag" ] || [ -n "$TAG" ]; then
	say "== 태그 정합 ($TAG)"
	[ -n "$TAG" ] || {
		echo "FAIL: --tag 값 없음"
		exit 1
	}
	VER="${TAG#v}"
	PJV=$("$PY" -c 'import json,sys;print(json.load(open(sys.argv[1],encoding="utf-8"))["version"])' "$PJ")
	[ "$PJV" = "$VER" ] || bad "package.json 버전 '$PJV' != 태그 '$VER'"
	grep -q "^## $VER\$" "$PKG_ROOT/CHANGELOG.md" || bad "Package/CHANGELOG.md에 '## $VER' 섹션 없음"
fi

echo "-----"
[ $FAIL -eq 0 ] && say "PASS: 패키지 정적 검증 통과" || {
	say "패키지 검증 실패 (위 FAIL 항목)"
	exit 1
}
