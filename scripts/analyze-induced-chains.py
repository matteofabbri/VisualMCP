#!/usr/bin/env python3
"""Which VisualMCP call makes the *next* call necessary, and which tours repeat.

The other script (analyze-tool-sequences.py) measures lookup bursts. This one
works at call level and answers a different question: for every pair of
consecutive VisualMCP calls (A, B) in a turn, did B exist only because A's
output taught the model a name it did not have before? Those are *induced*
calls -- the second round-trip is the price of A's incomplete answer.

It also reports:

* **repeated calls** -- the same tool asked in the same turn for the same name
  again: the batch/caching candidate.
* **turn openings** -- the same ordered sequence of calls at the start of many
  turns: the higher-level "orientation" command.
* **fallbacks** -- a VisualMCP call followed by a built-in tool (Read, Grep,
  Glob, Explore, Task): our answer was not enough even for the model that uses
  us.

Sources: ZCode's sqlite store (~/.zcode/cli/db/db.sqlite), read-only.

Usage:
    python scripts/analyze-induced-chains.py
    python scripts/analyze-induced-chains.py --project VisualMCP
    python scripts/analyze-induced-chains.py --detail 12
    python scripts/analyze-induced-chains.py --json --out chains.json
"""

from __future__ import annotations

import argparse
import json
import sqlite3
import sys
from collections import Counter, defaultdict
from pathlib import Path

MCP_PREFIX = "mcp__VisualMCP__"

# Argument keys whose value is a name the model can only know after a result.
# Everything else (timeouts, booleans, line ranges) is not a learned name.
SKIP_KEYS = {
    "timeoutSeconds",
    "maxResults",
    "maxChars",
    "maxLines",
    "maxTypes",
    "top",
    "topN",
    "limit",
    "fromLine",
    "toLine",
    "start",
    "end",
    "line",
    "column",
    "dryRun",
    "recursive",
    "ignoreCase",
    "contextLines",
    "includeContent",
    "includeMembers",
    "includeInherited",
    "includeTransitive",
    "includePrerelease",
    "overwrite",
    "confirm",
    "force",
    "all",
    "noBuild",
    "runAnalyzers",
    "noCopyOutput",
    "restore",
    "configuration",
    "project",
    "projectName",
    "durationSeconds",
    "intervalSeconds",
    "priority",
    "status",
    "assignee",
    "shell",
    "version",
    "repo",
    "label",
}


def load_turns(db_path: Path):
    """Every turn with its VisualMCP calls and their individual outputs."""
    con = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True, timeout=15)
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
    turns: list[dict] = []
    turn = None
    step = 0
    pending_context = ""
    previous_session = None
    previous_role = None

    def close_turn():
        nonlocal turn
        if turn and any(c["is_mcp"] for c in turn["calls"]):
            turns.append(turn)
        turn = None

    for session_id, directory, message_id, _mseq, _pseq, data, created in con.execute(query):
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

        if turn is None:
            turn = {
                "session": session_id,
                "project": directory or "",
                "started": created,
                "context": pending_context[:200_000],
                "calls": [],
            }
            pending_context = ""
        previous_role = role

        kind = part.get("type")
        if kind == "step-start":
            step += 1
            continue
        if kind != "tool":
            continue

        raw_name = part.get("tool") or ""
        state = part.get("state") or {}
        output = state.get("output")
        output_text = output if isinstance(output, str) else json.dumps(output)
        turn["calls"].append(
            {
                "name": raw_name[len(MCP_PREFIX) :] if raw_name.startswith(MCP_PREFIX) else raw_name,
                "is_mcp": raw_name.startswith(MCP_PREFIX),
                "input": state.get("input") or {},
                "output": output_text or "",
                "step": step,
                "index": len(turn["calls"]),
            }
        )

    close_turn()
    con.close()
    return turns


def tokens_of(call: dict):
    """(argument key, candidate name) pairs that could have been learned from a result."""
    out = []
    for key, value in call["input"].items():
        if key in SKIP_KEYS or not isinstance(value, str):
            continue
        value = value.strip()
        if len(value) < 5:
            continue
        out.append((key, value))
        base = value.replace("\\", "/").rstrip("/").rsplit("/", 1)[-1]
        if base != value and len(base) >= 4:
            out.append((key, base))
        if base.endswith(".cs") and len(base) > 8:
            out.append((key, base[:-3]))
        if base.endswith(".cshtml") and len(base) > 12:
            out.append((key, base[:-7]))
    return out


