#if WINDUP_TESTING || WINDUP_SELFTEST_UNITS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace WindUpKey.SelfTest;

/// <summary>
/// 04-D3 output: the chat summary (<c>Self-test: N PASS · N FAIL · N WARN · N SKIP · N MANUAL — log: selftest/&lt;utc&gt;.log</c>)
/// with one line per FAIL, WARN and SKIP, and the values-free log file in <c>pluginConfigs/WindUpKey/selftest/</c>,
/// keeping the newest <see cref="KeepLogs"/>. Every line goes through <see cref="SelfTestRedact.Scrub"/> first. No
/// Dalamud dependency.
/// </summary>
public static class SelfTestReport
{
    public const string LogFolder = "selftest";
    public const int KeepLogs = 5;
    private const string StampFormat = "yyyyMMdd'T'HHmmss'Z'";

    /// <summary>The log's file name for a run that started at <paramref name="startedUtc"/>.</summary>
    public static string LogFileName(DateTime startedUtc) =>
        startedUtc.ToUniversalTime().ToString(StampFormat, CultureInfo.InvariantCulture) + ".log";

    public static int Count(IEnumerable<CheckResult> results, CheckStatus status) => results.Count(r => r.Status == status);

    /// <summary>
    /// 04-D11: how long a run takes, for its start message: the usual time, and the worst case from the step budgets
    /// (<see cref="SelfTestTimings.SafeTierBudget"/>, <see cref="SelfTestTimings.VerbBudget"/>), rounded up to minutes.
    /// Null for the verbs whose start message gives none.
    /// </summary>
    public static string? Duration(SelfTestTier tier, SelfTestTimings timings) => tier switch
    {
        SelfTestTier.Safe => $"usually under a minute, at most about {Minutes(timings.SafeTierBudget)} minutes",
        SelfTestTier.Outage => $"about 2 minutes, at most about {Minutes(timings.VerbBudget(SelfTestTier.Outage))}",
        _ => null,
    };

    private static int Minutes(TimeSpan budget) => (int)Math.Ceiling(budget.TotalMinutes);

    /// <summary>The summary line, then one line per FAIL (loud ones first), WARN and SKIP. Scrubbed.</summary>
    public static IReadOnlyList<string> ChatLines(
        string title,
        IReadOnlyList<CheckResult> results,
        string logName,
        IEnumerable<string?> sensitive,
        bool cancelled = false)
    {
        var values = sensitive.ToList();
        var lines = new List<string>
        {
            $"{title}: {Count(results, CheckStatus.Pass)} PASS · {Count(results, CheckStatus.Fail)} FAIL · "
            + $"{Count(results, CheckStatus.Warn)} WARN · {Count(results, CheckStatus.Skip)} SKIP · "
            + $"{Count(results, CheckStatus.Manual)} MANUAL{(cancelled ? " (stopped)" : string.Empty)} — log: {LogFolder}/{logName}",
        };
        foreach (var result in results
                     .Where(r => r.Status is CheckStatus.Fail or CheckStatus.Warn or CheckStatus.Skip)
                     .OrderBy(r => r.Loud ? 0 : 1)
                     .ThenBy(r => r.Status switch { CheckStatus.Fail => 0, CheckStatus.Warn => 1, _ => 2 }))
        {
            lines.Add(Line(result));
        }

        return lines.Select(l => SelfTestRedact.Scrub(l, values)).ToList();
    }

    /// <summary>One result as a line: <c>FAIL 01-12: reason</c>.</summary>
    public static string Line(CheckResult result) =>
        $"{result.Status.ToString().ToUpperInvariant()} {result.Id}: {result.Reason}";

    /// <summary>The log file's text: a header, every result, and the manual steps for what isn't automated. Scrubbed.</summary>
    public static string LogText(
        string title,
        DateTime startedUtc,
        DateTime endedUtc,
        string pluginVersion,
        IReadOnlyList<CheckResult> results,
        IEnumerable<string?> sensitive,
        bool cancelled)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{title} {startedUtc.ToUniversalTime():yyyy-MM-dd HH:mm:ss}Z – {endedUtc.ToUniversalTime():HH:mm:ss}Z, plugin {pluginVersion}{(cancelled ? ", stopped before the end" : string.Empty)}");
        sb.AppendLine($"{Count(results, CheckStatus.Pass)} PASS, {Count(results, CheckStatus.Fail)} FAIL, {Count(results, CheckStatus.Warn)} WARN, "
                      + $"{Count(results, CheckStatus.Skip)} SKIP, {Count(results, CheckStatus.Manual)} MANUAL, {Count(results, CheckStatus.Info)} INFO");
        sb.AppendLine();
        foreach (var result in results)
            sb.AppendLine((result.Loud ? "!! " : string.Empty) + Line(result));

        var manualIds = results
            .Where(r => r.Status == CheckStatus.Manual || SelfTestCatalog.Find(r.Id)?.Coverage == CheckCoverage.Partial)
            .Select(r => r.Id)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (manualIds.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Manual steps:");
            foreach (var id in manualIds)
            {
                foreach (var line in SelfTestCatalog.ManualText(id))
                    sb.AppendLine(line);
            }
        }

        return SelfTestRedact.Scrub(sb.ToString(), sensitive);
    }

    /// <summary>
    /// Writes the log into <paramref name="pluginConfigDirectory"/>/selftest/ and deletes all but the newest
    /// <see cref="KeepLogs"/> logs there. Returns the file name, or null when it couldn't be written (logged by the
    /// caller without values).
    /// </summary>
    public static string? WriteLog(string pluginConfigDirectory, string fileName, string text)
    {
        try
        {
            var folder = Path.Combine(pluginConfigDirectory, LogFolder);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, fileName), text, new UTF8Encoding(false));
            Prune(folder, KeepLogs);
            return fileName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Deletes all but the newest <paramref name="keep"/> <c>*.log</c> files (by name, which is a UTC stamp).</summary>
    public static void Prune(string folder, int keep)
    {
        var logs = Directory.EnumerateFiles(folder, "*.log")
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .OrderByDescending(n => n, StringComparer.Ordinal)
            .ToList();
        foreach (var old in logs.Skip(keep))
        {
            try
            {
                File.Delete(Path.Combine(folder, old!));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Kept; the next run tries again.
            }
        }
    }
}
#endif
