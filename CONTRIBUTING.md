# Contributing to VisualMCP

Thanks for your interest in contributing! VisualMCP is **source-available** under the
[PolyForm Noncommercial License 1.0.0](LICENSE): free for noncommercial use, with
commercial/company use requiring a separate paid license.

## How to contribute

1. Open an issue first for anything non-trivial, so we can agree on the approach.
2. Fork, create a branch, and make your change.
3. Make sure it builds and is reasonably tested:
   ```bash
   dotnet build src/VisualMCP/VisualMCP.csproj -c Release
   ```
4. Keep the architecture: a tool is a thin `[McpServerTool]` shim in `Tools/<Area>`
   that delegates to logic in `Implementation/<Area>`. C#/.NET-specific tools live
   under `Tools/CSharp/` and `Implementation/CSharp/`; language-agnostic tools stay
   at the top level.
5. Open a pull request describing the change and why.

## Contributor License Agreement (CLA)

Because this project is offered under **both** a noncommercial license and separate
**paid commercial licenses**, contributions must come with rights that let the
maintainer keep doing that. By submitting a contribution (a pull request, patch,
or any other content) to this project, **you agree that:**

1. **You have the right to contribute it.** The contribution is your original work,
   or you otherwise have all rights necessary to grant the license below; and if your
   employer has rights to work you create, you have received permission to contribute
   or your employer has waived those rights.
2. **You grant the maintainer a broad license.** You grant **Matteo Fabbri** (the
   "Maintainer") a perpetual, worldwide, non-exclusive, royalty-free, irrevocable
   license — **with the right to sublicense and relicense** — to use, reproduce,
   modify, prepare derivative works of, publicly display, distribute, and **license
   your contribution under any terms, including the project's PolyForm Noncommercial
   license and separate commercial license terms.**
3. **You keep your copyright.** You retain ownership of your contribution; this is a
   license grant, not an assignment.
4. **No warranty.** Your contribution is provided "as is", without warranty of any
   kind, to the extent permitted by law.

If you do not agree to these terms, please do not submit a contribution.

*This document is provided for convenience and is not legal advice.*
