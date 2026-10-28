using LiteClean.Core.Native;

namespace LiteClean.Core.Security;

/// <summary>
/// Resolves TEMP into a canonical, protected-boundary-checked cleanup root.
/// There is no fallback to TMP, the current directory, a drive root, or any
/// other broader location: if TEMP is missing or fails validation, cleanup is
/// disabled and the caller receives an explicit reason.
/// </summary>
public static class TempRootResolver
{
    public static bool TryResolveFromEnvironment(
        out PathBoundary? boundary,
        out PathBoundaryFailure? failure)
    {
        var raw = Environment.GetEnvironmentVariable("TEMP");
        return TryResolve(raw, out boundary, out failure);
    }

    public static bool TryResolve(
        string? tempValue,
        out PathBoundary? boundary,
        out PathBoundaryFailure? failure)
    {
        boundary = null;

        if (string.IsNullOrWhiteSpace(tempValue))
        {
            failure = new PathBoundaryFailure(
                PathBoundaryErrorCode.TempVariableEmpty,
                "TEMP 环境变量为空或不存在。为避免扩大清理范围，LiteClean 不会回退到 TMP、当前目录或磁盘根目录。");
            return false;
        }

        var raw = tempValue;
        if (raw.Contains('%', StringComparison.Ordinal))
        {
            failure = new PathBoundaryFailure(
                PathBoundaryErrorCode.InvalidPath,
                "TEMP 包含未展开的环境变量，拒绝使用。");
            return false;
        }

        if (raw.Contains('\0', StringComparison.Ordinal) || raw.IndexOfAny(['*', '?', '"', '<', '>', '|']) >= 0)
        {
            failure = new PathBoundaryFailure(
                PathBoundaryErrorCode.InvalidPath,
                "TEMP 包含 Windows 路径不允许的字符。");
            return false;
        }

        if (PathUtil.HasUnsafeRawSegments(raw))
        {
            failure = new PathBoundaryFailure(
                PathBoundaryErrorCode.InvalidName,
                "TEMP 路径包含以点或空格结尾的名称段。");
            return false;
        }

        if (!Path.IsPathFullyQualified(raw))
        {
            failure = new PathBoundaryFailure(
                PathBoundaryErrorCode.NotAbsolutePath,
                "TEMP 不是绝对路径。为避免相对路径歧义，清理已禁用。");
            return false;
        }

        string normalized;
        try
        {
            normalized = PathUtil.NormalizeFullPath(raw);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            failure = new PathBoundaryFailure(
                PathBoundaryErrorCode.InvalidPath,
                "TEMP 路径格式无效或过长。");
            return false;
        }

        if (PathUtil.IsUncOrDevicePath(normalized))
        {
            failure = new PathBoundaryFailure(
                PathBoundaryErrorCode.NetworkOrDevicePath,
                "TEMP 指向 UNC、网络或设备路径。LiteClean 仅支持本地固定磁盘。");
            return false;
        }

        if (PathUtil.IsDriveRoot(normalized))
        {
            failure = new PathBoundaryFailure(
                PathBoundaryErrorCode.ProtectedLocation,
                "TEMP 指向磁盘根目录，拒绝清理。");
            return false;
        }

        var driveRoot = Path.GetPathRoot(normalized);
        if (string.IsNullOrEmpty(driveRoot) || !IsFixedDrive(driveRoot))
        {
            failure = new PathBoundaryFailure(
                PathBoundaryErrorCode.NotLocalFixedDrive,
                "TEMP 不在本地固定磁盘上。LiteClean 不支持网络盘、可移动盘或虚拟盘。");
            return false;
        }

        using var handle = Win32File.OpenDirectoryForMetadata(normalized, out var openError);
        if (handle.IsInvalid)
        {
            failure = MapOpenError(openError);
            return false;
        }

        if (!Win32File.TryGetInfo(handle, out var info, out var infoError))
        {
            failure = new PathBoundaryFailure(
                PathBoundaryErrorCode.Unreadable,
                $"无法读取 TEMP 目录信息（Win32 {infoError}）。");
            return false;
        }

        if (Win32File.IsReparsePoint(info.FileAttributes))
        {
            failure = new PathBoundaryFailure(
                PathBoundaryErrorCode.ReparsePoint,
                "TEMP 根目录本身是重解析点（符号链接/目录联接），拒绝清理。");
            return false;
        }

        if (!Win32File.IsDirectory(info.FileAttributes))
        {
            failure = new PathBoundaryFailure(
                PathBoundaryErrorCode.NotDirectory,
                "TEMP 不是一个目录。");
            return false;
        }

        if (!PathUtil.TryGetFinalPath(handle, out var canonical, out var finalPathError))
        {
            failure = new PathBoundaryFailure(
                PathBoundaryErrorCode.Unreadable,
                $"无法解析 TEMP 的实际路径（Win32 {finalPathError}）。");
            return false;
        }

        canonical = PathUtil.TrimTrailingSeparators(canonical);
        if (PathUtil.IsUncOrDevicePath(canonical) || !Path.IsPathFullyQualified(canonical))
        {
            failure = new PathBoundaryFailure(
                PathBoundaryErrorCode.NetworkOrDevicePath,
                "TEMP 的实际解析路径不是本地驱动器路径。");
            return false;
        }

        if (PathUtil.IsDriveRoot(canonical))
        {
            failure = new PathBoundaryFailure(
                PathBoundaryErrorCode.ProtectedLocation,
                "TEMP 实际解析到磁盘根目录，拒绝清理。");
            return false;
        }

        var canonicalDriveRoot = Path.GetPathRoot(canonical);
        if (string.IsNullOrEmpty(canonicalDriveRoot) || !IsFixedDrive(canonicalDriveRoot))
        {
            failure = new PathBoundaryFailure(
                PathBoundaryErrorCode.NotLocalFixedDrive,
                "TEMP 实际解析路径不在本地固定磁盘上。");
            return false;
        }

        if (IsInAnotherUserProfile(canonical))
        {
            failure = new PathBoundaryFailure(
                PathBoundaryErrorCode.ProtectedLocation,
                "TEMP 指向其他用户配置文件，拒绝清理。");
            return false;
        }

        if (ProtectedPathPolicy.TryFindProtectedRelationship(canonical, out var reason))
        {
            failure = new PathBoundaryFailure(PathBoundaryErrorCode.ProtectedLocation, reason);
            return false;
        }

        boundary = new PathBoundary(raw, canonical);
        failure = null;
        return true;
    }

