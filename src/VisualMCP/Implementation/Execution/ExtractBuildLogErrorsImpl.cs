using System.Text;
using System.Text.RegularExpressions;

namespace VisualMCP.Implementation.Execution;

internal static class ExtractBuildLogErrorsImpl
{
    private static readonly Regex ErrorRx = new(
        @"(?i)(:\s*(fatal\s+)?error\b|\berror\s+[A-Z]{1,4}\d+|\bLNK\d{1,4}\b|unresolved external|non risolt)",
        RegexOptions.Compiled);

    private static readonly Regex WarningRx = new(
        @"(?i)(:\s*warning\b|\bwarning\s+[A-Z]{1,4}\d+)",
        RegexOptions.Compiled);

    internal static object Run(string logPath, bool includeWarnings, int maxLines)
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

            if (ErrorRx.IsMatch(line)) errors.Add(line);
            else if (includeWarnings && WarningRx.IsMatch(line)) warnings.Add(line);
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
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode;
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8;

        var sample = Math.Min(bytes.Length, 4000);
        var nulls = 0;
        for (var i = 0; i < sample; i++) if (bytes[i] == 0) nulls++;
        return nulls > sample / 8 ? Encoding.Unicode : Encoding.UTF8;
    }
}
