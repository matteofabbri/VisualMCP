<#
.SYNOPSIS
    Convenience wrapper: installs VisualMCP globally (ZCode + Claude Code).
    Equivalent to:  install.ps1 -Scope all
    See install.ps1 for single-client (user/local/project/zcode) and uninstall.
#>
& (Join-Path $PSScriptRoot 'install.ps1') -Scope all @args
