#Requires -Version 5.1
<#
.SYNOPSIS
    Installa VsSolutionPlugin come server MCP globale in Claude Code.

.DESCRIPTION
    1. Verifica i prerequisiti (.NET SDK)
    2. Pubblica il server in %USERPROFILE%\.claude\mcp-servers\vs-solution\
    3. Registra il server in %USERPROFILE%\.claude.json (config globale di Claude Code)

.PARAMETER Uninstall
    Rimuove il server MCP dalla configurazione e cancella i file pubblicati.

.EXAMPLE
    .\install.ps1
    .\install.ps1 -Uninstall
#>
param(
    [switch]$Uninstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ServerName    = "vs-solution"
$ProjectFile   = Join-Path $PSScriptRoot "src\VsSolutionServer\VsSolutionServer.csproj"
$PublishDir    = Join-Path $env:USERPROFILE ".claude\mcp-servers\vs-solution"
$ExePath       = Join-Path $PublishDir "VsSolutionServer.exe"
$ClaudeConfig  = Join-Path $env:USERPROFILE ".claude.json"

function Write-Step([string]$msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)   { Write-Host "    OK  $msg" -ForegroundColor Green }
function Write-Err([string]$msg)  { Write-Host "    ERR $msg" -ForegroundColor Red; exit 1 }

# ── Uninstall ────────────────────────────────────────────────────────────────
if ($Uninstall) {
    Write-Step "Rimozione server MCP '$ServerName'..."

    if (Test-Path $ClaudeConfig) {
        $cfg = Get-Content $ClaudeConfig -Raw | ConvertFrom-Json
        if ($cfg.PSObject.Properties["mcpServers"] -and
            $cfg.mcpServers.PSObject.Properties[$ServerName]) {
            $cfg.mcpServers.PSObject.Properties.Remove($ServerName)
            $cfg | ConvertTo-Json -Depth 20 | Set-Content $ClaudeConfig -Encoding UTF8
            Write-Ok "Rimosso da $ClaudeConfig"
        } else {
            Write-Host "    (non presente in $ClaudeConfig)"
        }
    }

    if (Test-Path $PublishDir) {
        Remove-Item $PublishDir -Recurse -Force
        Write-Ok "Rimossa directory $PublishDir"
    }

    Write-Host "`nDisinstallazione completata. Riavvia Claude Code." -ForegroundColor Yellow
    exit 0
}

# ── Prerequisiti ─────────────────────────────────────────────────────────────
Write-Step "Verifica prerequisiti..."

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { Write-Err ".NET SDK non trovato. Installalo da https://dot.net" }

$sdkVersion = (dotnet --version 2>&1)
Write-Ok ".NET SDK $sdkVersion"

if (-not (Test-Path $ProjectFile)) {
    Write-Err "File progetto non trovato: $ProjectFile`nEsegui questo script dalla root del repository."
}
Write-Ok "Progetto trovato: $ProjectFile"

# ── Publish ───────────────────────────────────────────────────────────────────
Write-Step "Pubblicazione in $PublishDir ..."

$null = New-Item -ItemType Directory -Path $PublishDir -Force

dotnet publish $ProjectFile `
    --configuration Release `
    --output $PublishDir `
    --self-contained false `
    /p:PublishSingleFile=true `
    --nologo -v quiet

if ($LASTEXITCODE -ne 0) { Write-Err "dotnet publish fallito (exit code $LASTEXITCODE)." }
if (-not (Test-Path $ExePath)) { Write-Err "Exe non trovato dopo la pubblicazione: $ExePath" }

Write-Ok "Pubblicato: $ExePath"

# ── Registrazione in ~/.claude.json ─────────────────────────────────────────
Write-Step "Registrazione in $ClaudeConfig ..."

# Carica o crea il file di config
if (Test-Path $ClaudeConfig) {
    $cfg = Get-Content $ClaudeConfig -Raw | ConvertFrom-Json
} else {
    $cfg = [PSCustomObject]@{}
}

# Assicura che esista il nodo mcpServers
if (-not $cfg.PSObject.Properties["mcpServers"]) {
    $cfg | Add-Member -MemberType NoteProperty -Name "mcpServers" -Value ([PSCustomObject]@{})
}

# Aggiunge/aggiorna la entry vs-solution
$entry = [PSCustomObject]@{
    type    = "stdio"
    command = $ExePath
    args    = @()
}

if ($cfg.mcpServers.PSObject.Properties[$ServerName]) {
    $cfg.mcpServers.PSObject.Properties[$ServerName].Value = $entry
} else {
    $cfg.mcpServers | Add-Member -MemberType NoteProperty -Name $ServerName -Value $entry
}

$cfg | ConvertTo-Json -Depth 20 | Set-Content $ClaudeConfig -Encoding UTF8
Write-Ok "Aggiunto '$ServerName' in $ClaudeConfig"

# ── Fine ─────────────────────────────────────────────────────────────────────
Write-Host @"

Installazione completata.

Il server MCP '$ServerName' e' ora disponibile globalmente in Claude Code.
Riavvia Claude Code per attivarlo, poi prova:

  /vs-load C:\REPOSITORY\MiaSoluzione\MiaSoluzione.sln

Per disinstallare:  .\install.ps1 -Uninstall
"@ -ForegroundColor Green
