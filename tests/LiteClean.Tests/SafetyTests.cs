using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using LiteClean.Core.Models;

namespace LiteClean.Tests;

internal sealed class SafetyTests
{
    [Test("Locked directory is skipped without aborting")]
    public void LockedDirectoryIsSkipped()
    {
        using var workspace = new TestWorkspace();
        var lockedDirectory = workspace.CreateDirectory("locked");
        workspace.CreateFile("locked/inside.tmp", "inside");
        workspace.CreateFile("free.tmp", "free");

        using var exclusive = OpenDirectoryWithNoSharing(lockedDirectory);
        var result = workspace.Scan();

        Check.True(result.Completed);
        Check.True(result.Candidates.Any(candidate => candidate.RelativePath == "free.tmp"));
        var candidateDump = string.Join(",", result.Candidates.Select(c => $"{c.Kind}:{c.RelativePath}"));
        var skipDump = string.Join(",", result.SkippedCounts.Select(pair => $"{pair.Key}={pair.Value}"));
        Check.False(
            result.Candidates.Any(candidate =>
                candidate.RelativePath.Equals("locked/inside.tmp", StringComparison.OrdinalIgnoreCase)),
            $"locked child was scanned; candidates=[{candidateDump}] skips=[{skipDump}]");
        Check.False(
            result.Candidates.Any(candidate =>
                candidate.Kind == ItemKind.Directory &&
                candidate.RelativePath.Equals("locked", StringComparison.OrdinalIgnoreCase)),
            $"locked directory was a candidate; candidates=[{candidateDump}] skips=[{skipDump}]");
        Check.True(result.SkippedItemCount >= 1);
        Check.True(result.SkippedCounts.Keys.Any(reason =>
            reason is SkipReason.InUse or SkipReason.AccessDenied or SkipReason.Unreadable));
        Check.FileExists(workspace.RootPath("locked/inside.tmp"));
    }

    [Test("Directory replaced by a junction after scan is refused")]
    public void DirectoryReplacedByJunctionIsRefused()
    {
        using var workspace = new TestWorkspace();
        var victim = workspace.CreateDirectory("victim");
        var scan = workspace.Scan();
        var directory = scan.Candidates.Single(candidate =>
            candidate.Kind == ItemKind.Directory && candidate.RelativePath == "victim");
        Check.True(directory.Identity.IsEmpty is false);

        Directory.Delete(victim, recursive: false);
        workspace.CreateJunction("victim", workspace.Outside);

        var result = workspace.Clean(new[] { directory });

        Check.Equal(0, result.DeletedDirectoryCount);
        Check.True(result.SkippedItemCount >= 1);
        Check.True(result.SkippedCounts.ContainsKey(SkipReason.ReparsePoint) ||
                   result.SkippedCounts.ContainsKey(SkipReason.StateChanged));
        Check.Equal("SENTINEL-CONTENT-MUST-NOT-CHANGE", File.ReadAllText(workspace.OutsideSentinel));
        Check.True(Directory.Exists(workspace.RootPath("victim")));
    }

    [Test("Root replaced by a junction aborts cleanup before any deletion")]
    public void RootReplacedByJunctionAborts()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFile("one.tmp", "one");
        var scan = workspace.Scan();
        Check.Equal(1, scan.CandidateFileCount);

        var moved = Path.Combine(workspace.BasePath, "root-moved");
        Directory.Move(workspace.Root, moved);
        workspace.CreateJunctionAt(workspace.Root, workspace.Outside);

        var result = workspace.CleanAll(scan);

