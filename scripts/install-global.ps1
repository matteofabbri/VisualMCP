<#
.SYNOPSIS
    Convenience wrapper: installs VisualMCP globally (user scope).
    Equivalent to:  install.ps1 -Scope user
    See install.ps1 for project/local installs and uninstall.
#>
& (Join-Path $PSScriptRoot 'install.ps1') -Scope user @args
