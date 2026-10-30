#!/usr/bin/env python3
"""Find bursts of consecutive VisualMCP *lookup* calls and judge if they could be one call.

An MCP server sees its own calls one at a time; what it cannot see is that the
model needed several round-trips to get one job done. This script reconstructs,
per turn, the ordered list of model steps and groups the VisualMCP calls into
two kinds:

* **lookup** -- read_file, list_directory, search_text, find_symbol, ... : pure
  reads of the workspace. These are the merge candidates.
* **action** -- build_project, run_tests, run_command, git_*, add_nuget_package :
  they change or verify the world, so they are sequential by nature and are
  never counted as merge candidates. An action step also ends a burst, because
  the model stopped exploring and did something.

A *burst* is a maximal run of consecutive steps that each contain at least one
lookup call and no action call. Every step in a burst cost a full round-trip:
the model waited for the result before deciding what to do next. Calls inside
the *same* step were already parallel and cost one round-trip.

A step is *dependent* when one of its arguments names something that only
appeared in the previous step's result (the basename of a file it reads, a
symbol it asks about, ...). Dependent steps cannot be merged: the model cannot
ask for what it does not know yet. Independent steps are merge candidates.

Sources, in order of preference:

* ``~/.zcode/cli/db/db.sqlite`` -- ZCode's own store: every session, message,
  model step, tool call and tool *result*. This is the complete history.
* ``~/.zcode/cli/rollout/model-io-*.jsonl`` -- raw model I/O, used only with
  ``--rollout`` (fewer sessions, but independent of the app's database).

Usage:
    python scripts/analyze-tool-sequences.py
    python scripts/analyze-tool-sequences.py --detail 6
    python scripts/analyze-tool-sequences.py --project RICHELIEU
    python scripts/analyze-tool-sequences.py --rollout
    python scripts/analyze-tool-sequences.py --json --out report.json
"""

from __future__ import annotations

import argparse
import json
import sqlite3
import sys
from collections import Counter, OrderedDict
from pathlib import Path

MCP_PREFIX = "mcp__VisualMCP__"

LOOKUP_TOOLS = {
    "read_file",
    "list_directory",
    "search_text",
    "find_symbol",
    "get_symbol_info",
    "get_type_members",
    "find_references",
    "find_callers",
    "find_implementations",
    "find_derived_types",
    "find_unused_symbols",
    "get_xml_docs",
    "get_diagnostics",
    "get_metrics",
    "get_complexity_hotspots",
    "get_project_info",
    "read_project_docs",
    "list_solutions",
    "check_solution",
    "check_for_update",
    "locate_nuget_package",
    "inspect_assembly",
    "decompile_type",
    "decompile_package_type",
    "list_nuget_packages",
    "list_analysis_tools",
    "search_text",
}

# Argument keys that carry a name the model can only know after seeing a result.
NAME_KEYS = (
    "path",
    "file_path",
    "symbolName",
    "symbol_name",
    "containingType",
    "containing_type",
    "className",
    "typeName",
    "interfaceName",
    "query",
    "nameOrPath",
    "assemblyPath",
)

# The batch tool that would have covered a step made only of this tool.
BATCH_NAMES = {
    "read_file": "read_files(paths[])",
    "list_directory": "read_tree(path, depth, glob)",
    "search_text": "search_texts(patterns[])",
    "inspect_assembly": "inspect_assemblies(paths[])",
    "decompile_type": "decompile_types(names[])",
    "locate_nuget_package": "locate_nuget_packages(ids[])",
    "find_symbol": "find_symbols(names[])",
    "get_type_members": "symbol_source(names[])",
}


def arg_names(tool_input: dict) -> list[str]:
    """Name-like argument values, reduced to the token that identifies a file/symbol."""
    out = []
    for key in NAME_KEYS:
        value = tool_input.get(key)
        if not isinstance(value, str) or len(value) < 4:
            continue
        token = value.replace("\\", "/").rstrip("/").rsplit("/", 1)[-1]
        if token:
            out.append(token)
    return out


