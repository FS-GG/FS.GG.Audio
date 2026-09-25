#!/usr/bin/env python3
"""Independent black-box byte and path controls for provisional F# skill policy."""

import hashlib
import json
import subprocess
import tempfile
from pathlib import Path

root = Path(__file__).resolve().parents[2]
command = ["dotnet", str(root / "src/FS.GG.Audio.SkillPolicy/bin/Release/net10.0/FS.GG.Audio.SkillPolicy.dll")]
passed = 0


def call(*args):
    return subprocess.run(command + list(args), text=True, capture_output=True)


def expect_digest(raw, canonical):
    global passed
    result = call("digest", raw.hex())
    assert result.returncode == 0, result.stderr
    assert result.stdout.strip() == hashlib.sha256(canonical).hexdigest()
    passed += 1


expect_digest(b"\xef\xbb\xbfA\r\nB\rC\n", b"A\nB\rC\n")
expect_digest(b"\xef\xbb\xbf\xef\xbb\xbfX", b"\xef\xbb\xbfX")
expect_digest(b"\x00\xff\r\n\x80", b"\x00\xff\n\x80")
expect_digest(b"\r", b"\r")
assert call("digest", b"line\r\n".hex()).stdout == call("digest", b"line\n".hex()).stdout
passed += 1
assert call("digest", b"line\r".hex()).stdout != call("digest", b"line\n".hex()).stdout
passed += 1

for path in ("SKILL.md", "nested/example.md"):
    result = call("check-path", path)
    assert result.returncode == 0 and result.stdout.strip() == "ok", (path, result.stdout)
    passed += 1
for path in ("", "../outside", "a/../b", "a/./b", "a//b", "/absolute", "a/", "C:\\escape", "a\\b", "a\nother"):
    result = call("check-path", path)
    assert result.returncode == 2 and result.stdout.startswith("error:"), (path, result.stdout)
    passed += 1

with tempfile.TemporaryDirectory() as folder:
    payload = Path(folder) / "case.json"

    def closure(declared, observed):
        global passed
        payload.write_text(json.dumps({"declared": declared, "observed": observed}))
        result = call("check-closure", str(payload))
        assert result.returncode in (0, 2), result.stderr
        passed += 1
        return result.returncode, json.loads(result.stdout)

    manifest = json.loads((root / "template/skill-manifest/skill-manifest.json").read_text())
    row = next(item for item in manifest["skills"] if item["id"] == "fs-gg-browser-audio")
    source = root / row["supplied-by"]
    observed = [
        {"path": item["path"], "hex": (source / item["path"]).read_bytes().hex(), "regular": True}
        for item in row["files"]
    ]
    declared = [{"path": item["path"], "sha256": item["sha256"]} for item in row["files"]]
    assert row["sha256"] == next(item["sha256"] for item in declared if item["path"] == "SKILL.md")
    code, result = closure(declared, observed)
    assert code == 0 and result == {"ok": True, "diagnostics": []}

    def refused(d, o, expected):
        code, result = closure(d, o)
        assert code == 2 and not result["ok"] and any(
            diagnostic.startswith(expected) for diagnostic in result["diagnostics"]
        ), result

    refused(declared, observed + [{"path": "extra.md", "hex": "41", "regular": True}], "undeclared:")
    refused(declared, [], "missing:")
    refused([], [], "empty-declared")
    readme = {"path": "README.md", "hex": "41", "regular": True}
    readme_digest = hashlib.sha256(b"A").hexdigest()
    refused([{"path": "README.md", "sha256": readme_digest}], [readme], "missing-body")
    refused(declared, [{**observed[0], "hex": "41"}], "digest-mismatch:")
    refused(declared, [{**observed[0], "regular": False}], "not-regular:")
    refused(declared + declared, observed, "duplicate-declared:")
    refused(declared, observed + [{**observed[0], "path": "skill.md"}], "case-collision-observed:")
    unicode_aliases = ["Straße.md", "Strasse.md"]
    aliases_declared = declared + [{"path": path, "sha256": readme_digest} for path in unicode_aliases]
    aliases_observed = observed + [{"path": path, "hex": "41", "regular": True} for path in unicode_aliases]
    refused(aliases_declared, aliases_observed, "declared:unicode-casefold-unqualified:")
    refused([{**declared[0], "path": "../SKILL.md"}], observed, "declared:unsafe-component:")
    refused([{**declared[0], "path": "bad\x00name"}], observed, "declared:control-character:")
    refused([{**declared[0], "sha256": "bad"}], observed, "invalid-digest:")
    refused([{**declared[0], "sha256": None}], observed, "invalid-digest:")
    payload.write_text('{"declared": "wrong type", "observed": []}')
    malformed = call("check-closure", str(payload))
    assert malformed.returncode == 2 and json.loads(malformed.stdout)["diagnostics"] == ["invalid-input"]
    passed += 1
    payload.write_text('{"declared": [], "observed": [{"path": "SKILL.md"}]}')
    missing_field = call("check-closure", str(payload))
    assert missing_field.returncode == 2 and json.loads(missing_field.stdout)["diagnostics"] == ["invalid-input"]
    passed += 1
    for duplicate in (
        '{"declared": "ignored", "declared": [], "observed": []}',
        '{"declared": [{"path": "ignored", "path": "SKILL.md", "sha256": "' + declared[0]["sha256"] + '"}], "observed": []}',
    ):
        payload.write_text(duplicate)
        rejected = call("check-closure", str(payload))
        assert rejected.returncode == 2 and json.loads(rejected.stdout)["diagnostics"] == ["invalid-input"]
        passed += 1

print(f"audio F# skill policy black-box controls: {passed} passed")
