#!/usr/bin/env bash
set -euo pipefail
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
work="$(mktemp -d "${TMPDIR:-/tmp}/web-audio-browser.XXXXXX")"
feed="$work/feed"
tools="$work/tools"
packages="$work/packages"
mkdir -p "$feed" "$tools" "$packages"
export NUGET_PACKAGES="$packages"
dotnet pack "$repo/src/FS.GG.Audio.Core/FS.GG.Audio.Core.fsproj" -c Release -o "$feed" -p:Version=0.5.0-svg-present.1
dotnet pack "$repo/src/FS.GG.Audio.WebBrowser/FS.GG.Audio.WebBrowser.fsproj" -c Release -o "$feed" -p:Version=0.5.0-svg-present.1
cp -R "$repo/tests/WebAudio.Browser.Tests" "$work/Browser"
python3 - "$work/Browser" <<'PY'
import pathlib,shutil,sys
p=pathlib.Path(sys.argv[1])
for name in ('node_modules','dist','generated','obj','bin'):
    target=p/name
    if target.exists(): shutil.rmtree(target)
PY
cat > "$work/NuGet.Config" <<CONFIG
<configuration><packageSources><clear/><add key="candidate" value="$feed"/><add key="nuget" value="https://api.nuget.org/v3/index.json"/></packageSources><packageSourceMapping><packageSource key="candidate"><package pattern="FS.GG.Audio.*"/></packageSource><packageSource key="nuget"><package pattern="*"/></packageSource></packageSourceMapping></configuration>
CONFIG
dotnet restore "$work/Browser/BrowserFixture.fsproj" --configfile "$work/NuGet.Config"
dotnet tool install fable --version 5.17.0 --tool-path "$tools" --configfile "$work/NuGet.Config"
"$tools/fable" "$work/Browser/BrowserFixture.fsproj" --outDir "$work/Browser/generated" --noCache
npm ci --prefix "$work/Browser"
npm run --prefix "$work/Browser" build
evidence_prefix="${1:-$work/evidence/web-audio}"
mkdir -p "$(dirname "$evidence_prefix")"
for family in chromium firefox webkit; do node "$work/Browser/browser-test.mjs" --browser "$family" --out "$evidence_prefix.$family.json"; done
