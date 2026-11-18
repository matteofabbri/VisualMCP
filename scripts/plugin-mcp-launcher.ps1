# VisualMCP plugin MCP launcher (Windows).
#
# A plugin install ships this repository, not the published build, so the client cannot
# start the server from the plugin root alone. The launcher launches the build the script
# installer published (VISUALMCP_PLUGIN_EXE overrides, then the standard install
# locations), forwarding stdin/stdout unchanged: the server speaks stdio through here.
#
# When nothing is installed it exits 1 with a repair message instead of starting half a
# server; the skill's connection-and-recovery reference carries the same ladder.

$ErrorActionPreference = 'Stop'

$candidates = @()
if ($env:VISUALMCP_PLUGIN_EXE) { $candidates += $env:VISUALMCP_PLUGIN_EXE }
if ($env:USERPROFILE) {
    $candidates += (Join-Path $env:USERPROFILE '.zcode\mcp-servers\VisualMCP\app\VisualMCP.exe')
    $candidates += (Join-Path $env:USERPROFILE '.claude\mcp-servers\VisualMCP\app\VisualMCP.exe')
}

$exe = $null
foreach ($candidate in $candidates) {
    if ($candidate -and (Test-Path -LiteralPath $candidate)) { $exe = $candidate; break }
}

if (-not $exe) {
    [Console]::Error.WriteLine('VisualMCP plugin: runtime is missing; run scripts\install.ps1 once (checkout + .NET 10 SDK), then disable and re-enable the plugin.')
    exit 1
}

& $exe @args
exit $LASTEXITCODE
