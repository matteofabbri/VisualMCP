#Requires -Version 5.1
<#
.SYNOPSIS
    Installs VisualMCP as an MCP server for ZCode and/or Claude Code.

.DESCRIPTION
    Wrapper around scripts\install.ps1 (the single implementation: publish + registration,
    which now also installs the routing guard: PreToolUse hook + AGENTS.md block).

    This file adds -SetupProject, which writes into the destination C# project the same
    instructions block managed by VisualMCP (hooks\agents-block.md), both in CLAUDE.md
    (Claude Code) and in AGENTS.md (ZCode), so every client knows when to use the tools
    instead of Grep/PowerShell.

    The block is delimited by the "<!-- VisualMCP:begin/end -->" markers: running the
    script again replaces it instead of duplicating it, and leaves the rest of the file
    untouched.

.PARAMETER Scope
    all (default) = ZCode + Claude Code + desktop app; zcode = ZCode only;
    user/project/local = Claude Code only (see scripts\install.ps1).

.PARAMETER Uninstall
    Removes the MCP server (and the routing guard) from the configurations and deletes
    the published files.

.PARAMETER StopRunning
    Stops the running server instances before publishing (the installed exe is
    otherwise locked).

.PARAMETER SetupProject
    Writes (or updates) the VisualMCP instructions block in CLAUDE.md and AGENTS.md in
    the given directory. Example: .\install.ps1 -SetupProject C:\REPOSITORY\MyApp

.PARAMETER SolutionPath
    Optional: default solution to name in the block written by -SetupProject.

.PARAMETER ProjectDir
    For -Scope project/local only: the project the server is attached to.

.EXAMPLE
    .\install.ps1
    .\install.ps1 -Scope zcode
    .\install.ps1 -Uninstall
    .\install.ps1 -SetupProject C:\REPOSITORY\MyApp
    .\install.ps1 -SetupProject C:\REPOSITORY\MyApp -SolutionPath C:\REPOSITORY\MyApp\MyApp.sln
#>
param(
    [switch]$Uninstall,
    [switch]$StopRunning,
    [switch]$NoHooks,
    [string]$SetupProject = "",
    [string]$SolutionPath = "",
    [ValidateSet('all', 'zcode', 'user', 'project', 'local')]
    [string]$Scope = 'all',
    [string]$ProjectDir = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$InstallScript = Join-Path $PSScriptRoot "scripts\install.ps1"
$BlockSource   = Join-Path $PSScriptRoot "hooks\agents-block.md"

function Write-Step([string]$msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)   { Write-Host "    OK  $msg" -ForegroundColor Green }
function Write-Err([string]$msg)  { Write-Host "    ERR $msg" -ForegroundColor Red; exit 1 }

if (-not (Test-Path $InstallScript)) {
    Write-Err "Script not found: $InstallScript`nRun this script from the repository root."
}

# --- Setup-Project ----------------------------------------------------------
if ($SetupProject -ne "") {
    $targetDir = Resolve-Path $SetupProject -ErrorAction SilentlyContinue
    if (-not $targetDir) { Write-Err "Directory not found: $SetupProject" }
    if (-not (Test-Path $BlockSource)) { Write-Err "Instructions block not found: $BlockSource" }

    $block = (Get-Content $BlockSource -Raw).TrimEnd()

    if ($SolutionPath -ne "") {
        # Inside the managed block, so the next run replaces it instead of duplicating.
        $block = $block.Replace(
            '<!-- VisualMCP:end -->',
            "`nDefault solution for this repo: ``$SolutionPath```n<!-- VisualMCP:end -->")
    }

    $markerRx = '(?s)\r?\n?<!-- VisualMCP:begin.*?<!-- VisualMCP:end -->\r?\n?'
    # BOM-less UTF-8: PS 5.1's -Encoding UTF8 would prepend one.
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)

    Write-Step "Configuring project in $targetDir ..."

    # CLAUDE.md for Claude Code, AGENTS.md for ZCode: same block, both clients.
    foreach ($name in @("CLAUDE.md", "AGENTS.md")) {
        $docPath  = Join-Path $targetDir $name
        $existed  = Test-Path $docPath
        $existing = if ($existed) { Get-Content $docPath -Raw } else { "" }
        $clean    = [System.Text.RegularExpressions.Regex]::Replace($existing, $markerRx, '')

        $text = if ([string]::IsNullOrWhiteSpace($clean)) {
            $block + "`n"
        } else {
            $clean.TrimEnd() + "`n`n" + $block + "`n"
        }
        [System.IO.File]::WriteAllText($docPath, $text, $utf8NoBom)

        if ($existed) { Write-Ok "VisualMCP block updated in $docPath" }
        else          { Write-Ok "Created $docPath" }
    }

    Write-Host @"

Setup complete.

Claude Code (CLAUDE.md) and ZCode (AGENTS.md) will use the MCP tools for C# analysis in this project.

Note: the PreToolUse hook that blocks grep/sed/cat/ls on .NET is user-level, not
per-project: it is installed (or updated) with
  powershell -ExecutionPolicy Bypass -File scripts\install-hooks.ps1
"@ -ForegroundColor Green
    exit 0
}

# --- Install / Uninstall: delegate to the single implementation -------------
$forward = @{
    Scope       = $Scope
    Uninstall   = $Uninstall.IsPresent
    StopRunning = $StopRunning.IsPresent
    NoHooks     = $NoHooks.IsPresent
}
if ($Scope -in @('project', 'local')) {
    $forward.ProjectDir = if ($ProjectDir -ne "") { $ProjectDir } else { (Get-Location).Path }
}

& $InstallScript @forward
exit $LASTEXITCODE
