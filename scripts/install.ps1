<#
.SYNOPSIS
    Builds VisualMCP as a stable Release executable and registers it as an MCP
    server for ZCode and/or Claude Code.

.PARAMETER Scope
    Where to register the server:
      all     - ZCode (user) + Claude Code (user) + Claude desktop app. Default.
                Hosts that are not installed are skipped with a warning.
      zcode   - ZCode user config: ~/.zcode/cli/config.json -> mcp.servers
      user    - Claude Code, global: available in every folder on this machine
      project - Claude Code, written to a .mcp.json in the target project (shareable/committable)
      local   - Claude Code, private to you, tied to the target project folder

.PARAMETER ProjectDir
    For -Scope project/local, the project folder to attach the server to.
    Defaults to the current directory. Ignored for user/zcode/all.

.PARAMETER Uninstall
    Remove the registration at the given scope instead of installing.

.PARAMETER StopRunning
    Stop every running VisualMCP server process before publishing, so the
    installed executable is not locked. The clients relaunch the server on
    their next tool call. Used by the Visual Studio post-build step.

.PARAMETER NoHooks
    Skip the routing guard (scripts\install-hooks.ps1): the PreToolUse hook that
    denies read-only shell probes on .NET repos, and the instruction block in
    %USERPROFILE%\.zcode\AGENTS.md. The server is installed either way.

.EXAMPLE
    # ZCode + Claude Code on this machine
    powershell -ExecutionPolicy Bypass -File scripts\install.ps1

.EXAMPLE
    # ZCode only
    powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Scope zcode

.EXAMPLE
    # Just for the C# project in the current folder, private to me
    cd C:\REPOSITORY\MyApp
    powershell -ExecutionPolicy Bypass -File C:\REPOSITORY\VisualMCP\scripts\install.ps1 -Scope local

.EXAMPLE
    # Shared with the team via a committed .mcp.json
    powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Scope project -ProjectDir C:\REPOSITORY\MyApp

.NOTES
    After any change, RESTART the client (ZCode / Claude Code) so it reloads the
    configuration. If publishing fails with a file-in-use error, pass
    -StopRunning (or quit the clients) and re-run.

    PublishSingleFile must stay OFF: MSBuildWorkspace locates its out-of-process
    BuildHost through the assembly location, which is empty in a single-file
    bundle, and the first solution load then fails with ArgumentNullException.
#>
param(
    [ValidateSet('all', 'zcode', 'user', 'project', 'local')]
    [string]$Scope = 'all',
    [string]$ProjectDir = (Get-Location).Path,
    [switch]$Uninstall,
    [switch]$StopRunning,
    [switch]$NoHooks
)

$ErrorActionPreference = 'Stop'

# --- Paths -----------------------------------------------------------------
$RepoRoot          = Split-Path -Parent $PSScriptRoot
$Csproj            = Join-Path $RepoRoot 'src\VisualMCP\VisualMCP.csproj'
$ClaudeAppDir      = Join-Path $env:USERPROFILE '.claude\mcp-servers\VisualMCP\app'
$ZCodeAppDir       = Join-Path $env:USERPROFILE '.zcode\mcp-servers\VisualMCP\app'
$ClaudeConfigFile  = Join-Path $env:USERPROFILE '.claude.json'
$DesktopConfigFile = Join-Path $env:APPDATA 'Claude\claude_desktop_config.json'
$ZCodeConfigFile   = Join-Path $env:USERPROFILE '.zcode\cli\config.json'
$ZCodeConfigDir    = Join-Path $env:USERPROFILE '.zcode\cli'

$wantsZCode  = $Scope -in @('all', 'zcode')
$wantsClaude = $Scope -in @('all', 'user', 'project', 'local')
# 'all' means "user scope" for Claude Code (project/local make no sense there).
$claudeScope = if ($Scope -eq 'all') { 'user' } else { $Scope }

# The routing guard (hook + AGENTS.md block) lives in its own script so it can be
# refreshed without republishing the server. Never fatal: the server matters more.
$HooksScript = Join-Path $PSScriptRoot 'install-hooks.ps1'

function Invoke-Hooks([switch]$Remove) {
    if ($NoHooks -and -not $Remove) {
        Write-Host "    hooks   ~ skipped (-NoHooks)" -ForegroundColor DarkYellow
        return
    }
    if (-not (Test-Path $HooksScript)) {
        Write-Host "    hooks   ~ skipped: $HooksScript not found" -ForegroundColor DarkYellow
        return
    }
    try {
        if ($Remove) { & $HooksScript -Scope $Scope -Uninstall }
        else         { & $HooksScript -Scope $Scope }
    } catch {
        Write-Host "    hooks   ~ failed: $($_.Exception.Message)" -ForegroundColor DarkYellow
        Write-Host "              re-run scripts\install-hooks.ps1 manually to retry." -ForegroundColor DarkYellow
    }
}

