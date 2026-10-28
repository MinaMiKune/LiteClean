namespace LiteClean.Core.Models;

public sealed record ScanProgress(
    long EntriesVisited,
    int CandidateFiles,
    int CandidateDirectories,
    long CandidateFileBytes,
    int SkippedItems,
    string CurrentDirectory);

public sealed class ScanResult
{
    public required string RootPath { get; init; }
    public required IReadOnlyList<ScanCandidate> Candidates { get; init; }
    public required IReadOnlyDictionary<SkipReason, int> SkippedCounts { get; init; }
    public required IReadOnlyList<SkippedItem> SkippedPreview { get; init; }

    public bool Canceled { get; init; }
    public bool Completed { get; init; }
    public TimeSpan Duration { get; init; }
    public long EntriesVisited { get; init; }
    public int SkippedPreviewTruncatedCount { get; init; }
    public int ReadFailureCount { get; init; }

    public int CandidateFileCount => Candidates.Count(c => c.Kind == ItemKind.File);
    public int CandidateDirectoryCount => Candidates.Count(c => c.Kind == ItemKind.Directory);
    public long EstimatedFileBytes => Candidates.Where(c => c.Kind == ItemKind.File).Sum(c => c.SizeBytes);
    public int SkippedItemCount => SkippedCounts.Values.Sum();
    public int RecentSkippedCount => SkippedCounts.GetValueOrDefault(SkipReason.Recent);
    public int UnsafeOrUnknownSkippedCount =>
        SkippedCounts.GetValueOrDefault(SkipReason.OutsideBoundary) +
        SkippedCounts.GetValueOrDefault(SkipReason.InvalidPath) +
        SkippedCounts.GetValueOrDefault(SkipReason.StateChanged) +
        SkippedCounts.GetValueOrDefault(SkipReason.ReparsePoint) +
        SkippedCounts.GetValueOrDefault(SkipReason.Unknown);

    public static ScanResult Empty(string rootPath, bool canceled, TimeSpan duration) => new()
    {
        RootPath = rootPath,
        Candidates = Array.Empty<ScanCandidate>(),
        SkippedCounts = new Dictionary<SkipReason, int>(),
        SkippedPreview = Array.Empty<SkippedItem>(),
        Canceled = canceled,
        Completed = false,
        Duration = duration,
    };
}
