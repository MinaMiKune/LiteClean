using System.Diagnostics;
using LiteClean.Core.Models;
using LiteClean.Core.Native;
using LiteClean.Core.Security;

namespace LiteClean.Core.Cleaning;

public sealed class CleanOptions
{
    public TimeSpan RecentProtection { get; init; } = TimeSpan.FromHours(24);
    public int DetailsLimit { get; init; } = 5000;
    public int IssueDetailsLimit { get; init; } = 1000;
    public int ProgressIntervalMilliseconds { get; init; } = 100;
}

/// <summary>
/// Deletes previously scanned candidates. Every candidate is re-validated and
/// deleted through an open Win32 handle with FILE_FLAG_OPEN_REPARSE_POINT, so a
/// path swapped for a link or another file after scanning is refused. Deletion
/// is intentionally sequential, and cancellation is checked before every item
/// and immediately before the delete disposition is set.
/// </summary>
public sealed class TempCleaner
{
    private readonly CleanOptions _options;

    public TempCleaner(CleanOptions? options = null)
    {
        _options = options ?? new CleanOptions();
        if (_options.RecentProtection < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "RecentProtection must not be negative.");
        }

        if (_options.DetailsLimit is < 0 or > 200_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "DetailsLimit is outside the supported range.");
        }

        if (_options.IssueDetailsLimit is < 0 or > 200_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "IssueDetailsLimit is outside the supported range.");
        }
    }

    public Task<CleanResult> CleanAsync(
        PathBoundary boundary,
        IReadOnlyList<ScanCandidate> selectedCandidates,
        IProgress<CleanProgress>? progress,
        CancellationToken cancellationToken) =>
        Task.Run(() => CleanCore(boundary, selectedCandidates, progress, cancellationToken), CancellationToken.None);

    private CleanResult CleanCore(
        PathBoundary boundary,
        IReadOnlyList<ScanCandidate> selectedCandidates,
        IProgress<CleanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        ArgumentNullException.ThrowIfNull(selectedCandidates);

        var stopwatch = Stopwatch.StartNew();
        if (!TempRootResolver.TryResolve(boundary.RootPath, out var currentBoundary, out var boundaryFailure) ||
            currentBoundary is null)
        {
            stopwatch.Stop();
            return new CleanResult
            {
                RootPath = boundary.RootPath,
                SkippedCounts = new Dictionary<SkipReason, int>(),
                FailedCounts = new Dictionary<SkipReason, int>(),
                Details = Array.Empty<CleanOutcome>(),
                Issues = Array.Empty<CleanOutcome>(),
                TotalSelectedItemCount = selectedCandidates.Count,
                Canceled = cancellationToken.IsCancellationRequested,
                Completed = false,
                Duration = stopwatch.Elapsed,
                Error = boundaryFailure?.Message ?? "清理根目录未通过重新校验。",
            };
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<ScanCandidate>();
        foreach (var candidate in selectedCandidates)
        {
            if (candidate is null || string.IsNullOrWhiteSpace(candidate.FullPath))
            {
                continue;
            }

            if (seen.Add(candidate.FullPath))
            {
                unique.Add(candidate);
            }
        }

        var accumulator = new ResultAccumulator(
            currentBoundary.RootPath,
            unique.Count,
            _options.DetailsLimit,
            _options.IssueDetailsLimit);
        var reporter = new CleanProgressState(unique.Count, stopwatch, _options, progress);
        var cutoffUtc = _options.RecentProtection > TimeSpan.Zero
            ? DateTime.UtcNow - _options.RecentProtection
            : DateTime.MinValue;

        var files = unique.Where(c => c.Kind == ItemKind.File).ToList();
        var directories = unique
            .Where(c => c.Kind == ItemKind.Directory)
            .OrderByDescending(c => GetDepth(c.FullPath, currentBoundary.RootPath))
            .ThenBy(c => c.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var canceled = false;
        try
        {
            foreach (var candidate in files)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    canceled = true;
                    break;
                }

                AttemptResult attemptResult;
                if (!currentBoundary.TryResolveChild(candidate.FullPath, out var safePath, out var failure))
                {
                    attemptResult = AttemptResult.Skip(
                        MapBoundaryFailure(failure),
                        0);
                }
                else
                {
                    attemptResult = DeleteFile(candidate, safePath, currentBoundary, cutoffUtc, cancellationToken);
                }

                accumulator.Record(candidate, attemptResult);
                reporter.RecordAttempt(candidate.RelativePath, accumulator);
            }

            if (!canceled)
            {
                foreach (var candidate in directories)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        canceled = true;
                        break;
                    }

                    AttemptResult attemptResult;
                    if (!currentBoundary.TryResolveChild(candidate.FullPath, out var safePath, out var failure))
                    {
                        attemptResult = AttemptResult.Skip(
                            MapBoundaryFailure(failure),
                            0);
                    }
                    else
                    {
                        attemptResult = DeleteDirectory(candidate, safePath, currentBoundary, cancellationToken);
                    }

                    accumulator.Record(candidate, attemptResult);
                    reporter.RecordAttempt(candidate.RelativePath, accumulator);
                }
            }
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }

        stopwatch.Stop();
        reporter.ReportFinal(accumulator);
        var result = accumulator.Build(
            currentBoundary.RootPath,
            canceled: canceled || cancellationToken.IsCancellationRequested,
            stopwatch.Elapsed);

        return result;
    }

    private static AttemptResult DeleteFile(
        ScanCandidate candidate,
        string safePath,
        PathBoundary boundary,
        DateTime cutoffUtc,
        CancellationToken cancellationToken)
    {
        using var handle = Win32File.OpenFileForDelete(safePath, out var openError);
        if (handle.IsInvalid)
        {
            return MapOpenFailure(openError);
        }

        if (!Win32File.TryGetInfo(handle, out var info, out var infoError))
        {
            return AttemptResult.Fail(SkipReason.Unreadable, infoError);
        }

        if (Win32File.IsReparsePoint(info.FileAttributes))
        {
            return AttemptResult.Skip(SkipReason.ReparsePoint, 0);
        }

        if (Win32File.IsDirectory(info.FileAttributes))
        {
            return AttemptResult.Skip(SkipReason.StateChanged, 0);
        }

        if (Win32File.IsReadOnly(info.FileAttributes))
        {
            return AttemptResult.Skip(SkipReason.ReadOnly, 0);
        }

        if (candidate.Identity.IsEmpty || PathUtil.GetIdentity(info) != candidate.Identity)
        {
            return AttemptResult.Skip(SkipReason.StateChanged, 0);
        }

        if (info.FileSize != candidate.SizeBytes ||
            info.LastWriteTime != candidate.LastWriteFileTimeUtc)
        {
            return AttemptResult.Skip(SkipReason.StateChanged, 0);
        }

        if (cutoffUtc != DateTime.MinValue &&
            DateTime.FromFileTimeUtc(info.LastWriteTime) >= cutoffUtc)
        {
            return AttemptResult.Skip(SkipReason.Recent, 0);
        }

        if (!PathUtil.TryGetFinalPath(handle, out var finalPath, out var finalPathError))
        {
            return AttemptResult.Skip(SkipReason.Unreadable, finalPathError);
        }

        if (!PathUtil.PathsEqual(finalPath, safePath))
        {
            return boundary.TryResolveChild(finalPath, out _, out _)
                ? AttemptResult.Skip(SkipReason.StateChanged, 0)
                : AttemptResult.Skip(SkipReason.OutsideBoundary, 0);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var size = info.FileSize;
        if (!Win32File.TryMarkForDeletion(handle, out var deleteError))
        {
            return MapDeleteFailure(deleteError);
        }

        handle.Dispose();
        if (Win32File.PathStillExists(safePath))
        {
            return AttemptResult.Fail(SkipReason.Unknown, 0);
        }

        return AttemptResult.Deleted(size);
    }

    private static AttemptResult DeleteDirectory(
        ScanCandidate candidate,
        string safePath,
        PathBoundary boundary,
        CancellationToken cancellationToken)
    {
        using var handle = Win32File.OpenDirectoryForDelete(safePath, out var openError);
        if (handle.IsInvalid)
        {
            return MapOpenFailure(openError);
        }

        if (!Win32File.TryGetInfo(handle, out var info, out var infoError))
        {
            return AttemptResult.Fail(SkipReason.Unreadable, infoError);
        }

        if (Win32File.IsReparsePoint(info.FileAttributes))
        {
            return AttemptResult.Skip(SkipReason.ReparsePoint, 0);
        }

        if (!Win32File.IsDirectory(info.FileAttributes))
        {
            return AttemptResult.Skip(SkipReason.StateChanged, 0);
        }

        if (candidate.Identity.IsEmpty || PathUtil.GetIdentity(info) != candidate.Identity)
        {
            return AttemptResult.Skip(SkipReason.StateChanged, 0);
        }

        if (!PathUtil.TryGetFinalPath(handle, out var finalPath, out var finalPathError))
        {
            return AttemptResult.Skip(SkipReason.Unreadable, finalPathError);
        }

        if (!PathUtil.PathsEqual(finalPath, safePath))
        {
            return boundary.TryResolveChild(finalPath, out _, out _)
                ? AttemptResult.Skip(SkipReason.StateChanged, 0)
                : AttemptResult.Skip(SkipReason.OutsideBoundary, 0);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!Win32File.TryMarkForDeletion(handle, out var deleteError))
        {
            return deleteError == NativeMethods.ErrorDirectoryNotEmpty
                ? AttemptResult.Skip(SkipReason.DirectoryNotEmpty, deleteError)
                : MapDeleteFailure(deleteError);
        }

        handle.Dispose();
        if (Win32File.PathStillExists(safePath))
        {
            return AttemptResult.Fail(SkipReason.Unknown, 0);
        }

        return AttemptResult.Deleted(0);
    }

    private static AttemptResult MapOpenFailure(int error) => error switch
    {
        NativeMethods.ErrorSharingViolation or NativeMethods.ErrorLockViolation =>
            AttemptResult.Skip(SkipReason.InUse, error),
        NativeMethods.ErrorAccessDenied or 1920 =>
            AttemptResult.Skip(SkipReason.AccessDenied, error),
        NativeMethods.ErrorFileNotFound or NativeMethods.ErrorPathNotFound =>
            AttemptResult.Skip(SkipReason.NotFound, error),
        NativeMethods.ErrorInvalidParameter or 123 or 206 or 267 =>
            AttemptResult.Skip(SkipReason.InvalidPath, error),
        _ => AttemptResult.Fail(SkipReason.Unknown, error),
    };

    private static AttemptResult MapDeleteFailure(int error) => error switch
    {
        NativeMethods.ErrorAccessDenied or 1920 =>
            AttemptResult.Fail(SkipReason.AccessDenied, error),
        NativeMethods.ErrorSharingViolation or NativeMethods.ErrorLockViolation =>
            AttemptResult.Fail(SkipReason.InUse, error),
        NativeMethods.ErrorFileNotFound or NativeMethods.ErrorPathNotFound =>
            AttemptResult.Skip(SkipReason.NotFound, error),
        NativeMethods.ErrorDirectoryNotEmpty =>
            AttemptResult.Skip(SkipReason.DirectoryNotEmpty, error),
        _ => AttemptResult.Fail(SkipReason.IoError, error),
    };

    private static SkipReason MapBoundaryFailure(PathBoundaryFailure? failure) => failure?.Code switch
    {
        PathBoundaryErrorCode.OutsideRoot or
            PathBoundaryErrorCode.NetworkOrDevicePath or
            PathBoundaryErrorCode.ProtectedLocation => SkipReason.OutsideBoundary,
        PathBoundaryErrorCode.InvalidName or
            PathBoundaryErrorCode.InvalidPath or
            PathBoundaryErrorCode.NotAbsolutePath or
            PathBoundaryErrorCode.RootItself => SkipReason.InvalidPath,
        _ => SkipReason.InvalidPath,
    };

    private static int GetDepth(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Count(static ch => ch is '\\' or '/');
    }

    private readonly record struct AttemptResult(
        CleanItemStatus Status,
        long SizeBytes,
        SkipReason? Reason,
        int Win32Error)
    {
        public static AttemptResult Deleted(long bytes) =>
            new(CleanItemStatus.Deleted, bytes, null, 0);

        public static AttemptResult Skip(SkipReason reason, int error) =>
            new(CleanItemStatus.Skipped, 0, reason, error);

        public static AttemptResult Fail(SkipReason reason, int error) =>
            new(CleanItemStatus.Failed, 0, reason, error);
    }

    private sealed class ResultAccumulator
    {
        private readonly string _rootPath;
        private readonly int _totalSelected;
        private readonly int _detailsLimit;
        private readonly int _issueLimit;
        private readonly Dictionary<SkipReason, int> _skippedCounts = new();
        private readonly Dictionary<SkipReason, int> _failedCounts = new();
        private readonly List<CleanOutcome> _details = new();
        private readonly List<CleanOutcome> _issues = new();
        private int _attempted;
        private int _deletedFiles;
        private int _deletedDirectories;
        private long _deletedBytes;
        private int _skipped;
        private int _failed;
        private int _detailsTruncated;
        private int _issuesTruncated;

        public ResultAccumulator(string rootPath, int totalSelected, int detailsLimit, int issueLimit)
        {
            _rootPath = rootPath;
            _totalSelected = totalSelected;
            _detailsLimit = detailsLimit;
            _issueLimit = issueLimit;
        }

        public int DeletedFiles => _deletedFiles;
        public int DeletedDirectories => _deletedDirectories;
        public long DeletedBytes => _deletedBytes;
        public int AttemptedItems => _attempted;

        public void Record(ScanCandidate candidate, AttemptResult result)
        {
            _attempted++;
            var outcome = new CleanOutcome(
                SafeRelativePath(candidate),
                candidate.Kind,
                result.Status,
                result.SizeBytes,
                result.Reason,
                result.Win32Error);

            if (_details.Count < _detailsLimit)
            {
                _details.Add(outcome);
            }
            else
            {
                _detailsTruncated++;
            }

            switch (result.Status)
            {
                case CleanItemStatus.Deleted:
                    if (candidate.Kind == ItemKind.File)
                    {
                        _deletedFiles++;
                        _deletedBytes += result.SizeBytes;
                    }
                    else
                    {
                        _deletedDirectories++;
                    }

                    break;
                case CleanItemStatus.Skipped:
                    _skipped++;
                    var skipReason = result.Reason ?? SkipReason.Unknown;
                    _skippedCounts[skipReason] = _skippedCounts.GetValueOrDefault(skipReason) + 1;
                    AddIssue(outcome);
                    break;
                case CleanItemStatus.Failed:
                    _failed++;
                    var failReason = result.Reason ?? SkipReason.Unknown;
                    _failedCounts[failReason] = _failedCounts.GetValueOrDefault(failReason) + 1;
                    AddIssue(outcome);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(result), "Unknown clean status.");
            }
        }

        private void AddIssue(CleanOutcome outcome)
        {
            if (_issues.Count < _issueLimit)
            {
                _issues.Add(outcome);
            }
            else
            {
                _issuesTruncated++;
            }
        }

        private string SafeRelativePath(ScanCandidate candidate)
        {
            if (!string.IsNullOrWhiteSpace(candidate.RelativePath) &&
                !Path.IsPathRooted(candidate.RelativePath) &&
                !candidate.RelativePath.Split(['\\', '/']).Any(static part => part == ".."))
            {
                return candidate.RelativePath;
            }

            try
            {
                var normalized = PathUtil.NormalizeFullPath(candidate.FullPath);
                return PathUtil.IsDescendantOrSelf(normalized, _rootPath)
                    ? Path.GetRelativePath(_rootPath, normalized)
                    : Path.GetFileName(normalized);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return "<invalid path>";
            }
        }

        public CleanResult Build(string rootPath, bool canceled, TimeSpan duration) => new()
        {
            RootPath = rootPath,
            SkippedCounts = new Dictionary<SkipReason, int>(_skippedCounts),
            FailedCounts = new Dictionary<SkipReason, int>(_failedCounts),
            Details = _details.ToArray(),
            Issues = _issues.ToArray(),
            DeletedFileCount = _deletedFiles,
            DeletedDirectoryCount = _deletedDirectories,
            DeletedFileBytes = _deletedBytes,
            SkippedItemCount = _skipped,
            FailedItemCount = _failed,
            AttemptedItemCount = _attempted,
            TotalSelectedItemCount = _totalSelected,
            Canceled = canceled,
            Completed = !canceled,
            Duration = duration,
            DetailsTruncatedCount = _detailsTruncated,
            IssuesTruncatedCount = _issuesTruncated,
        };
    }

    private sealed class CleanProgressState
    {
        private readonly int _total;
        private readonly Stopwatch _stopwatch;
        private readonly CleanOptions _options;
        private readonly IProgress<CleanProgress>? _progress;
        private long _lastReportTimestamp;
        private int _attempted;
        private int _deletedFiles;
        private int _deletedDirectories;
        private long _deletedBytes;

        public CleanProgressState(
            int total,
            Stopwatch stopwatch,
            CleanOptions options,
            IProgress<CleanProgress>? progress)
        {
            _total = total;
            _stopwatch = stopwatch;
            _options = options;
            _progress = progress;
        }

        public void RecordAttempt(string relativePath, ResultAccumulator accumulator)
        {
            _attempted = accumulator.AttemptedItems;
            _deletedFiles = accumulator.DeletedFiles;
            _deletedDirectories = accumulator.DeletedDirectories;
            _deletedBytes = accumulator.DeletedBytes;
            MaybeReport(force: false, relativePath);
        }

        public void ReportFinal(ResultAccumulator accumulator)
        {
            _attempted = accumulator.AttemptedItems;
            _deletedFiles = accumulator.DeletedFiles;
            _deletedDirectories = accumulator.DeletedDirectories;
            _deletedBytes = accumulator.DeletedBytes;
            MaybeReport(force: true, string.Empty);
        }

        private void MaybeReport(bool force, string relativePath)
        {
            if (_progress is null)
            {
                return;
            }

            var now = Stopwatch.GetTimestamp();
            var previous = Interlocked.Read(ref _lastReportTimestamp);
            if (!force)
            {
                var elapsedMs = (now - previous) * 1000.0 / Stopwatch.Frequency;
                if (elapsedMs < _options.ProgressIntervalMilliseconds)
                {
                    return;
                }
            }

            if (Interlocked.CompareExchange(ref _lastReportTimestamp, now, previous) != previous)
            {
                return;
            }

            try
            {
                _progress.Report(new CleanProgress(
                    _attempted,
                    _total,
                    _deletedFiles,
                    _deletedDirectories,
                    _deletedBytes,
                    relativePath));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                // Progress rendering must never break a cleanup.
            }
        }
    }
}
