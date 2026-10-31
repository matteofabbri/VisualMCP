using Snitch.Profiling.Analysis;
using Snitch.Profiling.Model;
using Snitch.Profiling.Runtime;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The File I/O layer: the aggregation that turns kernel events into per-file and per-process
/// totals, the findings those totals imply, and the Markdown report. The ETW session itself needs
/// an elevated Windows machine, so the collector is exercised only through its platform guard;
/// everything that decides an answer is pure and runs everywhere.
/// </summary>
public sealed class ProfilingFileIoTests
{
    [Fact]
    public void Aggregator_Builds_Per_File_And_Per_Process_Totals()
    {
        var aggregator = new FileIoAggregator(processFilter: 42);
        aggregator.Add(FileIoOperation.Write, 42, @"C:\logs\app.log", 4096);
        aggregator.Add(FileIoOperation.Write, 42, @"C:\logs\app.log", 4096);
        aggregator.Add(FileIoOperation.Open, 42, @"C:\logs\app.log");
        aggregator.Add(FileIoOperation.Read, 42, @"C:\data\input.bin", 1024);
        aggregator.Add(FileIoOperation.Write, 99, @"C:\other\ignored.log", 1_000_000);

        var report = aggregator.Build("process 42", durationMs: 1000, top: 10, nameResolver: _ => "FakeApp");

        Assert.Equal(4, report.EventCount);
        Assert.Equal(8192, report.TotalWriteBytes);
        Assert.Equal(1024, report.TotalReadBytes);
        Assert.Equal(2, report.TotalWriteOps);
        Assert.Equal(1, report.TotalReadOps);
        Assert.Equal(2, report.DistinctFiles);
        Assert.Equal(1, report.DistinctProcesses);

        var app = Assert.Single(report.Files, f => f.Path.EndsWith("app.log", StringComparison.Ordinal));
        Assert.Equal(2, app.WriteOps);
        Assert.Equal(4096, app.AverageWriteBytes);
        Assert.Equal(100, app.WriteShare);
        Assert.Equal(1, app.Opens);

        var process = Assert.Single(report.Processes);
        Assert.Equal("FakeApp", process.ProcessName);
        Assert.Equal(2, process.Files);
        Assert.Equal(100, process.WriteShare);
    }

    [Fact]
    public void Aggregator_Applies_The_Path_Filter()
    {
        var aggregator = new FileIoAggregator(pathFilter: "logs");
        aggregator.Add(FileIoOperation.Write, 7, @"C:\logs\app.log", 100);
        aggregator.Add(FileIoOperation.Write, 7, @"C:\data\input.bin", 900);

        var report = aggregator.Build("process 7", durationMs: 500, top: 10);

        Assert.Equal(1, report.EventCount);
        Assert.Equal(100, report.TotalWriteBytes);
        Assert.Contains(report.Warnings, w => w.Contains("Only paths containing 'logs'", StringComparison.Ordinal));
    }

