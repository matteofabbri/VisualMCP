using System.Reflection;
using System.Text;

namespace Snitch.Scaffolding;

/// <summary>
/// The scaffold documents — license texts, CI workflows, CONTRIBUTING — are documents, not string
/// literals, so they live as files under <c>Scaffolding/Templates</c> and are embedded into this
/// assembly (see Snitch.csproj). Embedded resources are manifest resources: they are read through
/// <see cref="Assembly.GetManifestResourceStream(string)"/>, which needs no file on disk at runtime
/// and survives trimming, single-file publish and Native AOT. A new template is a new file plus (for
/// a license) one row in <c>licenses.index.tsv</c> — no C# change.
/// </summary>
internal static class TemplateCatalog
{
    private const string Prefix = "Snitch.Scaffolding.Templates.";

    public static IReadOnlyList<LicenseInfo> Licenses { get; } = LoadIndex();

    private static readonly Dictionary<string, LicenseInfo> ById =
        Licenses.ToDictionary(l => l.Id, StringComparer.OrdinalIgnoreCase);

    public static LicenseInfo? FindLicense(string id) => ById.GetValueOrDefault(id);

    /// <summary>Reads one embedded template by its file name, e.g. <c>license.mit.txt</c>.</summary>
    public static string Read(string fileName)
    {
        var resourceName = Prefix + fileName;
        using var stream = typeof(TemplateCatalog).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded template '{resourceName}' is missing. Add it under SNITCH/Scaffolding/Templates " +
                "and make sure Snitch.csproj embeds Scaffolding/Templates/*.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>Substitutes <c>{{TOKEN}}</c> placeholders. Ordinal, so a template's own text is never reinterpreted.</summary>
    public static string Render(string template, params (string Token, string Value)[] tokens)
    {
        var text = template;
        foreach (var (token, value) in tokens)
            text = text.Replace("{{" + token + "}}", value, StringComparison.Ordinal);
        return text;
    }

    private static IReadOnlyList<LicenseInfo> LoadIndex()
    {
        var licenses = new List<LicenseInfo>();
        foreach (var line in Read("licenses.index.tsv").Split('\n'))
        {
            var row = line.TrimEnd('\r');
            if (row.Length == 0 || row.StartsWith('#')) continue;

            var cells = row.Split('\t');
            if (cells.Length < 6 || cells[0] == "id") continue;

            licenses.Add(new LicenseInfo(
                cells[0].Trim(),
                cells[1].Trim(),
                cells[2].Trim(),
                cells[3].Trim(),
                cells[4].Trim().Equals("yes", StringComparison.OrdinalIgnoreCase),
                cells[5].Trim().Equals("yes", StringComparison.OrdinalIgnoreCase)));
        }
        return licenses;
    }
}
