#!/usr/bin/env python3
"""Extract authoritative runtime usage without reading conversation content."""

from __future__ import annotations

import argparse
import csv
import hashlib
import io
import json
import sys
import tempfile
from pathlib import Path

FIELDS = [
    "timestamp", "task", "session_id", "thread_id", "turn_id", "response_id", "provider", "model", "effort",
    "runtime_version", "coordination_version", "sdd_version", "contracts_version", "ledger_schema",
    "input", "cached_input", "cache_write_input", "output", "reasoning", "total",
    "turn_input", "turn_cached_input", "turn_cache_write_input", "turn_output", "turn_reasoning",
    "turn_total", "thread_input", "thread_cached_input", "thread_cache_write_input", "thread_output",
    "thread_reasoning", "thread_total", "source",
]
COUNTS = {
    "input": "input_tokens", "cached_input": "cached_input_tokens",
    "cache_write_input": "cache_write_input_tokens", "output": "output_tokens",
    "reasoning": "reasoning_output_tokens", "total": "total_tokens",
}


class InvalidUsage(ValueError):
    pass


def required_text(value: object, label: str) -> str:
    if not isinstance(value, str) or not value:
        raise InvalidUsage(f"{label} must be a non-empty string")
    return value


def counts(value: object, label: str) -> dict[str, int]:
    if not isinstance(value, dict):
        raise InvalidUsage(f"{label} must be an object")
    result: dict[str, int] = {}
    for target, source in COUNTS.items():
        current = value.get(source)
        if isinstance(current, bool) or not isinstance(current, int) or current < 0:
            raise InvalidUsage(f"{label}.{source} must be a non-negative integer")
        result[target] = current
    if result["total"] != result["input"] + result["output"]:
        raise InvalidUsage(f"{label}.total_tokens must equal input_tokens + output_tokens")
    if result["cached_input"] + result["cache_write_input"] > result["input"]:
        raise InvalidUsage(f"{label} cache token subsets exceed input_tokens")
    if result["reasoning"] > result["output"]:
        raise InvalidUsage(f"{label}.reasoning_output_tokens exceeds output_tokens")
    return result


def codex_rows(lines: list[str], task: str, wanted_turn: str | None, source: str,
               all_responses: bool = False, since: str | None = None,
               until: str | None = None) -> list[dict[str, object]]:
    contexts: dict[str, tuple[str, str]] = {}
    records: list[dict[str, object]] = []
    runtime_version = ""
    for line_number, line in enumerate(lines, 1):
        try:
            record = json.loads(line)
        except json.JSONDecodeError as error:
            raise InvalidUsage(f"line {line_number}: invalid JSON: {error.msg}") from error
        payload = record.get("payload", {})
        if record.get("type") == "session_meta" and isinstance(payload, dict):
            runtime_version = payload.get("cli_version", "")
        elif record.get("type") == "turn_context" and isinstance(payload, dict):
            turn = payload.get("turn_id")
            model = payload.get("model")
            effort = payload.get("effort", "")
            if isinstance(turn, str) and isinstance(model, str):
                contexts[turn] = (model, effort if isinstance(effort, str) else "")
        elif record.get("type") == "token_usage_record" and isinstance(payload, dict):
            turn = payload.get("turn_id")
            if isinstance(turn, str):
                records.append({"timestamp": record.get("timestamp"), "payload": payload})
    if not records:
        raise InvalidUsage("no token_usage_record rows found")
    required_text(runtime_version, "session_meta.cli_version")
    if wanted_turn:
        selected = [entry for entry in records if entry["payload"].get("turn_id") == wanted_turn]
        if not selected:
            raise InvalidUsage(f"turn_id not found: {wanted_turn}")
    else:
        selected = records
    if since:
        selected = [entry for entry in selected if isinstance(entry["timestamp"], str)
                    and entry["timestamp"] >= since]
    if until:
        selected = [entry for entry in selected if isinstance(entry["timestamp"], str)
                    and entry["timestamp"] < until]
    if not selected:
        raise InvalidUsage("no token usage records matched the requested turn/time window")
    if not all_responses:
        selected = [selected[-1]]
    rows: list[dict[str, object]] = []
    for entry in selected:
        payload = entry["payload"]
        assert isinstance(payload, dict)
        turn = required_text(payload.get("turn_id"), "turn_id")
        model, effort = contexts.get(turn, ("", ""))
        if not model:
            raise InvalidUsage(f"no turn_context model for turn_id {turn}")
        request_counts = counts(payload.get("usage"), "usage")
        turn_counts = counts(payload.get("turn_token_usage"), "turn_token_usage")
        thread_counts = counts(payload.get("thread_token_usage"), "thread_token_usage")
        row: dict[str, object] = {
            "timestamp": required_text(entry["timestamp"], "timestamp"), "task": task,
            "session_id": required_text(payload.get("session_id"), "session_id"),
            "thread_id": required_text(payload.get("thread_id"), "thread_id"), "turn_id": turn,
            "response_id": required_text(payload.get("response_id"), "response_id"),
            "provider": "OpenAI", "model": model, "effort": effort,
            "runtime_version": runtime_version, "source": source,
        }
        row.update(request_counts)
        row.update({f"turn_{key}": value for key, value in turn_counts.items()})
        row.update({f"thread_{key}": value for key, value in thread_counts.items()})
        rows.append(row)
    return rows


