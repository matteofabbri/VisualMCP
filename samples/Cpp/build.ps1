# Builds the sample with the C++ toolchain Visual Studio ships, then optionally runs it.
#
# The same Visual Studio installation provides the LLVM toolset whose clang the server uses to read
# these files (VC\Tools\Llvm\x64\bin\clang.exe), so a machine with the C++ workload installed needs no
# separate compiler.
[CmdletBinding()]
param(
    [switch]$Run
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$build = Join-Path $root 'build'

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw 'Visual Studio (or its Build Tools) is required: vswhere.exe was not found.' }

$installation = & $vswhere -products * -latest -property installationPath
if (-not $installation) { throw 'No Visual Studio installation was found.' }

$vcvars = Join-Path $installation 'VC\Auxiliary\Build\vcvars64.bat'
if (-not (Test-Path $vcvars)) { throw "vcvars64.bat was not found under $installation (the C++ workload is required)." }

New-Item -ItemType Directory -Force -Path $build | Out-Null

$include = Join-Path $root 'include'
$sourceArgs = '"{0}" "{1}"' -f (Join-Path $root 'src\order.cpp'), (Join-Path $root 'src\main.cpp')

Push-Location $build
try {
    $line = 'call "{0}" >nul && cl /nologo /std:c++20 /EHsc /W4 /I"{1}" {2} /Fe:CppDemo.exe' -f `
        $vcvars, $include, $sourceArgs

    cmd /c $line
    if ($LASTEXITCODE -ne 0) { throw "cl failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
}

Write-Host "built $(Join-Path $build 'CppDemo.exe')"
if ($Run) { & (Join-Path $build 'CppDemo.exe') }
