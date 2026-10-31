# vs-init

Initialise a project to use the `VisualMCP` MCP server: writes (or updates) a `CLAUDE.md` in the current working directory that instructs Claude to prefer MCP tools over Grep/PowerShell for all C# analysis.

## Usage

`/vs-init`

Optionally with an explicit solution path to embed in the CLAUDE.md:

`/vs-init C:\REPOSITORY\MyApp\MyApp.sln`

## What it does

1. Checks whether a `CLAUDE.md` already exists in the current directory
2. If it does, appends the `VisualMCP` section without overwriting existing content
3. If it does not, creates a new `CLAUDE.md`
4. Confirms what was written and reminds the user to call `load_solution` at the start of each session

## Skill instructions

When this skill is invoked:

1. Determine the current working directory.
2. If the user supplied a path argument, treat it as the solution path; otherwise leave it as a placeholder.
3. Check if `CLAUDE.md` exists in the cwd using the Read tool (catch the error if it does not exist).
4. Build the block to write (see template below). If CLAUDE.md already contains the string `VisualMCP MCP` do NOT append again - tell the user it is already configured.
5. If CLAUDE.md exists, append the block at the end. If it does not exist, write it as the entire file content.
6. Confirm to the user: what file was written, what the solution path is set to, and that they should restart Claude Code if this is the first time.

## CLAUDE.md block template

```
## VisualMCP MCP

This project uses the MCP server `VisualMCP` for semantic analysis of C# code.

### Mandatory rules

- At the start of every session on C# code call `load_solution` with the solution path.
  Default solution: {{SOLUTION_PATH}}

- For any operation on C# code use the MCP tools, NOT Grep/Bash/PowerShell:

  | Operation                        | MCP tool to use            |
  |----------------------------------|----------------------------|
  | Find a class/method/type         | `find_symbol`              |
  | Find all references              | `find_references`          |
  | Implementations of an interface  | `find_implementations`     |
  | Inheritance hierarchy            | `find_derived_types`       |
  | Who calls a method               | `find_callers`             |
  | Symbol at a specific line        | `get_symbol_info`          |
  | Members of a type                | `get_type_members`         |
  | Body/definition of a symbol      | `read_symbol`              |
  | Compiler errors and warnings     | `get_diagnostics`          |
  | Dependencies between projects    | `analyze_dependencies`     |
  | Dead code                        | `find_unused_symbols`      |
  | Cyclomatic complexity            | `get_metrics`              |
  | Code smells                      | `find_code_smells`         |
  | XML documentation                | `get_xml_docs`             |
  | Public APIs without docs         | `find_undocumented_public_api` |
  | Rename preview                   | `preview_rename`           |
  | Method extraction candidates     | `extract_method_candidates`|
  | Run the tests                    | `run_tests`                |
  | Test coverage                    | `get_test_coverage_map`    |

- Use Grep/Bash/PowerShell only for non-C# files (JSON, YAML, logs, scripts, etc.)
  or when the MCP server is unavailable.

### Notes on the MCP server

The `VisualMCP` server is a local process on your machine.
It does not upload or transmit source files anywhere - it reads from the local filesystem
and returns only structured results (symbol names, line numbers, etc.).
```
