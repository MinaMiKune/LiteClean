using System.Diagnostics;
using System.Security;
using System.Threading.Channels;
using LiteClean.Core.Models;
using LiteClean.Core.Native;
using LiteClean.Core.Security;

namespace LiteClean.Core.Scanning;

public sealed class ScanOptions
{
    /// <summary>Conservative protection window for recently modified files. 0 disables it.</summary>
    public TimeSpan RecentProtection { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Bounded number of concurrent file inspections. Traversal itself is single-threaded.</summary>
    public int MaxDegreeOfParallelism { get; init; } = 4;

    public int SkippedPreviewLimit { get; init; } = 1000;
    public int ProgressIntervalMilliseconds { get; init; } = 150;
    public int FileChannelCapacity { get; init; } = 256;
}

/// <summary>
/// Read-only scanner. It never deletes or writes anything. Files are inspected
/// with an exclusive DELETE-capable handle so the plan only contains files that
/// were closed and deletable at scan time; the cleaner re-verifies everything.
/// </summary>
public sealed class TempScanner
{
    private readonly ScanOptions _options;

    public TempScanner(ScanOptions? options = null)
    {
        _options = options ?? new ScanOptions();
        if (_options.RecentProtection < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "RecentProtection must not be negative.");
        }

        if (_options.MaxDegreeOfParallelism is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxDegreeOfParallelism must be between 1 and 16.");
        }

        if (_options.SkippedPreviewLimit is < 0 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "SkippedPreviewLimit is outside the supported range.");
        }