def learned_from(call_b: dict, output_a: str, known_before: str):
    """Names in B's arguments that first appear in A's output."""
    previous = output_a.lower()
    known = known_before.lower()
    hits = []
    for key, token in tokens_of(call_b):
        lowered = token.lower()
        if len(lowered) < 5:
            continue
        if lowered in previous and lowered not in known:
            hits.append((key, token))
    return hits


def same_call_signature(call: dict) -> tuple[str, str]:
    """Identity of a call for the repeated-call count: the exact same question.

    Every string argument that is not a knob (timeout, limit, line range) is
    part of the identity, so the same directory searched with a different
    pattern is not counted as a repeat.
    """
    parts = []
    for key, value in sorted(call["input"].items()):
        if key in SKIP_KEYS or not isinstance(value, str) or not value.strip():
            continue
        parts.append(f"{key}={value.strip()}")
    return call["name"], "|".join(parts)


def paging_signature(call: dict) -> tuple[str, str]:
    """Identity for 'same target, different question': tool + first path-like argument."""
    for key, value in call["input"].items():
        if key in ("path", "file_path", "assemblyPath", "tracePath", "dumpPath") and isinstance(value, str):
            if len(value.strip()) >= 5:
                return call["name"], value.strip()
    for key, token in tokens_of(call):
        if key in ("path", "file_path", "assemblyPath", "tracePath", "dumpPath"):
            return call["name"], token
    return call["name"], ""


WRITE_TOOLS = {"Edit", "Write", "MultiEdit", "NotebookEdit"}


def is_writer(call: dict) -> bool:
    """Whether a call changes a file on disk."""
    if call["name"] in WRITE_TOOLS:
        return True
    if "dryRun" in call["input"]:
        return True
    return call["name"].endswith("_edit")


def write_target(call: dict) -> str:
    for key in ("file_path", "filePath", "path"):
        value = call["input"].get(key)
        if isinstance(value, str):
            return value
    return ""


def read_target(call: dict) -> str:
    for key in ("path", "file_path", "filePath"):
        value = call["input"].get(key)
        if isinstance(value, str):
            return value
    return ""


