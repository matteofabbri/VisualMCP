#!/usr/bin/env python3
"""ZCode / Claude Code PreToolUse hook: route read-only shell probes on .NET
repositories through VisualMCP instead of Bash, and keep plans and memory on the
board.

This is the POSIX counterpart of pre-tool-use.ps1 and behaves identically:

- reads the hook payload from stdin (tool_name + tool_input);
- when the command is a read-only probe (grep/rg, sed -n, cat/head/tail,
  ls/find, Select-String, Get-Content, Get-ChildItem) inside a .NET repository,
  answers with a PreToolUse "deny" decision whose reason names the MCP tool;
- refuses shell file rewrites (inline python writes, sed -i, heredoc/redirection
  file creation) in favour of Edit/Write;
- refuses a Write/Edit into a memory store (MEMORY.md, ~/.zcode/.../memories,
  ~/.claude/projects/<slug>/memory): the memory is board_remember/board_recall;
- refuses the CREATION of any .md at the repository root except the published
  documents (README, CONTRIBUTING, LICENSE, CHANGELOG, ...), and of a plan-like
  one (checklist, plan, backlog, todo, handoff, notes, gaps, memory) in docs/:
  a plan or a working note is a board card, and create_task_checklist writes one
  instead of a file;
- passes everything else: builds, tests, git, scripts, non-.NET trees, edits to
  documents that already exist;
- never fails the session: any error or unparsable payload exits 0;
- "vmcp-allow" anywhere in a command is the explicit escape hatch; a protected
  Write can be done deliberately through the MCP tool run_command.

It needs Python 3 only because the payload is JSON; the installer registers it
only when an interpreter is found, and says so when it is not.
"""

import glob
import json
import os
import re
import sys

PROBES = {
    "search": "search_text{pattern, path, glob} (use find_symbol / find_references for C# symbols, never text search)",
    "read": "read_file{path, fromLine, toLine}",
    "list": "list_directory{path} (or search_text to match file names)",
    "rewrite": "Edit (or Write for a new file), not a shell heredoc",
}

WRITE_TOOLS = {"Write", "Edit", "MultiEdit", "NotebookEdit"}

# A path inside a memory store: the client's own (~/.zcode/cli/memories/...) or Claude Code's
# (~/.claude/projects/<slug>/memory/...). MEMORY.md is checked separately, by name.
RX_MEMORY_STORE = re.compile(
    r"[\\/]\.zcode[\\/]cli[\\/]memories[\\/]|[\\/]\.claude[\\/]projects[\\/][^\\/]+[\\/]memory[\\/]",
    re.IGNORECASE,
)

# The names of a file that is a plan/backlog rather than documentation.
RX_PLAN_FILE = re.compile(r"(checklist|plan|backlog|todo|handoff|notes|gaps|memory)", re.IGNORECASE)

# The published documents a repository root may hold. A new .md there that is not one of these is a
# plan, a working note or a scratch file, which is exactly what the board exists to replace.
ROOT_DOCUMENTS = {
    "readme", "contributing", "license", "changelog", "code_of_conduct",
    "security", "authors", "notice", "support", "governance", "citation",
}


def memory_route():
    return (
        "VisualMCP: project memory lives on the board. "
        "Use board_remember{topic, body, kind} and read it back with board_recall{query}; pinned notes ride "
        "along in board_brief and read_project_docs. A MEMORY.md or a note file is not read by the next session. "
        "To write it deliberately, use the MCP tool run_command."
    )


def root_route():
    return (
        "VisualMCP: a new Markdown file at the repository root is not where this belongs. "
        "The root holds the published documents (README, CONTRIBUTING, LICENSE, CHANGELOG); documentation goes "
        "under docs/, and work to be done goes on the board: create_task_checklist{title, steps} or "
        "board_add{title, description, steps} create a card, board_move{status, note} records progress — no file "
        "is written and the card is what the next session reads. "
        "To write the file deliberately, use the MCP tool run_command."
    )