def depends_on(calls: list[dict], previous_results: str, known_before: str = "") -> bool:
    """Whether a step needed the previous step's output to be formulated.

    A name only proves dependency when it is *new*: present in the previous
    step's result and absent from everything the model already had (the user
    prompt and the earlier results of the same turn). Without that check a
    project name like "Sperone", present in every file, would mark every step
    as dependent.
    """
    if not previous_results:
        return False
    previous = previous_results.lower()
    known = known_before.lower()
    for call in calls:
        for token in arg_names(call.get("input") or {}):
            if len(token) < 5:
                continue
            lowered = token.lower()
            if lowered in previous and lowered not in known:
                return True
    return False


# ---------------------------------------------------------------- sources


def load_from_db(db_path: Path):
    """Rebuild turns from ZCode's sqlite store (complete history)."""
    con = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True, timeout=10)
    roles: dict[str, str | None] = {}
    for message_id, message_data in con.execute("select id, data from message"):
        try:
            roles[message_id] = json.loads(message_data).get("role")
        except (TypeError, json.JSONDecodeError):
            roles[message_id] = None

    query = """
    select p.session_id, s.directory, m.id, m.sequence, p.sequence, p.data, p.time_created
    from part p
    join message m on m.id = p.message_id
    join session s on s.id = p.session_id
    order by p.session_id, m.sequence, p.sequence
    """
    turns = []
    current_turn = None
    current_step = None

    def close_step():
        nonlocal current_step
        if current_step and (current_step["lookup"] or current_step["action"]):
            current_turn["steps"].append(current_step)
        current_step = None

    def close_turn():
        nonlocal current_turn
        close_step()
        if current_turn and current_turn["steps"]:
            turns.append(current_turn)
        current_turn = None

    previous_session = None
    previous_role = None
    pending_context = ""
    for session_id, directory, message_id, mseq, _pseq, data, created in con.execute(query):
        try:
            part = json.loads(data)
        except (TypeError, json.JSONDecodeError):
            continue
        if session_id != previous_session:
            close_turn()
            previous_session = session_id
            previous_role = None
            pending_context = ""

        role = roles.get(message_id)

        if role == "user":
            if previous_role != "user":
                close_turn()
            previous_role = role
            if part.get("type") == "text":
                pending_context += "\n" + (part.get("text") or "")
            continue

        if current_turn is None:
            current_turn = {
                "session": session_id,
                "project": directory or "",
                "started": created,
                "context": pending_context[:200_000],
                "steps": [],
            }
            pending_context = ""
        previous_role = role

        kind = part.get("type")
        if kind == "step-start":
            close_step()
            current_step = {
                "lookup": [],
                "action": [],
                "previous_results": current_turn["steps"][-1]["results"]
                if current_turn["steps"]
                else "",
                "results": "",
                "bytes": 0,
                "text": "",
            }
        elif kind == "step-finish":
            close_step()
        elif current_step is None:
            continue
        elif kind == "text":
            if not current_step["text"]:
                current_step["text"] = (part.get("text") or "").strip()[:400]
        elif kind == "tool":
            name = (part.get("tool") or "")
            state = part.get("state") or {}
            output = state.get("output")
            output_text = output if isinstance(output, str) else json.dumps(output)
            current_step["results"] += "\n" + (output_text or "")
            meta = state.get("metadata") or {}
            size = ((meta.get("serialization") or {}).get("returnedBytes")) or len(
                output_text or ""
            )
            current_step["bytes"] += size or 0
            if name.startswith(MCP_PREFIX):
                call = {
                    "name": name[len(MCP_PREFIX) :],
                    "input": state.get("input") or {},
                }
                if call["name"] in LOOKUP_TOOLS:
                    current_step["lookup"].append(call)
                else:
                    current_step["action"].append(call)

    close_turn()
    con.close()
    return turns


