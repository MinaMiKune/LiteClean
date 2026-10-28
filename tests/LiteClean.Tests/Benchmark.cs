using System.Diagnostics;
using System.Globalization;
using LiteClean.Core.Cleaning;
using LiteClean.Core.Models;
using LiteClean.Core.Scanning;

namespace LiteClean.Tests;

internal static class Benchmark
{
    public static int Run(string[] args)
    {
        var smallFileCount = ReadInt(args, "--files", 6000);
        var bigFileCount = ReadInt(args, "--big", 25);
        if (smallFileCount is < 100 or > 200_000)
        {
            Console.Error.WriteLine("--files must be between 100 and 200000.");
            return 2;
        }

        using var workspace = new TestWorkspace();
        var process = Process.GetCurrentProcess();
        Console.WriteLine("LiteClean benchmark");
        Console.WriteLine($"OS: {Environment.OSVersion.VersionString}");
        Console.WriteLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"CPU: {Environment.ProcessorCount} logical processors");
        Console.WriteLine($"Root: {workspace.Root}");
        Console.WriteLine($"Parameters: small-files={smallFileCount}, big-files={bigFileCount}, directories~500, deep=120");
        Console.WriteLine();

        var generation = Stopwatch.StartNew();
        var expectedFiles = 0;
        long expectedBytes = 0;
        var smallBytes = new byte[512];
        for (var index = 0; index < smallBytes.Length; index++)
        {
            smallBytes[index] = (byte)(index % 251);
        }

        for (var index = 0; index < 500; index++)
        {
            var directory = $"data/d{index:D3}";
            workspace.CreateDirectory(directory);
            for (var file = 0; file < 6 && expectedFiles < smallFileCount - 3000; file++)
            {
                workspace.CreateBytes($"{directory}/f{file:D2}.tmp", smallBytes);
                expectedFiles++;
                expectedBytes += smallBytes.Length;
            }
        }

        workspace.CreateDirectory("wide");
        while (expectedFiles < smallFileCount)
        {
            workspace.CreateBytes($"wide/w{expectedFiles:D5}.tmp", smallBytes);
            expectedFiles++;
            expectedBytes += smallBytes.Length;
        }

        var deepRelative = string.Join(
            Path.DirectorySeparatorChar,
            Enumerable.Range(0, 120).Select(index => $"d{index:D3}"));
        workspace.CreateDirectory(deepRelative);
        for (var index = 0; index < 5; index++)
        {
            workspace.CreateBytes(Path.Combine(deepRelative, $"deep-{index}.tmp"), smallBytes);
            expectedFiles++;
            expectedBytes += smallBytes.Length;
        }

        var bigBytes = new byte[1024 * 1024];
        for (var index = 0; index < bigFileCount; index++)
        {
            workspace.CreateBytes($"big/b{index:D3}.bin", bigBytes);
            expectedFiles++;
            expectedBytes += bigBytes.Length;
        }

        generation.Stop();
        Console.WriteLine($"generated: files={expectedFiles} bytes={expectedBytes} duration={generation.Elapsed.TotalSeconds:0.0}s");
        Console.WriteLine($"file-system files: {workspace.CountFiles()}");
        Console.WriteLine();

        var scanner = new TempScanner(new ScanOptions
        {
            RecentProtection = TimeSpan.FromHours(24),
            MaxDegreeOfParallelism = 4,
            SkippedPreviewLimit = 1000,
            ProgressIntervalMilliseconds = 250,
            FileChannelCapacity = 512,
        });
        var cleaner = new TempCleaner(new CleanOptions
        {
            RecentProtection = TimeSpan.FromHours(24),
            DetailsLimit = 1000,
            IssueDetailsLimit = 1000,
            ProgressIntervalMilliseconds = 250,
        });

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        process.Refresh();
        var wsBefore = process.WorkingSet64;
        var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

