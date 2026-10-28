using LiteClean.Core.Security;

namespace LiteClean.Tests;

internal sealed class PathBoundaryTests
{
    [Test("Missing/empty TEMP is rejected with no fallback")]
    public void MissingTempIsRejected()
    {
        foreach (var value in new string?[] { null, string.Empty, "   " })
        {
            var success = TempRootResolver.TryResolve(value, out var boundary, out var failure);
            Check.False(success);
            Check.True(boundary is null);
            Check.Equal(PathBoundaryErrorCode.TempVariableEmpty, failure?.Code);
        }
    }

    [Test("Relative TEMP is rejected")]
    public void RelativeTempIsRejected()
    {
        Check.False(TempRootResolver.TryResolve("Temp", out _, out var failure));
        Check.Equal(PathBoundaryErrorCode.NotAbsolutePath, failure?.Code);
        Check.False(TempRootResolver.TryResolve(@"..\Temp", out _, out failure));
        Check.Equal(PathBoundaryErrorCode.NotAbsolutePath, failure?.Code);
    }

    [Test("Invalid TEMP characters are rejected")]
    public void InvalidTempCharactersAreRejected()
    {
        Check.False(TempRootResolver.TryResolve(@"C:\Temp\%BAD%", out _, out var failure));
        Check.Equal(PathBoundaryErrorCode.InvalidPath, failure?.Code);
        Check.False(TempRootResolver.TryResolve(@"C:\Temp\bad*name", out _, out failure));
        Check.Equal(PathBoundaryErrorCode.InvalidPath, failure?.Code);
    }

    [Test("UNC and device paths are rejected")]
    public void UncIsRejected()
    {
        Check.False(TempRootResolver.TryResolve(@"\\server\share\Temp", out _, out var failure));
        Check.Equal(PathBoundaryErrorCode.NetworkOrDevicePath, failure?.Code);
    }

    [Test("Drive root, Windows, profile and LocalAppData are protected")]
    public void ProtectedLocationsAreRejected()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var driveRoot = Path.GetPathRoot(windows)!;

        AssertRejected(driveRoot, PathBoundaryErrorCode.ProtectedLocation);
        AssertRejected(windows, PathBoundaryErrorCode.ProtectedLocation);
        AssertRejected(Path.Combine(windows, "Temp"), PathBoundaryErrorCode.ProtectedLocation);
        AssertRejected(profile, PathBoundaryErrorCode.ProtectedLocation);
        AssertRejected(localAppData, PathBoundaryErrorCode.ProtectedLocation);

