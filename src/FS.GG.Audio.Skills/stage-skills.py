#!/usr/bin/env python3
"""Stage exactly the product skill files declared by the closed manifest."""

import hashlib
import json
from pathlib import Path
import re
import shutil
import stat
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]
MANIFEST = ROOT / "template/skill-manifest/skill-manifest.json"
SHA256 = re.compile(r"^[0-9a-f]{64}$")


def digest_bytes(raw: bytes) -> str:
    if raw.startswith(b"\xef\xbb\xbf"):
        raw = raw[3:]
    return hashlib.sha256(raw.replace(b"\r\n", b"\n")).hexdigest()


def fail(message: str) -> None:
    print(f"stage-skills: {message}", file=sys.stderr)
    raise SystemExit(2)


def unique_json_object(pairs: list[tuple[str, object]]) -> dict:
    result = {}
    for key, value in pairs:
        if key in result:
            fail(f"manifest contains duplicate JSON key {key!r}")
        result[key] = value
    return result


def relative_path(value: object, label: str, *, trailing_slash: bool = False) -> Path:
    if not isinstance(value, str):
        fail(f"{label}: path must be a string")
    text = value[:-1] if trailing_slash and value.endswith("/") else value
    if not text or text.startswith("/") or "\\" in text or any(
        part in ("", ".", "..") for part in text.split("/")
    ) or any(ord(char) < 32 for char in text):
        fail(f"{label}: invalid relative path {value!r}")
    return Path(text)


def source_files(source: Path, skill_id: str) -> dict[str, str]:
    actual = {}
    for path in source.rglob("*"):
        rel = path.relative_to(source).as_posix()
        mode = path.lstat().st_mode
        if stat.S_ISLNK(mode):
            fail(f"{skill_id}: source contains symlink {rel!r}")
        if stat.S_ISDIR(mode):
            continue
        if not stat.S_ISREG(mode):
            fail(f"{skill_id}: source contains non-regular file {rel!r}")
        actual[rel] = digest_bytes(path.read_bytes())
    return actual


def selected_skills(doc: dict) -> list[tuple[str, Path, dict[str, str]]]:
    selected = []
    ids = set()
    for row in doc.get("skills", []):
        if not isinstance(row, dict):
            fail("skill row must be an object")
        if row.get("scope") != "product":
            continue
        skill_id = row.get("id")
        if not isinstance(skill_id, str) or relative_path(skill_id, "skill id").as_posix() != skill_id or "/" in skill_id:
            fail(f"invalid skill id {skill_id!r}")
        if skill_id.casefold() in ids:
            fail(f"duplicate skill id {skill_id!r}")
        ids.add(skill_id.casefold())

        rel_source = relative_path(row.get("supplied-by"), f"{skill_id} supplied-by", trailing_slash=True)
        source = ROOT / rel_source
        for parent in (ROOT / Path(*rel_source.parts[:n]) for n in range(1, len(rel_source.parts) + 1)):
            if parent.is_symlink():
                fail(f"{skill_id}: supplied-by contains symlink")
        if not source.is_dir() or ROOT not in source.resolve().parents:
            fail(f"{skill_id}: supplied-by is not a repository directory")

        declared = {}
        folded = set()
        for item in row.get("files", []):
            if not isinstance(item, dict):
                fail(f"{skill_id}: file row must be an object")
            rel = relative_path(item.get("path"), f"{skill_id} file").as_posix()
            if rel.casefold() in folded:
                fail(f"{skill_id}: duplicate or case-colliding path {rel!r}")
            folded.add(rel.casefold())
            sha = item.get("sha256")
            if not isinstance(sha, str) or not SHA256.fullmatch(sha):
                fail(f"{skill_id}: invalid file digest for {rel!r}")
            declared[rel] = sha
        if source_files(source, skill_id) != declared:
            fail(f"{skill_id}: source files do not match closed manifest")
        if "SKILL.md" not in declared or declared["SKILL.md"] != row.get("sha256"):
            fail(f"{skill_id}: legacy body digest does not match files entry")
        selected.append((skill_id, source, declared))
    if not selected:
        fail("manifest declares no product skills")
    return selected


def stage(out: Path, manifest_bytes: bytes, selected: list[tuple[str, Path, dict[str, str]]]) -> None:
    if out == ROOT or out in ROOT.parents or any(
        out == source or out in source.parents or source in out.parents for _, source, _ in selected
    ):
        fail("output overlaps repository root or a source skill")
    out.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=f".{out.name}.stage-", dir=out.parent) as temporary:
        temporary = Path(temporary)
        staged = temporary / "payload"
        staged.mkdir()
        (staged / "skill-manifest.json").write_bytes(manifest_bytes)
        for skill_id, source, declared in selected:
            for rel, expected in declared.items():
                source_file = source / rel
                if source_file.is_symlink() or not source_file.is_file():
                    fail(f"{skill_id}: source changed during staging: {rel!r}")
                raw = source_file.read_bytes()
                if digest_bytes(raw) != expected:
                    fail(f"{skill_id}: source changed during staging: {rel!r}")
                target = staged / "skills" / skill_id / rel
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(raw)
                if digest_bytes(target.read_bytes()) != expected:
                    fail(f"{skill_id}: staged bytes differ: {rel!r}")
        if MANIFEST.read_bytes() != manifest_bytes:
            fail("manifest changed during staging")
        backup = temporary / "previous"
        if out.exists():
            out.rename(backup)
        try:
            staged.rename(out)
        except OSError:
            if backup.exists():
                backup.rename(out)
            raise


def main() -> None:
    if len(sys.argv) != 2:
        fail("usage: stage-skills.py <output-dir>")
    output_arg = Path(sys.argv[1]).absolute()
    if output_arg.is_symlink():
        fail("output must not be a symlink")
    out = output_arg.resolve()
    if any(path.is_symlink() for path in (ROOT / "template", MANIFEST.parent, MANIFEST)):
        fail("manifest path must not contain a symlink")
    manifest_bytes = MANIFEST.read_bytes()
    try:
        doc = json.loads(manifest_bytes, object_pairs_hook=unique_json_object)
    except (UnicodeError, json.JSONDecodeError) as error:
        fail(f"invalid manifest JSON: {error}")
    if not isinstance(doc, dict) or doc.get("schemaVersion") != 2:
        fail("manifest must use schemaVersion 2")
    stage(out, manifest_bytes, selected_skills(doc))


if __name__ == "__main__":
    main()
