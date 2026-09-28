#!/usr/bin/env bash
# 기반 스택 DLL을 nupkg에서 추출해 패키지에 동봉한다 (ADR-0021 — DLL 캡슐 UPM).
# 사용: Package/tools/update-dlls.sh
# 요구: unzip, dotnet SDK (UniNet.CodeGenerator 빌드), 로컬 NuGet 소스(C:/Projects/DS/unity-nuget)
set -euo pipefail

NUGET_SOURCE="C:/Projects/DS/unity-nuget"                 # DS 로컬 NuGet 소스
PKG_ROOT="$(cd "$(dirname "$0")/.." && pwd)"               # Package/
DEPS="$PKG_ROOT/Runtime/Dependencies"
ANALYZERS="$PKG_ROOT/Runtime/UniNet.Unity/Analyzers"
REPO_ROOT="$(cd "$PKG_ROOT/.." && pwd)"

# 기반 스택 버전 고정 — 갱신 시 여기만 바꾼다
declare -A VERSIONS=(
  [Communication.Network.RUDP.Client]=2.7.0
  [Communication.Network.RUDP.Server]=2.7.0
  [Communication.Network.RUDP.Shared]=2.7.0
  [Communication.Shared]=2.7.0
  [DRPC.Attribute]=3.5.0
  [DRPC.Client]=3.5.0
  [DRPC.Server]=3.5.0
  [DRPC.Shared]=3.5.0
  [DRPC.CodeGenerator]=3.5.0
  [MessageProtocol]=3.2.0
  [MessageProtocol.CodeGenerator]=3.2.0
  [MessageProtocol.Core]=3.2.0
  [LiteNetLib]=2.1.4
  [BouncyCastle.Cryptography]=2.7.0
)

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

extract() {  # extract <pkg> <inner-path> <dest-dir>
  local pkg="$1" inner="$2" dest="$3"
  local nupkg="$NUGET_SOURCE/$pkg.${VERSIONS[$pkg]}.nupkg"
  [ -f "$nupkg" ] || { echo "MISSING: $nupkg"; exit 1; }
  unzip -o -j -q "$nupkg" "$inner" -d "$dest"
}

echo "== lib DLL → $DEPS"
for pkg in Communication.Network.RUDP.Client Communication.Network.RUDP.Server Communication.Network.RUDP.Shared Communication.Shared DRPC.Attribute DRPC.Client DRPC.Server DRPC.Shared LiteNetLib MessageProtocol MessageProtocol.Core; do
  extract "$pkg" "lib/netstandard2.1/*.dll" "$DEPS"
done
extract BouncyCastle.Cryptography "lib/netstandard2.0/*.dll" "$DEPS"

echo "== 소스 생성기 → $ANALYZERS"
extract DRPC.CodeGenerator "analyzers/dotnet/cs/*.dll" "$ANALYZERS"
extract MessageProtocol.CodeGenerator "analyzers/dotnet/cs/*.dll" "$ANALYZERS"

echo "== UniNet.CodeGenerator 빌드"
dotnet build "$REPO_ROOT/CodeGenerator/UniNet.CodeGenerator.csproj" -c Release
cp "$REPO_ROOT/CodeGenerator/bin/Release/netstandard2.0/UniNet.CodeGenerator.dll" "$ANALYZERS/"

echo "완료 — Unity 리프레시 후 EditMode/PlayMode 테스트, 이후 커밋. (.meta의 RoslynAnalyzer 라벨은 기존 파일명 유지 시 자동 승계)"
