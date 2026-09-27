#!/usr/bin/env python3
"""Black-box source-closure controls for the skills package stager."""

import hashlib
import importlib.util
import io
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
from contextlib import redirect_stderr

STAGER = Path(__file__).resolve().parents[2] / "src/FS.GG.Audio.Skills/stage-skills.py"


def run(script: Path, output: Path, expected: int) -> None:
    result = subprocess.run([sys.executable, str(script), str(output)], capture_output=True, text=True)
    assert result.returncode == expected, (result.returncode, result.stdout, result.stderr)


with tempfile.TemporaryDirectory() as temporary:
    root = Path(temporary)
    script = root / "src/FS.GG.Audio.Skills/stage-skills.py"
    script.parent.mkdir(parents=True)
    shutil.copyfile(STAGER, script)
    source = root / "template/product-skills/example"
    source.mkdir(parents=True)
    body = source / "SKILL.md"
    body.write_text("# Example\n", encoding="utf-8")
    sha = hashlib.sha256(body.read_bytes()).hexdigest()
    row = {
        "id": "example",
        "scope": "product",
        "supplied-by": "template/product-skills/example/",
        "sha256": sha,
        "files": [{"path": "SKILL.md", "sha256": sha}],
    }
    manifest = root / "template/skill-manifest/skill-manifest.json"
    manifest.parent.mkdir(parents=True)

    def write_manifest() -> None:
        manifest.write_text(json.dumps({"schemaVersion": 2, "skills": [row]}), encoding="utf-8")

    output = root / "package"
    write_manifest()
    run(script, output, 0)
    assert sorted(p.relative_to(output).as_posix() for p in output.rglob("*") if p.is_file()) == [
        "skill-manifest.json", "skills/example/SKILL.md"
    ]
    sentinel = output / "previous.txt"
    sentinel.write_text("previous package", encoding="utf-8")
    original_staged_manifest = (output / "skill-manifest.json").read_bytes()

    # Duplicate JSON members make the packaged manifest ambiguous across consumers. The pack
    # target invokes the stager directly, so it must refuse even when a separate generator check
    # would notice that the checked-in manifest changed.
    duplicate_skills = (
        '{"schemaVersion":2,"skills":[{"id":"phantom","scope":"product"}],"skills":'
        + json.dumps([row]) + "}"
    )
    good_row_json = json.dumps(row)
    assert '"path": "SKILL.md"' in good_row_json
    duplicate_path = good_row_json.replace(
        '"path": "SKILL.md"', '"path": "../outside", "path": "SKILL.md"'
    )
    for ambiguous in (
        duplicate_skills,
        '{"schemaVersion":2,"skills":[' + duplicate_path + "]}",
    ):
        manifest.write_text(ambiguous, encoding="utf-8")
        run(script, output, 2)
        assert sentinel.read_text(encoding="utf-8") == "previous package"
        assert (output / "skill-manifest.json").read_bytes() == original_staged_manifest
    write_manifest()

    outside = root / "outside"
    outside.mkdir()
    (outside / "not-in-manifest.txt").write_text("unmanifested", encoding="utf-8")
    directory_link = source / "extra"
    directory_link.symlink_to(outside, target_is_directory=True)
    run(script, output, 2)
    assert sentinel.read_text(encoding="utf-8") == "previous package"
    assert not (output / "skills/example/extra/not-in-manifest.txt").exists()
    directory_link.unlink()

    file_link = source / "outside.txt"
    file_link.symlink_to(outside / "not-in-manifest.txt")
    run(script, output, 2)
    assert sentinel.exists()
    file_link.unlink()

    cycle = source / "cycle"
    cycle.symlink_to(source, target_is_directory=True)
    run(script, output, 2)
    assert sentinel.exists()
    cycle.unlink()

    undeclared = source / "undeclared.txt"
    undeclared.write_text("extra", encoding="utf-8")
    run(script, output, 2)
    assert sentinel.exists()
    undeclared.unlink()

    row["files"].append({"path": "skill.md", "sha256": sha})
    write_manifest()
    run(script, output, 2)
    assert sentinel.exists()
    row["files"].pop()

    row["files"].append({"path": "../outside/not-in-manifest.txt", "sha256": sha})
    write_manifest()
    run(script, output, 2)
    assert sentinel.exists()
    row["files"].pop()

    row.pop("sha256")
    row["files"] = []
    write_manifest()
    run(script, output, 2)
    assert sentinel.exists()

    row["sha256"] = sha
    row["files"] = [{"path": "SKILL.md", "sha256": sha}]
    write_manifest()
    original_manifest = manifest.read_bytes()
    spec = importlib.util.spec_from_file_location("synthetic_skill_stager", script)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    selected = module.selected_skills(json.loads(original_manifest))
    manifest.write_text('{"schemaVersion":999,"skills":[]}', encoding="utf-8")
    try:
        with redirect_stderr(io.StringIO()):
            module.stage(output, original_manifest, selected)
    except SystemExit as error:
        assert error.code == 2
    else:
        raise AssertionError("changed manifest was published")
    assert sentinel.exists()

    manifest.unlink()
    manifest.symlink_to(outside / "not-in-manifest.txt")
    run(script, output, 2)
    assert sentinel.exists()

print("stage-skills source-closure controls passed")
