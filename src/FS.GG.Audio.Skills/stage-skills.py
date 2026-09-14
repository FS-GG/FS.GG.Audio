#!/usr/bin/env python3
import hashlib
import json
from pathlib import Path
import shutil
import sys

ROOT = Path(__file__).resolve().parents[2]
MANIFEST = ROOT / "template/skill-manifest/skill-manifest.json"

def digest(path: Path) -> str:
    raw = path.read_bytes()
    if raw.startswith(b"\xef\xbb\xbf"):
        raw = raw[3:]
    return hashlib.sha256(raw.replace(b"\r\n", b"\n")).hexdigest()

def fail(message: str) -> None:
    print(f"stage-skills: {message}", file=sys.stderr)
    raise SystemExit(2)

if len(sys.argv) != 2:
    fail("usage: stage-skills.py <output-dir>")
out = Path(sys.argv[1]).resolve()
doc = json.loads(MANIFEST.read_text())
if doc.get("schemaVersion") != 2:
    fail("manifest must use schemaVersion 2")
shutil.rmtree(out, ignore_errors=True)
out.mkdir(parents=True)
shutil.copyfile(MANIFEST, out / "skill-manifest.json")
for row in doc.get("skills", []):
    if row.get("scope") != "product":
        continue
    skill_id = row["id"]
    if "/" in skill_id or skill_id in (".", ".."):
        fail(f"invalid skill id {skill_id!r}")
    source = (ROOT / row["supplied-by"]).resolve()
    if ROOT not in source.parents:
        fail(f"{skill_id}: supplied-by escapes repository")
    actual = {
        str(path.relative_to(source)).replace("\\", "/"): digest(path)
        for path in source.rglob("*") if path.is_file()
    }
    declared = {item["path"]: item["sha256"] for item in row.get("files", [])}
    if actual != declared:
        fail(f"{skill_id}: source files do not match closed manifest")
    if declared.get("SKILL.md") != row.get("sha256"):
        fail(f"{skill_id}: legacy body digest does not match files entry")
    target = out / "skills" / skill_id
    shutil.copytree(source, target)

