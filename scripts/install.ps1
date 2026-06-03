<#
.SYNOPSIS
    Builds VisualMCP as a stable Release executable and registers it as an MCP
    server for Claude Code at the scope of your choice.

.PARAMETER Scope
    Where to register the server:
      user    - global: available in every folder on this machine (default)
      project - written to a .mcp.json in the target project (shareable/committable)
      local   - private to you, tied to the target project folder

.PARAMETER ProjectDir
    For -Scope project/local, the project folder to attach the server to.
    Defaults to the current directory. Ignored for -Scope user.

.PARAMETER Uninstall
    Remove the registration at the given scope instead of installing.

.EXAMPLE
    # Global (any folder)
    powershell -ExecutionPolicy Bypass -File scripts\install.ps1

.EXAMPLE
    # Just for the C# project in the current folder, private to me
    cd C:\REPOSITORY\MyApp
    powershell -ExecutionPolicy Bypass -File C:\REPOSITORY\VisualMCP\scripts\install.ps1 -Scope local

.EXAMPLE
    # Shared with the team via a committed .mcp.json
    powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Scope project -ProjectDir C:\REPOSITORY\MyApp

.NOTES
    After any change, RESTART Claude Code so it reloads the configuration.
    If publishing fails with a file-in-use error, quit running Claude Code
    instances first (they may hold the old executable open) and re-run.
#>
param(
    [ValidateSet('user', 'project', 'local')]
    [string]$Scope = 'user',
    [string]$ProjectDir = (Get-Location).Path,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

# --- Paths -----------------------------------------------------------------
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Csproj   = Join-Path $RepoRoot 'src\VisualMCP\VisualMCP.csproj'
$AppDir   = Join-Path $env:USERPROFILE '.claude\mcp-servers\VisualMCP\app'
$ExePath  = Join-Path $AppDir 'VisualMCP.exe'

# --- Locate the Claude CLI -------------------------------------------------
$claude = (Get-Command claude -ErrorAction SilentlyContinue).Source
if (-not $claude) {
    $fallback = Join-Path $env:USERPROFILE '.local\bin\claude.exe'
    if (Test-Path $fallback) { $claude = $fallback }
}
if (-not $claude) {
    throw "Could not find the 'claude' CLI. Open the terminal where Claude Code works and re-run, or add claude to PATH."
}

# For project/local scope the registration attaches to the current directory,
# so we must run the claude command from inside the target project folder.
# For user scope the directory is irrelevant.
$workDir = if ($Scope -eq 'user') { $RepoRoot } else { (Resolve-Path $ProjectDir).Path }

if ($Uninstall) {
    Write-Host "Removing 'VisualMCP' ($Scope scope)..."
    Push-Location $workDir
    try { & $claude mcp remove --scope $Scope VisualMCP } finally { Pop-Location }
    Write-Host "Removed. Restart Claude Code to apply." -ForegroundColor Yellow
    return
}

if (-not (Test-Path $Csproj)) { throw "Project not found: $Csproj" }

# --- Publish a stable single-file Release build ----------------------------
Write-Host "Publishing Release build to $AppDir ..."
& dotnet publish $Csproj -c Release -r win-x64 --self-contained false -o $AppDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE). If the file is in use, quit Claude Code and re-run." }
if (-not (Test-Path $ExePath)) { throw "Expected executable not produced: $ExePath" }

# --- Register (idempotent) -------------------------------------------------
Write-Host "Registering 'VisualMCP' ($Scope scope) -> $ExePath"
Push-Location $workDir
try {
    & $claude mcp remove --scope $Scope VisualMCP *> $null   # ignore "not found"
    & $claude mcp add --scope $Scope VisualMCP -- $ExePath
    if ($LASTEXITCODE -ne 0) { throw "claude mcp add failed (exit $LASTEXITCODE)." }
}
finally { Pop-Location }

Write-Host ""
Write-Host "Done ($Scope scope)." -ForegroundColor Green
if ($Scope -eq 'project') {
    Write-Host "A .mcp.json was written/updated in: $workDir" -ForegroundColor Green
    Write-Host "Note: it stores this machine's absolute exe path; teammates on other machines must run this script themselves." -ForegroundColor DarkYellow
}
Write-Host "RESTART Claude Code for the change to take effect." -ForegroundColor Yellow
Write-Host "Verify with:  claude mcp list"
