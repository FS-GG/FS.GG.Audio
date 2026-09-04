#!/usr/bin/env python3
"""Validate an append-only roadmap phase/time/token JSONL ledger."""

from __future__ import annotations

import argparse
import copy
import csv
import hashlib
import json
import re
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path

IDENTIFIER = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]*$")
LOWER_IDENTIFIER = re.compile(r"^[a-z0-9][a-z0-9._-]*$")
SHA = re.compile(r"^[0-9a-f]{40}$")
REPO = re.compile(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")
EVENTS = {"started", "completed", "blocked", "resumed"}
USAGE_FIELDS = [
    "timestamp", "task", "session_id", "thread_id", "turn_id", "response_id", "provider", "model", "effort",
    "runtime_version", "coordination_version", "sdd_version", "contracts_version", "ledger_schema",
    "input", "cached_input", "cache_write_input", "output", "reasoning", "total",
    "turn_input", "turn_cached_input", "turn_cache_write_input", "turn_output", "turn_reasoning",
    "turn_total", "thread_input", "thread_cached_input", "thread_cache_write_input", "thread_output",
    "thread_reasoning", "thread_total", "source",
]
FIELDS = {
    "schema_version", "run_id", "unit_id", "item", "sequence", "phase_order", "phase",
    "event", "at", "actor", "model", "source", "evidence", "actual_minutes",
    "historical_durations_minutes", "historical_average_minutes", "token_usage", "tooling",
    "revision", "previous_digest", "digest", "authority",
}


class InvalidLog(ValueError):
    pass


def fail(message: str) -> None:
    raise InvalidLog(message)


def utc(value: object, line: int) -> datetime:
    if not isinstance(value, str) or not re.fullmatch(r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z", value):
        fail(f"line {line}: at must be canonical UTC YYYY-MM-DDTHH:MM:SSZ")
    try:
        return datetime.strptime(value, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)
    except ValueError as error:
        fail(f"line {line}: invalid at timestamp: {error}")


def nonempty(value: object, label: str, line: int) -> str:
    if not isinstance(value, str) or not value.strip():
        fail(f"line {line}: {label} must be a non-empty string")
    return value


def canonical_digest(value: dict[str, object]) -> str:
    unsigned = {key: item for key, item in value.items() if key != "digest"}
    return hashlib.sha256(json.dumps(unsigned, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def tooling_fingerprint(value: dict[str, object]) -> str:
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def validate_tokens(value: object, terminal: bool, line: int,
                    usage_reports: dict[str, list[dict[str, str]]],
                    model: object, tooling: object,
                    expected_task: str,
                    verify_external_evidence: bool = True) -> None:
    if not isinstance(value, dict) or not isinstance(value.get("status"), str):
        fail(f"line {line}: token_usage must be an object with status")
    status = value["status"]
    if not terminal:
        if value != {"status": "pending"}:
            fail(f"line {line}: started/resumed token_usage must be exactly pending")
        return
    if status == "pending":
        fail(f"line {line}: terminal public events must be posted only after token reconciliation")
    elif status == "measured":
        expected = {"status", "input", "cached_input", "cache_write_input", "output", "reasoning",
                    "total", "source", "session_ids", "turn_ids"}
        if set(value) != expected:
            fail(f"line {line}: measured token_usage has missing or unexpected fields")
        counts = [value[name] for name in ("input", "cached_input", "cache_write_input", "output", "total")]
        if any(isinstance(count, bool) or not isinstance(count, int) or count < 0 for count in counts):
            fail(f"line {line}: measured token counts must be non-negative integers")
        if value["reasoning"] is not None and (isinstance(value["reasoning"], bool)
                                                or not isinstance(value["reasoning"], int)
                                                or value["reasoning"] < 0):
            fail(f"line {line}: measured reasoning must be a non-negative integer or null when unavailable")
        if value["total"] != value["input"] + value["output"]:
            fail(f"line {line}: measured token total must equal input + output")
        if value["cached_input"] + value["cache_write_input"] > value["input"]:
            fail(f"line {line}: measured cache counts exceed input")
        if value["reasoning"] is not None and value["reasoning"] > value["output"]:
            fail(f"line {line}: measured reasoning exceeds output")
        nonempty(value["source"], "token_usage.source", line)
        for field in ("session_ids", "turn_ids"):
            identifiers = value[field]
            if not isinstance(identifiers, list) or not identifiers or any(
                    not isinstance(item, str) or not item for item in identifiers):
                fail(f"line {line}: measured {field} must be a non-empty string array")
        if not verify_external_evidence:
            return
        usage_rows = usage_reports.get(value["source"])
        if usage_rows is None:
            fail(f"line {line}: measured token usage has no matching immutable --usage receipt digest")
        assert isinstance(model, dict) and isinstance(tooling, dict)
        selected = [row for row in usage_rows
                    if row.get("session_id") in value["session_ids"]
                    and row.get("turn_id") in value["turn_ids"]
                    and row.get("task") == expected_task]
        if not selected:
            fail(f"line {line}: measured token usage has no matching usage-report rows")
        for field in ("input", "cached_input", "cache_write_input", "output", "total"):
            try:
                observed = sum(int(row[field]) for row in selected)
            except (KeyError, ValueError):
                fail(f"line {line}: usage report has invalid {field}")
            if observed != value[field]:
                fail(f"line {line}: measured {field} does not equal usage-report sum ({observed})")
        reasoning_values = [row.get("reasoning", "") for row in selected]
        if value["reasoning"] is None:
            if any(reasoning_values):
                fail(f"line {line}: null reasoning conflicts with measured usage-report reasoning")
        else:
            try:
                observed_reasoning = sum(int(current) for current in reasoning_values)
            except ValueError:
                fail(f"line {line}: mixed available/unavailable reasoning rows cannot be aggregated")
            if observed_reasoning != value["reasoning"]:
                fail(f"line {line}: measured reasoning does not equal usage-report sum ({observed_reasoning})")
        expected_model = model.get("name") if model.get("status") == "recorded" else None
        expected_provider = model.get("provider") if model.get("status") == "recorded" else None
        expected_effort = model.get("effort", "") if model.get("status") == "recorded" else None
        for row in selected:
            if row.get("task") != expected_task:
                fail(f"line {line}: usage-report task is not bound to this item/phase")
            if expected_model is not None and (row.get("model") != expected_model
                                               or row.get("provider") != expected_provider
                                               or row.get("effort", "") != expected_effort):
                fail(f"line {line}: model does not match authoritative usage report")
            versions = {
                "runtime": row.get("runtime_version"), "coordination": row.get("coordination_version"),
                "sdd": row.get("sdd_version"), "contracts": row.get("contracts_version"),
            }
            for component, observed in versions.items():
                current = tooling.get(component)
                if isinstance(current, dict) and current.get("status") == "recorded" and current.get("version") != observed:
                    fail(f"line {line}: tooling.{component}.version does not match usage report")
    elif status == "unavailable":
        if set(value) != {"status", "reason", "source"}:
            fail(f"line {line}: unavailable token_usage has missing or unexpected fields")
        nonempty(value["reason"], "token_usage.reason", line)
        nonempty(value["source"], "token_usage.source", line)
    else:
        fail(f"line {line}: terminal token_usage must be pending, measured, or unavailable; estimates are forbidden")


def validate_model(value: object, line: int) -> None:
    if not isinstance(value, dict) or not isinstance(value.get("status"), str):
        fail(f"line {line}: model must be an object with status")
    if value["status"] == "recorded":
        if set(value) not in ({"status", "provider", "name", "source"},
                              {"status", "provider", "name", "effort", "source"}):
            fail(f"line {line}: recorded model has missing or unexpected fields")
        nonempty(value["provider"], "model.provider", line)
        nonempty(value["name"], "model.name", line)
        nonempty(value["source"], "model.source", line)
    elif value["status"] == "unavailable":
        if set(value) != {"status", "reason", "source"}:
            fail(f"line {line}: unavailable model has missing or unexpected fields")
        nonempty(value["reason"], "model.reason", line)
        nonempty(value["source"], "model.source", line)
    else:
        fail(f"line {line}: model status must be recorded or unavailable; inference is forbidden")


def validate_tooling(value: object, line: int) -> None:
    expected = {"ledger_schema", "runtime", "coordination", "sdd", "contracts"}
    if not isinstance(value, dict) or set(value) != expected or value.get("ledger_schema") != 1:
        fail(f"line {line}: tooling must contain ledger_schema 1 and all four tool components")
    for component in ("runtime", "coordination", "sdd", "contracts"):
        item = value[component]
        if not isinstance(item, dict) or not isinstance(item.get("status"), str):
            fail(f"line {line}: tooling.{component} must be a status object")
        status = item["status"]
        if status == "recorded":
            if set(item) != {"status", "name", "version", "source"}:
                fail(f"line {line}: recorded tooling.{component} has missing or unexpected fields")
            nonempty(item["name"], f"tooling.{component}.name", line)
            nonempty(item["version"], f"tooling.{component}.version", line)
            nonempty(item["source"], f"tooling.{component}.source", line)
        elif status in {"unavailable", "not_applicable"}:
            if set(item) != {"status", "name", "reason", "source"}:
                fail(f"line {line}: {status} tooling.{component} has missing or unexpected fields")
            nonempty(item["name"], f"tooling.{component}.name", line)
            nonempty(item["reason"], f"tooling.{component}.reason", line)
            nonempty(item["source"], f"tooling.{component}.source", line)
        else:
            fail(f"line {line}: tooling.{component}.status is invalid")


def validate_source(value: object, line: int) -> None:
    if not isinstance(value, dict):
        fail(f"line {line}: source must be an object")
    repository = nonempty(value.get("repository"), "source.repository", line)
    if not REPO.fullmatch(repository):
        fail(f"line {line}: source.repository must be owner/repo")
    has_revision = "revision" in value
    has_reason = "unavailable_reason" in value
    if has_revision == has_reason:
        fail(f"line {line}: source must contain exactly one of revision or unavailable_reason")
    expected = {"repository", "revision"} if has_revision else {"repository", "unavailable_reason"}
    if set(value) != expected:
        fail(f"line {line}: source has unexpected fields")
    if has_revision:
        revision = value["revision"]
        if not isinstance(revision, str) or not SHA.fullmatch(revision):
            fail(f"line {line}: source.revision must be a lowercase 40-hex commit")
    else:
        nonempty(value["unavailable_reason"], "source.unavailable_reason", line)


def validate_lines(records: list[object], run_id: str, unit_id: str,
                   require_terminal: bool = False, require_reconciled: bool = False,
                   required_phases: list[str] | None = None,
                   usage_reports: dict[str, list[dict[str, str]]] | None = None,
                   history_rows: list[dict[str, str]] | None = None,
                   verify_external_evidence: bool = True) -> None:
    if not records:
        fail("log is empty")
    if not LOWER_IDENTIFIER.fullmatch(run_id):
        fail("run id must be lowercase and path-safe")
    if not IDENTIFIER.fullmatch(unit_id):
        fail("unit id must be path-safe")

    item_identity: tuple[str, int, str] | None = None
    phases: dict[str, dict[str, object]] = {}
    active: set[str] = set()
    blocked: set[str] = set()
    previous_digest: str | None = None

    for index, raw in enumerate(records, 1):
        if not isinstance(raw, dict):
            fail(f"line {index}: entry must be a JSON object")
        if set(raw) != FIELDS:
            fail(f"line {index}: entry has missing or unexpected fields")
        if raw["schema_version"] != 1:
            fail(f"line {index}: schema_version must be 1")
        if raw["run_id"] != run_id or raw["unit_id"] != unit_id:
            fail(f"line {index}: run_id/unit_id does not match validator arguments")
        if raw["sequence"] != index:
            fail(f"line {index}: sequence must be contiguous and equal line number")
        if raw["revision"] != index:
            fail(f"line {index}: revision must be contiguous and equal line number")
        if raw["previous_digest"] != previous_digest:
            fail(f"line {index}: previous_digest does not bind the preceding event")
        digest = raw["digest"]
        if not isinstance(digest, str) or not re.fullmatch(r"[0-9a-f]{64}", digest):
            fail(f"line {index}: digest must be lowercase sha256")
        if digest != canonical_digest(raw):
            fail(f"line {index}: digest does not match canonical event bytes")
        previous_digest = digest

        authority = raw["authority"]
        if not isinstance(authority, dict) or set(authority) != {"kind", "subject", "claim_generation"}:
            fail(f"line {index}: authority must bind GitHub issue subject and claim generation")
        if authority.get("kind") != "github_issue_comment":
            fail(f"line {index}: live authority must be an append-only GitHub issue comment")
        nonempty(authority.get("subject"), "authority.subject", index)
        nonempty(authority.get("claim_generation"), "authority.claim_generation", index)

        item = raw["item"]
        if not isinstance(item, dict) or set(item) != {"repo", "number", "url"}:
            fail(f"line {index}: item must contain exactly repo, number, and url")
        repo = item["repo"]
        number = item["number"]
        url = item["url"]
        if not isinstance(repo, str) or not REPO.fullmatch(repo):
            fail(f"line {index}: item.repo must be owner/repo")
        if isinstance(number, bool) or not isinstance(number, int) or number <= 0:
            fail(f"line {index}: item.number must be a positive integer")
        if url != f"https://github.com/{repo}/issues/{number}":
            fail(f"line {index}: item.url must be the canonical GitHub issue URL")
        if authority["subject"] != f"{repo}#{number}":
            fail(f"line {index}: authority.subject must equal the canonical item")
        current_item = (repo, number, url)
        if item_identity is None:
            item_identity = current_item
        elif item_identity != current_item:
            fail(f"line {index}: item identity changed within the ledger")

        phase = raw["phase"]
        event = raw["event"]
        order = raw["phase_order"]
        if not isinstance(phase, str) or not LOWER_IDENTIFIER.fullmatch(phase):
            fail(f"line {index}: phase must be a lowercase path-safe identifier")
        if event not in EVENTS:
            fail(f"line {index}: unknown event")
        nonempty(raw["actor"], "actor", index)
        validate_model(raw["model"], index)
        validate_tooling(raw["tooling"], index)
        validate_source(raw["source"], index)
        evidence = raw["evidence"]
        if not isinstance(evidence, list) or not evidence or any(not isinstance(v, str) or not v.strip() for v in evidence):
            fail(f"line {index}: evidence must be a non-empty string array")

        timestamp = utc(raw["at"], index)
        terminal_event = event in {"completed", "blocked"}
        validate_tokens(raw["token_usage"], terminal_event, index, usage_reports or {},
                        raw["model"], raw["tooling"], f"{repo}#{number}/{phase}",
                        verify_external_evidence)
        history = raw["historical_durations_minutes"]
        average = raw["historical_average_minutes"]
        if not isinstance(history, list) or any(isinstance(v, bool) or not isinstance(v, int) or v < 0 for v in history):
            fail(f"line {index}: historical durations must be non-negative whole minutes")
        if event != "completed" and (history or average is not None):
            fail(f"line {index}: only completed events may carry historical average evidence")
        if event == "completed":
            matching_history: list[int] = history
            if verify_external_evidence:
                fingerprint = tooling_fingerprint(raw["tooling"])
                matching_history = []
                for history_row in history_rows or []:
                    if history_row.get("phase") == phase and history_row.get("tooling_fingerprint") == fingerprint:
                        try:
                            matching_history.append(int(history_row["actual_minutes"]))
                        except (KeyError, ValueError):
                            fail(f"line {index}: history report contains invalid actual_minutes")
                if history != matching_history:
                    fail(f"line {index}: historical durations do not equal the supplied same-tooling history report")
            expected_average = None if not matching_history else (2 * sum(matching_history) + len(matching_history)) // (2 * len(matching_history))
            if average != expected_average:
                fail(f"line {index}: historical_average_minutes does not match its basis")

        if event == "started":
            if phase in phases:
                fail(f"line {index}: phase may be started only once")
            expected_order = len(phases) + 1
            if order != expected_order:
                fail(f"line {index}: phase_order must be contiguous in first-seen order")
            if raw["actual_minutes"] is not None:
                fail(f"line {index}: started actual_minutes must be null")
            phases[phase] = {"order": order, "status": "active", "started": timestamp,
                             "last_at": timestamp, "model": raw["model"], "tooling": raw["tooling"]}
            active.add(phase)
        elif phase not in phases or phases[phase]["order"] != order:
            fail(f"line {index}: event references an unknown phase/order")
        elif timestamp < phases[phase]["last_at"]:
            fail(f"line {index}: timestamps must be nondecreasing within a phase")
        elif phases[phase]["model"] != raw["model"]:
            fail(f"line {index}: model changed within one phase; start a distinct continuation phase")
        elif phases[phase]["tooling"] != raw["tooling"]:
            fail(f"line {index}: tooling changed within one phase; start a distinct continuation phase")
        elif event == "resumed":
            if phase not in blocked or phase in active or phases[phase]["status"] != "blocked":
                fail(f"line {index}: only the blocked phase may resume")
            if raw["actual_minutes"] is not None:
                fail(f"line {index}: resumed actual_minutes must be null")
            blocked.remove(phase)
            active.add(phase)
            phases[phase]["status"] = "active"
            phases[phase]["last_at"] = timestamp
        else:
            if phase not in active or phases[phase]["status"] != "active":
                fail(f"line {index}: only the active phase may {event}")
            elapsed = int((timestamp - phases[phase]["started"]).total_seconds())
            expected_minutes = (elapsed + 30) // 60
            actual = raw["actual_minutes"]
            if isinstance(actual, bool) or not isinstance(actual, int) or actual != expected_minutes:
                fail(f"line {index}: actual_minutes must equal rounded elapsed wall time ({expected_minutes})")
            active.remove(phase)
            phases[phase]["status"] = event
            phases[phase]["last_at"] = timestamp
            if event == "blocked":
                blocked.add(phase)

    required = required_phases or []
    missing = [phase for phase in required if phase not in phases]
    if missing:
        fail("missing required phases: " + ", ".join(missing))
    if require_terminal:
        if active or blocked:
            fail("terminal log must have no active or blocked phase")
        incomplete = [name for name, value in phases.items() if value["status"] != "completed"]
        if incomplete:
            fail("terminal log has incomplete phases: " + ", ".join(incomplete))
    if require_reconciled:
        pending = [raw["phase"] for raw in records if isinstance(raw, dict)
                   and raw.get("event") in {"completed", "blocked"}
                   and raw.get("token_usage") == {"status": "pending"}]
        if pending:
            fail("terminal token usage still pending reconciliation: " + ", ".join(pending))


def load(path: Path) -> list[object]:
    records: list[object] = []
    for line_number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        if not line.strip():
            fail(f"line {line_number}: blank lines are not allowed")
        try:
            records.append(json.loads(line))
        except json.JSONDecodeError as error:
            fail(f"line {line_number}: invalid JSON: {error.msg}")
    return records


def read_usage_csv(path: Path) -> tuple[list[dict[str, str]], str]:
    content = path.read_bytes()
    with path.open(encoding="utf-8", newline="") as handle:
        reader = csv.DictReader(handle)
        if reader.fieldnames != USAGE_FIELDS:
            fail("usage report header does not match the stable collector schema")
        rows = list(reader)
    seen: set[tuple[str, str]] = set()
    for index, row in enumerate(rows, 2):
        identity = (row.get("provider", ""), row.get("response_id", ""))
        if not all(identity) or identity in seen:
            fail(f"usage report line {index}: response identity is empty or duplicated")
        seen.add(identity)
        source = row.get("source", "")
        if not re.fullmatch(r"(?:codex-session-jsonl|claude-statusline-json):sha256:[0-9a-f]{64}", source):
            fail(f"usage report line {index}: source is not normalized content-digest provenance")
        for field in ("input", "cached_input", "cache_write_input", "output", "total"):
            try:
                value = int(row[field])
            except (KeyError, ValueError):
                fail(f"usage report line {index}: {field} is not a non-negative integer")
            if value < 0:
                fail(f"usage report line {index}: {field} is not a non-negative integer")
        reasoning = row.get("reasoning", "")
        if reasoning and (not reasoning.isdigit() or int(reasoning) > int(row["output"])):
            fail(f"usage report line {index}: reasoning is invalid")
    digest = hashlib.sha256(content).hexdigest()
    return rows, f"runtime-usage-csv:sha256:{digest}"


def read_usage_reports(paths: list[Path]) -> dict[str, list[dict[str, str]]]:
    reports: dict[str, list[dict[str, str]]] = {}
    for path in paths:
        rows, source = read_usage_csv(path)
        if source in reports:
            fail(f"duplicate immutable usage receipt digest: {source}")
        reports[source] = rows
    return reports


def read_history_csv(path: Path | None) -> list[dict[str, str]] | None:
    if path is None:
        return None
    with path.open(encoding="utf-8", newline="") as handle:
        reader = csv.DictReader(handle)
        if reader.fieldnames != ["phase", "tooling_fingerprint", "actual_minutes", "source"]:
            fail("history report header must be phase,tooling_fingerprint,actual_minutes,source")
        rows = list(reader)
    seen: set[str] = set()
    for index, row in enumerate(rows, 2):
        source = row.get("source", "")
        if source in seen or not re.fullmatch(r"https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/issues/[1-9][0-9]*#issuecomment-[1-9][0-9]*", source):
            fail(f"history report line {index}: source must be a unique canonical issue-comment URL")
        seen.add(source)
        if not re.fullmatch(r"[0-9a-f]{64}", row.get("tooling_fingerprint", "")):
            fail(f"history report line {index}: tooling_fingerprint must be sha256")
        if not row.get("actual_minutes", "").isdigit():
            fail(f"history report line {index}: actual_minutes must be a whole minute")
    return rows


def seal(records: list[object]) -> list[object]:
    previous: str | None = None
    for index, raw in enumerate(records, 1):
        assert isinstance(raw, dict)
        raw["revision"] = index
        raw["previous_digest"] = previous
        raw["digest"] = "0" * 64
        raw["digest"] = canonical_digest(raw)
        previous = raw["digest"]
    return records


def seal_successor(existing: list[object], draft: list[object], run_id: str, unit_id: str,
                   usage_reports: dict[str, list[dict[str, str]]],
                   history_rows: list[dict[str, str]] | None) -> dict[str, object]:
    if len(draft) != 1 or not isinstance(draft[0], dict):
        fail("successor draft must contain exactly one JSON object")
    chain_fields = {"sequence", "revision", "previous_digest", "digest"}
    expected_draft_fields = FIELDS - chain_fields
    if set(draft[0]) != expected_draft_fields:
        fail("successor draft has missing or unexpected fields; omit sequence/revision/digest chain fields")
    successor = copy.deepcopy(draft[0])
    successor["sequence"] = len(existing) + 1
    successor["revision"] = len(existing) + 1
    successor["previous_digest"] = existing[-1]["digest"] if existing else None
    successor["digest"] = "0" * 64
    successor["digest"] = canonical_digest(successor)
    chain = copy.deepcopy(existing) + [successor]
    validate_lines(chain, run_id, unit_id, usage_reports=usage_reports, history_rows=history_rows)
    return successor


def export_comments(path: Path, run_id: str, unit_id: str) -> list[object]:
    raw = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(raw, list):
        fail("GitHub comment export must be an array (use gh api --paginate --slurp)")
    comments: list[object] = []
    for value in raw:
        if isinstance(value, list):
            comments.extend(value)
        else:
            comments.append(value)
    records: list[tuple[int, object]] = []
    marker = re.compile(r"\A<!-- fsgg:item-lifecycle/v1 -->\n```json\n([^\n]+)\n```\n?\Z")
    for index, value in enumerate(comments, 1):
        if not isinstance(value, dict):
            fail(f"GitHub comment export entry {index} is not an object")
        body = value.get("body")
        if not isinstance(body, str) or not body.startswith("<!-- fsgg:item-lifecycle/v1 -->"):
            continue
        match = marker.fullmatch(body)
        if match is None:
            fail(f"GitHub lifecycle comment {value.get('id')} has a malformed marker/body")
        if value.get("created_at") != value.get("updated_at"):
            fail(f"GitHub lifecycle comment {value.get('id')} was edited; append a correction instead")
        try:
            event = json.loads(match.group(1))
        except json.JSONDecodeError as error:
            fail(f"GitHub lifecycle comment {value.get('id')} contains invalid JSON: {error.msg}")
        if isinstance(event, dict) and event.get("run_id") == run_id and event.get("unit_id") == unit_id:
            comment_id = value.get("id")
            if isinstance(comment_id, bool) or not isinstance(comment_id, int) or comment_id <= 0:
                fail("GitHub lifecycle comment has no positive numeric id")
            records.append((comment_id, event))
    records.sort(key=lambda pair: pair[0])
    if not records:
        fail("GitHub comment export contains no matching lifecycle events")
    canonical: list[object] = []
    rejected_forks: list[int] = []
    previous_digest: str | None = None
    expected = 1
    for comment_id, event in records:
        if not isinstance(event, dict):
            fail(f"GitHub lifecycle comment {comment_id} event is not an object")
        sequence = event.get("sequence")
        revision = event.get("revision")
        predecessor = event.get("previous_digest")
        if sequence == expected and revision == expected and predecessor == previous_digest:
            digest = event.get("digest")
            if not isinstance(digest, str) or not re.fullmatch(r"[0-9a-f]{64}", digest):
                fail(f"GitHub lifecycle comment {comment_id} has no canonical digest")
            if canonical_digest(event) != digest:
                fail(f"GitHub lifecycle comment {comment_id} digest does not match its event")
            canonical.append(event)
            previous_digest = digest
            expected += 1
        elif (isinstance(sequence, int) and not isinstance(sequence, bool)
              and isinstance(revision, int) and not isinstance(revision, bool)
              and 1 <= revision == sequence < expected
              and predecessor == (None if revision == 1 else canonical[revision - 2]["digest"])
              and isinstance(event.get("digest"), str)
              and re.fullmatch(r"[0-9a-f]{64}", event["digest"])
              and canonical_digest(event) == event["digest"]):
            # Validate the complete versioned event contract in the exact history the sibling claims,
            # while deliberately omitting only joins to private usage/history receipts that an issue
            # comment export cannot possess. A digest-valid object missing `actor` (or any other v1
            # field) is malformed authority, never rejected-fork evidence.
            validate_lines(canonical[:revision - 1] + [event], run_id, unit_id,
                           verify_external_evidence=False)
            # GitHub assigns comment ids from one server-side total order. If two writers seal the same
            # predecessor concurrently, the lower comment id deterministically wins and every later
            # sibling is preserved as rejected audit evidence rather than corrupting the exported chain.
            rejected_forks.append(comment_id)
        else:
            fail(f"GitHub lifecycle comment {comment_id} does not extend canonical revision {expected - 1}")
    if rejected_forks:
        print("lifecycle-log: ignored deterministic fork loser comment id(s): "
              + ", ".join(str(value) for value in rejected_forks), file=sys.stderr)
    validate_lines(canonical, run_id, unit_id, verify_external_evidence=False)
    return canonical


def valid_fixture() -> list[object]:
    item = {"repo": "FS-GG/.github", "number": 42, "url": "https://github.com/FS-GG/.github/issues/42"}
    source = {"repository": "FS-GG/.github", "revision": "a" * 40}
    common = {"schema_version": 1, "run_id": "roadmap-v2", "unit_id": "GS2-01.1", "item": item,
              "actor": "worker-1234", "model": {"status": "recorded", "provider": "OpenAI",
              "name": "gpt-test", "effort": "high", "source": "runtime receipt"}, "source": source,
              "authority": {"kind": "github_issue_comment", "subject": "FS-GG/.github#42",
                            "claim_generation": "claim-generation-1"},
              "tooling": {"ledger_schema": 1,
                           "runtime": {"status": "recorded", "name": "codex", "version": "1.2.3", "source": "session"},
                           "coordination": {"status": "recorded", "name": "fsgg-coord", "version": "4.5.6", "source": "cli"},
                           "sdd": {"status": "recorded", "name": "fsgg-sdd", "version": "7.8.9", "source": "cli"},
                           "contracts": {"status": "recorded", "name": "fsgg-contracts", "version": "10.0.0", "source": "registry"}},
              "historical_average_minutes": None}
    return seal([
        {**common, "sequence": 1, "phase_order": 1, "phase": "claim", "event": "started",
         "at": "2026-09-04T08:00:00Z", "evidence": ["issue URL"], "actual_minutes": None,
         "historical_durations_minutes": [], "token_usage": {"status": "pending"}},
        {**common, "sequence": 2, "phase_order": 1, "phase": "claim", "event": "completed",
         "at": "2026-09-04T08:01:29Z", "evidence": ["claim receipt"], "actual_minutes": 1,
         "historical_durations_minutes": [],
         "token_usage": {"status": "measured", "input": 10, "cached_input": 4,
                         "cache_write_input": 0, "output": 5, "reasoning": 2, "total": 15,
                         "source": "runtime-usage-csv:sha256:fixture", "session_ids": ["session-1"],
                         "turn_ids": ["turn-1"]}},
        {**common, "sequence": 3, "phase_order": 2, "phase": "implement", "event": "started",
         "at": "2026-09-04T08:01:29Z", "evidence": ["commit base"], "actual_minutes": None,
         "historical_durations_minutes": [], "token_usage": {"status": "pending"}},
        {**common, "sequence": 4, "phase_order": 2, "phase": "implement", "event": "completed",
         "at": "2026-09-04T08:04:00Z", "evidence": ["green tests"], "actual_minutes": 3,
         "historical_durations_minutes": [], "historical_average_minutes": None,
         "token_usage": {"status": "unavailable", "reason": "host exposes no phase counters", "source": "host usage API"}},
    ])


def self_test() -> None:
    base = valid_fixture()
    usage_rows = [{
        "session_id": "session-1", "turn_id": "turn-1", "response_id": "response-1",
        "task": "FS-GG/.github#42/claim",
        "provider": "OpenAI", "model": "gpt-test", "effort": "high",
        "runtime_version": "1.2.3", "coordination_version": "4.5.6",
        "sdd_version": "7.8.9", "contracts_version": "10.0.0",
        "input": "10", "cached_input": "4", "cache_write_input": "0", "output": "5",
        "reasoning": "2", "total": "15",
    }]
    validate_lines(base, "roadmap-v2", "GS2-01.1", True, True, ["claim", "implement"],
                   {"runtime-usage-csv:sha256:fixture": usage_rows})
    chain_fields = {"sequence", "revision", "previous_digest", "digest"}
    first_draft = [{key: value for key, value in base[0].items() if key not in chain_fields}]
    first = seal_successor([], first_draft, "roadmap-v2", "GS2-01.1", {}, None)
    terminal_draft = [{key: value for key, value in base[1].items() if key not in chain_fields}]
    second = seal_successor([first], terminal_draft, "roadmap-v2", "GS2-01.1",
                            {"runtime-usage-csv:sha256:fixture": usage_rows}, None)
    assert second["revision"] == 2 and second["previous_digest"] == first["digest"]
    unsafe = copy.deepcopy(terminal_draft)
    unsafe[0]["token_usage"] = {"status": "pending"}
    try:
        seal_successor([first], unsafe, "roadmap-v2", "GS2-01.1", {}, None)
    except InvalidLog:
        pass
    else:
        fail("self-test successor sealer emitted an unreconciled terminal event")
    mutations = {
        "sequence gap": lambda rows: rows[2].__setitem__("sequence", 4),
        "wrong issue URL": lambda rows: rows[0]["item"].__setitem__("url", "https://example.invalid/42"),
        "overlapping phase": lambda rows: rows[2].__setitem__("at", "2026-09-04T08:00:30Z"),
        "wrong duration": lambda rows: rows[1].__setitem__("actual_minutes", 9),
        "wrong average": lambda rows: rows[3].__setitem__("historical_average_minutes", 2),
        "wrong token total": lambda rows: rows[1]["token_usage"].__setitem__("total", 99),
        "estimated tokens": lambda rows: rows[1]["token_usage"].__setitem__("status", "estimated"),
        "inferred model": lambda rows: rows[1]["model"].__setitem__("status", "inferred"),
        "missing tool version": lambda rows: rows[1]["tooling"]["sdd"].__setitem__("version", ""),
        "model changed in phase": lambda rows: rows[1].__setitem__("model", {"status": "recorded", "provider": "OpenAI", "name": "other", "source": "runtime receipt"}),
        "bad revision": lambda rows: rows[0]["source"].__setitem__("revision", "HEAD"),
        "empty evidence": lambda rows: rows[0].__setitem__("evidence", []),
        "phase order gap": lambda rows: rows[2].__setitem__("phase_order", 3),
        "active terminal": lambda rows: rows.pop(),
        "missing required phase": lambda rows: None,
    }
    for name, mutate in mutations.items():
        rows = copy.deepcopy(base)
        mutate(rows)
        try:
            required = ["claim", "implement", "acceptance"] if name == "missing required phase" else ["claim", "implement"]
            validate_lines(rows, "roadmap-v2", "GS2-01.1", True, True, required,
                           {"runtime-usage-csv:sha256:fixture": usage_rows})
        except InvalidLog:
            continue
        fail(f"self-test mutation was accepted: {name}")
    provenance_mutations = {
        "forged token report join": lambda rows: rows[1]["token_usage"].update({"input": 1_000_000_999,
                                                                                 "total": 1_000_001_004}),
        "forged tooling version": lambda rows: rows[1]["tooling"]["sdd"].update({"version": "999.999.999"}),
        "invented historical corpus": lambda rows: rows[3].update({"historical_durations_minutes": [999],
                                                                    "historical_average_minutes": 999}),
    }
    for name, mutate in provenance_mutations.items():
        rows = copy.deepcopy(base)
        mutate(rows)
        seal(rows)
        try:
            validate_lines(rows, "roadmap-v2", "GS2-01.1", True, True, ["claim", "implement"],
                           {"runtime-usage-csv:sha256:fixture": usage_rows})
        except InvalidLog:
            continue
        fail(f"self-test provenance mutation was accepted: {name}")
    with tempfile.TemporaryDirectory() as directory:
        comment_path = Path(directory) / "comments.json"
        body = "<!-- fsgg:item-lifecycle/v1 -->\n```json\n" + json.dumps(base[0], sort_keys=True, separators=(",", ":")) + "\n```\n"
        fixture_comment = {"id": 1, "created_at": "2026-09-04T08:00:00Z",
                           "updated_at": "2026-09-04T08:00:00Z", "body": body}
        comment_path.write_text(json.dumps([[fixture_comment]]), encoding="utf-8")
        assert export_comments(comment_path, "roadmap-v2", "GS2-01.1") == [base[0]]
        fork = copy.deepcopy(base[0])
        fork["actor"] = "critic-2"
        fork["digest"] = canonical_digest(fork)
        fork_body = "<!-- fsgg:item-lifecycle/v1 -->\n```json\n" + json.dumps(fork, sort_keys=True, separators=(",", ":")) + "\n```\n"
        fork_comment = {"id": 2, "created_at": "2026-09-04T08:00:01Z",
                        "updated_at": "2026-09-04T08:00:01Z", "body": fork_body}
        comment_path.write_text(json.dumps([[fixture_comment, fork_comment]]), encoding="utf-8")
        assert export_comments(comment_path, "roadmap-v2", "GS2-01.1") == [base[0]]
        bad_digest = copy.deepcopy(fork)
        bad_digest["digest"] = "f" * 64
        alternate_history = copy.deepcopy(fork)
        alternate_history["previous_digest"] = "e" * 64
        alternate_history["digest"] = canonical_digest(alternate_history)
        missing_actor = copy.deepcopy(fork)
        del missing_actor["actor"]
        missing_actor["digest"] = canonical_digest(missing_actor)
        for invalid_fork in (bad_digest, alternate_history, missing_actor):
            invalid_body = "<!-- fsgg:item-lifecycle/v1 -->\n```json\n" + json.dumps(invalid_fork, sort_keys=True, separators=(",", ":")) + "\n```\n"
            invalid_comment = {"id": 2, "created_at": "2026-09-04T08:00:01Z",
                               "updated_at": "2026-09-04T08:00:01Z", "body": invalid_body}
            comment_path.write_text(json.dumps([[fixture_comment, invalid_comment]]), encoding="utf-8")
            try:
                export_comments(comment_path, "roadmap-v2", "GS2-01.1")
            except InvalidLog:
                pass
            else:
                fail("self-test accepted a rejected fork without exact predecessor/digest provenance")
        fixture_comment["updated_at"] = "2026-09-04T08:01:00Z"
        comment_path.write_text(json.dumps([fixture_comment]), encoding="utf-8")
        try:
            export_comments(comment_path, "roadmap-v2", "GS2-01.1")
        except InvalidLog:
            pass
        else:
            fail("self-test accepted an edited authoritative lifecycle comment")
    print(f"lifecycle-log self-test: pass ({len(mutations) + len(provenance_mutations) + 1} rejection cases)")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", default=".")
    parser.add_argument("--run")
    parser.add_argument("--unit")
    parser.add_argument("--log")
    parser.add_argument("--usage", action="append", default=[],
                        help="immutable private runtime-usage receipt CSV; repeat for multiple phases")
    parser.add_argument("--history-report", help="validated prior-phase corpus: phase,tooling_fingerprint,actual_minutes")
    parser.add_argument("--seal-successor", help="one unposted event draft; validate the full chain and emit only its successor")
    parser.add_argument("--existing", help="exported existing issue-comment chain for --seal-successor")
    parser.add_argument("--export-comments", help="GitHub REST comment JSON; emit matching unedited lifecycle events")
    parser.add_argument("--require-terminal", action="store_true")
    parser.add_argument("--require-reconciled", action="store_true")
    parser.add_argument("--required-phase", action="append", default=[])
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    try:
        if args.self_test:
            self_test()
            return 0
        if args.seal_successor:
            if not args.run or not args.unit:
                fail("--seal-successor requires --run and --unit")
            root = Path(args.root).resolve()
            existing = load((root / args.existing).resolve()) if args.existing else []
            draft = load((root / args.seal_successor).resolve())
            usage_paths = [(root / value).resolve() for value in args.usage]
            history_path = ((root / args.history_report).resolve()
                            if args.history_report else None)
            record = seal_successor(existing, draft, args.run, args.unit,
                                    read_usage_reports(usage_paths), read_history_csv(history_path))
            print(json.dumps(record, sort_keys=True, separators=(",", ":")))
            return 0
        if args.export_comments:
            if not args.run or not args.unit:
                fail("--export-comments requires --run and --unit")
            for record in export_comments(Path(args.export_comments), args.run, args.unit):
                print(json.dumps(record, sort_keys=True, separators=(",", ":")))
            return 0
        if not args.run or not args.unit or not args.log:
            fail("--run, --unit, and --log are required unless --self-test is used")
        root = Path(args.root).resolve()
        path = (root / args.log).resolve()
        if not path.is_file():
            fail(f"log does not exist: {path}")
        usage_paths = [(root / value).resolve() for value in args.usage]
        history_path = (root / args.history_report).resolve() if args.history_report else None
        validate_lines(load(path), args.run, args.unit, args.require_terminal, args.require_reconciled,
                       args.required_phase, read_usage_reports(usage_paths), read_history_csv(history_path))
        state = "terminal" if args.require_terminal else "valid"
        print(f"lifecycle-log: {state} — {path}")
        return 0
    except (InvalidLog, OSError) as error:
        print(f"lifecycle-log: invalid — {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
