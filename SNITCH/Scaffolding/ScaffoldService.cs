namespace Snitch.Scaffolding;

/// <summary>Scaffolds project metadata files: LICENSE, CONTRIBUTING (with CLA), CI workflows.</summary>
public interface IScaffoldService
{
    object ListLicenses();
    object AddLicense(string licenseId, string directory, string? holder, int? year, bool overwrite);
    object AddContributing(string directory, string? holder, string? licenseName, bool includeCla, bool overwrite);
    object AddCiWorkflow(string directory, string projectPath, string name, string workflowType, bool overwrite);
}

public sealed class ScaffoldService : IScaffoldService
{
    // ── list_licenses ─────────────────────────────────────────────────────────
    public object ListLicenses() => new
    {
        count = TemplateCatalog.Licenses.Count,
        licenses = TemplateCatalog.Licenses,
    };

    // ── add_license ───────────────────────────────────────────────────────────
    public object AddLicense(string licenseId, string directory, string? holder, int? year, bool overwrite)
    {
        var id = (licenseId ?? "").Trim();
        var license = TemplateCatalog.FindLicense(id);
        if (license is null)
            return new { error = $"Unknown license '{licenseId}'. Supported: {string.Join(", ", TemplateCatalog.Licenses.Select(l => l.Id))}." };

        var h = string.IsNullOrWhiteSpace(holder) ? "the project authors" : holder!.Trim();
        var y = (year is > 0 ? year.Value : DateTime.Now.Year).ToString();

        var (dir, derr) = ResolveDir(directory);
        if (derr is not null) return derr;

        var path = Path.Combine(dir, "LICENSE");
        if (File.Exists(path) && !overwrite) return new { error = $"LICENSE already exists at {path} (set overwrite=true)." };

        var text = TemplateCatalog.Render(
            TemplateCatalog.Read($"license.{license.Id}.txt"),
            ("YEAR", y),
            ("HOLDER", h));
        try { Platform.FileText.Write(path, text); }
        catch (Exception ex) { return new { error = $"Failed to write LICENSE: {ex.Message}" }; }

        return new
        {
            license = license.Id, license.Name, license.Spdx, license.Family,
            holder = h, year = y, path, written = true,
        };
    }

    // ── add_contributing ──────────────────────────────────────────────────────
    public object AddContributing(string directory, string? holder, string? licenseName, bool includeCla, bool overwrite)
    {
        var h = string.IsNullOrWhiteSpace(holder) ? "the maintainer" : holder!.Trim();
        var lic = string.IsNullOrWhiteSpace(licenseName) ? "the project's LICENSE" : licenseName!.Trim();

        var (dir, derr) = ResolveDir(directory);
        if (derr is not null) return derr;

        var path = Path.Combine(dir, "CONTRIBUTING.md");
        if (File.Exists(path) && !overwrite) return new { error = $"CONTRIBUTING.md already exists at {path} (set overwrite=true)." };

        var cla = includeCla
            ? TemplateCatalog.Render(TemplateCatalog.Read("contributing-cla.md"), ("MAINTAINER", h))
            : "";
        var text = TemplateCatalog.Render(
            TemplateCatalog.Read("contributing.md"),
            ("LICENSE", lic),
            ("CLA", cla));

        try { Platform.FileText.Write(path, text); }
        catch (Exception ex) { return new { error = $"Failed to write CONTRIBUTING.md: {ex.Message}" }; }

        return new { path, includeCla, written = true };
    }

    // ── add_ci_workflow ───────────────────────────────────────────────────────
    public object AddCiWorkflow(string directory, string projectPath, string name, string workflowType, bool overwrite)
    {
        var (dir, derr) = ResolveDir(directory);
        if (derr is not null) return derr;

        var type = (workflowType ?? "dotnet-multi-os").Trim().ToLowerInvariant();
        if (type is not ("dotnet-multi-os" or "dotnet-release"))
            return new { error = $"Unknown workflowType '{workflowType}'. Supported: dotnet-multi-os, dotnet-release." };
        if (string.IsNullOrWhiteSpace(projectPath))
            return new { error = "projectPath (path to the .csproj/.sln relative to the repo) is required." };

        var safeName = string.IsNullOrWhiteSpace(name) ? (type == "dotnet-release" ? "release" : "build") : name.Trim();
        var wfDir = Path.Combine(dir, ".github", "workflows");
        var path = Path.Combine(wfDir, safeName + ".yml");
        if (File.Exists(path) && !overwrite) return new { error = $"Workflow already exists at {path} (set overwrite=true)." };

        var appName = Path.GetFileNameWithoutExtension(projectPath);
        var yml = TemplateCatalog.Render(
            TemplateCatalog.Read($"ci.{type}.yml"),
            ("NAME", safeName),
            ("PROJECT", projectPath.Replace('\\', '/')),
            ("APPNAME", appName));
        try { Directory.CreateDirectory(wfDir); Platform.FileText.Write(path, yml); }
        catch (Exception ex) { return new { error = $"Failed to write workflow: {ex.Message}" }; }

        return new
        {
            path, workflowType = type, project = projectPath, written = true,
            note = type == "dotnet-release"
                ? "Commit & push; then push a tag like 'v1.0.0' (or run it manually) to build self-contained binaries for Windows/Linux/macOS and attach them to a GitHub Release."
                : "Commit & push; the workflow runs on push/PR and builds for Windows, Linux and macOS.",
        };
    }

    // ── helpers ───────────────────────────────────────────────────────────────
    private static (string dir, object? error) ResolveDir(string directory)
    {
        var dir = string.IsNullOrWhiteSpace(directory) ? Directory.GetCurrentDirectory() : Path.GetFullPath(directory);
        if (!Directory.Exists(dir)) return ("", new { error = $"Directory not found: {dir}" });
        return (dir, null);
    }
}
