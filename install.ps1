#Requires -Version 5.1
<#
.SYNOPSIS
    Installa VisualMCP come server MCP per ZCode e/o Claude Code.

.DESCRIPTION
    Wrapper di scripts\install.ps1 (l'unica implementazione: publish + registrazione,
    che ora installa anche il routing guard: hook PreToolUse + blocco AGENTS.md).

    Questo file aggiunge -SetupProject, che scrive nel progetto C# di destinazione
    lo stesso blocco di istruzioni gestito da VisualMCP (hooks\agents-block.md),
    sia in CLAUDE.md (Claude Code) sia in AGENTS.md (ZCode), cosi' ogni client sa
    quando usare i tool invece di Grep/PowerShell.

    Il blocco e' delimitato dai marker "<!-- VisualMCP:begin/end -->": rieseguire
    lo script lo sostituisce invece di duplicarlo, e lascia intatto il resto del file.

.PARAMETER Scope
    all (default) = ZCode + Claude Code + app desktop; zcode = solo ZCode;
    user/project/local = solo Claude Code (vedi scripts\install.ps1).

.PARAMETER Uninstall
    Rimuove il server MCP (e il routing guard) dalle configurazioni e cancella i
    file pubblicati.

.PARAMETER StopRunning
    Ferma le istanze del server attualmente in esecuzione prima del publish
    (l'exe installato altrimenti e' bloccato).

.PARAMETER SetupProject
    Scrive (o aggiorna) il blocco istruzioni VisualMCP in CLAUDE.md e AGENTS.md
    nella directory indicata. Esempio: .\install.ps1 -SetupProject C:\REPOSITORY\MyApp

.PARAMETER SolutionPath
    Opzionale: soluzione di default da citare nel blocco scritto da -SetupProject.

.PARAMETER ProjectDir
    Solo per -Scope project/local: il progetto a cui agganciare il server.

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
    Write-Err "Script non trovato: $InstallScript`nEsegui questo script dalla root del repository."
}

# --- Setup-Project ----------------------------------------------------------
if ($SetupProject -ne "") {
    $targetDir = Resolve-Path $SetupProject -ErrorAction SilentlyContinue
    if (-not $targetDir) { Write-Err "Directory non trovata: $SetupProject" }
    if (-not (Test-Path $BlockSource)) { Write-Err "Blocco istruzioni non trovato: $BlockSource" }

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

    Write-Step "Configurazione progetto in $targetDir ..."

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

        if ($existed) { Write-Ok "Blocco VisualMCP aggiornato in $docPath" }
        else          { Write-Ok "Creato $docPath" }
    }

    Write-Host @"

Setup completato.

Claude Code (CLAUDE.md) e ZCode (AGENTS.md) useranno i tool MCP per l'analisi C# in questo progetto.

Nota: l'hook PreToolUse che blocca grep/sed/cat/ls su .NET e' a livello utente, non di
progetto: si installa (o si aggiorna) con
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