    [Fact]
    public void Aggregator_Reports_A_Write_Hotspot()
    {
        var aggregator = new FileIoAggregator();
        aggregator.Add(FileIoOperation.Write, 10, @"C:\data\blob.bin", 20L * 1024 * 1024);
        aggregator.Add(FileIoOperation.Write, 10, @"C:\logs\small.log", 1L * 1024 * 1024);

        var report = aggregator.Build("process 10", durationMs: 2000, top: 10);

        var finding = Assert.Single(report.Findings, f => f.Id == "file-write-hotspot");
        Assert.Equal("critical", finding.Severity);
        Assert.Contains("blob.bin", finding.Title, StringComparison.Ordinal);
        Assert.Contains("20.0 MB", finding.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Aggregator_Reports_Small_Write_Storm_And_Handle_Churn()
    {
        var aggregator = new FileIoAggregator();
        for (var i = 0; i < 2000; i++)
            aggregator.Add(FileIoOperation.Write, 11, @"C:\logs\audit.log", 100);
        for (var i = 0; i < 1200; i++)
            aggregator.Add(FileIoOperation.Open, 11, @"C:\logs\audit.log");

        var report = aggregator.Build("process 11", durationMs: 1000, top: 10);

        Assert.Contains(report.Findings, f => f.Id == "small-write-storm");
        Assert.Contains(report.Findings, f => f.Id == "handle-churn");
    }

    [Fact]
    public void Aggregator_Reports_Unresolved_Paths_And_No_Events()
    {
        var aggregator = new FileIoAggregator();
        aggregator.Add(FileIoOperation.Write, 12, null, 512);

        var report = aggregator.Build("process 12", durationMs: 1000, top: 10);

        Assert.Contains(report.Findings, f => f.Id == "unresolved-paths");
        Assert.Contains(report.Warnings, w => w.Contains("no resolved path", StringComparison.Ordinal));

        var empty = new FileIoAggregator().Build("process 13", durationMs: 1000, top: 10);
        Assert.Contains(empty.Findings, f => f.Id == "no-file-io");
        Assert.Contains(empty.Warnings, w => w.Contains("No file I/O", StringComparison.Ordinal));
    }

    [Fact]
    public void Aggregator_Reports_Temp_Writes()
    {
        var aggregator = new FileIoAggregator();
        aggregator.Add(FileIoOperation.Write, 14, @"C:\Users\me\AppData\Local\Temp\staging.bin", 40L * 1024 * 1024);
        aggregator.Add(FileIoOperation.Write, 14, @"C:\data\final.bin", 40L * 1024 * 1024);

        var report = aggregator.Build("process 14", durationMs: 1000, top: 10);

        Assert.Contains(report.Findings, f => f.Id == "temp-writes");
    }

    [Fact]
    public void Aggregator_Stops_At_The_Event_Budget_And_Says_So()
    {
        var aggregator = new FileIoAggregator(maxEvents: 2);
        aggregator.Add(FileIoOperation.Write, 15, @"C:\a.bin", 1);
        aggregator.Add(FileIoOperation.Write, 15, @"C:\a.bin", 1);
        aggregator.Add(FileIoOperation.Write, 15, @"C:\a.bin", 1);

        var report = aggregator.Build("process 15", durationMs: 1000, top: 10);

        Assert.True(report.Truncated);
        Assert.Equal(2, report.TotalWriteOps);
        Assert.Contains(report.Warnings, w => w.Contains("More than 2", StringComparison.Ordinal));
    }

    [Fact]
    public void Markdown_Report_Renders_The_Sections()
    {
        var aggregator = new FileIoAggregator();
        aggregator.Add(FileIoOperation.Write, 16, @"C:\logs\app.log", 4096);
        aggregator.Add(FileIoOperation.Read, 16, @"C:\data\input.bin", 1024);
        var report = aggregator.Build("process 16", durationMs: 1500, top: 10, nameResolver: _ => "FakeApp");

        var markdown = ProfilingReportWriter.BuildFileIoMarkdown(report);

        Assert.Contains("# File I/O report", markdown, StringComparison.Ordinal);
        Assert.Contains("## Processes", markdown, StringComparison.Ordinal);
        Assert.Contains("## Files", markdown, StringComparison.Ordinal);
        Assert.Contains("FakeApp", markdown, StringComparison.Ordinal);
        Assert.Contains("app.log", markdown, StringComparison.Ordinal);
        Assert.Contains("4.0 KB", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Collector_Refuses_Platforms_Without_The_Kernel_Provider()
    {
        if (OperatingSystem.IsWindows())
            return; // the guard does not apply; a real capture needs elevation and is not a unit test

        var service = new FileIoCollectionService();
        await Assert.ThrowsAsync<PlatformNotSupportedException>(
            () => service.CollectAsync(processId: 1, durationSeconds: 1, pathFilter: null, top: 5));
    }
}
