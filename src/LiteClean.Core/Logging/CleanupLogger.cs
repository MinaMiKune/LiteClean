using System.Globalization;
using System.Text;
using LiteClean.Core.Configuration;
using LiteClean.Core.Models;

namespace LiteClean.Core.Logging;

public sealed record LogWriteResult(bool Success, string? Error, string? FilePath);

/// <summary>
/// Append-only, size-bounded local log. It records aggregate counts and a small
/// capped set of issue entries (relative paths only); it never records file
/// contents, credentials, or a complete inventory of user files.
/// </summary>
public sealed class CleanupLogger
{
    private const string FileName = "liteclean.log";
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly long _maxFileBytes;
    private readonly int _maxFiles;

    public CleanupLogger(string logDirectory, long maxFileBytes = 1024 * 1024, int maxFiles = 3)
    {
        if (string.IsNullOrWhiteSpace(logDirectory))
        {
            throw new ArgumentException("Log directory must not be empty.", nameof(logDirectory));
        }

        if (maxFileBytes < 16 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFileBytes), "Log size limit is too small.");
        }

        if (maxFiles is < 1 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFiles));
        }

        LogDirectory = logDirectory;
        _maxFileBytes = maxFileBytes;
        _maxFiles = maxFiles;
    }

    public string LogDirectory { get; }

    public string LogFilePath => Path.Combine(LogDirectory, FileName);

    public LogWriteResult LogCleanSession(
        CleanResult result,
        ScanResult? scan,
        AppSettings settings,
        string appVersion,
        string? preparationError)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(LogDirectory);
                var text = BuildLogText(result, scan, settings, appVersion, preparationError);
                var bytes = Utf8NoBom.GetByteCount(text);
                var current = new FileInfo(LogFilePath);
                if (current.Exists && current.Length + bytes > _maxFileBytes)
                {
                    Rotate();
                }

                using var stream = new FileStream(
                    LogFilePath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 4096,
                    FileOptions.SequentialScan);
                using var writer = new StreamWriter(stream, Utf8NoBom);
                writer.Write(text);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            return new LogWriteResult(true, null, LogFilePath);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or NotSupportedException or
                ArgumentException)
        {
            return new LogWriteResult(false, $"写入日志失败：{ex.Message}", LogFilePath);
        }
    }

    private void Rotate()
    {
        // Keep the current file plus (maxFiles - 1) archives. If rotation fails,
        // the exception is reported and the oversized log is not appended to.
        for (var index = _maxFiles - 2; index >= 1; index--)
        {
            var source = $"{LogFilePath}.{index}";
            var destination = $"{LogFilePath}.{index + 1}";
            if (File.Exists(source))
            {
                File.Move(source, destination, overwrite: true);
            }
        }

        if (File.Exists(LogFilePath))
        {
            File.Move(LogFilePath, $"{LogFilePath}.1", overwrite: true);
        }
    }

    private static string BuildLogText(
        CleanResult result,
        ScanResult? scan,
        AppSettings settings,
        string appVersion,
        string? preparationError)
    {
        var culture = CultureInfo.InvariantCulture;
        var builder = new StringBuilder(4096);
        builder.AppendLine("===== LiteClean session =====");
        builder.AppendLine(culture, $"time-utc: {DateTime.UtcNow:O}");
        builder.AppendLine(culture, $"app-version: {Safe(appVersion)}");
        builder.AppendLine(culture, $"scope: {Safe(result.RootPath)}");
        builder.AppendLine(culture, $"recent-protection-hours: {settings.RecentFileProtectionHours}");
        if (scan is not null)
        {
            builder.AppendLine(
                culture,
                $"scan: candidates={scan.Candidates.Count} files={scan.CandidateFileCount} " +
                $"dirs={scan.CandidateDirectoryCount} estimated-bytes={scan.EstimatedFileBytes} " +
                $"skipped={scan.SkippedItemCount} recent={scan.RecentSkippedCount} " +
                $"read-failures={scan.ReadFailureCount} duration-ms={(long)scan.Duration.TotalMilliseconds} " +
                $"completed={scan.Completed}");
        }

        builder.AppendLine(
            culture,
            $"clean: deleted-files={result.DeletedFileCount} deleted-dirs={result.DeletedDirectoryCount} " +
            $"deleted-bytes={result.DeletedFileBytes} skipped={result.SkippedItemCount} " +
            $"failed={result.FailedItemCount} attempted={result.AttemptedItemCount} " +
            $"selected={result.TotalSelectedItemCount} canceled={result.Canceled} " +
            $"completed={result.Completed} duration-ms={(long)result.Duration.TotalMilliseconds}");
        builder.AppendLine(culture, $"skip-reasons: {FormatCounts(result.SkippedCounts)}");
        builder.AppendLine(culture, $"failure-reasons: {FormatCounts(result.FailedCounts)}");

        if (!string.IsNullOrWhiteSpace(preparationError) || !string.IsNullOrWhiteSpace(result.Error))
        {
            builder.AppendLine(culture, $"preparation-error: {Safe(preparationError ?? result.Error ?? string.Empty)}");
        }

        var issues = result.Issues.Take(30).ToArray();
        foreach (var issue in issues)
        {
            builder.AppendLine(
                culture,
                $"issue: {Safe(issue.RelativePath)} | status={issue.Status} | " +
                $"reason={issue.Reason?.ToString() ?? "-"} | win32={issue.Win32Error}");
        }

        if (result.Issues.Count > issues.Length)
        {
            builder.AppendLine(culture, $"issues-truncated: {result.Issues.Count - issues.Length}");
        }

        builder.AppendLine("note: file contents, credentials, and complete file inventories are not logged.");
        builder.AppendLine();
        return builder.ToString();
    }

    private static string FormatCounts(IReadOnlyDictionary<SkipReason, int> counts)
    {
        if (counts.Count == 0)
        {
            return "-";
        }

        return string.Join(
            ", ",
            counts
                .OrderByDescending(static pair => pair.Value)
                .ThenBy(static pair => pair.Key)
                .Select(static pair => $"{pair.Key}={pair.Value}"));
    }

    private static string Safe(string value)
    {
        var singleLine = value.Replace('\r', ' ').Replace('\n', ' ');
        return singleLine.Length <= 200
            ? singleLine
            : singleLine[..200] + "...";
    }
}