def plan_route():
    return (
        "VisualMCP: a plan/notes file is not the plan. "
        "Put the work on the board: create_task_checklist{title, steps} creates one card with the steps as a "
        "checklist (no file), board_add{title, steps} the same on a card you describe, board_move{status, note} "
        "records progress. The board is what the next session reads. "
        "To write the file deliberately, use the MCP tool run_command."
    )

RX_TOOLING = re.compile(
    r"(^|[\s;&|(])(dotnet|msbuild|nuget|git|gh|npm|pnpm|yarn|npx|node|python|python3|py|pip|"
    r"cargo|go|make|cmake|ninja|docker|kubectl|terraform|gradle|mvn|java|curl|wget|rm|rmdir|"
    r"mkdir|mv|cp|touch|chmod|chown|ln|tar|zip|unzip|7z)(\s|$)",
    re.IGNORECASE,
)
RX_WRITE_REDIRECT = re.compile(r">>?\s*(?!/dev/null)[^\s|&;]")
RX_SEARCH = re.compile(r"(^|[\s|;&(])(grep|egrep|fgrep|rg|ag|ack|Select-String)(\s|$)", re.IGNORECASE)
RX_SED_READ = re.compile(r"(^|[\s|;&(])sed\b[^|;&]*\s-n\b", re.IGNORECASE)
RX_CAT = re.compile(r"(^|[\s|;&(])(cat|head|tail|more|less|bat|nl|tac|Get-Content)(\s|$)", re.IGNORECASE)
RX_LIST = re.compile(r"(^|[\s|;&(])(ls|dir|find|tree|Get-ChildItem)(\s|$)", re.IGNORECASE)
RX_PYTHON_INLINE = re.compile(
    r"(^|[\s;&|(])(py|python3?(\.\d+)?)\b[^;|&]*?(<<\s*\S*|\s-c\b)", re.IGNORECASE
)
RX_WRITE_INTENT = re.compile(
    r"write_text\s*\(|\.write\s*\(|open\s*\([^)]*[\"']w|\.unlink\s*\(|os\.replace\s*\(|"
    r"shutil\.(copy|move|rmtree)|\.rename\s*\(",
    re.IGNORECASE,
)
RX_SED_IN_PLACE = re.compile(r"(^|[\s;&|(])sed\b[^|;&]*\s-i", re.IGNORECASE)
RX_SHELL_CREATE = re.compile(r"(^|[\s;&|(])(cat|printf|echo)\b[^|;&]*?>>?\s*[^\s|&;]+", re.IGNORECASE)
RX_TEE = re.compile(r"(^|[\s;&|(])tee(\s|$)", re.IGNORECASE)
RX_SEGMENT_SPLIT = re.compile(r"&&|\|\||;")


def is_dotnet_repository(directory, max_parents=3):
    """True when a .sln/.slnx sits in the directory or within max_parents above it."""
    return repository_root(directory, max_parents) is not None


def repository_root(directory, max_parents=3):
    """The nearest directory at or above `directory` holding a .sln/.slnx, or None."""
    if not directory:
        return None
    try:
        current = os.path.abspath(directory)
    except (OSError, ValueError):
        return None

    for _ in range(max_parents + 1):
        if os.path.isdir(current):
            for pattern in ("*.sln", "*.slnx"):
                if glob.glob(os.path.join(current, pattern)):
                    return current
        parent = os.path.dirname(current)
        if parent == current:
            break
        current = parent
    return None


def protected_write_reason(tool_name, path, cwd):
    """The deny reason for a memory-store write or a new plan-like .md, or None."""
    if not isinstance(path, str) or not path.strip():
        return None

    normalized = path.replace("\\", "/")
    if os.path.basename(normalized).lower() == "memory.md" or RX_MEMORY_STORE.search(normalized):
        return memory_route()

    # Only creation is refused, never the edit of a document that already exists: docs/gaps.md stays editable.
    if tool_name != "Write" or not normalized.lower().endswith(".md"):
        return None

    root = repository_root(cwd)
    if root is None:
        return None

    absolute = os.path.normcase(os.path.abspath(path if os.path.isabs(path) else os.path.join(cwd, path)))
    if os.path.exists(absolute):
        return None

    folder = os.path.dirname(absolute)
    # The repository root holds a fixed set of published documents. Anything else created there — a
    # plan, a checklist, a working note named after the task it describes — reads as documentation
    # to the next session while it is really a to-do list; that belongs on the board.
    if folder == os.path.normcase(root):
        stem = os.path.splitext(os.path.basename(absolute))[0].lower()
        return None if stem in ROOT_DOCUMENTS else root_route()

    if folder != os.path.normcase(os.path.join(root, "docs")):
        return None
    if not RX_PLAN_FILE.search(os.path.basename(absolute)):
        return None
    return plan_route()


