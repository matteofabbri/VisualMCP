@echo off
setlocal enabledelayedexpansion

:: ============================================================
:: install.bat  -  Installs VisualMCP into Claude Code
::
:: Usage:
::   install.bat                                      -> install
::   install.bat /uninstall                           -> uninstall
::   install.bat /setup-project C:\PATH\MyProject     -> configure a project
::   install.bat /setup-project C:\PATH\MyProject /sln C:\PATH\MyProject\App.sln
:: ============================================================

set SERVER_NAME=VisualMCP
set SCRIPT_DIR=%~dp0
set PROJECT=%SCRIPT_DIR%src\VisualMCP\VisualMCP.csproj
set PUBLISH_DIR=%USERPROFILE%\.claude\mcp-servers\VisualMCP
set EXE_PATH=%PUBLISH_DIR%\VisualMCP.exe
set CLAUDE_CFG=%USERPROFILE%\.claude.json

if /i "%1"=="/uninstall"      goto :uninstall
if /i "%1"=="/setup-project"  goto :setup_project

:: -- Prerequisites -------------------------------------------------------
echo.
echo =^> Checking prerequisites...
where dotnet >nul 2>&1
if errorlevel 1 (
    echo    ERR  .NET SDK not found. Install it from https://dot.net
    exit /b 1
)
for /f "tokens=*" %%v in ('dotnet --version') do set SDK_VER=%%v
echo    OK   .NET SDK %SDK_VER%

if not exist "%PROJECT%" (
    echo    ERR  Project file not found: %PROJECT%
    exit /b 1
)
echo    OK   Project found

:: -- Publish -------------------------------------------------------------
echo.
echo =^> Publishing to %PUBLISH_DIR% ...
if not exist "%PUBLISH_DIR%" mkdir "%PUBLISH_DIR%"

dotnet publish "%PROJECT%" ^
    --configuration Release ^
    --output "%PUBLISH_DIR%" ^
    --self-contained false ^
    /p:PublishSingleFile=true ^
    --nologo -v quiet

if errorlevel 1 ( echo    ERR  dotnet publish failed. & exit /b 1 )
if not exist "%EXE_PATH%" ( echo    ERR  Exe not found: %EXE_PATH% & exit /b 1 )
echo    OK   Published: %EXE_PATH%

:: -- Registration in ~/.claude.json --------------------------------------
echo.
echo =^> Registering in %CLAUDE_CFG% ...

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "$cfg = if (Test-Path '%CLAUDE_CFG%') { Get-Content '%CLAUDE_CFG%' -Raw | ConvertFrom-Json } else { [PSCustomObject]@{} }; ^
     if (-not $cfg.PSObject.Properties['mcpServers']) { $cfg | Add-Member -MemberType NoteProperty -Name 'mcpServers' -Value ([PSCustomObject]@{}) }; ^
     $entry = [PSCustomObject]@{ type='stdio'; command='%EXE_PATH:\=\\%'; args=@() }; ^
     if ($cfg.mcpServers.PSObject.Properties['%SERVER_NAME%']) { $cfg.mcpServers.PSObject.Properties['%SERVER_NAME%'].Value = $entry } ^
     else { $cfg.mcpServers | Add-Member -MemberType NoteProperty -Name '%SERVER_NAME%' -Value $entry }; ^
     $cfg | ConvertTo-Json -Depth 20 | Set-Content '%CLAUDE_CFG%' -Encoding UTF8; ^
     Write-Host '   OK   Added %SERVER_NAME% to %CLAUDE_CFG%'"

if errorlevel 1 ( echo    ERR  Registration failed. & exit /b 1 )

:: -- Done ----------------------------------------------------------------
echo.
echo Installation complete.
echo.
echo The MCP server '%SERVER_NAME%' is now available globally in Claude Code.
echo Restart Claude Code, then configure each C# project with:
echo.
echo   install.bat /setup-project C:\REPOSITORY\MyProject
echo   install.bat /setup-project C:\REPOSITORY\MyProject /sln C:\REPOSITORY\MyProject\App.sln
echo.
echo Or from inside Claude Code:  /vs-init
echo To uninstall: install.bat /uninstall
goto :eof

