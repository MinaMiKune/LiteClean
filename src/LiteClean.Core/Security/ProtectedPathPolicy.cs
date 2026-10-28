using System.Runtime.InteropServices;
using LiteClean.Core.Native;

namespace LiteClean.Core.Security;

internal static class ProtectedPathPolicy
{
    private static readonly string[] SuspiciousLeafNames =
    [
        "windows", "system32", "syswow64", "winsxs", "program files", "program files (x86)",
        "programdata", "users", "desktop", "documents", "downloads", "pictures", "music",
        "videos", "onedrive", "appdata", "roaming", "local", "start menu", "recovery",
        "$recycle.bin", "system volume information", "boot", "efi",
    ];

    public static IReadOnlyList<string> HardProtected { get; } = BuildHardProtected();

    public static IReadOnlyList<string> UserProtected { get; } = BuildUserProtected();

    public static IReadOnlyList<string> UserDataSpecific { get; } = BuildUserDataSpecific();

    public static string? LocalTempRoot { get; } = BuildLocalTempRoot();

    public static bool TryFindProtectedRelationship(string canonicalRoot, out string reason)
    {
        if (SuspiciousLeafNames.Contains(GetLeafName(canonicalRoot), StringComparer.OrdinalIgnoreCase))
        {
            reason = $"临时目录的末级名称 “{GetLeafName(canonicalRoot)}” 属于受保护名称。";
            return true;
        }

        foreach (var path in HardProtected)
        {
            if (PathUtil.IsDescendantOrSelf(canonicalRoot, path) ||
                PathUtil.IsDescendantOrSelf(path, canonicalRoot))
            {
                reason = $"临时目录与受保护的系统/程序位置重叠：{path}";
                return true;
            }
        }

        var insideLocalTemp = LocalTempRoot is not null &&
                              PathUtil.IsDescendantOrSelf(canonicalRoot, LocalTempRoot);

        foreach (var path in UserProtected)
        {
            if (PathUtil.IsDescendantOrSelf(path, canonicalRoot))
            {
                reason = $"临时目录是用户数据目录的上级目录：{path}";
                return true;
            }

            if (!PathUtil.IsDescendantOrSelf(canonicalRoot, path))
            {
                continue;
            }

            if (insideLocalTemp && !IsInsideSpecificUserData(canonicalRoot))
            {
                // The normal %LOCALAPPDATA%\Temp subtree is intentionally allowed.
                continue;
            }

            reason = $"临时目录位于受保护的用户数据位置：{path}";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static bool IsInsideSpecificUserData(string path)
    {
        foreach (var specific in UserDataSpecific)
        {
            if (PathUtil.IsDescendantOrSelf(path, specific))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetLeafName(string path)
    {
        var trimmed = PathUtil.TrimTrailingSeparators(path);
        var index = trimmed.LastIndexOfAny(['\\', '/']);
        return index >= 0 ? trimmed[(index + 1)..] : trimmed;
    }

    private static List<string> BuildHardProtected()
    {
        var result = new List<string>();
        AddKnown(result, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        AddKnown(result, Environment.GetFolderPath(Environment.SpecialFolder.System));
        AddKnown(result, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        AddKnown(result, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        AddKnown(result, Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles));
        AddKnown(result, Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86));
        AddKnown(result, Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        AddKnown(result, AppContext.BaseDirectory);

        var systemDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (!string.IsNullOrEmpty(systemDrive))
        {
            foreach (var name in new[]
                     {
                         "$Recycle.Bin", "Recovery", "System Volume Information", "Boot", "EFI", "PerfLogs",
                         "Windows.old",
                     })
            {
                AddKnown(result, Path.Combine(systemDrive, name));
            }
        }

        return result
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> BuildUserProtected()
    {
        var result = new List<string>();
        AddKnown(result, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        AddKnown(result, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        AddKnown(result, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        AddKnown(result, Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        AddKnown(result, Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        AddKnown(result, Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        AddKnown(result, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        AddKnown(result, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            AddKnown(result, Path.Combine(profile, "Downloads"));
        }

        AddKnown(result, Environment.GetEnvironmentVariable("OneDrive"));
        AddKnown(result, Environment.GetEnvironmentVariable("PUBLIC"));

        return result
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> BuildUserDataSpecific()
    {
        var result = new List<string>();
        AddKnown(result, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        AddKnown(result, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        AddKnown(result, Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        AddKnown(result, Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        AddKnown(result, Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            AddKnown(result, Path.Combine(profile, "Downloads"));
        }

        AddKnown(result, Environment.GetEnvironmentVariable("OneDrive"));
        AddKnown(result, Environment.GetEnvironmentVariable("PUBLIC"));

        return result
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? BuildLocalTempRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            return null;
        }

        var defaultTemp = Path.Combine(localAppData, "Temp");
        return CanonicalizeFollowingLinks(defaultTemp) ?? TryNormalize(defaultTemp);
    }

    private static void AddKnown(List<string> result, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var normalized = TryNormalize(path);
        if (normalized is not null)
        {
            result.Add(normalized);
        }

        // Also protect the target of redirected/known folders and the link path itself.
        var resolved = CanonicalizeFollowingLinks(path);
        if (resolved is not null)
        {
            result.Add(resolved);
        }
    }

    private static string? TryNormalize(string path)
    {
        try
        {
            return PathUtil.NormalizeFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? CanonicalizeFollowingLinks(string path)
    {
        if (!Directory.Exists(path))
        {
            return null;
        }

        using var handle = Win32File.OpenDirectoryResolved(path, out var error);
        if (handle.IsInvalid)
        {
            _ = error;
            return null;
        }

        if (!PathUtil.TryGetFinalPath(handle, out var finalPath, out _))
        {
            return null;
        }

        return PathUtil.IsUncOrDevicePath(finalPath) ? null : finalPath;
    }
}
