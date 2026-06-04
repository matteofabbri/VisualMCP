using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace VisualMCP.Tools.Execution;

[McpServerToolType]
public static class ExtractBuildLogErrorsTool
{
    // Compiler errors (C#### / CS####), linker errors (LNK####), unresolved externals, generic ": error".
    private static readonly Regex ErrorRx = new(
        @"(?i)(:\s*(fatal\s+)?error\b|\berror\s+[A-Z]{1,4}\d+|\bLNK\d{1,4}\b|unresolved external|non risolt)",
        RegexOptions.Compiled);

    private static readonly Regex WarningRx = new(
        @"(?i)(:\s*warning\b|\bwarning\s+[A-Z]{1,4}\d+)",
        RegexOptions.Compiled);

    [McpServerTool(Name = "extract_build_log_errors"), Description(
        "Read a native or MSBuild build-log file (auto-detecting UTF-16/UTF-8/ANSI encoding) and extract the " +
        "error lines — C/C++ compiler errors (Cxxxx), linker errors (LNKxxxx), unresolved externals, and " +
        "generic ': error' — optionally warnings too. Use this INSTEAD OF a shell 'iconv + grep' pipeline " +
        "on a build log. Read-only.")]
    public static object ExtractBuildLogErrors(
        [Description("Path to the build log file.")] string logPath,
        [Description("Also include warning lines (default: false).")] bool includeWarnings = false,
        [Description("Maximum lines to return per category (default: 200).")] int maxLines = 200)
    {
        if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath))
            return new { error = $"Log file not found: {logPath}" };

        if (maxLines < 1) maxLines = 1;
        if (maxLines > 2000) maxLines = 2000;

        string text;
        string encodingName;
        try
        {
            var bytes = File.ReadAllBytes(logPath);
            var enc = DetectEncoding(bytes);
            encodingName = enc.WebName;
            text = enc.GetString(bytes);
        }
        catch (Exception ex)
        {
            return new { error = $"Failed to read log: {ex.Message}" };
        }

        var errors = new List<string>();
        var warnings = new List<string>();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().Trim('﻿', '\0');
            if (line.Length == 0) continue;

            if (ErrorRx.IsMatch(line))
                errors.Add(line);
            else if (includeWarnings && WarningRx.IsMatch(line))
                warnings.Add(line);
        }

        var distinctErrors   = errors.Distinct().Take(maxLines).ToList();
        var distinctWarnings = warnings.Distinct().Take(maxLines).ToList();

        return new
        {
            logPath,
            encoding = encodingName,
            errorCount = distinctErrors.Count,
            warningCount = includeWarnings ? distinctWarnings.Count : (int?)null,
            errors = distinctErrors,
            warnings = includeWarnings ? distinctWarnings : null,
        };
    }

    private static Encoding DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode;          // UTF-16 LE BOM
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode; // UTF-16 BE BOM
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8;

        // No BOM: heuristically detect UTF-16 LE by counting NUL bytes in a sample.
        var sample = Math.Min(bytes.Length, 4000);
        var nulls = 0;
        for (var i = 0; i < sample; i++) if (bytes[i] == 0) nulls++;
        return nulls > sample / 8 ? Encoding.Unicode : Encoding.UTF8;
    }
}