:: -- Setup-Project -------------------------------------------------------
:setup_project
set TARGET_DIR=%2
set SLN_PATH=^<path\to\solution.sln^>

if "%TARGET_DIR%"=="" (
    echo    ERR  Specify the project directory: install.bat /setup-project C:\PATH\MyProject
    exit /b 1
)
if not exist "%TARGET_DIR%" (
    echo    ERR  Directory not found: %TARGET_DIR%
    exit /b 1
)

:: Optional /sln parameter
if /i "%3"=="/sln" (
    if not "%4"=="" set SLN_PATH=%4
)

set CLAUDE_MD=%TARGET_DIR%\CLAUDE.md

echo.
echo =^> Configuring project in %TARGET_DIR% ...

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "$slnDisplay = '%SLN_PATH%'; ^
     $claudeMd   = '%CLAUDE_MD:\=\\%'; ^
     $block = \"`n## VisualMCP MCP\`n\`nThis project uses the MCP server ``VisualMCP`` for semantic analysis of C# code.\`n\`n### Mandatory rules\`n\`n- At the start of every session call ``load_solution`` with the solution path.\`n  Default solution: $slnDisplay\`n\`n- Use the MCP tools instead of Grep/Bash/PowerShell for any operation on C# code.\`n  Available tools: find_symbol, find_references, find_implementations, find_derived_types,\`n  find_callers, get_symbol_info, get_type_members, get_diagnostics, analyze_dependencies,\`n  find_unused_symbols, get_metrics, find_code_smells, get_xml_docs,\`n  find_undocumented_public_api, preview_rename, extract_method_candidates,\`n  run_tests, get_test_coverage_map.\`n\`n- Use Grep/Bash/PowerShell only for non-C# files or when the MCP server is unavailable.\`n\"; ^
     if (Test-Path $claudeMd) { ^
         $existing = Get-Content $claudeMd -Raw; ^
         if ($existing -match 'VisualMCP MCP') { Write-Host '   (CLAUDE.md already configured - no change)' } ^
         else { Add-Content $claudeMd $block -Encoding UTF8; Write-Host '   OK   Section added to' $claudeMd } ^
     } else { ^
         Set-Content $claudeMd $block.TrimStart() -Encoding UTF8; ^
         Write-Host '   OK   Created' $claudeMd ^
     }"

if errorlevel 1 ( echo    ERR  Setup failed. & exit /b 1 )

echo.
echo Setup complete. Claude will use the MCP tools for C# code in this project.
echo Configured solution: %SLN_PATH%
goto :eof

:: -- Uninstall -----------------------------------------------------------
:uninstall
echo.
echo =^> Removing MCP server '%SERVER_NAME%'...

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "if (Test-Path '%CLAUDE_CFG%') { ^
        $cfg = Get-Content '%CLAUDE_CFG%' -Raw | ConvertFrom-Json; ^
        if ($cfg.PSObject.Properties['mcpServers'] -and $cfg.mcpServers.PSObject.Properties['%SERVER_NAME%']) { ^
            $cfg.mcpServers.PSObject.Properties.Remove('%SERVER_NAME%'); ^
            $cfg | ConvertTo-Json -Depth 20 | Set-Content '%CLAUDE_CFG%' -Encoding UTF8; ^
            Write-Host '   OK   Removed from %CLAUDE_CFG%' ^
        } else { Write-Host '   (not present in %CLAUDE_CFG%)' } ^
    }"

if exist "%PUBLISH_DIR%" (
    rmdir /s /q "%PUBLISH_DIR%"
    echo    OK   Removed directory %PUBLISH_DIR%
)

echo.
echo Uninstall complete. Restart Claude Code.
