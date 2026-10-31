#!/bin/sh
# VisualMCP installer for Linux and macOS: builds a stable Release executable and
# registers it as an MCP server for ZCode and/or Claude Code. The POSIX counterpart
# of scripts/install.ps1, and the two keep the same scopes and the same config shapes.
#
# Usage:
#   ./scripts/install.sh [--scope all|zcode|user|project|local] [--project-dir DIR]
#                        [--uninstall] [--stop-running] [--no-hooks] [--hooks-only]
#
# Scopes:
#   all      ZCode (user) + Claude Code (user) + the Claude desktop app. Default.
#            Hosts that are not installed are skipped with a warning.
#   zcode    ~/.zcode/cli/config.json -> mcp.servers.VisualMCP
#   user     Claude Code, global (every folder on this machine)
#   project  Claude Code, a .mcp.json in the target project (shareable/committable)
#   local    Claude Code, private to you, tied to the target project folder
#
# The routing guard (hooks/pre-tool-use.py + the AGENTS.md block) is installed for
# the zcode and user scopes when Python 3 is available; --no-hooks skips it and
# --hooks-only refreshes it without publishing.
#
# PublishSingleFile must stay OFF: MSBuildWorkspace locates its out-of-process
# BuildHost through the assembly location, which is empty in a single-file bundle.

set -eu

SCOPE=all
PROJECT_DIR=$(pwd)
UNINSTALL=0
STOP_RUNNING=0
NO_HOOKS=0
HOOKS_ONLY=0

usage() {
    cat <<'USAGE'
VisualMCP installer for Linux and macOS

Usage: scripts/install.sh [options]

  -s, --scope all|zcode|user|project|local
                        Where to register the server (default: all)
  -p, --project-dir DIR For project/local scope, the folder to attach to (default: .)
  -u, --uninstall       Remove the registration at this scope
      --stop-running    Stop running VisualMCP instances before publishing
      --no-hooks        Skip the routing guard (hook + AGENTS.md block)
      --hooks-only      Refresh the routing guard without publishing
  -h, --help            This text

After installing, RESTART ZCode / Claude Code so it reloads the configuration.
USAGE
}

while [ $# -gt 0 ]; do
    case "$1" in
        -s|--scope)      SCOPE=${2:?--scope needs a value}; shift 2 ;;
        -p|--project-dir) PROJECT_DIR=${2:?--project-dir needs a value}; shift 2 ;;
        -u|--uninstall)  UNINSTALL=1; shift ;;
        --stop-running)  STOP_RUNNING=1; shift ;;
        --no-hooks)      NO_HOOKS=1; shift ;;
        --hooks-only)    HOOKS_ONLY=1; shift ;;
        -h|--help)       usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
    esac
done

case "$SCOPE" in
    all|zcode|user|project|local) ;;
    *) echo "Invalid scope '$SCOPE': use all, zcode, user, project or local." >&2; exit 2 ;;
esac

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
REPO_ROOT=$(dirname -- "$SCRIPT_DIR")
CSPROJ="$REPO_ROOT/src/VisualMCP/VisualMCP.csproj"
HOOK_SOURCE="$REPO_ROOT/hooks/pre-tool-use.py"
BLOCK_SOURCE="$REPO_ROOT/hooks/agents-block.md"

HOME_DIR=${HOME:?HOME is not set}
ZCODE_APP="$HOME_DIR/.zcode/mcp-servers/VisualMCP/app"
CLAUDE_APP="$HOME_DIR/.claude/mcp-servers/VisualMCP/app"
ZCODE_CONFIG="$HOME_DIR/.zcode/cli/config.json"
ZCODE_CONFIG_DIR="$HOME_DIR/.zcode/cli"
ZCODE_AGENTS="$HOME_DIR/.zcode/AGENTS.md"
ZCODE_HOOK_DIR="$HOME_DIR/.zcode/mcp-servers/VisualMCP/hooks"
CLAUDE_SETTINGS="$HOME_DIR/.claude/settings.json"
CLAUDE_HOOK_DIR="$HOME_DIR/.claude/mcp-servers/VisualMCP/hooks"

if [ "$(uname -s 2>/dev/null || echo unknown)" = "Darwin" ]; then
    DESKTOP_CONFIG="$HOME_DIR/Library/Application Support/Claude/claude_desktop_config.json"
