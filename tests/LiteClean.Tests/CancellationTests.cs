using System.Diagnostics;
using LiteClean.Core.Models;
using LiteClean.Core.Scanning;

namespace LiteClean.Tests;

internal sealed class CancellationTests
{
    [Test("Scan cancellation returns a canceled partial result")]
    public void ScanCanBeCanceled()
    {
        using var workspace = new TestWorkspace();
        for (var index = 0; index < 1500; index++)
        {
            workspace.CreateFile($"cancel-{index:D4}.tmp", "x");
        }

        var scanner = new TempScanner(new ScanOptions
        {
            RecentProtection = TimeSpan.FromHours(24),
            MaxDegreeOfParallelism = 2,
            SkippedPreviewLimit = 100,
            ProgressIntervalMilliseconds = 0,
            FileChannelCapacity = 64,
        });
        using var cts = new CancellationTokenSource();
        var progress = new CallbackProgress<ScanProgress>(value =>
        {
            if (value.EntriesVisited >= 25)
            {
                cts.Cancel();
            }
        });

        var result = scanner.ScanAsync(workspace.Boundary, progress, cts.Token).GetAwaiter().GetResult();

        Check.True(result.Canceled);
        Check.False(result.Completed);
        Check.Equal(1500, workspace.CountFiles());
    }

    [Test("Pre-canceled scan does not enumerate or modify files")]
    public void PreCanceledScan()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFile("one.tmp", "one");
        var before = workspace.SnapshotText();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var scanner = new TempScanner(new ScanOptions { RecentProtection = TimeSpan.FromHours(24) });

        var result = scanner.ScanAsync(workspace.Boundary, progress: null, cts.Token).GetAwaiter().GetResult();

        Check.True(result.Canceled);
        Check.False(result.Completed);
        Check.Equal(before, workspace.SnapshotText());
    }

    [Test("Canceling before cleanup starts deletes nothing")]
    public void CleanupCanceledBeforeStart()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFile("one.tmp", "one");
        workspace.CreateFile("two.tmp", "two");
        var scan = workspace.Scan();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = workspace.Clean(scan.Candidates, cancellationToken: cts.Token);

        Check.True(result.Canceled);
        Check.Equal(0, result.DeletedFileCount);
        Check.Equal(2, workspace.CountFiles());
    }

    [Test("Clean cancellation leaves a truthful partial summary")]
    public void CleanupPartialSummary()
    {
        using var workspace = new TestWorkspace();
        for (var index = 0; index < 120; index++)
        {
            workspace.CreateBytes($"file-{index:D3}.bin", new byte[10]);
        }

        var scan = workspace.Scan();
        using var cts = new CancellationTokenSource();
        var cancelAt = 10;
        var progress = new CallbackProgress<CleanProgress>(value =>
        {
            if (value.AttemptedItems >= cancelAt)
            {
                cts.Cancel();
            }
        });

        var result = workspace.Clean(scan.Candidates, progress: progress, cancellationToken: cts.Token);

        Check.True(result.Canceled);
        Check.True(result.Completed is false);
        Check.True(result.DeletedFileCount <= cancelAt + 1);
        Check.True(result.AttemptedItemCount <= cancelAt + 2);
        Check.True(result.NotAttemptedItemCount > 0);
        Check.Equal(result.DeletedFileCount * 10L, result.DeletedFileBytes);
        Check.Equal(120 - result.DeletedFileCount, workspace.CountFiles());
    }

    private sealed class CallbackProgress<T> : IProgress<T>
    {
        private readonly Action<T> _callback;

        public CallbackProgress(Action<T> callback) => _callback = callback;

        public void Report(T value) => _callback(value);
    }
}
