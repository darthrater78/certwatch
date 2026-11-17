#!/usr/bin/env bash
# CertWatch build: the one script local builds, CI and releases all run.
#   scripts/build.sh test      build the solution (Release) and run the tests
#   scripts/build.sh publish   self-contained single-file win-x64 exe → artifacts/CertWatch-v<version>-win-x64.exe
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
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true --output artifacts/win-x64
  # The exe is shipped as-is (already compressed inside), named with the version for CI and releases.
  local exe="artifacts/CertWatch-v${version}-win-x64.exe"
  cp artifacts/win-x64/CertWatch.exe "$exe"
  echo "Built $exe"
}

case "$target" in
  test) run_tests ;;
  publish) publish ;;
  all) run_tests; publish ;;
  *) echo "usage: scripts/build.sh [test|publish|all]" >&2; exit 2 ;;
esac
