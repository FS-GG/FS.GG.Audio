#!/usr/bin/env bash
set -euo pipefail
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
work="$(mktemp -d "${TMPDIR:-/tmp}/web-audio-portable.XXXXXX")"
feed="$work/feed"
tools="$work/tools"
packages="$work/packages"
mkdir -p "$feed" "$tools" "$packages"
export NUGET_PACKAGES="$packages"
cp -R "$repo/tests/WebAudio.PortableConsumers/DotNet" "$work/DotNet"
cp -R "$repo/tests/WebAudio.PortableConsumers/Fable" "$work/Fable"
cp "$repo/tests/WebAudio.PortableConsumers/Correspondence.fs" "$work/DotNet/Correspondence.fs"
cp "$repo/tests/WebAudio.PortableConsumers/Correspondence.fs" "$work/Fable/Correspondence.fs"
dotnet pack "$repo/src/FS.GG.Audio.Core/FS.GG.Audio.Core.fsproj" -c Release -o "$feed" -p:Version=0.5.0-svg-present.1
dotnet pack "$repo/src/FS.GG.Audio.WebBrowser/FS.GG.Audio.WebBrowser.fsproj" -c Release -o "$feed" -p:Version=0.5.0-svg-present.1
cat > "$work/NuGet.Config" <<CONFIG
<configuration>
  <packageSources><clear /><add key="candidate" value="$feed" /><add key="nuget" value="https://api.nuget.org/v3/index.json" /></packageSources>
  <packageSourceMapping><packageSource key="candidate"><package pattern="FS.GG.Audio.*" /></packageSource><packageSource key="nuget"><package pattern="*" /></packageSource></packageSourceMapping>
</configuration>
CONFIG
dotnet restore "$work/DotNet/DotNet.fsproj" --configfile "$work/NuGet.Config"
dotnet run --project "$work/DotNet/DotNet.fsproj" --no-restore -- "$work/dotnet.txt"
dotnet restore "$work/Fable/Fable.fsproj" --configfile "$work/NuGet.Config"
dotnet tool install fable --version 5.17.0 --tool-path "$tools" --configfile "$work/NuGet.Config"
"$tools/fable" "$work/Fable/Fable.fsproj" --outDir "$work/javascript" --noCache
node "$work/javascript/Program.js" > "$work/fable.txt"
cmp "$work/dotnet.txt" "$work/fable.txt"
digest="$(sha256sum "$work/dotnet.txt" | cut -d' ' -f1)"
python3 - "$feed" <<'PY'
import pathlib,sys,zipfile
feed=pathlib.Path(sys.argv[1])
for package,expected in (
    ('FS.GG.Audio.Core.0.5.0-svg-present.1.nupkg',{'fable/FS.GG.Audio.Core.fsproj','fable/Audio.fsi','fable/Audio.fs'}),
    ('FS.GG.Audio.WebBrowser.0.5.0-svg-present.1.nupkg',{'fable/FS.GG.Audio.WebBrowser.fsproj','fable/WebAudio.fsi','fable/WebAudio.fs'})):
    with zipfile.ZipFile(feed/package) as archive:
        actual={name for name in archive.namelist() if name.startswith('fable/')}
        if actual != expected: raise SystemExit(f'{package}: unexpected Fable view {sorted(actual)}')
PY
echo "web-audio-portable: runtimes=dotnet,fable-node sha256=$digest native-openal-dependency=absent"
