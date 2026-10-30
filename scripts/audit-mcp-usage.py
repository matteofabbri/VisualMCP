#!/usr/bin/env python3
"""Report turns where C# analysis was done without VisualMCP.

The MCP server can only log calls that reach it; a tool call the model never
makes is invisible to the server. ZCode, however, records every model call --
prompt, response and the tool calls it chose -- in
``%USERPROFILE%/.zcode/cli/rollout/model-io-*.jsonl``. This script reads those
files and lists the turns where C# work was done with Read/Grep/Bash while no
VisualMCP tool was used, i.e. the "missed intent" candidates.

Usage:
    python scripts/audit-mcp-usage.py
    python scripts/audit-mcp-usage.py --json
    python scripts/audit-mcp-usage.py --include-implementation
    python scripts/audit-mcp-usage.py --rollout-dir D:\\backup\\rollout
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from collections import OrderedDict
from pathlib import Path

CS_EXTENSIONS = (".cs", ".csproj", ".sln", ".slnx", ".razor", ".cshtml")
MCP_PREFIX = "mcp__VisualMCP__"

# Shell invocations that do code intelligence: search, read, build, test.
SHELL_ANALYSIS = re.compile(
    r"\b(grep|rg|findstr|select-string|get-content|cat|type|"
    r"dotnet\s+(build|test|format|msbuild))\b",
    re.IGNORECASE,
)

# Tools that implement code rather than analyse it. They are only counted with
# --include-implementation, because VisualMCP is an analysis/refactoring MCP.
IMPLEMENTATION_TOOLS = {"Write", "Edit", "MultiEdit", "ApplyPatch"}


def last_user_text(messages: list) -> str:
    """Return the text of the most recent user prompt in the conversation."""
    for message in reversed(messages):
        if message.get("role") != "user":
            continue
        content = message.get("content")
        if isinstance(content, str) and content.strip():
            return content.strip()
        if isinstance(content, list):
            parts = [
                block.get("text", "")
                for block in content
                if isinstance(block, dict)
                and block.get("type") == "text"
                and block.get("text")
            ]
            if parts:
                return " ".join(parts).strip()
    return ""


def input_summary(tool_call: dict, limit: int = 120) -> str:
    """One-line summary of a tool call, without the payload's bulk."""
    name = tool_call.get("name", "?")
    data = tool_call.get("input") or {}
    for key in ("file_path", "path", "pattern", "command", "query", "symbol_name"):
        value = data.get(key)
        if value:
            text = str(value).replace("\r", " ").replace("\n", " ")
            return f"{name}: {text[:limit]}"
    text = json.dumps(data, ensure_ascii=False)
    return f"{name}: {text[:limit]}"


def is_analysis_call(tool_call: dict, include_implementation: bool) -> bool:
    """Whether a non-MCP call is the kind of thing VisualMCP should handle."""
    name = tool_call.get("name", "")
    data = tool_call.get("input") or {}
    blob = json.dumps(data, ensure_ascii=False).lower()

    if name in IMPLEMENTATION_TOOLS:
        return include_implementation

    if name in ("Read", "Write", "Edit", "MultiEdit", "ApplyPatch"):
        path = str(data.get("file_path") or data.get("path") or "").lower()
        return path.endswith(CS_EXTENSIONS)

    if name in ("Grep", "Glob"):
        return any(ext in blob for ext in CS_EXTENSIONS)

    if name in ("Bash", "PowerShell"):
        command = str(data.get("command") or "")
        if not SHELL_ANALYSIS.search(command):
            return False
        return any(ext in command.lower() for ext in CS_EXTENSIONS) or re.search(
            r"dotnet\s+(build|test|format|msbuild)", command, re.IGNORECASE
        ) is not None

    return False


