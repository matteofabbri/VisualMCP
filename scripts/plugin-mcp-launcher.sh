#!/bin/sh
# VisualMCP plugin MCP launcher (Linux / macOS). The POSIX counterpart of
# scripts/plugin-mcp-launcher.ps1: a plugin install ships sources, so the launcher
# starts the build the script installer published (VISUALMCP_PLUGIN_EXE overrides,
# then the standard install locations) and replaces itself with it, keeping stdio.

set -eu

candidates=""
[ -n "${VISUALMCP_PLUGIN_EXE:-}" ] && candidates="$candidates $VISUALMCP_PLUGIN_EXE"
[ -n "${HOME:-}" ] && candidates="$candidates $HOME/.zcode/mcp-servers/VisualMCP/app/VisualMCP $HOME/.zcode/mcp-servers/VisualMCP/app/VisualMCP.exe $HOME/.claude/mcp-servers/VisualMCP/app/VisualMCP $HOME/.claude/mcp-servers/VisualMCP/app/VisualMCP.exe"

for candidate in $candidates; do
    if [ -x "$candidate" ]; then
        exec "$candidate" "$@"
    fi
done

# No apphost anywhere: run the managed entry point through dotnet.
if [ -n "${HOME:-}" ]; then
    for app in "$HOME/.zcode/mcp-servers/VisualMCP/app" "$HOME/.claude/mcp-servers/VisualMCP/app"; do
        if [ -f "$app/VisualMCP.dll" ] && command -v dotnet >/dev/null 2>&1; then
            exec dotnet "$app/VisualMCP.dll" "$@"
        fi
    done
fi

echo "VisualMCP plugin: runtime is missing; run scripts/install.sh once (checkout + .NET SDK), then disable and re-enable the plugin." >&2
exit 1
