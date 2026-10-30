#Requires -Version 5.1
<#
.SYNOPSIS
    ZCode / Claude Code PreToolUse hook: route read-only shell probes on .NET
    repositories through VisualMCP instead of Bash.

.DESCRIPTION
    Reads the hook payload from stdin (tool_name + tool_input.command) and, when the
    command is a read-only probe (grep/rg, sed -n, cat/head/tail, ls/find/dir,
    Select-String, Get-Content, Get-ChildItem) inside a .NET repository, answers with a
    PreToolUse "deny" decision whose reason names the MCP tool that does the job.

    Why a hook and not a prompt: ZCode does not forward the MCP server's
    `instructions` to the model, so the server cannot advertise its own routing
    rules. A PreToolUse hook is the only channel that reaches the model in-band,
    and it corrects the behaviour at the exact moment of the wrong call.

    Design rules:
      - pass everything that is not a Bash probe (build, test, git, scripts, writes);
      - enforce only when a .sln/.slnx is visible within -SolutionSearchParents levels
        of the working directory (a non-.NET repo keeps its shell);
      - never fail the session: any error or unparsable payload exits 0 (pass);
      - "vmcp-allow" anywhere in the command is an explicit escape hatch.

.EXAMPLE
    '{"tool_name":"Bash","tool_input":{"command":"grep -rn Foo ."}}' | powershell -File hooks\pre-tool-use.ps1
    -> deny JSON on stdout, exit code 0
#>
param(
    # How many parent directories to walk up looking for a .sln/.slnx.
    [int] $SolutionSearchParents = 3
)

$ErrorActionPreference = 'Stop'

function Send-HookJson([string]$Json) {
    $bytes  = [System.Text.Encoding]::UTF8.GetBytes($Json)
    $stdout = [Console]::OpenStandardOutput()
    $stdout.Write($bytes, 0, $bytes.Length)
    $stdout.Flush()
}

function Send-Deny([string]$Reason) {
    # Schema (ZCode runtime, PreToolUse variant):
    #   { hookSpecificOutput: { hookEventName, permissionDecision, permissionDecisionReason } }
    # Extra keys are rejected, so emit exactly these three.
    $payload = [PSCustomObject]@{
        hookSpecificOutput = [PSCustomObject]@{
            hookEventName            = 'PreToolUse'
            permissionDecision       = 'deny'
            permissionDecisionReason = $Reason
        }
    }
    Send-HookJson ($payload | ConvertTo-Json -Depth 5 -Compress)
}

function Test-DotNetRepository([string]$Directory, [int]$MaxParents) {
    if ([string]::IsNullOrWhiteSpace($Directory)) { return $false }
    try { $dir = [System.IO.DirectoryInfo]::new($Directory) } catch { return $false }
    for ($i = 0; $i -le $MaxParents -and $null -ne $dir; $i++) {
        try {
            if ($dir.Exists) {
                $hit = Get-ChildItem -LiteralPath $dir.FullName -Filter '*.sln*' -File -ErrorAction SilentlyContinue |
                       Select-Object -First 1
                if ($hit) { return $true }
            }
        } catch { }
        $dir = $dir.Parent
    }
    return $false
}