def scan(paths: list[Path], include_implementation: bool) -> tuple[list[dict], int]:
    turns: "OrderedDict[tuple[str, str], dict]" = OrderedDict()
    model_calls = 0

    for path in paths:
        try:
            handle = path.open(encoding="utf-8", errors="replace")
        except OSError as exc:
            print(f"warning: cannot read {path}: {exc}", file=sys.stderr)
            continue

        with handle:
            for line in handle:
                line = line.strip()
                if not line:
                    continue
                try:
                    record = json.loads(line)
                except json.JSONDecodeError:
                    continue

                model_calls += 1
                session = record.get("sessionId") or path.stem
                turn_id = record.get("turnId") or f"{path.stem}:{model_calls}"
                turn = turns.setdefault(
                    (session, turn_id),
                    {
                        "session": session,
                        "turn": turn_id,
                        "started": record.get("startedAt") or "",
                        "prompt": "",
                        "steps": 0,
                        "mcp_calls": 0,
                        "candidates": 0,
                        "candidate_tools": {},
                        "examples": [],
                    },
                )

                if not turn["prompt"]:
                    turn["prompt"] = last_user_text(
                        (record.get("request") or {}).get("messages") or []
                    )
                if not turn["started"]:
                    turn["started"] = record.get("startedAt") or ""

                tool_calls = (record.get("response") or {}).get("toolCalls") or []
                if not tool_calls:
                    continue
                turn["steps"] += 1

                for call in tool_calls:
                    name = call.get("name", "")
                    if name.startswith(MCP_PREFIX):
                        turn["mcp_calls"] += 1
                    elif is_analysis_call(call, include_implementation):
                        turn["candidates"] += 1
                        turn["candidate_tools"][name] = (
                            turn["candidate_tools"].get(name, 0) + 1
                        )
                        if len(turn["examples"]) < 3:
                            turn["examples"].append(input_summary(call))

    return list(turns.values()), model_calls


def print_report(turns: list[dict], model_calls: int, files: int) -> None:
    flagged = [t for t in turns if t["candidates"] and not t["mcp_calls"]]
    flagged.sort(key=lambda t: t["candidates"], reverse=True)
    used = sum(1 for t in turns if t["mcp_calls"])

    print(
        f"Scanned {files} rollout file(s), {model_calls} model call(s), "
        f"{len(turns)} turn(s)."
    )
    print(f"Turns that used VisualMCP: {used}")
    print(f"Turns with C# work but no VisualMCP call: {len(flagged)}\n")

    for turn in flagged:
        tools = ", ".join(
            f"{name} x{count}"
            for name, count in sorted(
                turn["candidate_tools"].items(), key=lambda kv: -kv[1]
            )
        )
        print(f"[{turn['candidates']:3d}] {turn['started'] or '?'}  session={turn['session']}")
        print(f"      prompt: {turn['prompt'][:160] or '(not captured)'}")
        print(f"      non-MCP: {tools}")
        for example in turn["examples"]:
            print(f"      e.g. {example}")
        print()


def main() -> int:
    home = Path.home()
    default_dir = home / ".zcode" / "cli" / "rollout"

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--rollout-dir",
        type=Path,
        default=default_dir,
        help=f"directory with model-io-*.jsonl files (default: {default_dir})",
    )
    parser.add_argument(
        "--glob",
        default="model-io-*.jsonl",
        help="file name pattern (default: model-io-*.jsonl)",
    )
    parser.add_argument(
        "--include-implementation",
        action="store_true",
        help="also count Write/Edit calls as missed VisualMCP opportunities",
    )
    parser.add_argument(
        "--json",
        action="store_true",
        help="emit the report as JSON",
    )
    args = parser.parse_args()

    if not args.rollout_dir.is_dir():
        parser.error(f"rollout directory not found: {args.rollout_dir}")

    paths = sorted(args.rollout_dir.glob(args.glob))
    turns, model_calls = scan(paths, args.include_implementation)

    if args.json:
        flagged = [t for t in turns if t["candidates"] and not t["mcp_calls"]]
        flagged.sort(key=lambda t: t["candidates"], reverse=True)
        print(
            json.dumps(
                {
                    "files": len(paths),
                    "model_calls": model_calls,
                    "turns": len(turns),
                    "missed_turns": flagged,
                },
                indent=2,
                ensure_ascii=False,
            )
        )
    else:
        print_report(turns, model_calls, len(paths))

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
