#Requires -Version 5.1
<#
.SYNOPSIS
    Installs VisualMCP's routing guard: the PreToolUse hook that keeps read-only
    shell probes on .NET repositories inside the MCP tools, plus the AGENTS.md
    block that tells the model the same thing up front.

.DESCRIPTION
    Two independent pieces, both idempotent (re-running replaces, never duplicates):

      1. Hook - copies hooks\pre-tool-use.ps1 next to the installed server and
         registers it as a PreToolUse hook for the Bash tool:
           ZCode       -> %USERPROFILE%\.zcode\cli\config.json   hooks.events.PreToolUse
           Claude Code -> %USERPROFILE%\.claude\settings.json    hooks.PreToolUse
         ZCode's configuration hooks are disabled by default, so this also sets
         hooks.enabled = true.

      2. Instructions - writes the "use VisualMCP, not grep" block from
         hooks\agents-block.md into %USERPROFILE%\.zcode\AGENTS.md (user scope:
         loaded in every workspace). Claude Code already receives the server's
         own `instructions`, so its CLAUDE.md is never touched.

    This script does not publish or register the server; scripts\install.ps1 does
    that and calls this one at the end. Run it alone to refresh the guard after
    editing the hook or the block.

.PARAMETER Scope
    all     - ZCode + Claude Code (default)
    zcode   - ZCode only
    user    - Claude Code only (hooks are always user-level)
    project, local - skipped: hooks and AGENTS.md are machine-level

.PARAMETER Uninstall
    Remove the hook registration, the copied script and the AGENTS.md block.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\install-hooks.ps1
    powershell -ExecutionPolicy Bypass -File scripts\install-hooks.ps1 -Scope zcode
    powershell -ExecutionPolicy Bypass -File scripts\install-hooks.ps1 -Uninstall
#>
param(
    [ValidateSet('all', 'zcode', 'user', 'project', 'local')]
    [string]$Scope = 'all',
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

# --- Paths -----------------------------------------------------------------
$RepoRoot         = Split-Path -Parent $PSScriptRoot
$HookSource       = Join-Path $RepoRoot 'hooks\pre-tool-use.ps1'
$BlockSource      = Join-Path $RepoRoot 'hooks\agents-block.md'

$ZCodeConfigFile  = Join-Path $env:USERPROFILE '.zcode\cli\config.json'
$ZCodeHookDir     = Join-Path $env:USERPROFILE '.zcode\mcp-servers\VisualMCP\hooks'
$ZCodeHookPath    = Join-Path $ZCodeHookDir 'pre-tool-use.ps1'
$ZCodeAgentsFile  = Join-Path $env:USERPROFILE '.zcode\AGENTS.md'

$ClaudeSettings   = Join-Path $env:USERPROFILE '.claude\settings.json'
$ClaudeHookDir    = Join-Path $env:USERPROFILE '.claude\mcp-servers\VisualMCP\hooks'
$ClaudeHookPath   = Join-Path $ClaudeHookDir 'pre-tool-use.ps1'

$wantsZCode     = $Scope -in @('all', 'zcode')
$wantsClaude    = $Scope -in @('all', 'user')
$scopeSkipped   = $Scope -in @('project', 'local')

$MarkerBegin = '<!-- VisualMCP:begin'
$MarkerEnd   = '<!-- VisualMCP:end -->'
$MarkerRx    = '(?s)\r?\n?<!-- VisualMCP:begin.*?<!-- VisualMCP:end -->\r?\n?'

# The tools the guard watches: Bash for the shell probes, Write/Edit for a memory store or a new
# plan-like .md. Claude Code and ZCode treat the matcher as a regex over the tool name.
$Matcher = 'Bash|Write|Edit|MultiEdit|NotebookEdit'

function Write-Step([string]$Msg) { Write-Host "`n==> $Msg" -ForegroundColor Cyan }
function Write-Ok([string]$Msg)   { Write-Host "    OK  $Msg" -ForegroundColor Green }
function Write-Skip([string]$Msg) { Write-Host "    --  $Msg" -ForegroundColor DarkYellow }

# --- File + JSON helpers (PS 5.1-safe) --------------------------------------
function Read-Text([string]$Path) {
    if (-not (Test-Path $Path)) { return '' }
    return [System.IO.File]::ReadAllText($Path)
}

function Write-TextNoBom([string]$Path, [string]$Text) {
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($false)))
}

function Read-JsonObject([string]$Path) {
    $raw = Read-Text $Path
    if ([string]::IsNullOrWhiteSpace($raw)) { return [PSCustomObject]@{} }
    try { return $raw | ConvertFrom-Json }
    catch { throw "Invalid JSON in ${Path}: $($_.Exception.Message)" }
}

function Save-JsonObject([string]$Path, $Object) {
    # BOM-less UTF-8: both clients refuse a BOM'd config.
    Write-TextNoBom $Path ($Object | ConvertTo-Json -Depth 32)
}