def load_from_rollout(paths: list[Path]):
    """Rebuild turns from the raw model I/O transcripts."""
    turns: "OrderedDict[tuple[str, str], dict]" = OrderedDict()

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

                session = record.get("sessionId") or path.stem
                turn_id = record.get("turnId") or path.stem
                turn = turns.setdefault(
                    (session, turn_id),
                    {
                        "session": session,
                        "project": "",
                        "started": record.get("startedAt") or "",
                        "steps": [],
                        "_tool_msg_count": 0,
                    },
                )
                messages = (record.get("request") or {}).get("messages") or []
                tool_messages = [
                    message.get("content")
                    for message in messages
                    if message.get("role") == "tool"
                ]
                offset = turn["_tool_msg_count"]
                new_results = "\n".join(
                    str(text) for text in tool_messages[offset:] if isinstance(text, str)
                )
                turn["_tool_msg_count"] = max(offset, len(tool_messages))

                response = record.get("response") or {}
                calls, results, size = [], "", 0
                for raw in response.get("toolCalls") or []:
                    name = raw.get("name") or ""
                    is_mcp = name.startswith(MCP_PREFIX)
                    short = name[len(MCP_PREFIX) :] if is_mcp else name.split("__")[-1]
                    output = raw.get("output")
                    output_text = output if isinstance(output, str) else json.dumps(output)
                    results += "\n" + (output_text or "")
                    size += len(output_text or "")
                    calls.append(
                        {"name": short, "is_mcp": is_mcp, "input": raw.get("input") or {}}
                    )
                if not calls:
                    continue

                turn["steps"].append(
                    {
                        "lookup": [
                            {"name": c["name"], "input": c["input"]}
                            for c in calls
                            if c["is_mcp"] and c["name"] in LOOKUP_TOOLS
                        ],
                        "action": [
                            {"name": c["name"], "input": c["input"]}
                            for c in calls
                            if c["is_mcp"] and c["name"] not in LOOKUP_TOOLS
                        ],
                        "previous_results": new_results,
                        "results": results,
                        "bytes": size,
                        "text": (response.get("text") or "").strip()[:400],
                    }
                )

    out = []
    for turn in turns.values():
        turn.pop("_tool_msg_count", None)
        # A step's results arrive in the next request, so backfill them.
        for previous, step in zip(turn["steps"], turn["steps"][1:]):
            previous["results"] = step.get("previous_results") or ""
        if turn["steps"]:
            turn["steps"][-1]["results"] = ""
        turn["context"] = ""
        out.append(turn)
    return out


# ---------------------------------------------------------------- analysis


def annotate(turns: list[dict]) -> None:
    """Fill in the dependency flag for every step that holds a lookup call."""
    for turn in turns:
        known = turn.get("context") or ""
        previous_results = ""
        for step in turn["steps"]:
            step["dependent"] = (
                depends_on(step["lookup"], previous_results, known)
                if step["lookup"]
                else None
            )
            known += "\n" + previous_results
            if step.get("results"):
                previous_results = step["results"]
            elif step.get("previous_results"):
                previous_results = step["previous_results"]


def bursts_of(turn: dict) -> list[list[dict]]:
    """Maximal runs of consecutive lookup-only steps (an action step ends a burst)."""
    bursts, current = [], []
    for step in turn["steps"]:
        if step["lookup"] and not step["action"]:
            current.append(step)
        elif current:
            bursts.append(current)
            current = []
    if current:
        bursts.append(current)
    return bursts


def step_tools(step: dict) -> list[str]:
    return [call["name"] for call in step["lookup"]]


def proposal_kinds(step: dict) -> list[str]:
    return sorted({BATCH_NAMES.get(name, f"{name}_batch") for name in step_tools(step)})