        // The default user temp directory is the one intentional exception.
        var defaultTemp = Path.Combine(localAppData, "Temp");
        if (Directory.Exists(defaultTemp))
        {
            Check.True(TempRootResolver.TryResolve(defaultTemp, out var tempBoundary, out var failure));
            Check.True(tempBoundary is not null);
            Check.True(failure is null);
        }
    }

    [Test("Nonexistent TEMP is rejected")]
    public void NonexistentTempIsRejected()
    {
        using var workspace = new TestWorkspace();
        var missing = Path.Combine(workspace.Root, "missing-" + Guid.NewGuid().ToString("N"));
        Check.False(TempRootResolver.TryResolve(missing, out _, out var failure));
        Check.Equal(PathBoundaryErrorCode.NotFound, failure?.Code);
    }

    [Test("TEMP pointing to a file is rejected")]
    public void FileTempIsRejected()
    {
        using var workspace = new TestWorkspace();
        var file = workspace.CreateFile("not-a-directory.tmp", "x");
        Check.False(TempRootResolver.TryResolve(file, out _, out var failure));
        Check.Equal(PathBoundaryErrorCode.NotDirectory, failure?.Code);
    }

    [Test("TEMP with trailing dot/space segments is rejected")]
    public void AliasedTempIsRejected()
    {
        using var workspace = new TestWorkspace();
        Check.False(TempRootResolver.TryResolve(workspace.Root + "\\bad.", out _, out var failure));
        Check.Equal(PathBoundaryErrorCode.InvalidName, failure?.Code);
        Check.False(TempRootResolver.TryResolve(workspace.Root + "\\bad ", out _, out failure));
        Check.Equal(PathBoundaryErrorCode.InvalidName, failure?.Code);
    }

    [Test("Valid isolated test TEMP is accepted and canonicalized")]
    public void ValidTempIsAccepted()
    {
        using var workspace = new TestWorkspace();
        Check.True(TempRootResolver.TryResolve(workspace.Root, out var boundary, out var failure));
        Check.True(failure is null);
        Check.True(boundary is not null);
        Check.True(Path.IsPathFullyQualified(boundary!.RootPath));
        Check.True(Directory.Exists(boundary.RootPath));
        Check.True(boundary.TryResolveChild(Path.Combine(boundary.RootPath, "中文 目录", "file (1).tmp"), out var child, out _));
        Check.Contains("file (1).tmp", child);
    }

    [Test("TEMP with trailing separator is accepted")]
    public void TrailingSeparatorIsAccepted()
    {
        using var workspace = new TestWorkspace();
        Check.True(TempRootResolver.TryResolve(workspace.Root + Path.DirectorySeparatorChar, out var boundary, out _));
        Check.True(boundary is not null);
        Check.False(boundary!.RootPath.EndsWith(Path.DirectorySeparatorChar));
    }

    [Test("Actual environment TEMP resolves")]
    public void ActualEnvironmentTempResolves()
    {
        var raw = Environment.GetEnvironmentVariable("TEMP");
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new SkipException("TEMP is not set in this process.");
        }

        Check.True(
            TempRootResolver.TryResolveFromEnvironment(out var boundary, out var failure),
            $"TEMP should resolve for a normal session: {failure?.Code} {failure?.Message}");
        Check.True(boundary is not null);
        Check.True(Directory.Exists(boundary!.RootPath));
    }

    [Test("TEMP root that is a junction is rejected")]
    public void JunctionRootIsRejected()
    {
        using var workspace = new TestWorkspace();
        var link = Path.Combine(workspace.BasePath, "temp-link");
        workspace.CreateJunctionAt(link, workspace.Outside);
        Check.False(TempRootResolver.TryResolve(link, out _, out var failure));
        Check.Equal(PathBoundaryErrorCode.ReparsePoint, failure?.Code);
    }

    [Test("Child traversal and malformed names are rejected")]
    public void ChildTraversalIsRejected()
    {
        using var workspace = new TestWorkspace();
        var boundary = workspace.Boundary;

        Check.False(boundary.TryResolveChild(string.Empty, out _, out var failure), "empty accepted");
        Check.Equal(PathBoundaryErrorCode.InvalidPath, failure?.Code);

        Check.False(boundary.TryResolveChild("relative.txt", out _, out failure), "relative accepted");
        Check.Equal(PathBoundaryErrorCode.NotAbsolutePath, failure?.Code);

        Check.False(boundary.TryResolveChild(boundary.RootPath, out _, out failure), "root accepted");
        Check.Equal(PathBoundaryErrorCode.RootItself, failure?.Code);

        Check.False(
            boundary.TryResolveChild(Path.Combine(boundary.RootPath, "..", "outside.txt"), out _, out failure),
            "dot-dot traversal accepted");
        Check.Equal(PathBoundaryErrorCode.OutsideRoot, failure?.Code);

        Check.False(boundary.TryResolveChild(workspace.OutsideSentinel, out _, out failure), "outside accepted");
        Check.Equal(PathBoundaryErrorCode.OutsideRoot, failure?.Code);

        Check.False(
            boundary.TryResolveChild(Path.Combine(boundary.RootPath, "file:stream"), out _, out failure),
            "ADS accepted");
        Check.Equal(PathBoundaryErrorCode.InvalidName, failure?.Code);

        Check.False(
            boundary.TryResolveChild(Path.Combine(boundary.RootPath, "trailing."), out _, out failure),
            "trailing dot accepted");
        Check.Equal(PathBoundaryErrorCode.InvalidName, failure?.Code);

        Check.False(
            boundary.TryResolveChild(Path.Combine(boundary.RootPath, "trailing "), out _, out failure),
            "trailing space accepted");
        Check.Equal(PathBoundaryErrorCode.InvalidName, failure?.Code);
    }

    [Test("Spaces, Unicode and long child paths are accepted")]
    public void UnicodeAndLongPathsAreAccepted()
    {
        using var workspace = new TestWorkspace();
        var boundary = workspace.Boundary;
        Check.True(boundary.TryResolveChild(Path.Combine(boundary.RootPath, "空格 目录", "文件 (测试) #1.tmp"), out _, out _));

        var longRelative = string.Join(
            Path.DirectorySeparatorChar,
            Enumerable.Range(0, 32).Select(index => $"segment-{index:D2}-" + new string('x', 8)));
        var longPath = Path.Combine(boundary.RootPath, longRelative);
        Check.True(longPath.Length > 260);
        Check.True(
            boundary.TryResolveChild(longPath, out var normalized, out var failure),
            $"Long path should be accepted: {failure?.Message}");
        Check.Equal(longPath, normalized);
    }

    [Test("Child path with a link-like ancestor string cannot escape")]
    public void NormalizedTraversalCannotEscape()
    {
        using var workspace = new TestWorkspace();
        var boundary = workspace.Boundary;
        var attack = Path.Combine(boundary.RootPath, "safe", "..", "..", "outside", "sentinel.txt");
        Check.False(boundary.TryResolveChild(attack, out _, out var failure));
        Check.Equal(PathBoundaryErrorCode.OutsideRoot, failure?.Code);
    }

    private static void AssertRejected(string path, PathBoundaryErrorCode expected)
    {
        if (!Directory.Exists(path))
        {
            throw new SkipException($"Protected test path does not exist: {path}");
        }

        Check.False(
            TempRootResolver.TryResolve(path, out var boundary, out var failure),
            $"Expected rejection for protected path: {path}");
        Check.True(boundary is null);
        Check.Equal(expected, failure?.Code, $"Unexpected failure for {path}: {failure?.Message}");
    }
}