function Get-Or-Add-Child($Parent, [string]$Name) {
    if (-not $Parent.PSObject.Properties[$Name]) {
        $Parent | Add-Member -MemberType NoteProperty -Name $Name -Value ([PSCustomObject]@{})
    }
    return $Parent.PSObject.Properties[$Name].Value
}

function Remove-Child($Parent, [string]$Name) {
    if ($Parent -and $Parent.PSObject.Properties[$Name]) { $Parent.PSObject.Properties.Remove($Name) }
}

function New-HookEntry([string]$ScriptPath) {
    # Explicit powershell.exe (no `shell` field): works whichever shell the client
    # uses to run the hook, and Windows PowerShell 5.1 is present everywhere.
    return [PSCustomObject]@{
        type          = 'command'
        command       = 'powershell -NoProfile -ExecutionPolicy Bypass -File "' + $ScriptPath + '"'
        timeout       = 15
        statusMessage = 'VisualMCP: checking for a shell probe...'
    }
}

# True when this matcher entry is the one this script manages (any hook whose
# command mentions our script name). Everything else is left untouched.
function Test-Ours($MatcherEntry) {
    if (-not $MatcherEntry) { return $false }
    $hooks = $MatcherEntry.PSObject.Properties['hooks']
    if (-not $hooks) { return $false }
    foreach ($h in @($hooks.Value)) {
        $cmd = $h.PSObject.Properties['command']
        if ($cmd -and ([string]$cmd.Value) -match 'pre-tool-use\.ps1') { return $true }
    }
    return $false
}

function Copy-Hook([string]$Destination) {
    if (-not (Test-Path $HookSource)) { throw "Hook script not found: $HookSource" }
    $dir = Split-Path -Parent $Destination
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Copy-Item -LiteralPath $HookSource -Destination $Destination -Force
}

# --- ZCode: config.json -> hooks.enabled + hooks.events.PreToolUse[matcher "Bash"]
function Set-ZCodeHook() {
    if (-not (Test-Path (Split-Path -Parent $ZCodeConfigFile))) {
        Write-Skip "ZCode not found ($ZCodeConfigFile): hook skipped"
        return
    }

    Copy-Hook $ZCodeHookPath

    $cfg    = Read-JsonObject $ZCodeConfigFile
    $hooks  = Get-Or-Add-Child $cfg 'hooks'
    $hooks | Add-Member -MemberType NoteProperty -Name 'enabled' -Value $true -Force
    $events = Get-Or-Add-Child $hooks 'events'

    $existing = @()
    $prop = $events.PSObject.Properties['PreToolUse']
    if ($prop) { $existing = @($prop.Value) }

    $kept = @($existing | Where-Object { -not (Test-Ours $_) })
    $entry = [PSCustomObject]@{
        matcher = $Matcher
        hooks   = @(New-HookEntry $ZCodeHookPath)
    }
    $events | Add-Member -MemberType NoteProperty -Name 'PreToolUse' -Value (@($kept) + @($entry)) -Force

    Save-JsonObject $ZCodeConfigFile $cfg
    Write-Ok "ZCode   -> $ZCodeConfigFile (hooks.events.PreToolUse, matcher '$Matcher')"
    Write-Ok "ZCode   -> $ZCodeHookPath"
}

function Remove-ZCodeHook() {
    if (Test-Path $ZCodeHookPath) {
        Remove-Item -LiteralPath $ZCodeHookPath -Force
        Write-Ok "ZCode   <- removed $ZCodeHookPath"
    }
    if (-not (Test-Path $ZCodeConfigFile)) { return }

    $cfg = Read-JsonObject $ZCodeConfigFile
    if (-not $cfg.PSObject.Properties['hooks']) { return }
    $hooks  = $cfg.hooks
    $events = $hooks.PSObject.Properties['events']
    if (-not $events) { return }

    $prop = $events.Value.PSObject.Properties['PreToolUse']
    if (-not $prop) { return }

    $kept = @(@($prop.Value) | Where-Object { -not (Test-Ours $_) })
    if ($kept.Count -eq 0) { $events.Value.PSObject.Properties.Remove('PreToolUse') }
    else { $events.Value | Add-Member -MemberType NoteProperty -Name 'PreToolUse' -Value $kept -Force }

    # Drop the hooks node entirely when nothing is left, so the file returns to
    # its pre-install shape.
    if (-not $events.Value.PSObject.Properties['PreToolUse'] -and
        @($events.Value.PSObject.Properties).Count -eq 0) {
        $hooks.PSObject.Properties.Remove('events')
    }
    if (@($hooks.PSObject.Properties).Count -eq 0) {
        $cfg.PSObject.Properties.Remove('hooks')
    }

    Save-JsonObject $ZCodeConfigFile $cfg
    Write-Ok "ZCode   <- hook removed from $ZCodeConfigFile"
}

