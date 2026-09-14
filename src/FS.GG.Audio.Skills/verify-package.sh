#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
python3 "$root/scripts/generate-skill-manifest.py" --check
python3 "$here/stage-skills.py" "$work/stage"
dotnet pack "$here/FS.GG.Audio.Skills.csproj" -c Release -o "$work/out" --no-restore
nupkg="$work/out/FS.GG.Audio.Skills.0.1.0.nupkg"
test -f "$nupkg"
unzip -q "$nupkg" -d "$work/unpacked"
python3 - "$work/unpacked/audio-skills" <<'PY'
import hashlib, json, pathlib, sys
root = pathlib.Path(sys.argv[1])
doc = json.loads((root / "skill-manifest.json").read_text())
assert doc["schemaVersion"] == 2
assert [s["id"] for s in doc["skills"]] == ["fs-gg-browser-audio"]
for row in doc["skills"]:
    base = root / "skills" / row["id"]
    actual = {}
    for path in base.rglob("*"):
        if path.is_file():
            raw = path.read_bytes()
            if raw.startswith(b"\xef\xbb\xbf"):
                raw = raw[3:]
            actual[str(path.relative_to(base)).replace("\\", "/")] = hashlib.sha256(raw.replace(b"\r\n", b"\n")).hexdigest()
    declared = {f["path"]: f["sha256"] for f in row["files"]}
    assert actual == declared
    assert declared["SKILL.md"] == row["sha256"]
PY
echo "FS.GG.Audio.Skills package verified"
