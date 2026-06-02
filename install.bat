@echo off
setlocal enabledelayedexpansion

:: ============================================================
:: install.bat  —  Installa VsSolutionPlugin in Claude Code
::
:: Uso:
::   install.bat                                      -> installa
::   install.bat /uninstall                           -> disinstalla
::   install.bat /setup-project C:\PATH\MioProgetto  -> configura un progetto
::   install.bat /setup-project C:\PATH\MioProgetto /sln C:\PATH\MioProgetto\App.sln
:: ============================================================

set SERVER_NAME=vs-solution
set SCRIPT_DIR=%~dp0
set PROJECT=%SCRIPT_DIR%src\VsSolutionServer\VsSolutionServer.csproj
set PUBLISH_DIR=%USERPROFILE%\.claude\mcp-servers\vs-solution
set EXE_PATH=%PUBLISH_DIR%\VsSolutionServer.exe
set CLAUDE_CFG=%USERPROFILE%\.claude.json

if /i "%1"=="/uninstall"      goto :uninstall
if /i "%1"=="/setup-project"  goto :setup_project

:: ── Prerequisiti ─────────────────────────────────────────────
echo.
echo =^> Verifica prerequisiti...
where dotnet >nul 2>&1
if errorlevel 1 (
    echo    ERR  .NET SDK non trovato. Installalo da https://dot.net
    exit /b 1
)
for /f "tokens=*" %%v in ('dotnet --version') do set SDK_VER=%%v
echo    OK   .NET SDK %SDK_VER%

if not exist "%PROJECT%" (
    echo    ERR  File progetto non trovato: %PROJECT%
    exit /b 1
)
echo    OK   Progetto trovato

:: ── Publish ──────────────────────────────────────────────────
echo.
echo =^> Pubblicazione in %PUBLISH_DIR% ...
if not exist "%PUBLISH_DIR%" mkdir "%PUBLISH_DIR%"

dotnet publish "%PROJECT%" ^
    --configuration Release ^
    --output "%PUBLISH_DIR%" ^
    --self-contained false ^
    /p:PublishSingleFile=true ^
    --nologo -v quiet

if errorlevel 1 ( echo    ERR  dotnet publish fallito. & exit /b 1 )
if not exist "%EXE_PATH%" ( echo    ERR  Exe non trovato: %EXE_PATH% & exit /b 1 )
echo    OK   Pubblicato: %EXE_PATH%

:: ── Registrazione in ~/.claude.json ──────────────────────────
echo.
echo =^> Registrazione in %CLAUDE_CFG% ...

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "$cfg = if (Test-Path '%CLAUDE_CFG%') { Get-Content '%CLAUDE_CFG%' -Raw | ConvertFrom-Json } else { [PSCustomObject]@{} }; ^
     if (-not $cfg.PSObject.Properties['mcpServers']) { $cfg | Add-Member -MemberType NoteProperty -Name 'mcpServers' -Value ([PSCustomObject]@{}) }; ^
     $entry = [PSCustomObject]@{ type='stdio'; command='%EXE_PATH:\=\\%'; args=@() }; ^
     if ($cfg.mcpServers.PSObject.Properties['%SERVER_NAME%']) { $cfg.mcpServers.PSObject.Properties['%SERVER_NAME%'].Value = $entry } ^
     else { $cfg.mcpServers | Add-Member -MemberType NoteProperty -Name '%SERVER_NAME%' -Value $entry }; ^
     $cfg | ConvertTo-Json -Depth 20 | Set-Content '%CLAUDE_CFG%' -Encoding UTF8; ^
     Write-Host '   OK   Aggiunto %SERVER_NAME% in %CLAUDE_CFG%'"

if errorlevel 1 ( echo    ERR  Registrazione fallita. & exit /b 1 )

:: ── Fine ─────────────────────────────────────────────────────
echo.
echo Installazione completata.
echo.
echo Il server MCP '%SERVER_NAME%' e' ora disponibile globalmente in Claude Code.
echo Riavvia Claude Code, poi configura ogni progetto C# con:
echo.
echo   install.bat /setup-project C:\REPOSITORY\MioProgetto
echo   install.bat /setup-project C:\REPOSITORY\MioProgetto /sln C:\REPOSITORY\MioProgetto\App.sln
echo.
echo Oppure dall'interno di Claude Code:  /vs-init
echo Per disinstallare: install.bat /uninstall
goto :eof