# --- JSON helpers (PS 5.1-safe: keep every existing key) --------------------
function Read-JsonObject([string]$Path) {
    if (-not (Test-Path $Path)) { return [PSCustomObject]@{} }
    $raw = Get-Content $Path -Raw
    if ([string]::IsNullOrWhiteSpace($raw)) { return [PSCustomObject]@{} }
    try { return $raw | ConvertFrom-Json }
    catch { throw "Invalid JSON in ${Path}: $($_.Exception.Message)" }
}

function Save-JsonObject([string]$Path, $Object) {
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    # BOM-less UTF-8 on purpose: both ZCode and Claude Code parse these files as JSON and
    # a UTF-8 BOM makes the whole file unreadable (PS 5.1's -Encoding UTF8 adds one).
    $json = $Object | ConvertTo-Json -Depth 32
    [System.IO.File]::WriteAllText($Path, $json, (New-Object System.Text.UTF8Encoding($false)))
}

# Returns the child object, creating it when missing.
function Get-Or-Add-Child($Parent, [string]$Name) {
    if (-not $Parent.PSObject.Properties[$Name]) {
        $Parent | Add-Member -MemberType NoteProperty -Name $Name -Value ([PSCustomObject]@{})
    }
    return $Parent.PSObject.Properties[$Name].Value
}

function Remove-Child($Parent, [string]$Name) {
    if ($Parent -and $Parent.PSObject.Properties[$Name]) {
        $Parent.PSObject.Properties.Remove($Name)
    }
}

function New-ServerEntry([string]$ExePath) {
    return [PSCustomObject]@{
        type      = 'stdio'
        command   = $ExePath
        args      = @()
        timeoutMs = 60000
    }
}

# --- ZCode registration ----------------------------------------------------
function Set-ZCodeServer([string]$ExePath) {
    $cfg     = Read-JsonObject $ZCodeConfigFile
    $mcp     = Get-Or-Add-Child $cfg 'mcp'
    $servers = Get-Or-Add-Child $mcp 'servers'
    $servers | Add-Member -MemberType NoteProperty -Name 'VisualMCP' `
                          -Value (New-ServerEntry $ExePath) -Force
    Save-JsonObject $ZCodeConfigFile $cfg
    Write-Host "    ZCode   -> $ZCodeConfigFile (mcp.servers.VisualMCP)" -ForegroundColor Green
}

function Remove-ZCodeServer() {
    if (-not (Test-Path $ZCodeConfigFile)) { return }
    $cfg = Read-JsonObject $ZCodeConfigFile
    if (-not $cfg.PSObject.Properties['mcp']) { return }
    $servers = $cfg.mcp.PSObject.Properties['servers']
    if (-not $servers) { return }
    Remove-Child $servers.Value 'VisualMCP'
    Save-JsonObject $ZCodeConfigFile $cfg
    Write-Host "    ZCode   <- removed from $ZCodeConfigFile" -ForegroundColor Yellow
}

# --- Claude desktop app registration --------------------------------------
function Set-DesktopServer([string]$ExePath) {
    if (-not (Test-Path (Split-Path -Parent $DesktopConfigFile))) { return $false }
    $cfg     = Read-JsonObject $DesktopConfigFile
    $servers = Get-Or-Add-Child $cfg 'mcpServers'
    $servers | Add-Member -MemberType NoteProperty -Name 'VisualMCP' `
                          -Value (New-ServerEntry $ExePath) -Force
    Save-JsonObject $DesktopConfigFile $cfg
    Write-Host "    Desktop -> $DesktopConfigFile" -ForegroundColor Green
    return $true
}

function Remove-DesktopServer() {
    if (-not (Test-Path $DesktopConfigFile)) { return }
    $cfg = Read-JsonObject $DesktopConfigFile
    if (-not $cfg.PSObject.Properties['mcpServers']) { return }
    Remove-Child $cfg.mcpServers 'VisualMCP'
    Save-JsonObject $DesktopConfigFile $cfg
    Write-Host "    Desktop <- removed from $DesktopConfigFile" -ForegroundColor Yellow
}

# --- Claude CLI ------------------------------------------------------------
function Find-ClaudeCli() {
    $cmd = Get-Command claude -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $fallback = Join-Path $env:USERPROFILE '.local\bin\claude.exe'
    if (Test-Path $fallback) { return $fallback }
    return $null
}

$claude = if ($wantsClaude) { Find-ClaudeCli } else { $null }
if ($wantsClaude -and -not $claude -and $Scope -ne 'all') {
    throw "Could not find the 'claude' CLI. Open the terminal where Claude Code works and re-run, or use -Scope zcode for ZCode only."
}

$workDir = if ($claudeScope -eq 'user') { $RepoRoot } else { (Resolve-Path $ProjectDir).Path }

