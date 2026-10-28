namespace LiteClean.Core.Models;

public enum CleanItemStatus
{
    Deleted,
    Skipped,
    Failed,
}

public sealed record CleanOutcome(
    string RelativePath,
    ItemKind Kind,
    CleanItemStatus Status,
    long SizeBytes,
    SkipReason? Reason,
    int Win32Error);

public sealed record CleanProgress(
    int AttemptedItems,
    int TotalItems,
    int DeletedFiles,
    int DeletedDirectories,
    long DeletedBytes,
    string CurrentRelativePath);

public sealed class CleanResult
{
    public required string RootPath { get; init; }
    public required IReadOnlyDictionary<SkipReason, int> SkippedCounts { get; init; }
    public required IReadOnlyDictionary<SkipReason, int> FailedCounts { get; init; }
    public required IReadOnlyList<CleanOutcome> Details { get; init; }
    public IReadOnlyList<CleanOutcome> Issues { get; init; } = Array.Empty<CleanOutcome>();

    /// <summary>Set when the cleanup root or plan failed re-validation before any deletion.</summary>
    public string? Error { get; init; }

    public int DeletedFileCount { get; init; }
    public int DeletedDirectoryCount { get; init; }
    public long DeletedFileBytes { get; init; }
    public int SkippedItemCount { get; init; }
    public int FailedItemCount { get; init; }
    public int AttemptedItemCount { get; init; }
    public int TotalSelectedItemCount { get; init; }
    public int NotAttemptedItemCount => Math.Max(0, TotalSelectedItemCount - AttemptedItemCount);
    public bool Canceled { get; init; }
    public bool Completed { get; init; }
    public TimeSpan Duration { get; init; }
    public int DetailsTruncatedCount { get; init; }
    public int IssuesTruncatedCount { get; init; }

    public static CleanResult Empty(string rootPath, int selected, bool canceled) => new()
    {
        RootPath = rootPath,
        SkippedCounts = new Dictionary<SkipReason, int>(),
        FailedCounts = new Dictionary<SkipReason, int>(),
        Details = Array.Empty<CleanOutcome>(),
        TotalSelectedItemCount = selected,
        Canceled = canceled,
        Completed = false,
    };
}