        Check.True(result.Error is not null);
        Check.Equal(0, result.AttemptedItemCount);
        Check.Equal(0, result.DeletedFileCount);
        Check.Equal(1, result.NotAttemptedItemCount);
        Check.Equal("SENTINEL-CONTENT-MUST-NOT-CHANGE", File.ReadAllText(workspace.OutsideSentinel));
        Check.FileExists(Path.Combine(moved, "one.tmp"));
    }

    [Test("No-permission directory is skipped without aborting")]
    public void NoPermissionDirectoryIsSkipped()
    {
        using var workspace = new TestWorkspace();
        var lockedDirectory = workspace.CreateDirectory("locked");
        workspace.CreateFile("locked/inside.tmp", "inside");
        var free = workspace.CreateFile("free.tmp", "free");
        var user = WindowsIdentity.GetCurrent().User;
        if (user is null)
        {
            throw new SkipException("Current Windows user SID is unavailable.");
        }

        var rule = new FileSystemAccessRule(
            user,
            FileSystemRights.ListDirectory,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Deny);

        var directoryInfo = new DirectoryInfo(lockedDirectory);
        try
        {
            var security = directoryInfo.GetAccessControl();
            security.AddAccessRule(rule);
            directoryInfo.SetAccessControl(security);
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException or
                PrivilegeNotHeldException)
        {
            throw new SkipException($"Cannot apply a test deny ACL in this environment: {ex.Message}");
        }

        try
        {
            if (CanEnumerate(lockedDirectory))
            {
                throw new SkipException("Deny ACL did not prevent enumeration in this environment.");
            }

            var result = workspace.Scan();

            Check.True(result.Completed);
            Check.True(result.Candidates.Any(candidate => candidate.RelativePath == "free.tmp"));
            Check.False(result.Candidates.Any(candidate =>
                candidate.RelativePath.Equals("locked/inside.tmp", StringComparison.OrdinalIgnoreCase)));
            Check.True(result.SkippedItemCount >= 1);
            Check.True(result.SkippedCounts.Keys.Any(reason =>
                reason is SkipReason.AccessDenied or SkipReason.Unreadable));
            Check.FileExists(workspace.RootPath("locked/inside.tmp"));
            Check.FileExists(free);
        }
        finally
        {
            try
            {
                var security = directoryInfo.GetAccessControl();
                security.RemoveAccessRule(rule);
                directoryInfo.SetAccessControl(security);
            }
            catch (Exception ex) when (
                ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                Console.Error.WriteLine($"WARNING: failed to restore test ACL: {ex.Message}");
            }
        }
    }

    [Test("Candidate path containing .. is rejected")]
    public void CandidateContainingDotDotIsRejected()
    {
        using var workspace = new TestWorkspace();
        var malicious = Path.Combine(
            workspace.Boundary.RootPath,
            "..",
            "outside",
            "sentinel.txt");
        var candidate = new ScanCandidate(
            malicious,
            "sentinel.txt",
            ItemKind.File,
            new FileInfo(workspace.OutsideSentinel).Length,
            0,
            default);

        var result = workspace.Clean(new[] { candidate });

        Check.Equal(0, result.DeletedFileCount);
        Check.True(result.SkippedCounts.ContainsKey(SkipReason.OutsideBoundary) ||
                   result.SkippedCounts.ContainsKey(SkipReason.InvalidPath));
        Check.Equal("SENTINEL-CONTENT-MUST-NOT-CHANGE", File.ReadAllText(workspace.OutsideSentinel));
    }

    [Test("Selected empty directory is deleted only when truly empty")]
    public void DirectoryDeletionRequiresEmpty()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFile("full/child.tmp", "child");
        workspace.CreateDirectory("empty");
        var scan = workspace.Scan();
        var empty = scan.Candidates.Single(candidate =>
            candidate.Kind == ItemKind.Directory && candidate.RelativePath == "empty");
        var full = scan.Candidates.Single(candidate =>
            candidate.Kind == ItemKind.Directory && candidate.RelativePath == "full");

        var result = workspace.Clean(new[] { empty, full });

        Check.Equal(1, result.DeletedDirectoryCount);
        Check.DirectoryMissing(workspace.RootPath("empty"));
        Check.DirectoryExists(workspace.RootPath("full"));
        Check.FileExists(workspace.RootPath("full/child.tmp"));
        Check.True(result.SkippedCounts.ContainsKey(SkipReason.DirectoryNotEmpty));
    }

    private static bool CanEnumerate(string directory)
    {
        try
        {
            _ = Directory.EnumerateFileSystemEntries(directory).Take(1).ToArray();
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static SafeFileHandle OpenDirectoryWithNoSharing(string directory)
    {
        const uint fileListDirectory = 0x00000001;
        const uint fileReadAttributes = 0x00000080;
        const uint openExisting = 3;
        const uint fileFlagBackupSemantics = 0x02000000;
        var handle = CreateFileW(
            directory,
            fileListDirectory | fileReadAttributes,
            dwShareMode: 0,
            IntPtr.Zero,
            openExisting,
            fileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new SkipException(
                $"Could not open test directory with exclusive sharing (Win32 {Marshal.GetLastWin32Error()}).");
        }

        return handle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);
}
