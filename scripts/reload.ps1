<#
.SYNOPSIS
    Hot-reload VisualMCP after editing its source: stop every running server
    instance (which frees the locked DLL/exe), rebuild the Debug DLL and re-publish
    the Release exe. Claude Code relaunches the stdio server on the next tool call,
    so the new build takes effect without a full Claude Code restart.

.DESCRIPTION
    The build outputs are normally locked by the running server, which is why a
    plain rebuild fails with "file in use". This script stops the instances FIRST,
    then rewrites the files, in the correct order:
      1. Kill all VisualMCP server processes (dotnet ...VisualMCP.dll and the
         published VisualMCP.exe).
      2. dotnet build -c Debug   -> updates bin\Debug (used by the project .mcp.json).
      3. dotnet publish -c Release -> updates %USERPROFILE%\.claude\mcp-servers\
         VisualMCP\app (used by the global user-scope registration).

    Seamless for changes to EXISTING tools. If you ADDED or REMOVED tools, the MCP
    tool list is cached by the client for the session — reconnect the VisualMCP
    server (or restart Claude Code) so the new list is picked up.

.PARAMETER NoPublish
    Only rebuild the Debug DLL (project-scope); skip the Release publish.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\reload.ps1
#>
param([switch]$NoPublish)

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Csproj   = Join-Path $RepoRoot 'src\VisualMCP\VisualMCP.csproj'
$AppDir   = Join-Path $env:USERPROFILE '.claude\mcp-servers\VisualMCP\app'

if (-not (Test-Path $Csproj)) { throw "Project not found: $Csproj" }

# 1. Stop every running VisualMCP server instance so the build outputs unlock.
$stopped = @()
Get-CimInstance Win32_Process -Filter "Name='dotnet.exe' OR Name='VisualMCP.exe'" |
    Where-Object { $_.CommandLine -match 'VisualMCP\.dll|VisualMCP\.exe' } |
    ForEach-Object {
        $stopped += $_.ProcessId
        Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
    }
if ($stopped.Count) { Write-Host "Stopped server instance(s): $($stopped -join ', ')" }
else                 { Write-Host "No running server instances." }
Start-Sleep -Milliseconds 800

# 2. Rebuild the Debug DLL (project-scoped .mcp.json points here).
Write-Host "Building Debug..."
& dotnet build $Csproj -c Debug --nologo | Select-Object -Last 2
if ($LASTEXITCODE -ne 0) { throw "Debug build failed (exit $LASTEXITCODE)." }

# 3. Re-publish the Release exe (global user-scope registration points here).
if (-not $NoPublish) {
    Write-Host "Publishing Release to $AppDir ..."
    & dotnet publish $Csproj -c Release -r win-x64 --self-contained false -o $AppDir --nologo | Select-Object -Last 2
    if ($LASTEXITCODE -ne 0) { throw "Release publish failed (exit $LASTEXITCODE)." }
}

Write-Host ""
Write-Host "Reloaded. The next VisualMCP tool call spawns a fresh server with the new build." -ForegroundColor Green
Write-Host "Added/removed a tool? Reconnect the VisualMCP MCP server (or restart Claude Code) to refresh the tool list." -ForegroundColor Yellow
