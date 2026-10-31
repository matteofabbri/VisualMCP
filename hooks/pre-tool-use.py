#!/usr/bin/env python3
"""ZCode / Claude Code PreToolUse hook: route read-only shell probes on .NET
repositories through VisualMCP instead of Bash.

This is the POSIX counterpart of pre-tool-use.ps1 and behaves identically:

- reads the hook payload from stdin (tool_name + tool_input.command);
- when the command is a read-only probe (grep/rg, sed -n, cat/head/tail,
  ls/find, Select-String, Get-Content, Get-ChildItem) inside a .NET repository,
  answers with a PreToolUse "deny" decision whose reason names the MCP tool;
- refuses shell file rewrites (inline python writes, sed -i, heredoc/redirection
  file creation) in favour of Edit/Write;
- passes everything else: builds, tests, git, scripts, non-.NET trees;
- never fails the session: any error or unparsable payload exits 0;
- "vmcp-allow" anywhere in the command is the explicit escape hatch.

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
    if not directory:
        return False
    try:
        current = os.path.abspath(directory)
    except (OSError, ValueError):
        return False

    for _ in range(max_parents + 1):
        if os.path.isdir(current):
            for pattern in ("*.sln", "*.slnx"):
                if glob.glob(os.path.join(current, pattern)):
                    return True
        parent = os.path.dirname(current)
        if parent == current:
            break
        current = parent
    return False


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

    if payload.get("tool_name") != "Bash":
        return 0

    tool_input = payload.get("tool_input") or {}
    command = tool_input.get("command") if isinstance(tool_input, dict) else None
    if not isinstance(command, str) or not command.strip():
        return 0

    if "vmcp-allow" in command:
        return 0

    cwd = payload.get("cwd") or os.getcwd()
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
