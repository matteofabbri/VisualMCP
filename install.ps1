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

.PARAMETER SetupProject
    Scrive (o aggiorna) un CLAUDE.md nella directory indicata per istruire Claude
    a usare i tool MCP invece di Grep/PowerShell su quel progetto C#.
    Esempio: .\install.ps1 -SetupProject C:\REPOSITORY\MyApp

.PARAMETER SolutionPath
    Path della soluzione da incorporare nel CLAUDE.md generato da -SetupProject.
    Se omesso viene inserito un segnaposto.

.EXAMPLE
    .\install.ps1
    .\install.ps1 -Uninstall
    .\install.ps1 -SetupProject C:\REPOSITORY\MyApp
    .\install.ps1 -SetupProject C:\REPOSITORY\MyApp -SolutionPath C:\REPOSITORY\MyApp\MyApp.sln
#>
param(
    [switch]$Uninstall,
    [string]$SetupProject = "",
    [string]$SolutionPath = ""
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

# ── Setup-Project ─────────────────────────────────────────────────────────────
if ($SetupProject -ne "") {
    $targetDir = Resolve-Path $SetupProject -ErrorAction SilentlyContinue
    if (-not $targetDir) { Write-Err "Directory non trovata: $SetupProject" }

    $claudeMd   = Join-Path $targetDir "CLAUDE.md"
    $slnDisplay = if ($SolutionPath -ne "") { $SolutionPath } else { "<path\to\solution.sln>" }

    $block = @"

## vs-solution MCP

Questo progetto usa il server MCP ``vs-solution`` per l'analisi semantica del codice C#.

### Regole obbligatorie

- All'inizio di ogni sessione su codice C# chiama ``load_solution`` con il path della soluzione.
  Soluzione di default: $slnDisplay

- Per qualsiasi operazione su codice C# usa i tool MCP, NON Grep/Bash/PowerShell:

  | Operazione                        | Tool MCP da usare              |
  |-----------------------------------|--------------------------------|
  | Cercare una classe/metodo/tipo    | ``find_symbol``                |
  | Trovare tutti i riferimenti       | ``find_references``            |
  | Implementazioni di un'interfaccia | ``find_implementations``       |
  | Gerarchia di ereditarietà         | ``find_derived_types``         |
  | Chi chiama un metodo              | ``find_callers``               |
  | Simbolo a riga specifica          | ``get_symbol_info``            |
  | Membri di un tipo                 | ``get_type_members``           |
  | Errori e warning del compilatore  | ``get_diagnostics``            |
  | Dipendenze tra progetti           | ``analyze_dependencies``       |
  | Codice morto                      | ``find_unused_symbols``        |
  | Complessità ciclomatica           | ``get_metrics``                |
  | Code smell                        | ``find_code_smells``           |
  | Documentazione XML                | ``get_xml_docs``               |
  | API pubbliche senza doc           | ``find_undocumented_public_api``|
  | Anteprima rename                  | ``preview_rename``             |
  | Candidati estrazione metodo       | ``extract_method_candidates``  |
  | Eseguire i test                   | ``run_tests``                  |
  | Coverage dei test                 | ``get_test_coverage_map``      |

- Usa Grep/Bash/PowerShell solo per file non-C# (JSON, YAML, log, script, ecc.)
  o quando il server MCP non e' disponibile.

### Note

Il server ``vs-solution`` e' un processo locale — non trasmette file sorgente da nessuna parte.
"@

    Write-Step "Configurazione progetto in $targetDir ..."

    if (Test-Path $claudeMd) {
        $existing = Get-Content $claudeMd -Raw
        if ($existing -match "vs-solution MCP") {
            Write-Host "    (CLAUDE.md esiste gia' con la sezione vs-solution — nessuna modifica)" -ForegroundColor Yellow
        } else {
            Add-Content $claudeMd $block -Encoding UTF8
            Write-Ok "Sezione vs-solution aggiunta a $claudeMd"
        }
    } else {
        Set-Content $claudeMd $block.TrimStart() -Encoding UTF8
        Write-Ok "Creato $claudeMd"
    }

    Write-Host @"

Setup completato.

Claude Code usera' i tool MCP per l'analisi C# in questo progetto.
Soluzione configurata: $slnDisplay

Se hai modificato la soluzione, aggiorna il path in $claudeMd
oppure riesegui:  .\install.ps1 -SetupProject "$targetDir" -SolutionPath "path\to\solution.sln"
"@ -ForegroundColor Green
    exit 0
}

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

if (Test-Path $ClaudeConfig) {
    $cfg = Get-Content $ClaudeConfig -Raw | ConvertFrom-Json
} else {
    $cfg = [PSCustomObject]@{}
}

if (-not $cfg.PSObject.Properties["mcpServers"]) {
    $cfg | Add-Member -MemberType NoteProperty -Name "mcpServers" -Value ([PSCustomObject]@{})
}

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
Riavvia Claude Code per attivarlo, poi configura ogni progetto C# con:

  .\install.ps1 -SetupProject C:\REPOSITORY\MioProgetto
  .\install.ps1 -SetupProject C:\REPOSITORY\MioProgetto -SolutionPath C:\REPOSITORY\MioProgetto\MioProgetto.sln

Oppure dall'interno di Claude Code:  /vs-init

Per disinstallare:  .\install.ps1 -Uninstall
"@ -ForegroundColor Green