def summarize(turns: list[dict]) -> dict:
    total_lookup = total_action = lookup_steps = parallel_steps = 0
    burst_lengths: Counter = Counter()
    signatures: Counter = Counter()
    transitions: Counter = Counter()
    proposals: Counter = Counter()
    per_project: Counter = Counter()
    project_saving: Counter = Counter()
    dependent_steps = merge_saving = 0
    burst_bytes: list[int] = []
    call_bytes: list[int] = []
    duplicates: Counter = Counter()
    turns_with_mcp = 0
    details: list[dict] = []

    turns = [turn for turn in turns if turn["steps"]]
    annotate(turns)

    for turn in turns:
        mcp_steps = [s for s in turn["steps"] if s["lookup"] or s["action"]]
        if not mcp_steps:
            continue
        turns_with_mcp += 1
        total_lookup += sum(len(s["lookup"]) for s in mcp_steps)
        total_action += sum(len(s["action"]) for s in mcp_steps)
        lookup_steps += sum(1 for s in mcp_steps if s["lookup"])
        parallel_steps += sum(1 for s in mcp_steps if len(s["lookup"]) > 1)
        per_project[turn["project"]] += sum(len(s["lookup"]) for s in mcp_steps)

        # Same lookup asked twice in one turn: the answer was already paid for.
        seen: Counter = Counter()
        for step in mcp_steps:
            for call in step["lookup"]:
                if call["name"] not in ("read_file", "get_type_members", "inspect_assembly"):
                    continue
                tokens = arg_names(call["input"])
                if tokens:
                    seen[(call["name"], tokens[0])] += 1
        for (tool, token), count in seen.items():
            if count > 1:
                duplicates[(tool, token)] += count - 1

        for step in mcp_steps:
            if step["lookup"]:
                per_call = (step.get("bytes") or 0) / len(step["lookup"])
                call_bytes.extend([per_call] * len(step["lookup"]))

        for burst in bursts_of(turn):
            burst_lengths[len(burst)] += 1
            signatures[" -> ".join("+".join(step_tools(step)) for step in burst)] += 1
            deps = sum(1 for step in burst[1:] if step["dependent"])
            dependent_steps += deps
            saving = len(burst) - (1 + deps)
            merge_saving += saving
            project_saving[turn["project"]] += saving
            burst_bytes.append(sum(step.get("bytes") or 0 for step in burst))

            for previous, step in zip(burst, burst[1:]):
                transitions[
                    (
                        "+".join(step_tools(previous)),
                        "+".join(step_tools(step)),
                        "DEPENDENT" if step["dependent"] else "independent",
                    )
                ] += 1
                if not step["dependent"]:
                    for kind in proposal_kinds(step):
                        proposals[kind] += 1

            if len(burst) >= 3 and len(details) < 80:
                details.append(
                    {
                        "session": turn["session"],
                        "project": turn["project"],
                        "started": burst[0].get("started") or turn["started"],
                        "steps": len(burst),
                        "minimal": 1 + deps,
                        "bytes": sum(step.get("bytes") or 0 for step in burst),
                        "intent": burst[0]["text"][:200],
                        "signature": " -> ".join(
                            "+".join(step_tools(step)) for step in burst
                        ),
                        "dependent_flags": [step["dependent"] for step in burst[1:]],
                        "calls": [
                            {"tool": call["name"], "args": arg_names(call["input"])}
                            for step in burst
                            for call in step["lookup"]
                        ],
                    }
                )

    return {
        "turns_with_mcp": turns_with_mcp,
        "lookup_calls": total_lookup,
        "action_calls": total_action,
        "lookup_steps": lookup_steps,
        "steps_with_parallel_calls": parallel_steps,
        "burst_lengths": dict(sorted(burst_lengths.items())),
        "bursts": sum(burst_lengths.values()),
        "dependent_steps": dependent_steps,
        "merge_saving": merge_saving,
        "median_burst_bytes": sorted(burst_bytes)[len(burst_bytes) // 2] if burst_bytes else 0,
        "median_call_bytes": int(sorted(call_bytes)[len(call_bytes) // 2]) if call_bytes else 0,
        "duplicate_reads": duplicates.most_common(10),
        "duplicate_read_total": sum(duplicates.values()),
        "signatures": signatures.most_common(25),
        "transitions": transitions.most_common(40),
        "proposals": proposals.most_common(20),
        "per_project_calls": per_project.most_common(10),
        "per_project_saving": project_saving.most_common(10),
        "details": details,
    }


def print_report(data: dict, source: str) -> None:
    lengths = data["burst_lengths"]
    bursts = data["bursts"]
    in_burst = sum(k * v for k, v in lengths.items())
    steps_after_first = in_burst - bursts

    print(f"Source: {source}")
    print(
        f"VisualMCP used in {data['turns_with_mcp']} turn(s): "
        f"{data['lookup_calls']} lookup call(s) + {data['action_calls']} action call(s)."
    )
    print(
        f"Lookup bursts: {bursts} burst(s), {in_burst} step(s), "
        f"{data['steps_with_parallel_calls']} step(s) already batching several calls."
    )
    print()
    print("Burst length (steps) -> count:")
    for length, count in lengths.items():
        print(f"  {length:2d} steps: {count:3d} burst(s)")
    print()
    print(
        f"Lookup steps after the first of their burst: {steps_after_first} "
        f"({data['dependent_steps']} DEPENDENT, "
        f"{steps_after_first - data['dependent_steps']} independent)."
    )
    print(
        f"Merge potential: {data['merge_saving']} round-trip(s) removable "
        f"({in_burst} -> {in_burst - data['merge_saving']} steps)."
    )
    print(
        f"Result size: median {data['median_call_bytes']:,} B per lookup call, "
        f"{data['median_burst_bytes']:,} B per burst (a merged call returns the burst's total)."
    )
    if data["duplicate_read_total"]:
        print(
            f"Duplicate lookups inside one turn: {data['duplicate_read_total']} "
            f"re-read(s) of something already read in the same turn."
        )
        for (tool, token), count in data["duplicate_reads"][:5]:
            print(f"  {count:2d}x extra  {tool}({token})")
    print()
    print("Top burst signatures:")
    for signature, count in data["signatures"][:10]:
        print(f"  {count:3d}x  {signature[:120]}")
    print()
    print("Top consecutive step pairs (independent = mergeable):")
    for (before, after, verdict), count in data["transitions"][:12]:
        print(f"  {count:3d}x  {verdict:11s}  {before[:40]}  ->  {after[:40]}")
    print()
    print("Batch tool that would have covered the independent steps:")
    for kind, count in data["proposals"][:10]:
        print(f"  {count:3d}x  {kind}")
    if data["per_project_saving"]:
        print()
        print("Merge potential by project:")
        for project, count in data["per_project_saving"][:8]:
            print(f"  {count:3d} round-trip(s)  {project or '(unknown)'}")


def main() -> int:
    home = Path.home()
    default_db = home / ".zcode" / "cli" / "db" / "db.sqlite"
    default_rollout = home / ".zcode" / "cli" / "rollout"

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--db", type=Path, default=default_db, help="ZCode sqlite store")
    parser.add_argument(
        "--rollout",
        action="store_true",
        help="read the raw model I/O transcripts instead of the sqlite store",
    )
    parser.add_argument("--rollout-dir", type=Path, default=default_rollout)
    parser.add_argument("--glob", default="model-io-*.jsonl")
    parser.add_argument("--project", help="only sessions whose directory contains this")
    parser.add_argument("--session", help="only turns whose session id contains this")
    parser.add_argument("--detail", type=int, metavar="N", help="print the N longest bursts")
    parser.add_argument("--json", action="store_true")
    parser.add_argument("--out", type=Path, help="write the JSON report to this file")
    args = parser.parse_args()

    if args.rollout:
        if not args.rollout_dir.is_dir():
            parser.error(f"rollout directory not found: {args.rollout_dir}")
        paths = sorted(args.rollout_dir.glob(args.glob))
        turns = load_from_rollout(paths)
        source = f"{args.rollout_dir} ({len(paths)} file(s))"
    else:
        if not args.db.is_file():
            parser.error(f"sqlite store not found: {args.db}")
        turns = load_from_db(args.db)
        source = str(args.db)

    if args.project:
        turns = [t for t in turns if args.project.lower() in (t["project"] or "").lower()]
    if args.session:
        turns = [t for t in turns if args.session in t["session"]]

    data = summarize(turns)
    if args.out:
        args.out.write_text(json.dumps(data, indent=2, ensure_ascii=False), encoding="utf-8")
    if args.json:
        print(json.dumps(data, indent=2, ensure_ascii=False))
        return 0

    print_report(data, source)

    if args.detail:
        print()
        print(f"Longest bursts (top {args.detail}):")
        for burst in sorted(data["details"], key=lambda b: -b["steps"])[: args.detail]:
            print(
                f"\n  {burst['started']}  {burst['project']}  "
                f"steps={burst['steps']} minimal={burst['minimal']} "
                f"bytes={burst['bytes']}"
            )
            print(f"    intent: {burst['intent'] or '(no prose)'}")
            print(f"    {burst['signature'][:200]}")
            print(f"    dependent (from step 2): {burst['dependent_flags']}")
            for call in burst["calls"]:
                print(f"      {call['tool']}({', '.join(call['args'])})")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
