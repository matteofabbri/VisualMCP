using System.Text;

namespace VisualMCP.Implementation.Scaffolding;

/// <summary>Scaffolds project metadata files: LICENSE, CONTRIBUTING (with CLA), CI workflows.</summary>
internal static class ScaffoldImpl
{
    // ── add_license ───────────────────────────────────────────────────────────
    internal static object AddLicense(string licenseId, string directory, string? holder, int? year, bool overwrite)
    {
        var id = (licenseId ?? "").Trim().ToLowerInvariant();
        var h = string.IsNullOrWhiteSpace(holder) ? "the project authors" : holder!.Trim();
        var y = (year is > 0 ? year.Value : DateTime.Now.Year).ToString();

        if (!Licenses.TryGetValue(id, out var template))
            return new { error = $"Unknown license '{licenseId}'. Supported: {string.Join(", ", Licenses.Keys)}." };

        var (dir, derr) = ResolveDir(directory);
        if (derr is not null) return derr;

        var path = Path.Combine(dir, "LICENSE");
        if (File.Exists(path) && !overwrite) return new { error = $"LICENSE already exists at {path} (set overwrite=true)." };

        var text = template.Replace("{YEAR}", y).Replace("{HOLDER}", h);
        try { File.WriteAllText(path, text); }
        catch (Exception ex) { return new { error = $"Failed to write LICENSE: {ex.Message}" }; }

        return new { license = id, holder = h, year = y, path, written = true };
    }

    // ── add_contributing ──────────────────────────────────────────────────────
    internal static object AddContributing(string directory, string? holder, string? licenseName, bool includeCla, bool overwrite)
    {
        var h = string.IsNullOrWhiteSpace(holder) ? "the maintainer" : holder!.Trim();
        var lic = string.IsNullOrWhiteSpace(licenseName) ? "the project's LICENSE" : licenseName!.Trim();

        var (dir, derr) = ResolveDir(directory);
        if (derr is not null) return derr;

        var path = Path.Combine(dir, "CONTRIBUTING.md");
        if (File.Exists(path) && !overwrite) return new { error = $"CONTRIBUTING.md already exists at {path} (set overwrite=true)." };

        var sb = new StringBuilder();
        sb.AppendLine("# Contributing");
        sb.AppendLine();
        sb.AppendLine($"Thanks for contributing! This project is licensed under {lic}.");
        sb.AppendLine();
        sb.AppendLine("## How to contribute");
        sb.AppendLine();
        sb.AppendLine("1. Open an issue for anything non-trivial to agree on the approach.");
        sb.AppendLine("2. Fork, branch, make your change, and ensure it builds and is tested.");
        sb.AppendLine("3. Open a pull request describing the change and why.");
        if (includeCla)
        {
            sb.AppendLine();
            sb.AppendLine("## Contributor License Agreement (CLA)");
            sb.AppendLine();
            sb.AppendLine("By submitting a contribution to this project, you agree that:");
            sb.AppendLine();
            sb.AppendLine("1. **You have the right to contribute it** (it is your original work, or you have all");
            sb.AppendLine("   necessary rights, including any employer permission/waiver).");
            sb.AppendLine($"2. **You grant {h} a perpetual, worldwide, non-exclusive, royalty-free, irrevocable");
            sb.AppendLine("   license — with the right to sublicense and relicense — to use, reproduce, modify,");
            sb.AppendLine("   distribute and license your contribution under any terms, including the project's");
            sb.AppendLine("   current license and separate commercial license terms.**");
            sb.AppendLine("3. **You keep your copyright** (this is a license grant, not an assignment).");
            sb.AppendLine("4. Your contribution is provided \"as is\", without warranty, to the extent permitted by law.");
            sb.AppendLine();
            sb.AppendLine("If you do not agree to these terms, please do not submit a contribution.");
            sb.AppendLine();
            sb.AppendLine("*This document is provided for convenience and is not legal advice.*");
        }

        try { File.WriteAllText(path, sb.ToString()); }
        catch (Exception ex) { return new { error = $"Failed to write CONTRIBUTING.md: {ex.Message}" }; }

        return new { path, includeCla, written = true };
    }

    // ── add_ci_workflow ───────────────────────────────────────────────────────
    internal static object AddCiWorkflow(string directory, string projectPath, string name, string workflowType, bool overwrite)
    {
        var (dir, derr) = ResolveDir(directory);
        if (derr is not null) return derr;

