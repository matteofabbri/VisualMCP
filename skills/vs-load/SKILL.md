# vs-load

Load a Visual Studio solution and explore its projects.

## Usage

`/vs-load <path-to-solution>`

Examples:
- `/vs-load C:\REPOSITORY\PredicitiveCoding\PrediciiveCoding.slnx`
- `/vs-load C:\REPOSITORY` (scans for solutions)

## What it does

1. Calls `VisualMCP__list_solutions` to find .sln/.slnx files if a directory is given
2. Calls `VisualMCP__load_solution` to parse the solution
3. Calls `VisualMCP__get_project_info` for each project
4. Summarizes: target frameworks, source file counts, NuGet packages, project references
5. Offers to drill into a specific project or search for a symbol
