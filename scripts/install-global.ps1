<#
.SYNOPSIS
    Publishes VisualMCP as a stable Release executable and registers it as a
    GLOBAL (user-scope) MCP server for Claude Code, so it is available in any
    folder — not just this repository.

.NOTES
    Run from anywhere:
        powershell -ExecutionPolicy Bypass -File scripts\install-global.ps1

    Re-run any time to update the installed build. After running, RESTART
    Claude Code so it picks up the new server configuration.

    If publishing fails with a file-in-use error, quit any running Claude Code
    instance first (it may be holding the old executable open) and re-run.
#>

$ErrorActionPreference = 'Stop'

# --- Paths -----------------------------------------------------------------
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Csproj   = Join-Path $RepoRoot 'src\VisualMCP\VisualMCP.csproj'
$AppDir   = Join-Path $env:USERPROFILE '.claude\mcp-servers\VisualMCP\app'
$ExePath  = Join-Path $AppDir 'VisualMCP.exe'

Write-Host "Repo:        $RepoRoot"
Write-Host "Project:     $Csproj"
Write-Host "Install dir: $AppDir"
Write-Host ""

if (-not (Test-Path $Csproj)) { throw "Project not found: $Csproj" }

# --- Locate the Claude CLI -------------------------------------------------
$claude = (Get-Command claude -ErrorAction SilentlyContinue).Source
if (-not $claude) {
    $fallback = Join-Path $env:USERPROFILE '.local\bin\claude.exe'
    if (Test-Path $fallback) { $claude = $fallback }
}
if (-not $claude) {
    throw "Could not find the 'claude' CLI. Open the terminal where Claude Code works and re-run, or add claude to PATH."
}
Write-Host "Claude CLI:  $claude"
Write-Host ""

# --- Publish a stable single-file Release build ----------------------------
Write-Host "Publishing Release build (single file, framework-dependent)..."
& dotnet publish $Csproj -c Release -r win-x64 --self-contained false -o $AppDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE). If the file is in use, quit Claude Code and re-run." }
if (-not (Test-Path $ExePath)) { throw "Expected executable not produced: $ExePath" }
Write-Host "Published:   $ExePath"
Write-Host ""

# --- Register globally (user scope), idempotently --------------------------
Write-Host "Registering 'VisualMCP' as a user-scope MCP server..."
# Remove any previous user-scope registration so re-runs don't error/duplicate.
& $claude mcp remove --scope user VisualMCP *> $null
& $claude mcp add --scope user VisualMCP -- $ExePath
if ($LASTEXITCODE -ne 0) { throw "claude mcp add failed (exit $LASTEXITCODE)." }

Write-Host ""
Write-Host "Done. VisualMCP is now registered globally (user scope)." -ForegroundColor Green
Write-Host "RESTART Claude Code for the change to take effect." -ForegroundColor Yellow
Write-Host ""
Write-Host "Verify with:  claude mcp list"