:: ── Setup-Project ─────────────────────────────────────────────
:setup_project
set TARGET_DIR=%2
set SLN_PATH=^<path\to\solution.sln^>

if "%TARGET_DIR%"=="" (
    echo    ERR  Specifica la directory del progetto: install.bat /setup-project C:\PATH\MioProgetto
    exit /b 1
)
if not exist "%TARGET_DIR%" (
    echo    ERR  Directory non trovata: %TARGET_DIR%
    exit /b 1
)

:: Parametro /sln opzionale
if /i "%3"=="/sln" (
    if not "%4"=="" set SLN_PATH=%4
)

set CLAUDE_MD=%TARGET_DIR%\CLAUDE.md

echo.
echo =^> Configurazione progetto in %TARGET_DIR% ...

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "$slnDisplay = '%SLN_PATH%'; ^
     $claudeMd   = '%CLAUDE_MD:\=\\%'; ^
     $block = \"`n## vs-solution MCP\`n\`nQuesto progetto usa il server MCP ``vs-solution`` per l'analisi semantica del codice C#.\`n\`n### Regole obbligatorie\`n\`n- All'inizio di ogni sessione chiama ``load_solution`` con il path della soluzione.\`n  Soluzione di default: $slnDisplay\`n\`n- Usa i tool MCP invece di Grep/Bash/PowerShell per qualsiasi operazione su codice C#.\`n  Tool disponibili: find_symbol, find_references, find_implementations, find_derived_types,\`n  find_callers, get_symbol_info, get_type_members, get_diagnostics, analyze_dependencies,\`n  find_unused_symbols, get_metrics, find_code_smells, get_xml_docs,\`n  find_undocumented_public_api, preview_rename, extract_method_candidates,\`n  run_tests, get_test_coverage_map.\`n\`n- Usa Grep/Bash/PowerShell solo per file non-C# o quando il server MCP non e' disponibile.\`n\"; ^
     if (Test-Path $claudeMd) { ^
         $existing = Get-Content $claudeMd -Raw; ^
         if ($existing -match 'vs-solution MCP') { Write-Host '   (CLAUDE.md gia'' configurato — nessuna modifica)' } ^
         else { Add-Content $claudeMd $block -Encoding UTF8; Write-Host '   OK   Sezione aggiunta a' $claudeMd } ^
     } else { ^
         Set-Content $claudeMd $block.TrimStart() -Encoding UTF8; ^
         Write-Host '   OK   Creato' $claudeMd ^
     }"

if errorlevel 1 ( echo    ERR  Setup fallito. & exit /b 1 )

echo.
echo Setup completato. Claude usera' i tool MCP per il codice C# in questo progetto.
echo Soluzione configurata: %SLN_PATH%
goto :eof

:: ── Uninstall ────────────────────────────────────────────────
:uninstall
echo.
echo =^> Rimozione server MCP '%SERVER_NAME%'...

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "if (Test-Path '%CLAUDE_CFG%') { ^
        $cfg = Get-Content '%CLAUDE_CFG%' -Raw | ConvertFrom-Json; ^
        if ($cfg.PSObject.Properties['mcpServers'] -and $cfg.mcpServers.PSObject.Properties['%SERVER_NAME%']) { ^
            $cfg.mcpServers.PSObject.Properties.Remove('%SERVER_NAME%'); ^
            $cfg | ConvertTo-Json -Depth 20 | Set-Content '%CLAUDE_CFG%' -Encoding UTF8; ^
            Write-Host '   OK   Rimosso da %CLAUDE_CFG%' ^
        } else { Write-Host '   (non presente in %CLAUDE_CFG%)' } ^
    }"

if exist "%PUBLISH_DIR%" (
    rmdir /s /q "%PUBLISH_DIR%"
    echo    OK   Rimossa directory %PUBLISH_DIR%
)

echo.
echo Disinstallazione completata. Riavvia Claude Code.
