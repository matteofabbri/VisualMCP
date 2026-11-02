using Snitch.Languages.CSharp.Inspections;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The predictive-coding pack: PCN0001–PCN0004 must fire on code that breaks one of the invariants
/// and stay quiet on a compliant layer. The auto-selection test proves a solution named like a PCN
/// gets the pack without passing packs=pcn.
/// </summary>
public sealed class PcnInspectionTests
{
    private const string ViolatingSource = """
        public sealed class PCN
        {
            public double[] W = new double[4];
            public double[] A = new double[2];
            public double[] E = new double[2];
            public double[] Pi = new double[2];

            public void ComputeError(double[] prediction)
            {
                E[0] = prediction[0] - A[0];
            }

            public void UpdateActivity(double dt)
            {
                A[0] += dt * E[0];
            }

            public void Step(double[] lower)
            {
                W[0] += 0.1 * E[0] * lower[0];
            }

            public double[] Route(double[] f)
            {
                var best = 0;
                for (var i = 1; i < f.Length; i++)
                {
                    if (f[i] > f[best]) best = i;
                }
                return new double[] { best };
            }
        }
        """;

    private const string CompliantSource = """
        public sealed class Layer
        {
            public double[] W = new double[4];
            public double[] V = new double[4];
            public double[] A = new double[2];
            public double[] E = new double[2];
            public double[] Pi = new double[2];
            public double[] APrev = new double[2];
            private const double Eta = 0.1;

            public void ComputeError(double[] prediction)
            {
                var residual = prediction[0] - A[0];
                E[0] = Pi[0] * residual;
            }

            public void UpdateActivity(double dt)
            {
                A[0] += dt * E[0] + (A[0] - APrev[0]);
            }

            public void UpdateWeights(double[] lower)
            {
                W[0] += Eta * E[0] * lower[0];
            }

            public double[] Gate(double beta, double[] f)
            {
                return Softmax(Scale(f, -beta));
            }

            private static double[] Scale(double[] values, double factor)
            {
                var result = new double[values.Length];
                for (var i = 0; i < values.Length; i++) result[i] = values[i] * factor;
                return result;
            }

            private static double[] Softmax(double[] values)
            {
                var result = new double[values.Length];
                var total = 0.0;
                for (var i = 0; i < values.Length; i++)
                {
                    result[i] = System.Math.Exp(values[i]);
                    total += result[i];
                }
                for (var i = 0; i < values.Length; i++) result[i] /= total;
                return result;
            }
        }
        """;

    [Fact]
    public async Task ViolatingCodeProducesAllFourFindings()
    {
        using var workspace = FakeWorkspace.Create(("Pcn.cs", ViolatingSource));
        var report = await Inspect(workspace, packs: "pcn");

        Assert.Equal("completed", report.State);

        var ids = report.Findings.Select(finding => finding.InspectionId).ToHashSet();
        Assert.Contains("PCN0001", ids);
        Assert.Contains("PCN0002", ids);
        Assert.Contains("PCN0003", ids);
        Assert.Contains("PCN0004", ids);
    }

    [Fact]
    public async Task CompliantLayerHasNoFindings()
    {
        using var workspace = FakeWorkspace.Create(("Layer.cs", CompliantSource));
        var report = await Inspect(workspace, packs: "pcn");

        Assert.Equal("completed", report.State);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public async Task IdFilterNarrowsToOneRule()
    {
        using var workspace = FakeWorkspace.Create(("Pcn.cs", ViolatingSource));
        var report = await Inspect(workspace, packs: "pcn", ids: "PCN0003");

        Assert.NotEmpty(report.Findings);
        Assert.All(report.Findings, finding => Assert.Equal("PCN0003", finding.InspectionId));
    }

    [Fact]
    public async Task PcnPackIsAutoSelectedForAPredictiveSolution()
    {
        using var workspace = FakeWorkspace.CreateProjects(
            ("Predicting", new[] { ("Pcn.cs", ViolatingSource) }));

        var report = await Inspect(workspace, packs: "", ids: "PCN*");

        Assert.Contains("pcn", report.Packs);
        Assert.Contains(report.Findings, finding => finding.InspectionId.StartsWith("PCN", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PcnPackIsAutoSelectedForAPcnShapedTypeWithoutAPredictiveName()
    {
        using var workspace = FakeWorkspace.Create(("Layer.cs", CompliantSource));

        var report = await Inspect(workspace, packs: "", ids: "PCN*");

        Assert.Contains("pcn", report.Packs);
        Assert.Empty(report.Findings);
    }

    private static Task<InspectionReport> Inspect(
        FakeWorkspace workspace, string packs, string? ids = null)
    {
        var service = new InspectCodeService(workspace);

        return service.RunAsync(
            projectName: null,
            filePath: null,
            packs: packs,
            minSeverity: "info",
            ids: ids,
            excludeIds: null,
            enableAll: true,
            maxResults: 100,
            timeoutSeconds: 180);
    }
}