        var type = (workflowType ?? "dotnet-multi-os").Trim().ToLowerInvariant();
        if (type != "dotnet-multi-os")
            return new { error = $"Unknown workflowType '{workflowType}'. Supported: dotnet-multi-os." };
        if (string.IsNullOrWhiteSpace(projectPath))
            return new { error = "projectPath (path to the .csproj/.sln relative to the repo) is required." };

        var safeName = string.IsNullOrWhiteSpace(name) ? "build" : name.Trim();
        var wfDir = Path.Combine(dir, ".github", "workflows");
        var path = Path.Combine(wfDir, safeName + ".yml");
        if (File.Exists(path) && !overwrite) return new { error = $"Workflow already exists at {path} (set overwrite=true)." };

        var yml = DotnetMultiOsWorkflow.Replace("{NAME}", safeName).Replace("{PROJECT}", projectPath.Replace('\\', '/'));
        try { Directory.CreateDirectory(wfDir); File.WriteAllText(path, yml); }
        catch (Exception ex) { return new { error = $"Failed to write workflow: {ex.Message}" }; }

        return new { path, workflowType = type, project = projectPath, written = true, note = "Commit and push to GitHub; the workflow runs on push/PR and builds for Windows, Linux and macOS." };
    }

    // ── helpers ───────────────────────────────────────────────────────────────
    private static (string dir, object? error) ResolveDir(string directory)
    {
        var dir = string.IsNullOrWhiteSpace(directory) ? Directory.GetCurrentDirectory() : Path.GetFullPath(directory);
        if (!Directory.Exists(dir)) return ("", new { error = $"Directory not found: {dir}" });
        return (dir, null);
    }

    private const string DotnetMultiOsWorkflow = """
name: {NAME}

on:
  push:
    branches: [ master, main ]
  pull_request:
  workflow_dispatch:

jobs:
  build:
    name: build (${{ matrix.os }})
    runs-on: ${{ matrix.os }}
    strategy:
      fail-fast: false
      matrix:
        include:
          - os: windows-latest
            rid: win-x64
          - os: ubuntu-latest
            rid: linux-x64
          - os: macos-latest
            rid: osx-arm64
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - run: dotnet restore "{PROJECT}"
      - run: dotnet build "{PROJECT}" -c Release --no-restore
      - run: dotnet publish "{PROJECT}" -c Release -r ${{ matrix.rid }} --self-contained false -o publish/${{ matrix.rid }}
      - uses: actions/upload-artifact@v4
        with:
          name: build-${{ matrix.rid }}
          path: publish/${{ matrix.rid }}
""";

    private static readonly Dictionary<string, string> Licenses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mit"] = """
MIT License

Copyright (c) {YEAR} {HOLDER}

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
""",
        ["isc"] = """
ISC License

Copyright (c) {YEAR} {HOLDER}

Permission to use, copy, modify, and/or distribute this software for any
purpose with or without fee is hereby granted, provided that the above
copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH
REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY
AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY SPECIAL, DIRECT,
INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM
LOSS OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR
OTHER TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR
PERFORMANCE OF THIS SOFTWARE.
""",
        ["bsd-2-clause"] = """
BSD 2-Clause License

Copyright (c) {YEAR} {HOLDER}

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES ARE DISCLAIMED. IN NO EVENT SHALL THE
COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT,
INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES ARISING IN ANY WAY
OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH
DAMAGE.
""",
        ["unlicense"] = """
This is free and unencumbered software released into the public domain.

Anyone is free to copy, modify, publish, use, compile, sell, or distribute this
software, either in source code form or as a compiled binary, for any purpose,
commercial or non-commercial, and by any means.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED. IN NO EVENT SHALL THE AUTHORS BE LIABLE FOR ANY CLAIM, DAMAGES OR
OTHER LIABILITY, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE
USE OR OTHER DEALINGS IN THE SOFTWARE.

For more information, please refer to <https://unlicense.org>
""",
        ["polyform-noncommercial-1.0.0"] = """
# PolyForm Noncommercial License 1.0.0

Copyright {YEAR} {HOLDER}

Required Notice: Copyright {YEAR} {HOLDER}. Free for noncommercial use under these
terms. Commercial use requires a separate paid license.

<https://polyformproject.org/licenses/noncommercial/1.0.0>

See the full text at the URL above. Noncommercial use is permitted; any commercial
use (including use by or for a company for commercial advantage or monetary
compensation) requires a separate license from the licensor.
""",
    };
}