else
    DESKTOP_CONFIG="${XDG_CONFIG_HOME:-$HOME_DIR/.config}/Claude/claude_desktop_config.json"
fi

case "$SCOPE" in
    all)      WANTS_ZCODE=1; WANTS_CLAUDE=1; CLAUDE_SCOPE=user ;;
    zcode)    WANTS_ZCODE=1; WANTS_CLAUDE=0; CLAUDE_SCOPE=user ;;
    user)     WANTS_ZCODE=0; WANTS_CLAUDE=1; CLAUDE_SCOPE=user ;;
    project)  WANTS_ZCODE=0; WANTS_CLAUDE=1; CLAUDE_SCOPE=project ;;
    local)    WANTS_ZCODE=0; WANTS_CLAUDE=1; CLAUDE_SCOPE=local ;;
esac

say()  { printf '%s\n' "$*"; }
step() { printf '\n==> %s\n' "$*"; }
ok()   { printf '    OK  %s\n' "$*"; }
skip() { printf '    --  %s\n' "$*"; }
warn() { printf '    !!  %s\n' "$*" >&2; }

# --- Python 3: JSON editing and the routing hook ----------------------------
PYTHON=""
for candidate in python3 python; do
    if command -v "$candidate" >/dev/null 2>&1 &&
       "$candidate" -c 'import sys; sys.exit(0 if sys.version_info >= (3, 7) else 1)' >/dev/null 2>&1; then
        PYTHON=$(command -v "$candidate")
        break
    fi
done