        var scanWatch = Stopwatch.StartNew();
        var scan = scanner.ScanAsync(workspace.Boundary, progress: null, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        scanWatch.Stop();
        process.Refresh();
        var wsAfterScan = process.WorkingSet64;
        var peakAfterScan = process.PeakWorkingSet64;
        var heapAfterScan = GC.GetTotalMemory(forceFullCollection: false);

        Console.WriteLine("SCAN");
        Console.WriteLine($"  duration_ms: {scanWatch.Elapsed.TotalMilliseconds:0}");
        Console.WriteLine($"  files: {scan.CandidateFileCount}");
        Console.WriteLine($"  directories: {scan.CandidateDirectoryCount}");
        Console.WriteLine($"  bytes: {scan.EstimatedFileBytes}");
        Console.WriteLine($"  skipped: {scan.SkippedItemCount}");
        Console.WriteLine($"  completed: {scan.Completed}");
        Console.WriteLine($"  files_per_sec: {scan.CandidateFileCount / Math.Max(scanWatch.Elapsed.TotalSeconds, 0.001):0}");
        Console.WriteLine($"  working_set_before_mb: {wsBefore / 1048576.0:0.0}");
        Console.WriteLine($"  working_set_after_mb: {wsAfterScan / 1048576.0:0.0}");
        Console.WriteLine($"  process_peak_working_set_mb: {peakAfterScan / 1048576.0:0.0}");
        Console.WriteLine($"  managed_heap_before_mb: {heapBefore / 1048576.0:0.0}");
        Console.WriteLine($"  managed_heap_after_mb: {heapAfterScan / 1048576.0:0.0}");
        Console.WriteLine();

        var cleanWatch = Stopwatch.StartNew();
        var clean = cleaner.CleanAsync(workspace.Boundary, scan.Candidates, progress: null, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        cleanWatch.Stop();
        process.Refresh();
        var wsAfterClean = process.WorkingSet64;
        var peakAfterClean = process.PeakWorkingSet64;

        Console.WriteLine("CLEAN");
        Console.WriteLine($"  duration_ms: {cleanWatch.Elapsed.TotalMilliseconds:0}");
        Console.WriteLine($"  deleted_files: {clean.DeletedFileCount}");
        Console.WriteLine($"  deleted_directories: {clean.DeletedDirectoryCount}");
        Console.WriteLine($"  deleted_bytes: {clean.DeletedFileBytes}");
        Console.WriteLine($"  skipped: {clean.SkippedItemCount}");
        Console.WriteLine($"  failed: {clean.FailedItemCount}");
        Console.WriteLine($"  canceled: {clean.Canceled}");
        Console.WriteLine($"  completed: {clean.Completed}");
        Console.WriteLine($"  files_per_sec: {clean.DeletedFileCount / Math.Max(cleanWatch.Elapsed.TotalSeconds, 0.001):0}");
        Console.WriteLine($"  working_set_after_mb: {wsAfterClean / 1048576.0:0.0}");
        Console.WriteLine($"  process_peak_working_set_mb: {peakAfterClean / 1048576.0:0.0}");
        Console.WriteLine($"  remaining_files: {workspace.CountFiles()}");
        Console.WriteLine($"  root_exists: {Directory.Exists(workspace.Root)}");
        Console.WriteLine();

        var ok = true;
        if (!scan.Completed || scan.Canceled)
        {
            Console.Error.WriteLine("BENCHMARK FAILURE: scan did not complete.");
            ok = false;
        }

        if (scan.CandidateFileCount != expectedFiles)
        {
            Console.Error.WriteLine(
                $"BENCHMARK FAILURE: scan files {scan.CandidateFileCount} != expected {expectedFiles}.");
            ok = false;
        }

        if (scan.EstimatedFileBytes != expectedBytes)
        {
            Console.Error.WriteLine(
                $"BENCHMARK FAILURE: scan bytes {scan.EstimatedFileBytes} != expected {expectedBytes}.");
            ok = false;
        }

        if (clean.DeletedFileCount != scan.CandidateFileCount ||
            clean.DeletedFileBytes != scan.EstimatedFileBytes ||
            clean.FailedItemCount != 0 ||
            workspace.CountFiles() != 0 ||
            !Directory.Exists(workspace.Root))
        {
            Console.Error.WriteLine(
                $"BENCHMARK FAILURE: cleanup mismatch files={clean.DeletedFileCount} " +
                $"bytes={clean.DeletedFileBytes} failed={clean.FailedItemCount} " +
                $"remaining={workspace.CountFiles()} root={Directory.Exists(workspace.Root)}");
            ok = false;
        }

        Console.WriteLine(ok ? "BENCHMARK RESULT: PASS" : "BENCHMARK RESULT: FAIL");
        return ok ? 0 : 1;
    }

    private static int ReadInt(string[] args, string option, int fallback)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        return fallback;
    }
}
