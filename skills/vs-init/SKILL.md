# vs-init

Initialise a project to use the `vs-solution` MCP server: writes (or updates) a `CLAUDE.md` in the current working directory that instructs Claude to prefer MCP tools over Grep/PowerShell for all C# analysis.

## Usage

`/vs-init`

Optionally with an explicit solution path to embed in the CLAUDE.md:

`/vs-init C:\REPOSITORY\MyApp\MyApp.sln`

## What it does

1. Checks whether a `CLAUDE.md` already exists in the current directory
2. If it does, appends the `vs-solution` section without overwriting existing content
3. If it does not, creates a new `CLAUDE.md`
4. Confirms what was written and reminds the user to call `load_solution` at the start of each session

## Skill instructions

When this skill is invoked:

1. Determine the current working directory.
2. If the user supplied a path argument, treat it as the solution path; otherwise leave it as a placeholder.
3. Check if `CLAUDE.md` exists in the cwd using the Read tool (catch the error if it does not exist).
4. Build the block to write (see template below). If CLAUDE.md already contains the string `vs-solution MCP` do NOT append again — tell the user it is already configured.
5. If CLAUDE.md exists, append the block at the end. If it does not exist, write it as the entire file content.
6. Confirm to the user: what file was written, what the solution path is set to, and that they should restart Claude Code if this is the first time.

## CLAUDE.md block template

```
## vs-solution MCP

Questo progetto usa il server MCP `vs-solution` per l'analisi semantica del codice C#.

### Regole obbligatorie

- All'inizio di ogni sessione su codice C# chiama `load_solution` con il path della soluzione.
  Soluzione di default: {{SOLUTION_PATH}}

- Per qualsiasi operazione su codice C# usa i tool MCP, NON Grep/Bash/PowerShell:

  | Operazione                        | Tool MCP da usare          |
  |-----------------------------------|----------------------------|
  | Cercare una classe/metodo/tipo    | `find_symbol`              |
  | Trovare tutti i riferimenti       | `find_references`          |
  | Implementazioni di un'interfaccia | `find_implementations`     |
  | Gerarchia di ereditarietà         | `find_derived_types`       |
  | Chi chiama un metodo              | `find_callers`             |
  | Simbolo a riga specifica          | `get_symbol_info`          |
  | Membri di un tipo                 | `get_type_members`         |
  | Errori e warning del compilatore  | `get_diagnostics`          |
  | Dipendenze tra progetti           | `analyze_dependencies`     |
  | Codice morto                      | `find_unused_symbols`      |
  | Complessità ciclomatica           | `get_metrics`              |
  | Code smell                        | `find_code_smells`         |
  | Documentazione XML                | `get_xml_docs`             |
  | API pubbliche senza doc           | `find_undocumented_public_api` |
  | Anteprima rename                  | `preview_rename`           |
  | Candidati estrazione metodo       | `extract_method_candidates`|
  | Eseguire i test                   | `run_tests`                |
  | Coverage dei test                 | `get_test_coverage_map`    |

- Usa Grep/Bash/PowerShell solo per file non-C# (JSON, YAML, log, script, ecc.)
  o quando il server MCP non è disponibile.

### Note sul server MCP

Il server `vs-solution` è un processo locale sulla tua macchina.
Non carica né trasmette file sorgente da nessuna parte — legge dal filesystem locale
e restituisce solo risultati strutturati (nomi di simboli, numeri di riga, ecc.).
```