        if (_options.FileChannelCapacity is < 1 or > 65536)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "FileChannelCapacity is outside the supported range.");
        }
    }

    public Task<ScanResult> ScanAsync(
        PathBoundary boundary,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken) =>
        Task.Run(() => ScanCore(boundary, progress, cancellationToken), CancellationToken.None);

    private ScanResult ScanCore(
        PathBoundary boundary,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        var stopwatch = Stopwatch.StartNew();
        var candidates = new List<ScanCandidate>();
        var state = new ScanState(boundary.RootPath, _options, progress);
        var cutoffUtc = _options.RecentProtection > TimeSpan.Zero
            ? DateTime.UtcNow - _options.RecentProtection
            : DateTime.MinValue;

        var channel = Channel.CreateBounded<FileEntry>(
            new BoundedChannelOptions(_options.FileChannelCapacity)
            {
                SingleWriter = true,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });

        var workers = new Task[_options.MaxDegreeOfParallelism];
        for (var i = 0; i < workers.Length; i++)
        {
            workers[i] = Task.Run(() => FileWorkerAsync(
                channel.Reader,
                boundary,
                cutoffUtc,
                state,
                candidates,
                cancellationToken), CancellationToken.None);
        }

        var canceled = false;
        var stack = new Stack<string>();
        stack.Push(boundary.RootPath);

        try
        {
            while (stack.Count > 0)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    canceled = true;
                    break;
                }

                var currentDirectory = stack.Pop();
                state.SetCurrentDirectory(currentDirectory);

                string safeDirectory;
                if (PathUtil.PathsEqual(currentDirectory, boundary.RootPath))
                {
                    // The root itself is enumerated but is never a deletion candidate.
                    safeDirectory = boundary.RootPath;
                }
                else if (!boundary.TryResolveChild(currentDirectory, out safeDirectory, out var boundaryFailure))
                {
                    state.RecordSkip(
                        MakeDisplayPath(boundary, currentDirectory),
                        ItemKind.Directory,
                        MapBoundaryFailure(boundaryFailure),
                        0);
                    continue;
                }

                IEnumerator<FileSystemInfo>? enumerator = null;
                try
                {
                    var directoryInfo = new DirectoryInfo(safeDirectory);
                    enumerator = directoryInfo
                        .EnumerateFileSystemInfos("*", CreateEnumerationOptions())
                        .GetEnumerator();

                    while (true)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            canceled = true;
                            break;
                        }

                        FileSystemInfo entry;
                        try
                        {
                            if (!enumerator.MoveNext())
                            {
                                break;
                            }

                            entry = enumerator.Current;
                        }
                        catch (Exception ex) when (IsFileSystemException(ex))
                        {
                            state.RecordReadFailure();
                            state.RecordSkip(
                                MakeDisplayPath(boundary, safeDirectory),
                                ItemKind.Directory,
                                SkipReason.Unreadable,
                                GetHResultOrWin32(ex));
                            state.RemoveCandidate(safeDirectory, candidates);
                            break;
                        }

                        state.IncrementEntriesVisited();
                        var continueScan = HandleEntry(
                            entry,
                            boundary,
                            cutoffUtc,
                            state,
                            stack,
                            channel.Writer,
                            candidates,
                            cancellationToken);
                        state.MaybeReport(force: false);
                        if (!continueScan)
                        {
                            canceled = true;
                            break;
                        }
                    }
                }
                catch (Exception ex) when (IsFileSystemException(ex))
                {
                    state.RecordReadFailure();
                    state.RecordSkip(
                        MakeDisplayPath(boundary, safeDirectory),
                        ItemKind.Directory,
                        SkipReason.Unreadable,
                        GetHResultOrWin32(ex));
                    state.RemoveCandidate(safeDirectory, candidates);
                }
                finally
                {
                    enumerator?.Dispose();
                }

                if (canceled)
                {
                    break;
                }

                state.MaybeReport(force: false);
            }
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }
        finally
        {
            channel.Writer.TryComplete();
            try
            {
                Task.WhenAll(workers).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                canceled = true;
            }
        }

        candidates.Sort(static (left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));
        stopwatch.Stop();

        state.MaybeReport(force: true);
        return new ScanResult
        {
            RootPath = boundary.RootPath,
            Candidates = candidates.AsReadOnly(),
            SkippedCounts = state.GetSkippedCounts(),
            SkippedPreview = state.GetSkippedPreview(),
            Canceled = canceled || cancellationToken.IsCancellationRequested,
            Completed = !canceled && !cancellationToken.IsCancellationRequested,
            Duration = stopwatch.Elapsed,
            EntriesVisited = state.EntriesVisited,
            SkippedPreviewTruncatedCount = state.SkippedPreviewTruncatedCount,
            ReadFailureCount = state.ReadFailureCount,
        };
    }

    private static bool HandleEntry(
        FileSystemInfo entry,
        PathBoundary boundary,
        DateTime cutoffUtc,
        ScanState state,
        Stack<string> directories,
        ChannelWriter<FileEntry> writer,
        List<ScanCandidate> candidates,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = PathUtil.NormalizeFullPath(entry.FullName);
        }
        catch (Exception ex) when (IsFileSystemException(ex))
        {
            state.RecordSkip(
                MakeDisplayPath(boundary, entry.Name),
                ItemKind.File,
                SkipReason.InvalidPath,
                GetHResultOrWin32(ex));
            return true;
        }

        if (!boundary.TryResolveChild(fullPath, out var safePath, out var boundaryFailure))
        {
            state.RecordSkip(
                MakeDisplayPath(boundary, fullPath),
                ItemKind.File,
                MapBoundaryFailure(boundaryFailure),
                0);
            return true;
        }

        FileAttributes attributes;
        try
        {
            attributes = entry.Attributes;
        }
        catch (Exception ex) when (IsFileSystemException(ex))
        {
            state.RecordSkip(
                MakeDisplayPath(boundary, safePath),
                ItemKind.File,
                SkipReason.Unreadable,
                GetHResultOrWin32(ex));
            return true;
        }

        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            state.RecordSkip(
                MakeDisplayPath(boundary, safePath),
                isDirectory ? ItemKind.Directory : ItemKind.File,
                SkipReason.ReparsePoint,
                0);
            return true;
        }

        if (isDirectory)
        {
            if (!InspectDirectory(
                    safePath,
                    boundary,
                    state,
                    candidates))
            {
                return true;
            }

            directories.Push(safePath);
            return true;
        }

        DateTime lastWriteUtc;
        try
        {
            lastWriteUtc = entry.LastWriteTimeUtc;
        }
        catch (Exception ex) when (IsFileSystemException(ex))
        {
            state.RecordSkip(
                MakeDisplayPath(boundary, safePath),
                ItemKind.File,
                SkipReason.Unreadable,
                GetHResultOrWin32(ex));
            return true;
        }

        if (cutoffUtc != DateTime.MinValue && lastWriteUtc >= cutoffUtc)
        {
            state.RecordSkip(
                MakeDisplayPath(boundary, safePath),
                ItemKind.File,
                SkipReason.Recent,
                0);
            return true;
        }

        try
        {
            writer.WriteAsync(new FileEntry(safePath, MakeDisplayPath(boundary, safePath)), cancellationToken)
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        return true;
    }

    private static bool InspectDirectory(
        string safePath,
        PathBoundary boundary,
        ScanState state,
        List<ScanCandidate> candidates)
    {
        using var handle = Win32File.OpenDirectoryForMetadata(safePath, out var error);
        if (handle.IsInvalid)
        {
            state.RecordSkip(
                MakeDisplayPath(boundary, safePath),
                ItemKind.Directory,
                MapOpenError(error),
                error);
            return false;
        }

        if (!Win32File.TryGetInfo(handle, out var info, out var infoError))
        {
            state.RecordSkip(
                MakeDisplayPath(boundary, safePath),
                ItemKind.Directory,
                SkipReason.Unreadable,
                infoError);
            return false;
        }

        if (Win32File.IsReparsePoint(info.FileAttributes))
        {
            state.RecordSkip(
                MakeDisplayPath(boundary, safePath),
                ItemKind.Directory,
                SkipReason.ReparsePoint,
                0);
            return false;
        }

        if (!Win32File.IsDirectory(info.FileAttributes))
        {
            state.RecordSkip(
                MakeDisplayPath(boundary, safePath),
                ItemKind.Directory,
                SkipReason.StateChanged,
                0);
            return false;
        }

        if (!PathUtil.TryGetFinalPath(handle, out var finalPath, out var finalPathError))
        {
            state.RecordSkip(
                MakeDisplayPath(boundary, safePath),
                ItemKind.Directory,
                SkipReason.Unreadable,
                finalPathError);
            return false;
        }

        if (!PathUtil.PathsEqual(finalPath, safePath))
        {
            var reason = boundary.TryResolveChild(finalPath, out _, out _)
                ? SkipReason.StateChanged
                : SkipReason.OutsideBoundary;
            state.RecordSkip(
                MakeDisplayPath(boundary, safePath),
                ItemKind.Directory,
                reason,
                0);
            return false;
        }

        var candidate = new ScanCandidate(
            safePath,
            MakeDisplayPath(boundary, safePath),
            ItemKind.Directory,
            0,
            info.LastWriteTime,
            PathUtil.GetIdentity(info));

        state.AddCandidate(candidate, candidates);
        return true;
    }

    private static async Task FileWorkerAsync(
        ChannelReader<FileEntry> reader,
        PathBoundary boundary,
        DateTime cutoffUtc,
        ScanState state,
        List<ScanCandidate> candidates,
        CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var entry))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    try
                    {
                        InspectFile(
                            entry,
                            boundary,
                            cutoffUtc,
                            state,
                            candidates);
                    }
                    catch (Exception ex) when (!IsFastFatal(ex))
                    {
                        state.RecordSkip(
                            entry.RelativePath,
                            ItemKind.File,
                            SkipReason.Unknown,
                            GetHResultOrWin32(ex));
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation path.
        }
    }

    private static void InspectFile(
        FileEntry entry,
        PathBoundary boundary,
        DateTime cutoffUtc,
        ScanState state,
        List<ScanCandidate> candidates)
    {
        if (!boundary.TryResolveChild(entry.FullPath, out var safePath, out var boundaryFailure))
        {
            state.RecordSkip(
                entry.RelativePath,
                ItemKind.File,
                MapBoundaryFailure(boundaryFailure),
                0);
            return;
        }

        using var handle = Win32File.OpenFileForInspection(safePath, out var error);
        if (handle.IsInvalid)
        {
            state.RecordSkip(
                entry.RelativePath,
                ItemKind.File,
                MapOpenError(error),
                error);
            return;
        }

        if (!Win32File.TryGetInfo(handle, out var info, out var infoError))
        {
            state.RecordSkip(
                entry.RelativePath,
                ItemKind.File,
                SkipReason.Unreadable,
                infoError);
            return;
        }

        if (Win32File.IsReparsePoint(info.FileAttributes))
        {
            state.RecordSkip(
                entry.RelativePath,
                ItemKind.File,
                SkipReason.ReparsePoint,
                0);
            return;
        }

        if (Win32File.IsDirectory(info.FileAttributes))
        {
            state.RecordSkip(
                entry.RelativePath,
                ItemKind.File,
                SkipReason.StateChanged,
                0);
            return;
        }

        if (Win32File.IsReadOnly(info.FileAttributes))
        {
            state.RecordSkip(
                entry.RelativePath,
                ItemKind.File,
                SkipReason.ReadOnly,
                0);
            return;
        }

        if (cutoffUtc != DateTime.MinValue)
        {
            var lastWriteUtc = DateTime.FromFileTimeUtc(info.LastWriteTime);
            if (lastWriteUtc >= cutoffUtc)
            {
                state.RecordSkip(
                    entry.RelativePath,
                    ItemKind.File,
                    SkipReason.Recent,
                    0);
                return;
            }
        }

        if (!PathUtil.TryGetFinalPath(handle, out var finalPath, out var finalPathError))
        {
            state.RecordSkip(
                entry.RelativePath,
                ItemKind.File,
                SkipReason.Unreadable,
                finalPathError);
            return;
        }

        if (!PathUtil.PathsEqual(finalPath, safePath))
        {
            var reason = boundary.TryResolveChild(finalPath, out _, out _)
                ? SkipReason.StateChanged
                : SkipReason.OutsideBoundary;
            state.RecordSkip(
                entry.RelativePath,
                ItemKind.File,
                reason,
                0);
            return;
        }

        var candidate = new ScanCandidate(
            safePath,
            entry.RelativePath,
            ItemKind.File,
            info.FileSize,
            info.LastWriteTime,
            PathUtil.GetIdentity(info));

        state.AddCandidate(candidate, candidates);
    }

    private static EnumerationOptions CreateEnumerationOptions() => new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
        MatchType = MatchType.Simple,
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    private static string MakeDisplayPath(PathBoundary boundary, string path)
    {
        try
        {
            var normalized = PathUtil.NormalizeFullPath(path);
            if (PathUtil.IsDescendantOrSelf(normalized, boundary.RootPath) &&
                !PathUtil.PathsEqual(normalized, boundary.RootPath))
            {
                return Path.GetRelativePath(boundary.RootPath, normalized);
            }

            return Path.GetFileName(normalized);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "<invalid path>";
        }
    }

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

    private static SkipReason MapOpenError(int error) => error switch
    {
        NativeMethods.ErrorSharingViolation or NativeMethods.ErrorLockViolation => SkipReason.InUse,
        NativeMethods.ErrorAccessDenied or 1920 => SkipReason.AccessDenied,
        NativeMethods.ErrorFileNotFound or NativeMethods.ErrorPathNotFound => SkipReason.NotFound,
        NativeMethods.ErrorInvalidParameter or 123 or 206 or 267 => SkipReason.InvalidPath,
        _ => SkipReason.Unreadable,
    };

    private static int GetHResultOrWin32(Exception exception) =>
        exception.HResult != 0 ? exception.HResult : -1;

    private static bool IsFileSystemException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or
            ArgumentException or NotSupportedException or PathTooLongException or
            FileNotFoundException or DirectoryNotFoundException;

    private static bool IsFastFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private sealed record FileEntry(string FullPath, string RelativePath);

    /// <summary>Thread-safe scan state. UI callbacks are throttled and exceptions from them are ignored.</summary>
    private sealed class ScanState
    {
        private readonly object _gate = new();
        private readonly ScanOptions _options;
        private readonly IProgress<ScanProgress>? _progress;
        private readonly Dictionary<SkipReason, int> _skipCounts = new();
        private readonly List<SkippedItem> _skippedPreview = new();
        private long _lastReportTimestamp;
        private long _entriesVisited;
        private int _candidateFiles;
        private int _candidateDirectories;
        private long _candidateBytes;
        private int _skippedItems;
        private int _skippedPreviewTruncated;
        private int _readFailures;
        private string _currentDirectory;

        public ScanState(string rootPath, ScanOptions options, IProgress<ScanProgress>? progress)
        {
            _options = options;
            _progress = progress;
            _currentDirectory = rootPath;
        }

        public long EntriesVisited => Interlocked.Read(ref _entriesVisited);

        public int SkippedPreviewTruncatedCount => Volatile.Read(ref _skippedPreviewTruncated);

        public int ReadFailureCount => Volatile.Read(ref _readFailures);

        public void IncrementEntriesVisited() => Interlocked.Increment(ref _entriesVisited);

        public void SetCurrentDirectory(string directory)
        {
            lock (_gate)
            {
                _currentDirectory = directory;
            }
        }

        public void AddCandidate(ScanCandidate candidate, List<ScanCandidate> candidates)
        {
            lock (_gate)
            {
                candidates.Add(candidate);
                if (candidate.Kind == ItemKind.File)
                {
                    _candidateFiles++;
                    _candidateBytes += candidate.SizeBytes;
                }
                else
                {
                    _candidateDirectories++;
                }
            }
        }

        public void RemoveCandidate(string fullPath, List<ScanCandidate> candidates)
        {
            lock (_gate)
            {
                var index = candidates.FindIndex(candidate =>
                    string.Equals(candidate.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    return;
                }

                var candidate = candidates[index];
                candidates.RemoveAt(index);
                if (candidate.Kind == ItemKind.File)
                {
                    _candidateFiles--;
                    _candidateBytes -= candidate.SizeBytes;
                }
                else
                {
                    _candidateDirectories--;
                }
            }
        }

        public void RecordSkip(
            string relativePath,
            ItemKind kind,
            SkipReason reason,
            int win32Error)
        {
            lock (_gate)
            {
                _skipCounts[reason] = _skipCounts.GetValueOrDefault(reason) + 1;
                _skippedItems++;
                if (_skippedPreview.Count < _options.SkippedPreviewLimit)
                {
                    _skippedPreview.Add(new SkippedItem(relativePath, kind, reason, win32Error));
                }
                else
                {
                    _skippedPreviewTruncated++;
                }
            }
        }

        public void RecordReadFailure() => Interlocked.Increment(ref _readFailures);

        public Dictionary<SkipReason, int> GetSkippedCounts()
        {
            lock (_gate)
            {
                return new Dictionary<SkipReason, int>(_skipCounts);
            }
        }

        public SkippedItem[] GetSkippedPreview()
        {
            lock (_gate)
            {
                return _skippedPreview.ToArray();
            }
        }

        public void MaybeReport(bool force)
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

            ScanProgress snapshot;
            lock (_gate)
            {
                snapshot = new ScanProgress(
                    Interlocked.Read(ref _entriesVisited),
                    _candidateFiles,
                    _candidateDirectories,
                    _candidateBytes,
                    _skippedItems,
                    _currentDirectory);
            }

            try
            {
                _progress.Report(snapshot);
            }
            catch (Exception ex) when (!IsFastFatal(ex))
            {
                // A faulty UI progress callback must not abort the read-only scan.
            }
        }
    }
}
