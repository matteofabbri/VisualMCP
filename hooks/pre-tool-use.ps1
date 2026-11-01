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

function Find-RepositoryRoot([string]$Directory, [int]$MaxParents) {
    if ([string]::IsNullOrWhiteSpace($Directory)) { return $null }
    try { $dir = [System.IO.DirectoryInfo]::new($Directory) } catch { return $null }
    for ($i = 0; $i -le $MaxParents -and $null -ne $dir; $i++) {
        try {
            if ($dir.Exists) {
                $hit = Get-ChildItem -LiteralPath $dir.FullName -Filter '*.sln*' -File -ErrorAction SilentlyContinue |
                       Select-Object -First 1
                if ($hit) { return $dir.FullName }
            }
        } catch { }
        $dir = $dir.Parent
    }
    return $null
}

function Test-DotNetRepository([string]$Directory, [int]$MaxParents) {
    return $null -ne (Find-RepositoryRoot $Directory $MaxParents)
}

function Get-ProtectedWriteReason([string]$ToolName, [string]$Path, [string]$Cwd) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }

    $normalized = $Path -replace '\\', '/'
    $name = [System.IO.Path]::GetFileName($normalized)

    # The clients' memory stores, and the index file every client calls MEMORY.md.
    if ($name -ieq 'MEMORY.md' -or
        $normalized -match '[\\/]\.zcode[\\/]cli[\\/]memories[\\/]' -or
        $normalized -match '[\\/]\.claude[\\/]projects[\\/][^\\/]+[\\/]memory[\\/]') {
        return ("VisualMCP: project memory lives on the board. " +
            "Use board_remember{topic, body, kind} and read it back with board_recall{query}; pinned notes ride " +
            "along in board_brief and read_project_docs. A MEMORY.md or a note file is not read by the next session. " +
            "To write it deliberately, use the MCP tool run_command.")
    }

    # A NEW .md at the repository root, or a plan-like one in docs/. The root holds a fixed set of
    # documents; anything else created there (a plan, a checklist, a working note named after the
    # task it describes) is noise the next session reads as documentation. The plan is a card.
    # Editing a document that already exists is never refused (docs/gaps.md stays editable).
    if ($ToolName -ne 'Write') { return $null }
    if ($normalized -notmatch '\.md$') { return $null }

    $root = Find-RepositoryRoot $Cwd 3
    if ($null -eq $root) { return $null }

    $absolute = if ([System.IO.Path]::IsPathRooted($Path)) { $Path } else { Join-Path $Cwd $Path }
    try { $absolute = [System.IO.Path]::GetFullPath($absolute) } catch { return $null }
    if (Test-Path -LiteralPath $absolute) { return $null }

    $folder = [System.IO.Path]::GetDirectoryName($absolute)
    $rootFolder = [System.IO.Path]::GetFullPath($root)
    $docsFolder = [System.IO.Path]::GetFullPath((Join-Path $root 'docs'))

    if ($folder -ieq $rootFolder) {
        $documents = @(
            'readme', 'contributing', 'license', 'changelog', 'code_of_conduct',
            'security', 'authors', 'notice', 'support', 'governance', 'citation'
        )
        $stem = [System.IO.Path]::GetFileNameWithoutExtension($name).ToLowerInvariant()
        if ($documents -contains $stem) { return $null }

        return ("VisualMCP: a new Markdown file at the repository root is not where this belongs. " +
            "The root holds the published documents (README, CONTRIBUTING, LICENSE, CHANGELOG); documentation " +
            "goes under docs/, and work to be done goes on the board: create_task_checklist{title, steps} or " +
            "board_add{title, description, steps} create a card, board_move{status, note} records progress; " +
            "no file is written and the card is what the next session reads. " +
            "To write the file deliberately, use the MCP tool run_command.")
    }

    if ($folder -ine $docsFolder) { return $null }
    if ($name -notmatch '(?i)(checklist|plan|backlog|todo|handoff|notes|gaps|memory)') { return $null }

    return ("VisualMCP: a plan/notes file is not the plan. " +
        "Put the work on the board: create_task_checklist{title, steps} creates one card with the steps as a " +
        "checklist (no file), board_add{title, steps} the same on a card you describe, board_move{status, note} " +
        "records progress. The board is what the next session reads. " +
        "To write the file deliberately, use the MCP tool run_command.")
}

try {
    if (-not [Console]::IsInputRedirected) { exit 0 }
    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }

    try { $payload = $raw | ConvertFrom-Json } catch { exit 0 }

    $toolName = [string]$payload.tool_name
    $cwd = [string]$payload.cwd
    if ([string]::IsNullOrWhiteSpace($cwd)) { $cwd = (Get-Location).Path }

    # A write into a memory store, or the creation of a plan-like .md, belongs on the board.
    if ($toolName -in @('Write', 'Edit', 'MultiEdit', 'NotebookEdit')) {
        $path = ''
        if ($payload.tool_input -and $payload.tool_input.PSObject.Properties['file_path']) {
            $path = [string]$payload.tool_input.file_path
        }
        elseif ($payload.tool_input -and $payload.tool_input.PSObject.Properties['path']) {
            $path = [string]$payload.tool_input.path
        }

        $reason = Get-ProtectedWriteReason $toolName $path $cwd
        if ($reason) { Send-Deny $reason }
        exit 0
    }

    if ($toolName -ne 'Bash') { exit 0 }

    $command = ''
    if ($payload.tool_input -and $payload.tool_input.PSObject.Properties['command']) {
        $command = [string]$payload.tool_input.command
    }
    if ([string]::IsNullOrWhiteSpace($command)) { exit 0 }

    # Explicit escape hatch: the model (or a user) can keep the shell on purpose.
    if ($command -match 'vmcp-allow') { exit 0 }

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