def analyze(turns: list[dict], detail: int) -> dict:
    pair_stats: dict[tuple[str, str], dict] = defaultdict(
        lambda: {
            "same_step": 0,
            "across": 0,
            "induced": 0,
            "induced_across": 0,
            "args": Counter(),
            "examples": [],
            "bytes_a": [],
            "bytes_b": [],
        }
    )
    induced_total = 0
    transitions_total = 0
    mcp_calls_total = 0
    repeats: Counter = Counter()
    cold_repeats: Counter = Counter()
    paging: Counter = Counter()
    paging_examples: dict[tuple[str, str], set] = defaultdict(set)
    openings: Counter = Counter()
    opening_turns: Counter = Counter()
    fallbacks: Counter = Counter()
    tool_turns: Counter = Counter()
    cooc: Counter = Counter()
    per_project_induced: Counter = Counter()
    details: list[dict] = []

    for turn in turns:
        calls = turn["calls"]
        mcp_calls = [c for c in calls if c["is_mcp"]]
        if not mcp_calls:
            continue
        mcp_calls_total += len(mcp_calls)

        # Which tools share a turn: a tour the model performs every time.
        present = sorted({c["name"] for c in mcp_calls})
        for name in present:
            tool_turns[name] += 1
        for first_index, first_name in enumerate(present):
            for second_name in present[first_index + 1 :]:
                cooc[(first_name, second_name)] += 1

        # Orientation tour: the first four VisualMCP calls of the turn, in order.
        if len(mcp_calls) >= 2:
            names = [c["name"] for c in mcp_calls[:4]]
            openings[" -> ".join(names)] += 1
            opening_turns[turn["project"]] += 1

        lows = [c["output"].lower() for c in calls]
        # When the pair (A, B) is examined, `known` must hold the context and
        # every output *before* A -- A's own output is what we test against.
        known = (turn["context"] or "").lower()
        seen_calls: dict[tuple[str, str], int] = {}
        writes: dict[str, list[int]] = defaultdict(list)

        for index, call in enumerate(calls):
            if is_writer(call):
                target = write_target(call).lower()
                if target:
                    writes[target].append(index)

            if index > 0:
                # The arguments of A were written by the model, so they were
                # already visible when B was formulated: they cannot be learned.
                known += "\n" + json.dumps(calls[index - 1]["input"]).lower()
                previous = calls[index - 1]
                hits = learned_from(call, lows[index - 1], known) if call["is_mcp"] else []

                key = (previous["name"], call["name"] if call["is_mcp"] else "!" + call["name"])
                stats = pair_stats[key]
                transitions_total += 1
                if previous["step"] == call["step"]:
                    stats["same_step"] += 1
                else:
                    stats["across"] += 1
                if hits:
                    induced_total += 1
                    stats["induced"] += 1
                    if previous["step"] != call["step"]:
                        stats["induced_across"] += 1
                    per_project_induced[turn["project"]] += 1
                    for arg_key, token in hits[:2]:
                        stats["args"][arg_key] += 1
                    if len(stats["examples"]) < 4:
                        stats["examples"].append(hits[0][1])
                    stats["bytes_a"].append(len(previous["output"]))
                    stats["bytes_b"].append(len(call["output"]))
                if hits and len(details) < 120 and previous["step"] != call["step"]:
                    details.append(
                        {
                            "session": turn["session"],
                            "project": turn["project"],
                            "started": turn["started"],
                            "pair": f"{previous['name']} -> {call['name']}",
                            "arg": hits[0][0],
                            "token": hits[0][1],
                            "bytes_a": len(previous["output"]),
                            "bytes_b": len(call["output"]),
                        }
                    )

                # Same exact question asked again in the same turn.
                if call["is_mcp"]:
                    signature = same_call_signature(call)
                    if signature[1]:
                        repeats[signature] += 1
                        first = seen_calls.get(signature)
                        if first is None:
                            seen_calls[signature] = index
                        else:
                            target = read_target(call).lower()
                            hot = any(first < w < index for w in writes.get(target, []))
                            if not hot:
                                cold_repeats[signature] += 1

                # Same tool, same target, a different question: hand-made paging.
                if call["is_mcp"] and previous["is_mcp"]:
                    page = paging_signature(call)
                    if page[1] and page == paging_signature(previous) and same_call_signature(call) != same_call_signature(previous):
                        paging[page] += 1
                        query = same_call_signature(call)[1]
                        paging_examples[page].add(query[:80])

                if not call["is_mcp"] and previous["is_mcp"]:
                    fallbacks[(previous["name"], call["name"])] += 1

            # Lag the known set by one call: after processing index, the output
            # of index-1 becomes visible.
            if index >= 1:
                known += "\n" + lows[index - 1]

    rows = []
    for (a, b), stats in pair_stats.items():
        if stats["induced"] == 0:
            continue
        bytes_a = stats["bytes_a"]
        bytes_b = stats["bytes_b"]
        rows.append(
            {
                "from": a,
                "to": b,
                "induced": stats["induced"],
                "induced_across_steps": stats["induced_across"],
                "occurrences": stats["same_step"] + stats["across"],
                "same_step": stats["same_step"],
                "args": stats["args"].most_common(3),
                "examples": stats["examples"],
                "median_bytes_a": sorted(bytes_a)[len(bytes_a) // 2] if bytes_a else 0,
                "median_bytes_b": sorted(bytes_b)[len(bytes_b) // 2] if bytes_b else 0,
            }
        )
    rows.sort(key=lambda row: (-row["induced_across_steps"], -row["induced"], row["from"], row["to"]))

    repeated = [
        {"tool": tool, "name": name, "calls": count, "extra": count - 1}
        for (tool, name), count in repeats.items()
        if count > 1
    ]
    repeated.sort(key=lambda row: -row["extra"])
    cold = [
        {"tool": tool, "name": name, "extra": count}
        for (tool, name), count in cold_repeats.items()
        if count
    ]
    cold.sort(key=lambda row: -row["extra"])
    paged = [
        {"tool": tool, "target": target, "extra": count, "questions": sorted(paging_examples[(tool, target)])[:5]}
        for (tool, target), count in paging.items()
    ]
    paged.sort(key=lambda row: -row["extra"])
    tours = []
    for (first_name, second_name), count in cooc.items():
        if tool_turns[first_name] < 5 or tool_turns[second_name] < 5:
            continue
        share = count / min(tool_turns[first_name], tool_turns[second_name])
        if share >= 0.7:
            tours.append(
                {
                    "a": first_name,
                    "b": second_name,
                    "turns": count,
                    "share": round(share, 2),
                    "a_turns": tool_turns[first_name],
                    "b_turns": tool_turns[second_name],
                }
            )
    tours.sort(key=lambda row: (-row["turns"], -row["share"]))

    return {
        "turns": len(turns),
        "calls": mcp_calls_total,
        "transitions": transitions_total,
        "induced_transitions": induced_total,
        "induced_round_trips": sum(row["induced_across_steps"] for row in rows),
        "pairs": rows,
        "repeated": repeated[:60],
        "repeated_extra_total": sum(row["extra"] for row in repeated),
        "cold_repeated": cold[:40],
        "cold_extra_total": sum(row["extra"] for row in cold),
        "paged": paged[:30],
        "tours": tours[:20],
        "openings": openings.most_common(20),
        "projects": [(project, count) for project, count in opening_turns.most_common(6)],
        "induced_by_project": per_project_induced.most_common(10),
        "fallbacks": fallbacks.most_common(15),
        "details": details[:detail],
    }


def print_report(data: dict, source: str) -> None:
    print(f"Source: {source}")
    print(
        f"{data['turns']} turn(s) with VisualMCP, {data['calls']} call(s); "
        f"{data['transitions']} consecutive pair(s), {data['induced_transitions']} induced."
    )
    print(
        f"Induced round-trips (B in a later step, only because A returned a name): "
        f"{data['induced_round_trips']}"
    )
    print()
    print("Induced pairs (from -> to), ranked by avoidable round-trips:")
    print(f"  {'rt':>4}  {'ind':>4}  {'same':>4}  {'A -> B':<46} args")
    for row in data["pairs"][:20]:
        args = ",".join(key for key, _ in row["args"])
        print(
            f"  {row['induced_across_steps']:4d}  {row['induced']:4d}  {row['same_step']:4d}  "
            f"{row['from'] + ' -> ' + row['to']:<46.46} {args}"
        )
        if row["examples"]:
            print(f"        e.g. {', '.join(row['examples'][:3])}")
    print()
    print(
        f"Repeated calls inside one turn: {data['repeated_extra_total']} extra call(s), of which "
        f"{data['cold_extra_total']} re-asked the same question on a file NOT modified in between."
    )
    for row in data["cold_repeated"][:12]:
        print(f"  {row['extra']:3d} cold  {row['tool']}({row['name'][:100]})")
    if data["paged"]:
        print()
        print("Same tool, same target, different question (hand-made paging):")
        for row in data["paged"][:10]:
            target = row["target"].replace("\\", "/").rsplit("/", 1)[-1]
            print(f"  {row['extra']:3d} extra  {row['tool']}({target})")
            for question in row["questions"][:3]:
                print(f"        {question}")
    print()
    print("Tools that share a turn (a tour performed every time):")
    for row in data["tours"][:12]:
        print(
            f"  {row['turns']:3d} turn(s)  {row['a']}({row['a_turns']}) + {row['b']}({row['b_turns']})  "
            f"share={row['share']}"
        )
    print()
    print("Turn openings (first calls, in order) -> turns:")
    for sequence, count in data["openings"][:10]:
        print(f"  {count:3d}x  {sequence}")
    if data["fallbacks"]:
        print()
        print("VisualMCP call followed by a built-in tool (our answer was not enough):")
        for (ours, builtin), count in data["fallbacks"][:10]:
            print(f"  {count:3d}x  {ours} -> {builtin}")
    print()
    print("Induced round-trips by project:")
    for project, count in data["induced_by_project"]:
        print(f"  {count:3d}  {project or '(unknown)'}")


def main() -> int:
    home = Path.home()
    default_db = home / ".zcode" / "cli" / "db" / "db.sqlite"
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--db", type=Path, default=default_db)
    parser.add_argument("--project", help="only sessions whose directory contains this")
    parser.add_argument("--session", help="only turns whose session id contains this")
    parser.add_argument("--detail", type=int, default=10)
    parser.add_argument("--json", action="store_true")
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()

    if not args.db.is_file():
        parser.error(f"sqlite store not found: {args.db}")
    turns = load_turns(args.db)
    if args.project:
        turns = [t for t in turns if args.project.lower() in (t["project"] or "").lower()]
    if args.session:
        turns = [t for t in turns if args.session in t["session"]]

    data = analyze(turns, args.detail)
    if args.out:
        args.out.write_text(json.dumps(data, indent=2, ensure_ascii=False), encoding="utf-8")
    if args.json:
        print(json.dumps(data, indent=2, ensure_ascii=False))
        return 0
    print_report(data, str(args.db))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
