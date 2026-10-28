using LiteClean.Core.Configuration;
using LiteClean.Core.Logging;
using LiteClean.Core.Models;

namespace LiteClean.Tests;

internal sealed class SettingsLoggingTests
{
    [Test("Settings default, save and load round-trip")]
    public void SettingsRoundTrip()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.BasePath, "appdata", "settings.json");
        var store = new SettingsStore(path);
        var missing = store.Load();
        Check.Equal(24, missing.Settings.RecentFileProtectionHours);
        Check.True(missing.Warning is null);

        var save = store.Save(new AppSettings { RecentFileProtectionHours = 12 });
        Check.True(save.Success, save.Error);
        var loaded = store.Load();
        Check.Equal(12, loaded.Settings.RecentFileProtectionHours);
        Check.True(loaded.Warning is null);
    }

    [Test("Invalid settings JSON falls back to defaults")]
    public void InvalidSettingsFallsBack()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.BasePath, "settings.json");
        File.WriteAllText(path, "{ this is not json");
        var store = new SettingsStore(path);
        var loaded = store.Load();
        Check.Equal(24, loaded.Settings.RecentFileProtectionHours);
        Check.True(loaded.Warning is not null);
    }

    [Test("Out-of-range settings are clamped")]
    public void OutOfRangeSettingsAreClamped()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.BasePath, "settings.json");
        File.WriteAllText(path, """{"recentFileProtectionHours": 100000, "schemaVersion": 1}""");
        var store = new SettingsStore(path);
        var loaded = store.Load();
        Check.Equal(AppSettings.MaxProtectionHours, loaded.Settings.RecentFileProtectionHours);
        Check.True(loaded.Warning is not null);

        var save = store.Save(new AppSettings { RecentFileProtectionHours = -5 });
        Check.True(save.Success, save.Error);
        Check.Equal(
            AppSettings.MinProtectionHours,
            new SettingsStore(path).Load().Settings.RecentFileProtectionHours);
    }

    [Test("Settings save failure is returned, not thrown")]
    public void SettingsSaveFailure()
    {
        using var workspace = new TestWorkspace();
        var blocker = workspace.CreateFile("settings-blocker", "file");
        var store = new SettingsStore(Path.Combine(blocker, "settings.json"));
        var save = store.Save(new AppSettings());
        Check.False(save.Success);
        Check.True(save.Error is not null);
    }

    [Test("Custom AppPaths requires an absolute path")]
    public void AppPathsCustom()
    {
        using var workspace = new TestWorkspace();
        Check.True(AppPaths.TryCreateCustom(Path.Combine(workspace.BasePath, "data"), out var paths, out _));
        Check.True(paths is not null);
        Check.Equal(
            Path.Combine(workspace.BasePath, "data"),
            Path.GetFullPath(paths!.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar));
        Check.False(AppPaths.TryCreateCustom("relative", out _, out var error));
        Check.True(error is not null);
    }

    [Test("Log contains aggregate counts but no file content or inventory")]
    public void LogContainsOnlySafeSummary()
    {
        using var workspace = new TestWorkspace();
        var secret = "TOP-SECRET-FILE-CONTENT-DO-NOT-LOG-1234567890";
        workspace.CreateFile("secret.tmp", secret);
        workspace.CreateFile("other.tmp", "other");
        var scan = workspace.Scan();
        var result = workspace.CleanAll(scan);
        Check.Equal(2, result.DeletedFileCount);

        var logDirectory = Path.Combine(workspace.BasePath, "logs");
        var logger = new CleanupLogger(logDirectory);
        var write = logger.LogCleanSession(
            result,
            scan,
            new AppSettings { RecentFileProtectionHours = 24 },
            "1.0.0-test",
            null);

        Check.True(write.Success, write.Error);
        Check.True(File.Exists(logger.LogFilePath));
        var text = File.ReadAllText(logger.LogFilePath);
        Check.Contains("deleted-files=2", text);
        Check.Contains("deleted-bytes=", text);
        Check.Contains("recent-protection-hours: 24", text);
        Check.DoesNotContain(secret, text);
        Check.DoesNotContain("other.tmp", text);
        Check.DoesNotContain("secret.tmp", text);
        Check.Contains("file contents", text);
    }

    [Test("Log rotates within the configured cap")]
    public void LogRotates()
    {
        using var workspace = new TestWorkspace();
        var logDirectory = Path.Combine(workspace.BasePath, "logs");
        var logger = new CleanupLogger(logDirectory, maxFileBytes: 16 * 1024, maxFiles: 3);
        var result = BuildSyntheticResult(workspace.Boundary.RootPath);
        var scan = workspace.Scan();
        for (var index = 0; index < 80; index++)
        {
            var write = logger.LogCleanSession(
                result,
                scan,
                AppSettings.Default,
                "1.0.0",
                null);
            Check.True(write.Success, write.Error);
        }

        var files = Directory.GetFiles(logDirectory, "liteclean.log*");
        Check.True(files.Length is >= 1 and <= 3, $"Unexpected log file count: {files.Length}");
        foreach (var file in files)
        {
            Check.True(new FileInfo(file).Length <= 16 * 1024, $"Log file exceeded cap: {file}");
        }
    }

    [Test("Log write failure is reported without throwing")]
    public void LogWriteFailureIsReported()
    {
        using var workspace = new TestWorkspace();
        var blocker = workspace.CreateFile("log-blocker", "not a directory");
        var logger = new CleanupLogger(blocker);
        var result = BuildSyntheticResult(workspace.Boundary.RootPath);
        var write = logger.LogCleanSession(result, scan: null, AppSettings.Default, "1.0.0", null);
        Check.False(write.Success);
        Check.True(write.Error is not null);
    }

    private static CleanResult BuildSyntheticResult(string rootPath) => new()
    {
        RootPath = rootPath,
        SkippedCounts = new Dictionary<SkipReason, int> { [SkipReason.InUse] = 2 },
        FailedCounts = new Dictionary<SkipReason, int> { [SkipReason.AccessDenied] = 1 },
        Details = Array.Empty<CleanOutcome>(),
        Issues = new[]
        {
            new CleanOutcome("skipped.tmp", ItemKind.File, CleanItemStatus.Skipped, 0, SkipReason.InUse, 32),
            new CleanOutcome("failed.tmp", ItemKind.File, CleanItemStatus.Failed, 0, SkipReason.AccessDenied, 5),
        },
        DeletedFileCount = 3,
        DeletedDirectoryCount = 2,
        DeletedFileBytes = 12345,
        SkippedItemCount = 2,
        FailedItemCount = 1,
        AttemptedItemCount = 6,
        TotalSelectedItemCount = 6,
        Canceled = false,
        Completed = true,
        Duration = TimeSpan.FromMilliseconds(321),
    };
}
