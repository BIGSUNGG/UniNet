#!/usr/bin/env bash
# Hermetic regression for Package/tools/verify-install.sh result handling (AUDIT-20261027-F04).
#
# What it exercises: the REAL script's Unity-invocation + result-judgment block, extracted VERBATIM
# from Package/tools/verify-install.sh by anchor (never re-modeled here), executed under the same
# `set -euo pipefail` options against a fake Unity executable inside a throwaway temp directory.
#
# Regression being guarded: `set -e` must not abort the script on Unity's non-zero exit before the
# exit code is captured — the FAIL summary, the compiler-error scan and the detailed-log path must
# all still run. No real Unity, no git clone, no network, no repository/user assets are touched.
#
# Usage: bash Tests/PackageTools/run-tests.sh   (exit 0 = all scenarios green)

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SCRIPT="$REPO_ROOT/Package/tools/verify-install.sh"

# ── extract the real block: everything from the log-path assignment to end of script ──
# The anchor is the exact assignment line in the real script; if the script is restructured the
# harness must fail loudly (anchor not found) instead of passing vacuously.
BLOCK="$(sed -n '/^LOG="\$WORK\/unity\.log"$/,$p' "$SCRIPT")"
if [ -z "$BLOCK" ]; then
	echo "FAIL: anchor 'LOG=\"\$WORK/unity.log\"' not found in $SCRIPT — extraction impossible"
	exit 1
fi
PASS_COUNT=0
TOTAL=0
FAILED=0
WORK_ROOT="$(mktemp -d -t uninet-package-tests-XXXXXX)"
trap 'rm -rf -- "$WORK_ROOT"' EXIT

# Builds a fake Unity executable at $1 that writes the given log lines (harness stdin) to the file
# passed via -logFile, then exits with $2. This stubs ONLY Unity; the tested code is the real block.
make_unity_stub() {
	local target="$1" code="$2"
	{
		echo '#!/usr/bin/env bash'
		echo '# Fake Unity stub (hermetic test): writes the -logFile argument, then exits with the scripted code.'
		cat <<'STUB'
log=""
prev=""
for a in "$@"; do
	[ "$prev" = "-logFile" ] && log="$a"
	prev="$a"
done
cat <<'UNINET_TEST_LOG' > "$log"
STUB
		cat # literal fixture text; never execute log lines as shell commands
		echo 'UNINET_TEST_LOG'
		echo "exit $code"
	} >"$target"
	chmod +x "$target"
}

# Runs the REAL extracted block in a fresh sandbox with the given fake Unity stub.
# A fresh Bash process is essential: invoking a shell function on the left of || suppresses errexit
# even in its subshells. The child must independently enforce the real script's -e behavior.
run_block() {
	local sandbox="$1" stub="$2"
	WORK="$sandbox" WIN_WORK="$sandbox" UNITY="$stub" \
		bash -euo pipefail -c "$BLOCK" >"$sandbox/out.txt" 2>&1
}

check() { # label, expected_rc, expected_grep, sandbox
	local label="$1" want_rc="$2" want_grep="$3" sandbox="$4"
	TOTAL=$((TOTAL + 1))
	local rc=0
	run_block "$sandbox" "$sandbox/unity.sh" || rc=$?
	local out
	out="$(cat "$sandbox/out.txt")"
	if [ "$rc" -ne "$want_rc" ]; then
		echo "[FAIL] $label — exit $rc, expected $want_rc"
		echo "$out" | sed 's/^/       /'
		FAILED=1
		return
	fi
	if ! grep -qF "$want_grep" "$sandbox/out.txt"; then
		echo "[FAIL] $label — output missing '$want_grep'"
		echo "$out" | sed 's/^/       /'
		FAILED=1
		return
	fi
	PASS_COUNT=$((PASS_COUNT + 1))
	echo "[PASS] $label"
}

# ── scenario 1: fake Unity exits 23 — FAIL summary + detailed log path + exit 1 (the F04 core) ──
S1="$WORK_ROOT/s1"
mkdir "$S1"
printf '%s\n' \
	"[Package] com.ds.uninet@v0.23.0 imported" \
	"Import took 12.3s" |
	make_unity_stub "$S1/unity.sh" 23
check "fake Unity exit 23 → FAIL + detailed log path + exit 1" 1 "FAIL: Unity exit 23" "$S1"
grep -qF "상세 로그" "$S1/out.txt" || {
	echo "[FAIL] fake Unity exit 23 — detailed log path line missing"
	FAILED=1
}
[ -f "$S1/unity.log" ] || {
	echo "[FAIL] fake Unity exit 23 — log file not written"
	FAILED=1
}

# ── scenario 2: exit 0, healthy log → PASS ──
S2="$WORK_ROOT/s2"
mkdir "$S2"
printf '%s\n' \
	"[Package] com.ds.uninet@v0.23.0 imported" \
	"Compilation succeeded" \
	"Import took 9.8s" |
	make_unity_stub "$S2/unity.sh" 0
check "fake Unity exit 0, healthy log → PASS" 0 "PASS:" "$S2"

# ── scenario 3: exit 0 but the log carries a compiler error → FAIL with the CS line echoed ──
S3="$WORK_ROOT/s3"
mkdir "$S3"
printf '%s\n' \
	"[Package] com.ds.uninet@v0.23.0 imported" \
	"Assets/BasicsSample.cs(10,5): error CS1002: ; expected" |
	make_unity_stub "$S3/unity.sh" 0
check "fake Unity exit 0 + compiler error → FAIL" 1 "FAIL: 컴파일 에러" "$S3"
grep -qF "error CS1002" "$S3/out.txt" || {
	echo "[FAIL] compiler-error scenario — offending line not surfaced"
	FAILED=1
}

# ── scenario 4: exit 0 but no com.ds.uninet trace in the log → FAIL (install never happened) ──
S4="$WORK_ROOT/s4"
mkdir "$S4"
printf '%s\n' \
	"Compilation succeeded" \
	"Import took 5.1s" |
	make_unity_stub "$S4/unity.sh" 0
check "fake Unity exit 0 + missing com.ds.uninet trace → FAIL" 1 "FAIL: com.ds.uninet 흔적 없음" "$S4"

echo
echo "$PASS_COUNT core scenarios green ($TOTAL checks incl. sub-assertions) — $(basename "$SCRIPT") judgment block"
exit "$FAILED"