    private static bool IsFixedDrive(string driveRoot)
    {
        try
        {
            return new DriveInfo(driveRoot).DriveType == DriveType.Fixed;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsInAnotherUserProfile(string canonical)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile))
        {
            return false;
        }

        string normalizedProfile;
        try
        {
            normalizedProfile = PathUtil.NormalizeFullPath(profile);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var usersRoot = Directory.GetParent(normalizedProfile)?.FullName;
        if (string.IsNullOrWhiteSpace(usersRoot))
        {
            return false;
        }

        return PathUtil.IsDescendantOrSelf(canonical, usersRoot) &&
               !PathUtil.IsDescendantOrSelf(canonical, normalizedProfile);
    }

    private static PathBoundaryFailure MapOpenError(int error) => error switch
    {
        NativeMethods.ErrorFileNotFound or NativeMethods.ErrorPathNotFound =>
            new PathBoundaryFailure(PathBoundaryErrorCode.NotFound, "TEMP 目录不存在。"),
        NativeMethods.ErrorAccessDenied =>
            new PathBoundaryFailure(PathBoundaryErrorCode.AccessDenied, "没有权限读取 TEMP 目录。"),
        NativeMethods.ErrorSharingViolation or NativeMethods.ErrorLockViolation =>
            new PathBoundaryFailure(PathBoundaryErrorCode.AccessDenied, "TEMP 目录当前被占用或无法打开。"),
        _ => new PathBoundaryFailure(
            PathBoundaryErrorCode.Unreadable,
            $"无法打开 TEMP 目录（Win32 {error}）。"),
    };
}