# --- Uninstall -------------------------------------------------------------
if ($Uninstall) {
    Write-Host "Removing 'VisualMCP' ($Scope scope)..." -ForegroundColor Cyan

    if ($wantsZCode) {
        Remove-ZCodeServer
        if (Test-Path $ZCodeAppDir) {
            Remove-Item $ZCodeAppDir -Recurse -Force
            Write-Host "    ZCode   <- removed $ZCodeAppDir" -ForegroundColor Yellow
        }
    }

    if ($wantsClaude) {
        if ($claude) {
            Push-Location $workDir
            try { & $claude mcp remove --scope $claudeScope VisualMCP *> $null }
            finally { Pop-Location }
            Write-Host "    Claude  <- removed from Claude Code ($claudeScope)" -ForegroundColor Yellow
        }
        if ($claudeScope -eq 'user') { Remove-DesktopServer }
        if (Test-Path $ClaudeAppDir) {
            Remove-Item $ClaudeAppDir -Recurse -Force
            Write-Host "    Claude  <- removed $ClaudeAppDir" -ForegroundColor Yellow
        }
    }

    Invoke-Hooks -Remove

    Write-Host ""
    Write-Host "Done. Restart ZCode / Claude Code to apply." -ForegroundColor Green
    return
}
# --- Stop running instances (optional) --------------------------------------
if ($StopRunning) {
    $stopped = @()
    Get-CimInstance Win32_Process -Filter "Name='dotnet.exe' OR Name='VisualMCP.exe'" |
        Where-Object { $_.CommandLine -match 'VisualMCP\.exe|VisualMCP\.dll' } |
        ForEach-Object {
            $stopped += $_.ProcessId
            Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
        }
    if ($stopped.Count) {
        Write-Host "Stopped $($stopped.Count) running server instance(s): $($stopped -join ', ')" -ForegroundColor DarkYellow
    }
    Start-Sleep -Milliseconds 800
}

# --- Publish ---------------------------------------------------------------
if (-not (Test-Path $Csproj)) { throw "Project not found: $Csproj" }

# One build, then copy into the other host's folder when both are wanted.
$primaryDir = if ($wantsZCode) { $ZCodeAppDir } else { $ClaudeAppDir }

Write-Host "Publishing Release build to $primaryDir ..." -ForegroundColor Cyan
& dotnet publish $Csproj -c Release -r win-x64 --self-contained false -o $primaryDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed (exit $LASTEXITCODE). If the file is in use, quit ZCode / Claude Code and re-run."
}
$primaryExe = Join-Path $primaryDir 'VisualMCP.exe'
if (-not (Test-Path $primaryExe)) { throw "Expected executable not produced: $primaryExe" }

if ($wantsZCode -and $wantsClaude) {
    New-Item -ItemType Directory -Path $ClaudeAppDir -Force | Out-Null
    Copy-Item (Join-Path $primaryDir '*') $ClaudeAppDir -Recurse -Force
}

$exeForZCode  = Join-Path $ZCodeAppDir 'VisualMCP.exe'
$exeForClaude = Join-Path $ClaudeAppDir 'VisualMCP.exe'

# --- Register --------------------------------------------------------------
Write-Host "Registering 'VisualMCP' ($Scope scope) ..." -ForegroundColor Cyan

if ($wantsZCode) {
    if (-not (Test-Path $ZCodeConfigDir)) {
        Write-Host "    ZCode   ~ skipped: $ZCodeConfigDir not found (is ZCode installed for this user?)" -ForegroundColor DarkYellow
    } else {
        Set-ZCodeServer $exeForZCode
    }
}

if ($wantsClaude) {
    if ($claude) {
        Push-Location $workDir
        try {
            & $claude mcp remove --scope $claudeScope VisualMCP *> $null   # ignore "not found"
            & $claude mcp add --scope $claudeScope VisualMCP -- $exeForClaude
            if ($LASTEXITCODE -ne 0) { throw "claude mcp add failed (exit $LASTEXITCODE)." }
        }
        finally { Pop-Location }
        Write-Host "    Claude  -> Claude Code ($claudeScope), $exeForClaude" -ForegroundColor Green
    } else {
        Write-Host "    Claude  ~ skipped: 'claude' CLI not found" -ForegroundColor DarkYellow
    }

    if ($claudeScope -eq 'user') {
        if (-not (Set-DesktopServer $exeForClaude)) {
            Write-Host "    Desktop ~ skipped: Claude desktop app config folder not found" -ForegroundColor DarkYellow
        }
    }
}

# --- Routing guard: hook + AGENTS.md block ---------------------------------
Invoke-Hooks

Write-Host ""
Write-Host "Done ($Scope scope)." -ForegroundColor Green
if ($Scope -eq 'project') {
    Write-Host "A .mcp.json was written/updated in: $workDir" -ForegroundColor Green
    Write-Host "Note: it stores this machine's absolute exe path; teammates on other machines must run this script themselves." -ForegroundColor DarkYellow
}
Write-Host "RESTART ZCode / Claude Code for the change to take effect." -ForegroundColor Yellow
Write-Host "Verify in ZCode: Settings -> MCP. In Claude Code: claude mcp list" -ForegroundColor Yellow