def claude_row(snapshot: dict[str, object], task: str, source: str) -> dict[str, object]:
    model = snapshot.get("model")
    context = snapshot.get("context_window")
    if not isinstance(model, dict) or not isinstance(context, dict):
        raise InvalidUsage("Claude snapshot requires model and context_window objects")
    usage = context.get("current_usage")
    if not isinstance(usage, dict):
        raise InvalidUsage("Claude context_window.current_usage is absent")
    uncached = usage.get("input_tokens")
    cached = usage.get("cache_read_input_tokens")
    cache_write = usage.get("cache_creation_input_tokens")
    output = usage.get("output_tokens")
    values = [uncached, cached, cache_write, output]
    if any(isinstance(value, bool) or not isinstance(value, int) or value < 0 for value in values):
        raise InvalidUsage("Claude usage counts must be non-negative integers")
    input_total = uncached + cached + cache_write
    effort = snapshot.get("effort", {})
    response_key = json.dumps({
        "session_id": snapshot.get("session_id"), "prompt_id": snapshot.get("prompt_id"),
        "timestamp": snapshot.get("timestamp"), "model": model.get("id"), "usage": usage,
    }, sort_keys=True, separators=(",", ":")).encode()
    response_id = "claude-" + hashlib.sha256(response_key).hexdigest()
    return {
        "timestamp": required_text(snapshot.get("timestamp"), "timestamp"), "task": task,
        "session_id": required_text(snapshot.get("session_id"), "session_id"), "thread_id": "",
        "turn_id": required_text(snapshot.get("prompt_id"), "prompt_id"), "response_id": response_id,
        "provider": "Anthropic",
        "model": required_text(model.get("id"), "model.id"),
        "effort": effort.get("level", "") if isinstance(effort, dict) else "",
        "runtime_version": required_text(snapshot.get("version"), "version"),
        "input": input_total, "cached_input": cached, "cache_write_input": cache_write,
        "output": output, "reasoning": "", "total": input_total + output,
        "turn_input": input_total, "turn_cached_input": cached, "turn_cache_write_input": cache_write,
        "turn_output": output, "turn_reasoning": "", "turn_total": input_total + output,
        "thread_input": "", "thread_cached_input": "", "thread_cache_write_input": "",
        "thread_output": "", "thread_reasoning": "", "thread_total": "", "source": source,
    }


def emit(rows: list[dict[str, object]], output_format: str, append: Path | None) -> None:
    if output_format == "json":
        rendered = "\n".join(json.dumps(row, separators=(",", ":"), sort_keys=True) for row in rows) + "\n"
    else:
        stream = io.StringIO()
        writer = csv.DictWriter(stream, fieldnames=FIELDS, lineterminator="\n")
        needs_header = append is None or not append.exists() or append.stat().st_size == 0
        if append is not None and not needs_header:
            with append.open(encoding="utf-8", newline="") as handle:
                existing = {row.get("response_id", "") for row in csv.DictReader(handle)}
            rows = [row for row in rows if row.get("response_id") not in existing]
        if needs_header:
            writer.writeheader()
        writer.writerows(rows)
        rendered = stream.getvalue()
    if append:
        append.parent.mkdir(parents=True, exist_ok=True)
        with append.open("a", encoding="utf-8", newline="") as handle:
            handle.write(rendered)
    else:
        sys.stdout.write(rendered)