def is_rewrite(command):
    """Whether the command writes files through the shell instead of a harness tool."""
    python_inline = RX_PYTHON_INLINE.search(command)
    write_intent = RX_WRITE_INTENT.search(command)
    if python_inline and write_intent:
        return True
    if RX_SED_IN_PLACE.search(command):
        return True
    return bool(RX_SHELL_CREATE.search(command) or RX_TEE.search(command))


def probe_for(command):
    """The probe this command performs, or None when it is legitimate shell work."""
    if is_rewrite(command):
        return "rewrite"

    for segment in RX_SEGMENT_SPLIT.split(command):
        segment = segment.strip()
        if not segment:
            continue
        if RX_TOOLING.search(segment):
            continue
        if RX_WRITE_REDIRECT.search(segment):
            continue
        if RX_SEARCH.search(segment):
            return "search"
        if RX_SED_READ.search(segment) or RX_CAT.search(segment):
            return "read"
        if RX_LIST.search(segment):
            return "list"
    return None


def reason_for(probe):
    if probe == "rewrite":
        return (
            "VisualMCP: this command rewrites files through the shell inside a .NET repository. "
            f"Use the harness tool instead: {PROBES['rewrite']} - no quoting or encoding traps, "
            "and the change stays reviewable. "
            "To keep Bash deliberately, include 'vmcp-allow' in the command."
        )
    name = {"search": "full-text search", "read": "line-range read", "list": "directory listing"}[probe]
    return (
        f"VisualMCP: this is a {name} shell probe inside a .NET repository. "
        f"Use the MCP tool instead: {PROBES[probe]}. "
        "The solution is already loaded; do not call load_solution first. "
        "If the shell form is genuinely required (binary files, counting, complex pipelines), "
        "call the MCP tool run_command with the exact same command. "
        "To keep Bash deliberately, include 'vmcp-allow' in the command."
    )


def deny(reason):
    # Schema (ZCode runtime, PreToolUse variant): exactly these three keys.
    payload = {
        "hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "permissionDecision": "deny",
            "permissionDecisionReason": reason,
        }
    }
    sys.stdout.write(json.dumps(payload, separators=(",", ":")))
    sys.stdout.flush()


def main():
    if sys.stdin.isatty():
        return 0

    raw = sys.stdin.read()
    if not raw.strip():
        return 0

    try:
        payload = json.loads(raw)
    except (ValueError, TypeError):
        return 0
    if not isinstance(payload, dict):
        return 0

    tool_name = payload.get("tool_name")
    tool_input = payload.get("tool_input") or {}
    cwd = payload.get("cwd") or os.getcwd()

    # A write into a memory store, or the creation of a plan-like .md, belongs on the board.
    if isinstance(tool_name, str) and tool_name in WRITE_TOOLS:
        path = tool_input.get("file_path") or tool_input.get("path") if isinstance(tool_input, dict) else None
        reason = protected_write_reason(tool_name, path, cwd)
        if reason:
            deny(reason)
        return 0

    if tool_name != "Bash":
        return 0

    command = tool_input.get("command") if isinstance(tool_input, dict) else None
    if not isinstance(command, str) or not command.strip():
        return 0

    if "vmcp-allow" in command:
        return 0

    if not is_dotnet_repository(cwd):
        return 0

    probe = probe_for(command)
    if probe is None:
        return 0

    deny(reason_for(probe))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception:
        # A hook must never break the session.
        sys.exit(0)