try {
    if (-not [Console]::IsInputRedirected) { exit 0 }
    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }

    try { $payload = $raw | ConvertFrom-Json } catch { exit 0 }

    $toolName = [string]$payload.tool_name
    if ($toolName -ne 'Bash') { exit 0 }

    $command = ''
    if ($payload.tool_input -and $payload.tool_input.PSObject.Properties['command']) {
        $command = [string]$payload.tool_input.command
    }
    if ([string]::IsNullOrWhiteSpace($command)) { exit 0 }

    # Explicit escape hatch: the model (or a user) can keep the shell on purpose.
    if ($command -match 'vmcp-allow') { exit 0 }

    $cwd = [string]$payload.cwd
    if ([string]::IsNullOrWhiteSpace($cwd)) { $cwd = (Get-Location).Path }
    if (-not (Test-DotNetRepository $cwd $SolutionSearchParents)) { exit 0 }

    $probes = @(
        @{ Name = 'full-text search'; Tool = 'search_text{pattern, path, glob} (use find_symbol / find_references for C# symbols, never text search)' },
        @{ Name = 'line-range read';  Tool = 'read_file{path, fromLine, toLine}' },
        @{ Name = 'directory listing'; Tool = 'list_directory{path} (or search_text to match file names)' },
        @{ Name = 'inline file rewrite'; Tool = 'Edit (or Write for a new file), not a shell heredoc' }
    )

    # Rewriting files from a shell heredoc / inline code: Edit and Write do the same job
    # without quoting or encoding traps, and the change stays reviewable. Only inline
    # python is judged (the script content is visible); `python build.py` is left alone.
    $pythonInline = $command -match '(^|[\s;&|(])(py|python3?(\.\d+)?)\b[^;|&]*?(<<\s*\S*|\s-c\b)'
    $writeIntent  = $command -match 'write_text\s*\(|\.write\s*\(|open\s*\([^)]*["'']w|\.unlink\s*\(|os\.replace\s*\(|shutil\.(copy|move|rmtree)|\.rename\s*\('
    $sedInPlace   = $command -match '(^|[\s;&|(])sed\b[^|;&]*\s-i'

    # Creating a file from the shell: "cat > f <<'EOF'", "echo … > f", "… | tee f".
    # Write does the same without heredoc quoting, and the content lands reviewable.
    $shellCreate  = ($command -match '(^|[\s;&|(])(cat|printf|echo)\b[^|;&]*?>>?\s*[^\s|&;]+') -or
                    ($command -match '(^|[\s;&|(])tee(\s|$)')

    $rewrite      = (($pythonInline -and $writeIntent) -or $sedInPlace -or $shellCreate)

    # Build / test / VCS / tooling / file mutations, and a segment whose output goes to a
    # file, are never probes. Evaluated per segment: a probe glued to a build
    # ("rm old && grep Foo") is still caught, while a pipeline ("dotnet test | tail -3")
    # keeps its build segment as the one that decides.
    $rxTooling       = '(^|[\s;&|(])(dotnet|msbuild|nuget|git|gh|npm|pnpm|yarn|npx|node|python|python3|py|pip|cargo|go|make|cmake|ninja|docker|kubectl|terraform|gradle|mvn|java|curl|wget|rm|rmdir|mkdir|mv|cp|touch|chmod|chown|ln|tar|zip|unzip|7z)(\s|$)'
    $rxWriteRedirect = '>>?\s*(?!/dev/null)[^\s|&;]'
    $rxSearch        = '(^|[\s|;&(])(grep|egrep|fgrep|rg|ag|ack|Select-String)(\s|$)'
    $rxSedRead       = '(^|[\s|;&(])sed\b[^|;&]*\s-n\b'
    $rxCat           = '(^|[\s|;&(])(cat|head|tail|more|less|bat|nl|tac|Get-Content)(\s|$)'
    $rxList          = '(^|[\s|;&(])(ls|dir|find|tree|Get-ChildItem)(\s|$)'

    $hit = $null
    if ($rewrite) {
        $hit = $probes[3]
    }
    else {
        foreach ($segment in ($command -split '&&|\|\||;')) {
            $seg = $segment.Trim()
            if (-not $seg) { continue }
            if ($seg -match $rxTooling) { continue }
            if ($seg -match $rxWriteRedirect) { continue }
            if ($seg -match $rxSearch)  { $hit = $probes[0]; break }
            if ($seg -match $rxSedRead) { $hit = $probes[1]; break }
            if ($seg -match $rxCat)     { $hit = $probes[1]; break }
            if ($seg -match $rxList)    { $hit = $probes[2]; break }
        }
    }

    if (-not $hit) { exit 0 }

    # Short on purpose: the model reads this, it is not a document.
    $reason = if ($hit.Name -eq 'inline file rewrite') {
        @(
            "VisualMCP: this command rewrites files through the shell inside a .NET repository.",
            "Use the harness tool instead: $($hit.Tool) - no quoting or encoding traps, and the change stays reviewable.",
            "To keep Bash deliberately, include 'vmcp-allow' in the command."
        ) -join ' '
    } else {
        @(
            "VisualMCP: this is a $($hit.Name) shell probe inside a .NET repository.",
            "Use the MCP tool instead: $($hit.Tool).",
            "The solution is already loaded; do not call load_solution first.",
            "If the shell form is genuinely required (binary files, counting, complex pipelines),",
            "call the MCP tool run_command with the exact same command.",
            "To keep Bash deliberately, include 'vmcp-allow' in the command."
        ) -join ' '
    }

    Send-Deny $reason
    exit 0
}
catch {
    # A hook must never break the session.
    exit 0
}
