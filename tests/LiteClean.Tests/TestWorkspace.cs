using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using LiteClean.Core.Cleaning;
using LiteClean.Core.Models;
using LiteClean.Core.Scanning;
using LiteClean.Core.Security;

namespace LiteClean.Tests;

internal sealed class TestWorkspace : IDisposable
{
    private static readonly DateTime OldTimestamp = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    public TestWorkspace()
    {
        BasePath = Path.Combine(
            Path.GetTempPath(),
            "LiteClean.Tests",
            Guid.NewGuid().ToString("N"));
        Root = Path.Combine(BasePath, "root");
        Outside = Path.Combine(BasePath, "outside");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Outside);
        OutsideSentinel = Path.Combine(Outside, "sentinel.txt");
        File.WriteAllText(OutsideSentinel, "SENTINEL-CONTENT-MUST-NOT-CHANGE");

        if (!TempRootResolver.TryResolve(Root, out var boundary, out var failure) || boundary is null)
        {
            throw new InvalidOperationException(
                $"Could not create a valid test cleanup boundary: {failure?.Message}");
        }

        Boundary = boundary;
    }

    public string BasePath { get; }
    public string Root { get; }
    public string Outside { get; }
    public string OutsideSentinel { get; }
    public PathBoundary Boundary { get; }

    public string RootPath(string relative) => Path.Combine(Root, relative);

    public string OutsidePath(string relative) => Path.Combine(Outside, relative);

    public string CreateFile(string relative, string content = "content", DateTime? lastWriteUtc = null)
    {
        var path = RootPath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.SetLastWriteTimeUtc(path, lastWriteUtc ?? OldTimestamp);
        return path;
    }

    public string CreateEmptyFile(string relative, DateTime? lastWriteUtc = null) =>
        CreateFile(relative, string.Empty, lastWriteUtc);

    public string CreateBytes(string relative, byte[] bytes, DateTime? lastWriteUtc = null)
    {
        var path = RootPath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, lastWriteUtc ?? OldTimestamp);
        return path;
    }

    public string CreateDirectory(string relative)
    {
        var path = RootPath(relative);
        Directory.CreateDirectory(path);
        return path;
    }

    public string CreateReadOnlyFile(string relative, string content = "readonly")
    {
        var path = CreateFile(relative, content);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        return path;
    }

    public string CreateJunction(string relative, string target)
    {
        var link = RootPath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        return CreateJunctionAt(link, target);
    }

    public string CreateJunctionAt(string link, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var command = $"/c mklink /J \"{link}\" \"{target}\"";
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", command)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        using var process = Process.Start(info);
        if (process is null)
        {
            throw new SkipException("cmd.exe could not be started for junction creation.");
        }

        process.WaitForExit();
        var attributes = File.Exists(link) || Directory.Exists(link)
            ? File.GetAttributes(link)
            : (FileAttributes)0;
        if (process.ExitCode != 0 || (attributes & FileAttributes.ReparsePoint) == 0)
        {
            throw new SkipException(
                $"Junction creation failed (exit={process.ExitCode}); privilege may be restricted.");
        }

        return link;
    }

    public string CreateFileSymbolicLink(string relative, string target)
    {
        var link = RootPath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        const uint allowUnprivilegedCreate = 0x2;
        var created = CreateSymbolicLink(link, target, allowUnprivilegedCreate);
        var error = Marshal.GetLastWin32Error();
        if (created == 0 || !File.Exists(link))
        {
            throw new SkipException(
                "File symlink creation requires Developer Mode or SeCreateSymbolicLinkPrivilege " +
                $"(Win32 {error}); this environment cannot exercise file symlinks.");
        }

        return link;
    }

    public string CreateDirectorySymbolicLink(string relative, string target)
    {
        var link = RootPath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        const uint directoryFlag = 0x1;
        const uint allowUnprivilegedCreate = 0x2;
        var created = CreateSymbolicLink(link, target, directoryFlag | allowUnprivilegedCreate);
        var error = Marshal.GetLastWin32Error();
        if (created == 0 || !Directory.Exists(link))
        {
            throw new SkipException(
                "Directory symlink creation requires Developer Mode or SeCreateSymbolicLinkPrivilege " +
                $"(Win32 {error}); this environment cannot exercise directory symlinks.");
        }

        return link;
    }

    public ScanResult Scan(TimeSpan? recentProtection = null, int previewLimit = 1000)
    {
        var scanner = new TempScanner(new ScanOptions
        {
            RecentProtection = recentProtection ?? TimeSpan.FromHours(24),
            MaxDegreeOfParallelism = 2,
            SkippedPreviewLimit = previewLimit,
            ProgressIntervalMilliseconds = 10,
        });
        return scanner.ScanAsync(Boundary, progress: null, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    public CleanResult Clean(
        IReadOnlyList<ScanCandidate> selected,
        TimeSpan? recentProtection = null,
        IProgress<CleanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var cleaner = new TempCleaner(new CleanOptions
        {
            RecentProtection = recentProtection ?? TimeSpan.FromHours(24),
            DetailsLimit = 10_000,
            IssueDetailsLimit = 10_000,
            ProgressIntervalMilliseconds = 0,
        });
        return cleaner.CleanAsync(Boundary, selected, progress, cancellationToken)
            .GetAwaiter()
            .GetResult();
    }

    public CleanResult CleanAll(ScanResult scan, CancellationToken cancellationToken = default) =>
        Clean(scan.Candidates, cancellationToken: cancellationToken);

    public string SnapshotText()
    {
        var builder = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(Root, "*", new EnumerationOptions
                 {
                     RecurseSubdirectories = true,
                     AttributesToSkip = 0,
                     IgnoreInaccessible = false,
                     ReturnSpecialDirectories = false,
                 }).OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(Path.GetRelativePath(Root, file));
            builder.Append('|');
            builder.Append(new FileInfo(file).Length);
            builder.Append('|');
            builder.Append(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))));
            builder.AppendLine();
        }

        return builder.ToString();
    }

    public int CountFiles() =>
        Directory.EnumerateFiles(Root, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
        }).Count();

    public void Dispose()
    {
        try
        {
            SafeDeleteTree(BasePath);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"WARNING: failed to clean test workspace '{BasePath}': {ex.Message}");
        }
    }

    public static void SafeDeleteTree(string path)
    {
        var expectedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "LiteClean.Tests"));
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(expectedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to recursively delete outside test root: {full}");
        }

        DeleteRecursively(full, attempts: 0);
    }

    private static void DeleteRecursively(string directory, int attempts)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                AttributesToSkip = 0,
                IgnoreInaccessible = false,
                ReturnSpecialDirectories = false,
            };
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", options).ToArray())
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    continue;
                }

                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    try
                    {
                        File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // The delete attempt below will report the real error.
                    }
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        Directory.Delete(entry, recursive: false);
                    }
                    else
                    {
                        File.Delete(entry);
                    }

                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    DeleteRecursively(entry, attempts);
                }
                else
                {
                    File.Delete(entry);
                }
            }

            Directory.Delete(directory, recursive: false);
        }
        catch (Exception ex) when (
            attempts < 3 && ex is IOException or UnauthorizedAccessException)
        {
            Thread.Sleep(50 * (attempts + 1));
            DeleteRecursively(directory, attempts + 1);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern byte CreateSymbolicLink(
        string lpSymlinkFileName,
        string lpTargetFileName,
        uint dwFlags);
}
