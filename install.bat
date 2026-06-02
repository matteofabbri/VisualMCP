@echo off
setlocal enabledelayedexpansion

:: ============================================================
:: install.bat  —  Installa VsSolutionPlugin in Claude Code
::
:: Uso:
::   install.bat           -> installa
::   install.bat /uninstall -> disinstalla
:: ============================================================

set SERVER_NAME=vs-solution
set SCRIPT_DIR=%~dp0
set PROJECT=%SCRIPT_DIR%src\VsSolutionServer\VsSolutionServer.csproj
set PUBLISH_DIR=%USERPROFILE%\.claude\mcp-servers\vs-solution
set EXE_PATH=%PUBLISH_DIR%\VsSolutionServer.exe
set CLAUDE_CFG=%USERPROFILE%\.claude.json

:: ── Uninstall ────────────────────────────────────────────────
if /i "%1"=="/uninstall" goto :uninstall

:: ── Prerequisiti ─────────────────────────────────────────────
echo.
echo =^> Verifica prerequisiti...
where dotnet >nul 2>&1
if errorlevel 1 (
    echo    ERR  .NET SDK non trovato.
    echo         Installalo da https://dot.net
    exit /b 1
)
for /f "tokens=*" %%v in ('dotnet --version') do set SDK_VER=%%v
echo    OK   .NET SDK %SDK_VER%

if not exist "%PROJECT%" (
    echo    ERR  File progetto non trovato: %PROJECT%
    echo         Esegui questo script dalla root del repository.
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

if errorlevel 1 (
    echo    ERR  dotnet publish fallito.
    exit /b 1
)
if not exist "%EXE_PATH%" (
    echo    ERR  Exe non trovato: %EXE_PATH%
    exit /b 1
)
echo    OK   Pubblicato: %EXE_PATH%

:: ── Registrazione in ~/.claude.json via PowerShell ───────────
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

if errorlevel 1 (
    echo    ERR  Registrazione fallita.
    exit /b 1
)

:: ── Fine ─────────────────────────────────────────────────────
echo.
echo Installazione completata.
echo.
echo Il server MCP '%SERVER_NAME%' e' ora disponibile globalmente in Claude Code.
echo Riavvia Claude Code per attivarlo, poi prova:
echo.
echo   /vs-load C:\REPOSITORY\MiaSoluzione\MiaSoluzione.sln
echo.
echo Per disinstallare:  install.bat /uninstall
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