# Applies one JSON edit. Modes:
#   zcode-set|zcode-remove <config> [command] [arg]
#   mcpservers-set|mcpservers-remove <config> [command] [arg]
#   zcode-hook-set|zcode-hook-remove <config> <command>
#   claude-hook-set|claude-hook-remove <config> <command>
#   agents-block-set|agents-block-remove <file>
json_edit() {
    if [ -z "$PYTHON" ]; then
        return 1
    fi
    "$PYTHON" - "$@" "$BLOCK_SOURCE" <<'PY'
import json
import os
import re
import sys
import tempfile

MODE = sys.argv[1]
PATH = sys.argv[2]
EXTRA = sys.argv[3] if len(sys.argv) > 3 else None
EXTRA2 = sys.argv[4] if len(sys.argv) > 4 else None
BLOCK_SOURCE = sys.argv[-1] if MODE.startswith("agents-block") else None

SERVER = "VisualMCP"
OURS = re.compile(r"pre-tool-use\.(py|ps1)", re.IGNORECASE)


def load(path):
    try:
        with open(path, "r", encoding="utf-8-sig") as handle:
            raw = handle.read()
    except FileNotFoundError:
        return {}
    if not raw.strip():
        return {}
    return json.loads(raw)


def save(path, data):
    folder = os.path.dirname(path)
    if folder:
        os.makedirs(folder, exist_ok=True)
    handle, temporary = tempfile.mkstemp(dir=folder or ".", prefix=".visualmcp-", suffix=".json")
    try:
        with os.fdopen(handle, "w", encoding="utf-8", newline="\n") as stream:
            json.dump(data, stream, indent=2, ensure_ascii=False)
            stream.write("\n")
        os.replace(temporary, path)
    except BaseException:
        try:
            os.unlink(temporary)
        except OSError:
            pass
        raise


def child(parent, name):
    value = parent.get(name)
    if not isinstance(value, dict):
        value = {}
        parent[name] = value
    return value


def server_entry(command, argument=None):
    entry = {"type": "stdio", "command": command, "args": [], "timeoutMs": 60000}
    if argument:
        entry["args"] = [argument]
    return entry


def hook_entry(command):
    return {
        "type": "command",
        "command": command,
        "timeout": 15,
        "statusMessage": "VisualMCP: checking for a shell probe...",
    }


def is_ours(matcher):
    hooks = matcher.get("hooks") if isinstance(matcher, dict) else None
    for hook in hooks or []:
        if isinstance(hook, dict) and OURS.search(str(hook.get("command", ""))):
            return True
    return False


def keep_foreign(entries):
    return [entry for entry in entries or [] if not is_ours(entry)]


if MODE == "zcode-set":
    config = load(PATH)
    child(child(config, "mcp"), "servers")[SERVER] = server_entry(EXTRA, EXTRA2)
    save(PATH, config)
elif MODE == "zcode-remove":
    config = load(PATH)
    servers = config.get("mcp", {}).get("servers")
    if isinstance(servers, dict):
        servers.pop(SERVER, None)
        save(PATH, config)
elif MODE == "mcpservers-set":
    config = load(PATH)
    child(config, "mcpServers")[SERVER] = server_entry(EXTRA, EXTRA2)
    save(PATH, config)
elif MODE == "mcpservers-remove":
    config = load(PATH)
    servers = config.get("mcpServers")
    if isinstance(servers, dict) and SERVER in servers:
        servers.pop(SERVER, None)
        save(PATH, config)
elif MODE == "zcode-hook-set":
    config = load(PATH)
    hooks = child(config, "hooks")
    hooks["enabled"] = True
    events = child(hooks, "events")
    events["PreToolUse"] = keep_foreign(events.get("PreToolUse")) + [
        {"matcher": "Bash", "hooks": [hook_entry(EXTRA)]}
    ]
    save(PATH, config)
elif MODE == "zcode-hook-remove":
    config = load(PATH)
    hooks = config.get("hooks")
    if isinstance(hooks, dict):
        events = hooks.get("events")
        if isinstance(events, dict) and "PreToolUse" in events:
            kept = keep_foreign(events.get("PreToolUse"))
            if kept:
                events["PreToolUse"] = kept
            else:
                events.pop("PreToolUse", None)
            if not events:
                hooks.pop("events", None)
        if not hooks:
            config.pop("hooks", None)
        save(PATH, config)
elif MODE == "claude-hook-set":
    config = load(PATH)
    hooks = child(config, "hooks")
    hooks["PreToolUse"] = keep_foreign(hooks.get("PreToolUse")) + [
        {"matcher": "Bash", "hooks": [hook_entry(EXTRA)]}
    ]
    save(PATH, config)
elif MODE == "claude-hook-remove":
    config = load(PATH)
    hooks = config.get("hooks")
    if isinstance(hooks, dict) and "PreToolUse" in hooks:
        kept = keep_foreign(hooks.get("PreToolUse"))
        if kept:
            hooks["PreToolUse"] = kept
        else:
            hooks.pop("PreToolUse", None)
        if not hooks:
            config.pop("hooks", None)
        save(PATH, config)
elif MODE in ("agents-block-set", "agents-block-remove"):
    marker = re.compile(r"\r?\n?<!-- VisualMCP:begin.*?<!-- VisualMCP:end -->\r?\n?", re.DOTALL)
    try:
        with open(PATH, "r", encoding="utf-8-sig") as handle:
            text = handle.read()
    except FileNotFoundError:
        text = ""
    cleaned = marker.sub("", text).strip()

    if MODE == "agents-block-remove":
        if not cleaned:
            try:
                os.unlink(PATH)
            except FileNotFoundError:
                pass
        else:
            with open(PATH, "w", encoding="utf-8", newline="\n") as handle:
                handle.write(cleaned + "\n")
    else:
        with open(BLOCK_SOURCE, "r", encoding="utf-8") as handle:
            block = handle.read().strip()
        content = (cleaned + "\n\n" + block + "\n") if cleaned else (block + "\n")
        os.makedirs(os.path.dirname(PATH), exist_ok=True)
        with open(PATH, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(content)
else:
    sys.exit(f"unknown mode {MODE}")
PY
}

# --- Routing guard ----------------------------------------------------------
install_hooks() {
    [ "$NO_HOOKS" -eq 1 ] && { skip "hooks ~ skipped (--no-hooks)"; return 0; }
    [ "$SCOPE" = "project" ] || [ "$SCOPE" = "local" ] && {
        skip "hooks ~ skipped: hooks and AGENTS.md are machine-level"
        return 0
    }
    if [ -z "$PYTHON" ]; then
        skip "hooks ~ skipped: no Python 3 found (the routing hook reads a JSON payload)"
        return 0
    fi
    if [ ! -f "$HOOK_SOURCE" ]; then
        skip "hooks ~ skipped: $HOOK_SOURCE not found"
        return 0
    fi

    if [ "$WANTS_ZCODE" -eq 1 ] && [ -d "$ZCODE_CONFIG_DIR" ]; then
        mkdir -p "$ZCODE_HOOK_DIR"
        cp "$HOOK_SOURCE" "$ZCODE_HOOK_DIR/pre-tool-use.py"
        if json_edit zcode-hook-set "$ZCODE_CONFIG" "$PYTHON \"$ZCODE_HOOK_DIR/pre-tool-use.py\""; then
            ok "ZCode   -> $ZCODE_CONFIG (hooks.events.PreToolUse, matcher 'Bash')"
            ok "ZCode   -> $ZCODE_HOOK_DIR/pre-tool-use.py"
        else
            warn "ZCode   ~ could not update $ZCODE_CONFIG"
        fi
        if json_edit agents-block-set "$ZCODE_AGENTS"; then
            ok "ZCode   -> $ZCODE_AGENTS (routing block)"
        else
            warn "ZCode   ~ could not update $ZCODE_AGENTS"
        fi
    elif [ "$WANTS_ZCODE" -eq 1 ]; then
        skip "ZCode   ~ not found ($ZCODE_CONFIG_DIR): hook skipped"
    fi

    if [ "$WANTS_CLAUDE" -eq 1 ] && [ -d "$HOME_DIR/.claude" ]; then
        mkdir -p "$CLAUDE_HOOK_DIR"
        cp "$HOOK_SOURCE" "$CLAUDE_HOOK_DIR/pre-tool-use.py"
        if json_edit claude-hook-set "$CLAUDE_SETTINGS" "$PYTHON \"$CLAUDE_HOOK_DIR/pre-tool-use.py\""; then
            ok "Claude  -> $CLAUDE_SETTINGS (hooks.PreToolUse, matcher 'Bash')"
            ok "Claude  -> $CLAUDE_HOOK_DIR/pre-tool-use.py"
        else
            warn "Claude  ~ could not update $CLAUDE_SETTINGS"
        fi
    fi
}

remove_hooks() {
    [ -f "$ZCODE_HOOK_DIR/pre-tool-use.py" ] && rm -f "$ZCODE_HOOK_DIR/pre-tool-use.py" && ok "ZCode   <- removed $ZCODE_HOOK_DIR/pre-tool-use.py"
    [ -f "$CLAUDE_HOOK_DIR/pre-tool-use.py" ] && rm -f "$CLAUDE_HOOK_DIR/pre-tool-use.py" && ok "Claude  <- removed $CLAUDE_HOOK_DIR/pre-tool-use.py"
    if [ "$WANTS_ZCODE" -eq 1 ] && [ -f "$ZCODE_CONFIG" ]; then
        json_edit zcode-hook-remove "$ZCODE_CONFIG" || warn "ZCode   ~ could not update $ZCODE_CONFIG"
        json_edit agents-block-remove "$ZCODE_AGENTS" || warn "ZCode   ~ could not update $ZCODE_AGENTS"
        ok "ZCode   <- hook and routing block removed"
    fi
    if [ "$WANTS_CLAUDE" -eq 1 ] && [ -f "$CLAUDE_SETTINGS" ]; then
        json_edit claude-hook-remove "$CLAUDE_SETTINGS" || warn "Claude  ~ could not update $CLAUDE_SETTINGS"
        ok "Claude  <- hook removed"
    fi
}

# --- Claude CLI -------------------------------------------------------------
find_claude() {
    if command -v claude >/dev/null 2>&1; then
        command -v claude
        return 0
    fi
    if [ -x "$HOME_DIR/.local/bin/claude" ]; then
        printf '%s\n' "$HOME_DIR/.local/bin/claude"
        return 0
    fi
    return 1
}

CLAUDE=""
if [ "$WANTS_CLAUDE" -eq 1 ]; then
    CLAUDE=$(find_claude || true)
    if [ -z "$CLAUDE" ] && [ "$SCOPE" != "all" ]; then
        echo "Could not find the 'claude' CLI. Open the terminal where Claude Code works and re-run, or use --scope zcode." >&2
        exit 1
    fi
fi

WORK_DIR="$REPO_ROOT"
if [ "$CLAUDE_SCOPE" != "user" ]; then
    [ -d "$PROJECT_DIR" ] || { echo "Project folder not found: $PROJECT_DIR" >&2; exit 1; }
    WORK_DIR=$(CDPATH= cd -- "$PROJECT_DIR" && pwd)
fi

# --- Uninstall --------------------------------------------------------------
if [ "$UNINSTALL" -eq 1 ]; then
    step "Removing 'VisualMCP' ($SCOPE scope)..."

    if [ "$WANTS_ZCODE" -eq 1 ]; then
        if [ -f "$ZCODE_CONFIG" ]; then
            json_edit zcode-remove "$ZCODE_CONFIG" && ok "ZCode   <- removed from $ZCODE_CONFIG"
        fi
        if [ -d "$ZCODE_APP" ]; then rm -rf "$ZCODE_APP" && ok "ZCode   <- removed $ZCODE_APP"; fi
    fi

    if [ "$WANTS_CLAUDE" -eq 1 ]; then
        if [ -n "$CLAUDE" ]; then
            ( cd "$WORK_DIR" && "$CLAUDE" mcp remove --scope "$CLAUDE_SCOPE" VisualMCP >/dev/null 2>&1 || true )
            ok "Claude  <- removed from Claude Code ($CLAUDE_SCOPE)"
        fi
        if [ "$CLAUDE_SCOPE" = "user" ] && [ -f "$DESKTOP_CONFIG" ]; then
            json_edit mcpservers-remove "$DESKTOP_CONFIG" && ok "Desktop <- removed from $DESKTOP_CONFIG"
        fi
        if [ -d "$CLAUDE_APP" ]; then rm -rf "$CLAUDE_APP" && ok "Claude  <- removed $CLAUDE_APP"; fi
    fi

    remove_hooks

    say ""
    say "Done. Restart ZCode / Claude Code to apply."
    exit 0
fi

if [ "$HOOKS_ONLY" -eq 1 ]; then
    step "Installing VisualMCP routing guard ($SCOPE)..."
    install_hooks
    say ""
    say "Hook denies read-only shell probes on .NET repos and names the MCP tool instead."
    say "Escape hatch: include 'vmcp-allow' in a command to keep Bash deliberately."
    say "RESTART ZCode / Claude Code: hooks are read at session start."
    exit 0
fi

# --- Stop running instances (optional) --------------------------------------
if [ "$STOP_RUNNING" -eq 1 ]; then
    pkill -f "$ZCODE_APP/VisualMCP" 2>/dev/null || true
    pkill -f "$CLAUDE_APP/VisualMCP" 2>/dev/null || true
    pkill -f "VisualMCP.dll" 2>/dev/null || true
    sleep 1
    ok "stopped running VisualMCP instances (if any)"
fi

# --- Publish ---------------------------------------------------------------
[ -f "$CSPROJ" ] || { echo "Project not found: $CSPROJ" >&2; exit 1; }

PRIMARY_DIR="$CLAUDE_APP"
[ "$WANTS_ZCODE" -eq 1 ] && PRIMARY_DIR="$ZCODE_APP"

step "Publishing Release build to $PRIMARY_DIR ..."
mkdir -p "$PRIMARY_DIR"
if ! dotnet publish "$CSPROJ" -c Release --self-contained false -o "$PRIMARY_DIR"; then
    echo "dotnet publish failed. If a file is in use, quit ZCode / Claude Code and re-run (or pass --stop-running)." >&2
    exit 1
fi

COMMAND="$PRIMARY_DIR/VisualMCP"
ARGUMENT=""
if [ -f "$PRIMARY_DIR/VisualMCP.exe" ]; then
    # Windows apphost, when this script runs under MSYS/Git Bash.
    COMMAND="$PRIMARY_DIR/VisualMCP.exe"
elif [ ! -f "$COMMAND" ]; then
    # No apphost on this platform: run the managed entry point through dotnet.
    COMMAND="dotnet"
    ARGUMENT="$PRIMARY_DIR/VisualMCP.dll"
fi
[ -f "$PRIMARY_DIR/VisualMCP.dll" ] || { echo "Expected build output not produced in $PRIMARY_DIR" >&2; exit 1; }

if [ "$WANTS_ZCODE" -eq 1 ] && [ "$WANTS_CLAUDE" -eq 1 ]; then
    mkdir -p "$CLAUDE_APP"
    cp -R "$PRIMARY_DIR"/. "$CLAUDE_APP"/
fi

EXE_FOR_ZCODE="$ZCODE_APP/VisualMCP"
EXE_FOR_CLAUDE="$CLAUDE_APP/VisualMCP"
if [ -f "$ZCODE_APP/VisualMCP.exe" ]; then EXE_FOR_ZCODE="$ZCODE_APP/VisualMCP.exe"; fi
if [ -f "$CLAUDE_APP/VisualMCP.exe" ]; then EXE_FOR_CLAUDE="$CLAUDE_APP/VisualMCP.exe"; fi
ZCODE_COMMAND="$COMMAND"
ZCODE_ARGUMENT="$ARGUMENT"
CLAUDE_COMMAND="$COMMAND"
CLAUDE_ARGUMENT="$ARGUMENT"
if [ "$WANTS_ZCODE" -eq 1 ] && [ "$WANTS_CLAUDE" -eq 1 ]; then
    ZCODE_COMMAND="$EXE_FOR_ZCODE"
    CLAUDE_COMMAND="$EXE_FOR_CLAUDE"
    if [ ! -f "$EXE_FOR_ZCODE" ]; then
        ZCODE_COMMAND="dotnet"
        ZCODE_ARGUMENT="$ZCODE_APP/VisualMCP.dll"
        CLAUDE_COMMAND="dotnet"
        CLAUDE_ARGUMENT="$CLAUDE_APP/VisualMCP.dll"
    fi
fi

# --- Register --------------------------------------------------------------
step "Registering 'VisualMCP' ($SCOPE scope) ..."

if [ "$WANTS_ZCODE" -eq 1 ]; then
    if [ ! -d "$ZCODE_CONFIG_DIR" ]; then
        skip "ZCode   ~ skipped: $ZCODE_CONFIG_DIR not found (is ZCode installed for this user?)"
    elif [ -z "$PYTHON" ]; then
        warn "ZCode   ~ cannot edit $ZCODE_CONFIG without Python 3; register it by hand:"
        warn "          mcp.servers.VisualMCP = { type: stdio, command: \"$ZCODE_COMMAND\", args: [$([ -n "$ZCODE_ARGUMENT" ] && printf '"%s"' "$ZCODE_ARGUMENT")] }"
    else
        json_edit zcode-set "$ZCODE_CONFIG" "$ZCODE_COMMAND" "$ZCODE_ARGUMENT" \
            && ok "ZCode   -> $ZCODE_CONFIG (mcp.servers.VisualMCP)"
    fi
fi

if [ "$WANTS_CLAUDE" -eq 1 ]; then
    if [ -n "$CLAUDE" ]; then
        (
            cd "$WORK_DIR"
            "$CLAUDE" mcp remove --scope "$CLAUDE_SCOPE" VisualMCP >/dev/null 2>&1 || true
            if [ -n "$CLAUDE_ARGUMENT" ]; then
                "$CLAUDE" mcp add --scope "$CLAUDE_SCOPE" VisualMCP -- "$CLAUDE_COMMAND" "$CLAUDE_ARGUMENT"
            else
                "$CLAUDE" mcp add --scope "$CLAUDE_SCOPE" VisualMCP -- "$CLAUDE_COMMAND"
            fi
        ) || { echo "claude mcp add failed." >&2; exit 1; }
        ok "Claude  -> Claude Code ($CLAUDE_SCOPE), $CLAUDE_COMMAND"
    else
        skip "Claude  ~ skipped: 'claude' CLI not found"
    fi

    if [ "$CLAUDE_SCOPE" = "user" ]; then
        if [ -f "$DESKTOP_CONFIG" ] || [ -d "$(dirname -- "$DESKTOP_CONFIG")" ]; then
            json_edit mcpservers-set "$DESKTOP_CONFIG" "$CLAUDE_COMMAND" "$CLAUDE_ARGUMENT" \
                && ok "Desktop -> $DESKTOP_CONFIG"
        else
            skip "Desktop ~ skipped: Claude desktop app config folder not found"
        fi
    fi
fi

# --- Routing guard: hook + AGENTS.md block ---------------------------------
install_hooks

say ""
say "Done ($SCOPE scope)."
if [ "$SCOPE" = "project" ]; then
    say "A .mcp.json was written/updated in: $WORK_DIR"
    say "Note: it stores this machine's absolute path; teammates on other machines must run this script themselves."
fi
say "RESTART ZCode / Claude Code for the change to take effect."
say "Verify in ZCode: Settings -> MCP. In Claude Code: claude mcp list"
