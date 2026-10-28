using LiteClean.Core.Models;

namespace LiteClean.Tests;

internal sealed class CleanTests
{
    [Test("Clean deletes selected files and empty directories")]
    public void CleanDeletesSelectedFilesAndDirectories()
    {
        using var workspace = new TestWorkspace();
        var fileA = workspace.CreateFile("a.tmp", "12345");
        var fileB = workspace.CreateFile("sub/b.tmp", "1234567");
        var empty = workspace.CreateEmptyFile("sub/empty.tmp");
        var dir = workspace.CreateDirectory("empty-dir");
        workspace.CreateFile("unicode 目录/文件.txt", "unicode");

        var scan = workspace.Scan();
        var expectedBytes = scan.EstimatedFileBytes;
        Check.Equal(4, scan.CandidateFileCount);

        var result = workspace.CleanAll(scan);

        Check.True(result.Completed);
        Check.False(result.Canceled);
        Check.Equal(4, result.DeletedFileCount);
        Check.Equal(expectedBytes, result.DeletedFileBytes);
        Check.True(result.DeletedDirectoryCount >= 3);
        Check.Equal(0, result.FailedItemCount);
        Check.FileMissing(fileA);
        Check.FileMissing(fileB);
        Check.FileMissing(empty);
        Check.DirectoryMissing(dir);
        Check.DirectoryMissing(workspace.RootPath("sub"));
        Check.DirectoryExists(workspace.Root);
    }

    [Test("Selection controls exactly what is deleted")]
    public void SelectionControlsDeletion()
    {
        using var workspace = new TestWorkspace();
        var keep = workspace.CreateFile("keep.tmp", "keep");
        var remove = workspace.CreateFile("remove.tmp", "remove");

        var scan = workspace.Scan();
        var selected = scan.Candidates
            .Where(candidate => candidate.RelativePath == "remove.tmp")
            .ToArray();
        Check.Equal(1, selected.Length);

        var result = workspace.Clean(selected);

        Check.Equal(1, result.DeletedFileCount);
        Check.Equal(6L, result.DeletedFileBytes);
        Check.FileExists(keep);
        Check.FileMissing(remove);
        Check.Equal(1, result.TotalSelectedItemCount);
    }

    [Test("Recent files are not deleted")]
    public void RecentFilesAreNotDeleted()
    {
        using var workspace = new TestWorkspace();
        var recent = workspace.CreateFile("recent.tmp", "recent", DateTime.UtcNow.AddMinutes(-2));
        var old = workspace.CreateFile("old.tmp", "old", DateTime.UtcNow.AddDays(-3));

        var scan = workspace.Scan(TimeSpan.FromHours(24));
        Check.Equal(1, scan.CandidateFileCount);

        var result = workspace.Clean(scan.Candidates, recentProtection: TimeSpan.FromHours(24));
        Check.Equal(1, result.DeletedFileCount);
        Check.FileExists(recent);
        Check.FileMissing(old);
    }

    [Test("Read-only files are never deleted")]
    public void ReadOnlyFilesAreNotDeleted()
    {
        using var workspace = new TestWorkspace();
        var readOnly = workspace.CreateReadOnlyFile("readonly.tmp");
        var normal = workspace.CreateFile("normal.tmp", "normal");
        var scan = workspace.Scan();
        Check.Equal(1, scan.CandidateFileCount);

        var result = workspace.CleanAll(scan);
        Check.Equal(1, result.DeletedFileCount);
        Check.FileExists(readOnly);
        Check.FileMissing(normal);
    }

    [Test("Locked file at clean time is skipped without aborting")]
    public void LockedAtCleanTimeIsSkipped()
    {
        using var workspace = new TestWorkspace();
        var locked = workspace.CreateFile("locked.tmp", "locked");
        var free = workspace.CreateFile("free.tmp", "free");
        var scan = workspace.Scan();
        Check.Equal(2, scan.CandidateFileCount);

        using var stream = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = workspace.CleanAll(scan);

        Check.Equal(1, result.DeletedFileCount);
        Check.True(result.SkippedItemCount >= 1);
        Check.True(result.SkippedCounts.ContainsKey(SkipReason.InUse) ||
                   result.SkippedCounts.ContainsKey(SkipReason.AccessDenied));
        Check.FileExists(locked);
        Check.FileMissing(free);
    }

    [Test("File changed after scan is refused")]
    public void ChangedFileIsRefused()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.CreateFile("victim.tmp", "original");
        var scan = workspace.Scan();
        Check.Equal(1, scan.CandidateFileCount);

