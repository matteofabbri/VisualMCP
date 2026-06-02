# vs-load

Load a Visual Studio solution and explore its projects.

## Usage

`/vs-load <path-to-solution>`

Examples:
- `/vs-load C:\REPOSITORY\PredicitiveCoding\PrediciiveCoding.slnx`
- `/vs-load C:\REPOSITORY` (scans for solutions)

## What it does

1. Calls `vs-solution__list_solutions` to find .sln/.slnx files if a directory is given
2. Calls `vs-solution__load_solution` to parse the solution
3. Calls `vs-solution__get_project_info` for each project
4. Summarizes: target frameworks, source file counts, NuGet packages, project references
5. Offers to drill into a specific project or search for a symbol
