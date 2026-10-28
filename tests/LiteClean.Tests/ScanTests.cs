using System.Text;
using LiteClean.Core.Models;

namespace LiteClean.Tests;

internal sealed class ScanTests
{
    [Test("Scan reports exact file/dir counts and bytes")]
    public void ScanReportsExactStats()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFile("a.tmp", "12345");
        workspace.CreateFile("sub/b.tmp", "1234567");
        workspace.CreateEmptyFile("sub/empty.bin");
        workspace.CreateFile("空格 目录/文件 (测试).tmp", "hello world", DateTime.UtcNow.AddDays(-3));
        workspace.CreateDirectory("empty-dir");

        var result = workspace.Scan();

        Check.True(result.Completed);
        Check.False(result.Canceled);
        Check.Equal(4, result.CandidateFileCount);
        Check.Equal(4, result.Candidates.Count(static candidate => candidate.Kind == ItemKind.File));
        Check.True(result.CandidateDirectoryCount >= 2);
        Check.Equal(5L + 7L + 0L + 11L, result.EstimatedFileBytes);
        Check.Equal(0, result.SkippedItemCount);
        Check.Equal(0, result.ReadFailureCount);
        Check.True(result.Candidates.All(candidate => candidate.FullPath.StartsWith(
            workspace.Boundary.RootPath,
            StringComparison.OrdinalIgnoreCase)));
        Check.False(result.Candidates.Any(candidate =>
            string.Equals(candidate.FullPath, workspace.Boundary.RootPath, StringComparison.OrdinalIgnoreCase)));
        Check.True(result.Candidates.Any(candidate => candidate.RelativePath == "a.tmp"));
    }

    [Test("Recent files are protected")]
    public void RecentFilesAreProtected()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFile("old.tmp", "old", DateTime.UtcNow.AddDays(-2));
        workspace.CreateFile("recent.tmp", "recent", DateTime.UtcNow.AddMinutes(-5));

        var result = workspace.Scan(TimeSpan.FromHours(24));

        Check.Equal(1, result.CandidateFileCount);
        Check.True(result.Candidates.Any(candidate => candidate.RelativePath == "old.tmp"));
        Check.False(result.Candidates.Any(candidate => candidate.RelativePath == "recent.tmp"));
        Check.Equal(1, result.RecentSkippedCount);
        Check.True(result.SkippedCounts.ContainsKey(SkipReason.Recent));
    }

    [Test("Zero protection window includes recent files")]
    public void ZeroProtectionIncludesRecentFiles()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFile("recent.tmp", "recent", DateTime.UtcNow.AddMinutes(-1));
        var result = workspace.Scan(TimeSpan.Zero);
        Check.Equal(1, result.CandidateFileCount);
        Check.Equal(0, result.RecentSkippedCount);
    }

    [Test("Read-only files are skipped, not deleted")]
    public void ReadOnlyFilesAreSkipped()
    {
        using var workspace = new TestWorkspace();
        var readOnly = workspace.CreateReadOnlyFile("readonly.tmp");
        workspace.CreateFile("normal.tmp", "normal");

        var result = workspace.Scan();

        Check.Equal(1, result.CandidateFileCount);
        Check.True(result.Candidates.Any(candidate => candidate.RelativePath == "normal.tmp"));
        Check.False(result.Candidates.Any(candidate => candidate.RelativePath == "readonly.tmp"));
        Check.True(result.SkippedItemCount >= 1);
        Check.FileExists(readOnly);
    }

    [Test("In-use file is skipped and scan continues")]
    public void InUseFileIsSkipped()
    {
        using var workspace = new TestWorkspace();
        var lockedPath = workspace.CreateFile("locked.tmp", "locked");
        workspace.CreateFile("free.tmp", "free");
        using var stream = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = workspace.Scan();

        Check.True(result.Candidates.Any(candidate => candidate.RelativePath == "free.tmp"));
        Check.False(result.Candidates.Any(candidate => candidate.RelativePath == "locked.tmp"));
        Check.True(result.SkippedItemCount >= 1);
        Check.True(
            result.SkippedCounts.ContainsKey(SkipReason.InUse) ||
            result.SkippedCounts.ContainsKey(SkipReason.AccessDenied));
        Check.FileExists(lockedPath);
    }

    [Test("Directory junction is not followed")]
    public void JunctionIsNotFollowed()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.Outside, "outside.tmp"), "outside");
        File.SetLastWriteTimeUtc(Path.Combine(workspace.Outside, "outside.tmp"), DateTime.UtcNow.AddDays(-2));
        workspace.CreateJunction("junction", workspace.Outside);
        workspace.CreateFile("inside.tmp", "inside");

        var result = workspace.Scan();

        Check.Equal(1, result.CandidateFileCount);
        Check.True(result.Candidates.Any(candidate => candidate.RelativePath == "inside.tmp"));
        Check.False(result.Candidates.Any(candidate => candidate.RelativePath.Contains("junction", StringComparison.OrdinalIgnoreCase)));
        Check.True(result.SkippedCounts.ContainsKey(SkipReason.ReparsePoint));
        Check.FileExists(workspace.OutsideSentinel);
    }

    [Test("File symlink is not followed")]
    public void FileSymlinkIsNotFollowed()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFileSymbolicLink("link.txt", workspace.OutsideSentinel);
        workspace.CreateFile("inside.tmp", "inside");

        var result = workspace.Scan();

        Check.Equal(1, result.CandidateFileCount);
        Check.True(result.Candidates.Any(candidate => candidate.RelativePath == "inside.tmp"));
        Check.False(result.Candidates.Any(candidate => candidate.RelativePath == "link.txt"));
        Check.True(result.SkippedCounts.ContainsKey(SkipReason.ReparsePoint));
        Check.Equal("SENTINEL-CONTENT-MUST-NOT-CHANGE", File.ReadAllText(workspace.OutsideSentinel));
    }

    [Test("Scan is strictly read-only")]
    public void ScanIsReadOnly()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFile("one.tmp", "one");
        workspace.CreateFile("sub/two.tmp", "two");
        var before = workspace.SnapshotText();
        var filesBefore = workspace.CountFiles();

        var result = workspace.Scan();

        Check.True(result.Completed);
        Check.Equal(filesBefore, workspace.CountFiles());
        Check.Equal(before, workspace.SnapshotText());
    }

    [Test("Empty directory produces an empty completed scan")]
    public void EmptyDirectoryScan()
    {
        using var workspace = new TestWorkspace();
        var result = workspace.Scan();
        Check.True(result.Completed);
        Check.False(result.Canceled);
        Check.Equal(0, result.Candidates.Count);
        Check.Equal(0, result.SkippedItemCount);
    }

    [Test("Skipped preview is capped while counts stay exact")]
    public void SkippedPreviewIsCapped()
    {
        using var workspace = new TestWorkspace();
        for (var index = 0; index < 120; index++)
        {
            workspace.CreateFile($"recent-{index:D3}.tmp", "r", DateTime.UtcNow.AddMinutes(-1));
        }

        var result = workspace.Scan(TimeSpan.FromHours(24), previewLimit: 10);

        Check.Equal(120, result.RecentSkippedCount);
        Check.Equal(120, result.SkippedItemCount);
        Check.Equal(10, result.SkippedPreview.Count);
        Check.Equal(110, result.SkippedPreviewTruncatedCount);
    }

    [Test("Unicode, spaces and long paths are scanned")]
    public void UnicodeAndLongPathsAreScanned()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFile("空格 目录/中文 名称 (1).tmp", "unicode");
        var deepRelative = string.Join(
            Path.DirectorySeparatorChar,
            Enumerable.Range(0, 14).Select(index => $"deep-{index:D2}-" + new string('y', 14)));
        var deepFile = Path.Combine(deepRelative, "deep.tmp");
        try
        {
            workspace.CreateFile(deepFile, "deep");
        }
        catch (Exception ex) when (ex is PathTooLongException or IOException or DirectoryNotFoundException)
        {
            throw new SkipException($"Long-path creation is not available in this environment: {ex.Message}");
        }

        var result = workspace.Scan();

        Check.True(result.Candidates.Any(candidate => candidate.RelativePath.Contains("中文 名称 (1).tmp", StringComparison.Ordinal)));
        Check.True(result.Candidates.Any(candidate => candidate.RelativePath.EndsWith("deep.tmp", StringComparison.OrdinalIgnoreCase)));
        Check.True(result.Candidates.First(candidate => candidate.RelativePath.EndsWith("deep.tmp", StringComparison.OrdinalIgnoreCase)).FullPath.Length > 260);
    }

    [Test("Wide directory is fully enumerated")]
    public void WideDirectoryIsFullyEnumerated()
    {
        using var workspace = new TestWorkspace();
        for (var index = 0; index < 800; index++)
        {
            workspace.CreateFile($"wide-{index:D4}.tmp", "x");
        }

        var result = workspace.Scan();
        Check.Equal(800, result.CandidateFileCount);
        Check.Equal(800L, result.EstimatedFileBytes);
        Check.Equal(800, result.Candidates.Count);
    }

    [Test("Scan candidates never include the cleanup root")]
    public void RootIsNeverACandidate()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFile("inside.tmp", "x");
        var result = workspace.Scan();
        Check.False(result.Candidates.Any(candidate =>
            string.Equals(candidate.FullPath, workspace.Boundary.RootPath, StringComparison.OrdinalIgnoreCase)));
    }
}
