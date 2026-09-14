#!/usr/bin/env python3
"""Generate the closed FS.GG.Audio product-skill manifest."""
import hashlib
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "template/skill-manifest/skill-manifest.json"
ROWS = [
    (
        "fs-gg-browser-audio",
        "template/product-skills/fs-gg-browser-audio",
        "template == fable-game",
    ),
]

def digest(path: Path) -> str:
    raw = path.read_bytes()
    if raw.startswith(b"\xef\xbb\xbf"):
        raw = raw[3:]
    return hashlib.sha256(raw.replace(b"\r\n", b"\n")).hexdigest()

skills = []
for skill_id, source, predicate in ROWS:
    directory = ROOT / source
    files = [
        {"path": str(path.relative_to(directory)).replace("\\", "/"), "sha256": digest(path)}
        for path in sorted(directory.rglob("*"))
        if path.is_file()
    ]
    body = directory / "SKILL.md"
    skills.append(
        {
            "id": skill_id,
            "scope": "product",
            "sha256": digest(body),
            "resolvablePath": f".agents/skills/{skill_id}/SKILL.md",
            "materializes-when": predicate,
            "supplied-by": source + "/",
            "files": files,
        }
    )

rendered = json.dumps({"schemaVersion": 2, "skills": skills}, indent=2) + "\n"
if "--check" in sys.argv:
    if not OUT.exists() or OUT.read_text() != rendered:
        print("skill-manifest: STALE — run scripts/generate-skill-manifest.py", file=sys.stderr)
        raise SystemExit(1)
    print(f"skill-manifest: up to date ({len(skills)} skill)")
else:
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(rendered)
    print(f"skill-manifest: wrote {OUT} ({len(skills)} skill)")