# --- Claude Code: settings.json -> hooks.PreToolUse[matcher "Bash"] ----------
function Set-ClaudeHook() {
    if (-not (Test-Path (Split-Path -Parent $ClaudeSettings))) {
        Write-Skip "Claude Code not found ($ClaudeSettings): hook skipped"
        return
    }

    Copy-Hook $ClaudeHookPath

    $cfg   = Read-JsonObject $ClaudeSettings
    $hooks = Get-Or-Add-Child $cfg 'hooks'

    $existing = @()
    $prop = $hooks.PSObject.Properties['PreToolUse']
    if ($prop) { $existing = @($prop.Value) }

    $kept = @($existing | Where-Object { -not (Test-Ours $_) })
    $entry = [PSCustomObject]@{
        matcher = $Matcher
        hooks   = @(New-HookEntry $ClaudeHookPath)
    }
    $hooks | Add-Member -MemberType NoteProperty -Name 'PreToolUse' -Value (@($kept) + @($entry)) -Force

    Save-JsonObject $ClaudeSettings $cfg
    Write-Ok "Claude  -> $ClaudeSettings (hooks.PreToolUse, matcher '$Matcher')"
    Write-Ok "Claude  -> $ClaudeHookPath"
}

function Remove-ClaudeHook() {
    if (Test-Path $ClaudeHookPath) {
        Remove-Item -LiteralPath $ClaudeHookPath -Force
        Write-Ok "Claude  <- removed $ClaudeHookPath"
    }
    if (-not (Test-Path $ClaudeSettings)) { return }

    $cfg = Read-JsonObject $ClaudeSettings
    $hooks = $cfg.PSObject.Properties['hooks']
    if (-not $hooks) { return }
    $prop = $hooks.Value.PSObject.Properties['PreToolUse']
    if (-not $prop) { return }

    $kept = @(@($prop.Value) | Where-Object { -not (Test-Ours $_) })
    if ($kept.Count -eq 0) { $hooks.Value.PSObject.Properties.Remove('PreToolUse') }
    else { $hooks.Value | Add-Member -MemberType NoteProperty -Name 'PreToolUse' -Value $kept -Force }
    if (@($hooks.Value.PSObject.Properties).Count -eq 0) { $cfg.PSObject.Properties.Remove('hooks') }

    Save-JsonObject $ClaudeSettings $cfg
    Write-Ok "Claude  <- hook removed from $ClaudeSettings"
}

# --- AGENTS.md (ZCode user scope: loaded in every workspace) ----------------
function Set-AgentsBlock() {
    if (-not (Test-Path (Split-Path -Parent $ZCodeAgentsFile))) {
        Write-Skip "ZCode not found ($ZCodeAgentsFile): instructions skipped"
        return
    }
    if (-not (Test-Path $BlockSource)) { throw "Instruction block not found: $BlockSource" }

    $block = (Read-Text $BlockSource).TrimEnd()
    $clean = [System.Text.RegularExpressions.Regex]::Replace((Read-Text $ZCodeAgentsFile), $MarkerRx, '')

    $text = if ([string]::IsNullOrWhiteSpace($clean)) {
        $block + "`n"
    } else {
        $clean.TrimEnd() + "`n`n" + $block + "`n"
    }
    Write-TextNoBom $ZCodeAgentsFile $text
    Write-Ok "ZCode   -> $ZCodeAgentsFile (routing block)"
}

function Remove-AgentsBlock() {
    if (-not (Test-Path $ZCodeAgentsFile)) { return }

    $clean = [System.Text.RegularExpressions.Regex]::Replace((Read-Text $ZCodeAgentsFile), $MarkerRx, '')
    if ([string]::IsNullOrWhiteSpace($clean)) {
        Remove-Item -LiteralPath $ZCodeAgentsFile -Force
        Write-Ok "ZCode   <- removed $ZCodeAgentsFile (it only contained the block)"
    } else {
        Write-TextNoBom $ZCodeAgentsFile ($clean.TrimEnd() + "`n")
        Write-Ok "ZCode   <- routing block removed from $ZCodeAgentsFile"
    }
}

# --- Main ------------------------------------------------------------------
if ($scopeSkipped) {
    Write-Skip "Scope '$Scope' is a Claude Code project registration: hooks and AGENTS.md are machine-level, nothing to do here."
    return
}

if ($Uninstall) {
    Write-Step "Removing VisualMCP routing guard ($Scope)..."
    if ($wantsZCode)  { Remove-ZCodeHook;  Remove-AgentsBlock }
    if ($wantsClaude) { Remove-ClaudeHook }
    Write-Host "`nDone. Restart ZCode / Claude Code to apply." -ForegroundColor Green
    return
}

Write-Step "Installing VisualMCP routing guard ($Scope)..."
if ($wantsZCode)  { Set-ZCodeHook;  Set-AgentsBlock }
if ($wantsClaude) { Set-ClaudeHook }

Write-Host "`nHook denies read-only shell probes on .NET repos, writes into a memory store and new plan-like .md files, and names the MCP tool (or board call) instead." -ForegroundColor Green
Write-Host "Escape hatch: include 'vmcp-allow' in a command to keep Bash deliberately." -ForegroundColor DarkYellow
Write-Host "RESTART ZCode / Claude Code: hooks are read at session start." -ForegroundColor Yellow
