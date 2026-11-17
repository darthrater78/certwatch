#!/usr/bin/env bash
# CertWatch build: the one script local builds, CI and releases all run.
#   scripts/build.sh test      build the solution (Release) and run the tests
#   scripts/build.sh publish   self-contained single-file win-x64 build, zipped into artifacts/
#   scripts/build.sh all       both (default)
# Works on Linux (the WPF app cross-compiles via EnableWindowsTargeting) and Windows (Git Bash).
set -euo pipefail

cd "$(dirname "$0")/.."
target="${1:-all}"
csproj="src/CertWatch.App/CertWatch.App.csproj"
version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$csproj" | head -1)"
[ -n "$version" ] || { echo "No <Version> in $csproj" >&2; exit 1; }

run_tests() {
  dotnet build CertWatch.slnx --configuration Release
  dotnet test CertWatch.slnx --configuration Release --no-build
}

publish() {
  rm -rf artifacts/win-x64
  dotnet publish "$csproj" --configuration Release --runtime win-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --output artifacts/win-x64
  local zip="artifacts/CertWatch-v${version}-win-x64.zip"
  rm -f "$zip"
  # python3 is on both CI runners and dev machines; no zip tool needed.
  python3 -I -c 'import sys, zipfile
with zipfile.ZipFile(sys.argv[1], "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    z.write(sys.argv[2], "CertWatch.exe")' "$zip" artifacts/win-x64/CertWatch.exe
  echo "Built $zip"
}

case "$target" in
  test) run_tests ;;
  publish) publish ;;
  all) run_tests; publish ;;
  *) echo "usage: scripts/build.sh [test|publish|all]" >&2; exit 2 ;;
esac