        File.WriteAllText(path, "changed-and-longer");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));
        var result = workspace.CleanAll(scan);

        Check.Equal(0, result.DeletedFileCount);
        Check.True(result.SkippedItemCount >= 1);
        Check.True(result.SkippedCounts.ContainsKey(SkipReason.StateChanged));
        Check.FileExists(path);
        Check.Equal("changed-and-longer", File.ReadAllText(path));
    }

    [Test("File replaced by a symlink is refused and target survives")]
    public void FileReplacedBySymlinkIsRefused()
    {
        using var workspace = new TestWorkspace();
        var victim = workspace.CreateFile("victim.tmp", "original");
        var scan = workspace.Scan();
        Check.Equal(1, scan.CandidateFileCount);

        File.Delete(victim);
        workspace.CreateFileSymbolicLink("victim.tmp", workspace.OutsideSentinel);

        var result = workspace.CleanAll(scan);

        Check.Equal(0, result.DeletedFileCount);
        Check.True(result.SkippedItemCount >= 1);
        Check.Equal("SENTINEL-CONTENT-MUST-NOT-CHANGE", File.ReadAllText(workspace.OutsideSentinel));
    }

    [Test("File replaced by another file is refused")]
    public void FileReplacedByAnotherFileIsRefused()
    {
        using var workspace = new TestWorkspace();
        var victim = workspace.CreateFile("victim.tmp", "original");
        var scan = workspace.Scan();

        File.Delete(victim);
        File.WriteAllText(victim, "REPLACEMENT");
        File.SetLastWriteTimeUtc(victim, DateTime.UtcNow.AddDays(-2));

        var result = workspace.CleanAll(scan);

        Check.Equal(0, result.DeletedFileCount);
        Check.True(result.SkippedCounts.ContainsKey(SkipReason.StateChanged));
        Check.FileExists(victim);
        Check.Equal("REPLACEMENT", File.ReadAllText(victim));
    }

    [Test("Externally deleted file is reported as not found")]
    public void ExternallyDeletedFileIsReported()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.CreateFile("gone.tmp", "gone");
        var scan = workspace.Scan();
        File.Delete(path);

        var result = workspace.CleanAll(scan);

        Check.Equal(0, result.DeletedFileCount);
        Check.Equal(0, result.FailedItemCount);
        Check.True(result.SkippedCounts.ContainsKey(SkipReason.NotFound));
    }

    [Test("Non-empty selected directory is skipped")]
    public void NonEmptyDirectoryIsSkipped()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFile("keep/child.tmp", "child");
        var scan = workspace.Scan();
        var directory = scan.Candidates.Single(candidate =>
            candidate.Kind == ItemKind.Directory &&
            candidate.RelativePath.EndsWith("keep", StringComparison.OrdinalIgnoreCase));

        var result = workspace.Clean(new[] { directory });

        Check.Equal(0, result.DeletedDirectoryCount);
        Check.True(result.SkippedCounts.ContainsKey(SkipReason.DirectoryNotEmpty));
        Check.DirectoryExists(workspace.RootPath("keep"));
        Check.FileExists(workspace.RootPath("keep/child.tmp"));
    }

    [Test("Empty directories are removed")]
    public void EmptyDirectoriesAreRemoved()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateDirectory("one");
        workspace.CreateDirectory("one/two");
        workspace.CreateDirectory("one/two/three");
        var scan = workspace.Scan();
        Check.Equal(3, scan.CandidateDirectoryCount);

        var result = workspace.CleanAll(scan);

        Check.Equal(3, result.DeletedDirectoryCount);
        Check.DirectoryMissing(workspace.RootPath("one"));
        Check.DirectoryExists(workspace.Root);
    }

    [Test("Malicious candidate outside the boundary is refused")]
    public void OutsideCandidateIsRefused()
    {
        using var workspace = new TestWorkspace();
        var sentinel = workspace.OutsideSentinel;
        var info = new FileInfo(sentinel);
        var candidate = new ScanCandidate(
            sentinel,
            "sentinel.txt",
            ItemKind.File,
            info.Length,
            info.LastWriteTimeUtc.ToFileTimeUtc(),
            default);

        var result = workspace.Clean(new[] { candidate });

        Check.Equal(0, result.DeletedFileCount);
        Check.True(result.SkippedCounts.ContainsKey(SkipReason.OutsideBoundary));
        Check.Equal("SENTINEL-CONTENT-MUST-NOT-CHANGE", File.ReadAllText(sentinel));
    }

    [Test("Root itself can never be selected for deletion")]
    public void RootCandidateIsRefused()
    {
        using var workspace = new TestWorkspace();
        var candidate = new ScanCandidate(
            workspace.Boundary.RootPath,
            string.Empty,
            ItemKind.Directory,
            0,
            0,
            default);

        var result = workspace.Clean(new[] { candidate });

        Check.Equal(0, result.DeletedDirectoryCount);
        Check.True(result.SkippedCounts.ContainsKey(SkipReason.InvalidPath));
        Check.DirectoryExists(workspace.Root);
    }

    [Test("Clean statistics match the filesystem")]
    public void StatisticsMatchFileSystem()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateBytes("one.bin", new byte[3]);
        workspace.CreateBytes("two.bin", new byte[5]);
        workspace.CreateEmptyFile("zero.bin");
        workspace.CreateFile("skip/keep.tmp", "keep");

        var scan = workspace.Scan();
        Check.Equal(4, scan.CandidateFileCount);
        Check.Equal(12L, scan.EstimatedFileBytes);

        var result = workspace.CleanAll(scan);
        Check.Equal(4, result.DeletedFileCount);
        Check.Equal(12L, result.DeletedFileBytes);
        Check.True(result.DeletedDirectoryCount >= 1);
        Check.Equal(scan.CandidateDirectoryCount, result.DeletedDirectoryCount);
        Check.Equal(0, workspace.CountFiles());
    }

    [Test("Repeated cleanup is safe and reports missing items")]
    public void RepeatedCleanupIsSafe()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFile("a.tmp", "abc");
        var scan = workspace.Scan();
        var first = workspace.CleanAll(scan);
        Check.Equal(1, first.DeletedFileCount);

        var second = workspace.CleanAll(scan);
        Check.Equal(0, second.DeletedFileCount);
        Check.Equal(0, second.FailedItemCount);
        Check.True(second.SkippedItemCount >= 1);
        Check.True(second.SkippedCounts.ContainsKey(SkipReason.NotFound));
    }

    [Test("Clean cancellation stops issuing new deletes")]
    public void CancellationStopsNewDeletes()
    {
        using var workspace = new TestWorkspace();
        for (var index = 0; index < 200; index++)
        {
            workspace.CreateFile($"file-{index:D3}.tmp", "x" + index);
        }

        var scan = workspace.Scan();
        Check.Equal(200, scan.CandidateFileCount);
        using var cts = new CancellationTokenSource();
        var progress = new CallbackProgress<CleanProgress>(value =>
        {
            if (value.AttemptedItems >= 5)
            {
                cts.Cancel();
            }
        });

        var result = workspace.Clean(scan.Candidates, progress: progress, cancellationToken: cts.Token);

        Check.True(result.Canceled);
        Check.False(result.Completed);
        Check.True(result.AttemptedItemCount <= 7, $"Attempted={result.AttemptedItemCount}");
        Check.True(result.NotAttemptedItemCount > 0);
        Check.True(workspace.CountFiles() > 0);
    }

    [Test("Pre-canceled cleanup deletes nothing")]
    public void PreCanceledCleanupDeletesNothing()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFile("one.tmp", "one");
        var scan = workspace.Scan();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = workspace.Clean(scan.Candidates, cancellationToken: cts.Token);

        Check.True(result.Canceled);
        Check.Equal(0, result.DeletedFileCount);
        Check.Equal(0, result.AttemptedItemCount);
        Check.FileExists(workspace.RootPath("one.tmp"));
    }

    [Test("Unicode, spaces and long names can be cleaned")]
    public void UnicodeSpacesAndLongNamesAreCleaned()
    {
        using var workspace = new TestWorkspace();
        var unicode = workspace.CreateFile("空格 目录/中文 文件 (1).tmp", "unicode");
        var deepRelative = string.Join(
            Path.DirectorySeparatorChar,
            Enumerable.Range(0, 14).Select(index => $"deep-{index:D2}-" + new string('z', 14)));
        string deepFile;
        try
        {
            deepFile = workspace.CreateFile(Path.Combine(deepRelative, "deep.tmp"), "deep");
        }
        catch (Exception ex) when (ex is PathTooLongException or IOException or DirectoryNotFoundException)
        {
            throw new SkipException($"Long-path creation is not available in this environment: {ex.Message}");
        }

        var scan = workspace.Scan();
        Check.Equal(2, scan.CandidateFileCount);

        var result = workspace.CleanAll(scan);
        Check.Equal(2, result.DeletedFileCount);
        Check.FileMissing(unicode);
        Check.FileMissing(deepFile);
    }

    [Test("Clean does not delete the root directory")]
    public void RootDirectorySurvives()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFile("child.tmp", "child");
        var scan = workspace.Scan();
        var result = workspace.CleanAll(scan);
        Check.True(result.Completed);
        Check.DirectoryExists(workspace.Root);
        Check.Equal(0, workspace.CountFiles());
    }

    private sealed class CallbackProgress<T> : IProgress<T>
    {
        private readonly Action<T> _callback;

        public CallbackProgress(Action<T> callback) => _callback = callback;

        public void Report(T value) => _callback(value);
    }
}