def self_test() -> None:
    session = {"timestamp": "2026-01-01T00:00:00Z", "type": "session_meta",
               "payload": {"cli_version": "1.2.3"}}
    context = {"timestamp": "2026-01-01T00:00:00Z", "type": "turn_context",
               "payload": {"turn_id": "turn-1", "model": "gpt-test-sol", "effort": "high"}}
    usage = {"input_tokens": 10, "cached_input_tokens": 4, "cache_write_input_tokens": 0,
             "output_tokens": 5, "reasoning_output_tokens": 2, "total_tokens": 15}
    record = {"timestamp": "2026-01-01T00:01:00Z", "type": "token_usage_record",
              "payload": {"thread_id": "thread-1", "turn_id": "turn-1", "session_id": "session-1",
                          "response_id": "response-1", "usage": usage,
                          "turn_token_usage": usage, "thread_token_usage": usage}}
    lines = [json.dumps(session), json.dumps(context), json.dumps(record)]
    row = codex_rows(lines, "repo#1/claim", None, "fixture")[0]
    assert row["model"] == "gpt-test-sol" and row["reasoning"] == 2 and row["total"] == 15
    bad = json.loads(json.dumps(record))
    bad["payload"]["turn_token_usage"]["total_tokens"] = 99
    try:
        codex_rows([json.dumps(context), json.dumps(bad)], "task", None, "fixture")
    except InvalidUsage:
        claude = {
            "timestamp": "2026-01-01T00:02:00Z", "session_id": "claude-session",
            "prompt_id": "prompt-1", "version": "2.3.4", "model": {"id": "claude-test"},
            "effort": {"level": "high"}, "context_window": {"current_usage": {
                "input_tokens": 7, "cache_read_input_tokens": 2,
                "cache_creation_input_tokens": 1, "output_tokens": 3}},
        }
        claude_usage = claude_row(claude, "task", "claude-statusline:sha256:fixture")
        assert claude_usage["response_id"].startswith("claude-")
        with tempfile.TemporaryDirectory() as directory:
            report = Path(directory) / "usage.csv"
            emit([claude_usage], "csv", report)
            emit([claude_usage], "csv", report)
            with report.open(encoding="utf-8", newline="") as handle:
                assert len(list(csv.DictReader(handle))) == 1
        print("runtime-usage self-test: pass (2 positive, 1 rejection, append dedupe)")
        return
    raise InvalidUsage("self-test accepted an invalid total")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("runtime", nargs="?", choices=("codex", "claude"))
    parser.add_argument("--session-file", type=Path)
    parser.add_argument("--snapshot", type=Path)
    parser.add_argument("--task")
    parser.add_argument("--turn-id")
    parser.add_argument("--all-responses", action="store_true")
    parser.add_argument("--since", help="inclusive canonical UTC lower bound")
    parser.add_argument("--until", help="exclusive canonical UTC upper bound")
    parser.add_argument("--format", choices=("csv", "json"), default="csv")
    parser.add_argument("--append", type=Path)
    parser.add_argument("--coord-version", required=False)
    parser.add_argument("--sdd-version", required=False)
    parser.add_argument("--contracts-version", required=False)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    try:
        if args.self_test:
            self_test()
            return 0
        task = required_text(args.task, "--task")
        if args.runtime == "codex":
            if args.session_file is None:
                raise InvalidUsage("codex requires --session-file")
            session_bytes = args.session_file.read_bytes()
            source = "codex-session-jsonl:sha256:" + hashlib.sha256(session_bytes).hexdigest()
            rows = codex_rows(session_bytes.decode("utf-8").splitlines(), task,
                              args.turn_id, source, args.all_responses,
                              args.since, args.until)
        elif args.runtime == "claude":
            if args.snapshot is None:
                raise InvalidUsage("claude requires --snapshot")
            snapshot_bytes = args.snapshot.read_bytes()
            snapshot = json.loads(snapshot_bytes.decode("utf-8"))
            source = "claude-statusline-json:sha256:" + hashlib.sha256(snapshot_bytes).hexdigest()
            rows = [claude_row(snapshot, task, source)]
        else:
            raise InvalidUsage("runtime is required")
        for label, value in (("--coord-version", args.coord_version), ("--sdd-version", args.sdd_version),
                             ("--contracts-version", args.contracts_version)):
            required_text(value, label)
        for row in rows:
            row.update({"coordination_version": args.coord_version, "sdd_version": args.sdd_version,
                        "contracts_version": args.contracts_version, "ledger_schema": 1})
        emit(rows, args.format, args.append)
        return 0
    except (InvalidUsage, OSError, json.JSONDecodeError) as error:
        print(f"runtime-usage: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
