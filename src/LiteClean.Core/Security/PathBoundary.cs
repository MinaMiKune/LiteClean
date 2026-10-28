namespace LiteClean.Core.Security;

public enum PathBoundaryErrorCode
{
    None,
    TempVariableEmpty,
    NotAbsolutePath,
    InvalidPath,
    NetworkOrDevicePath,
    NotFound,
    AccessDenied,
    NotDirectory,
    ReparsePoint,
    NotLocalFixedDrive,
    ProtectedLocation,
    Unreadable,
    OutsideRoot,
    RootItself,
    InvalidName,
}

public sealed record PathBoundaryFailure(PathBoundaryErrorCode Code, string Message);

/// <summary>
/// An immutable, canonicalized cleanup boundary. Production code obtains one
/// only from the TEMP environment variable through <see cref="TempRootResolver"/>.
/// Every candidate path is normalized and checked against this boundary again
/// immediately before it is used.
/// </summary>
public sealed class PathBoundary
{
    internal PathBoundary(string inputPath, string rootPath)
    {
        InputPath = inputPath;
        RootPath = rootPath;
    }

    /// <summary>The value supplied by TEMP, before canonicalization.</summary>
    public string InputPath { get; }

    /// <summary>Canonical (final) path of the cleanup root, without a trailing separator.</summary>
    public string RootPath { get; }

    public bool TryResolveChild(
        string candidate,
        out string normalizedPath,
        out PathBoundaryFailure? failure)
    {
        normalizedPath = string.Empty;

        if (string.IsNullOrWhiteSpace(candidate))
        {
            failure = new PathBoundaryFailure(PathBoundaryErrorCode.InvalidPath, "路径为空。");
            return false;
        }

        if (candidate.Contains('\0', StringComparison.Ordinal))
        {
            failure = new PathBoundaryFailure(PathBoundaryErrorCode.InvalidPath, "路径包含 NUL 字符。");
            return false;
        }

        if (PathUtil.HasUnsafeRawSegments(candidate))
        {
            failure = new PathBoundaryFailure(
                PathBoundaryErrorCode.InvalidName,
                "路径包含以点或空格结尾的名称段，或非法字符。");
            return false;
        }

        string full;
        try
        {
            if (!Path.IsPathFullyQualified(candidate))
            {
                failure = new PathBoundaryFailure(PathBoundaryErrorCode.NotAbsolutePath, "路径不是绝对路径。");
                return false;
            }

            full = PathUtil.NormalizeFullPath(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            failure = new PathBoundaryFailure(PathBoundaryErrorCode.InvalidPath, "路径格式无效或过长。");
            return false;
        }

        if (PathUtil.IsUncOrDevicePath(full))
        {
            failure = new PathBoundaryFailure(PathBoundaryErrorCode.NetworkOrDevicePath, "不允许 UNC 或设备路径。");
            return false;
        }

        if (!PathUtil.IsDescendantOrSelf(full, RootPath))
        {
            failure = new PathBoundaryFailure(PathBoundaryErrorCode.OutsideRoot, "路径位于临时目录边界之外。");
            return false;
        }

        if (PathUtil.PathsEqual(full, RootPath))
        {
            failure = new PathBoundaryFailure(PathBoundaryErrorCode.RootItself, "临时目录根目录本身不可删除。");
            return false;
        }

        if (!PathUtil.TryGetRelativeSegments(full, RootPath, out var segments) ||
            PathUtil.HasUnsafeNameSegments(segments))
        {
            failure = new PathBoundaryFailure(PathBoundaryErrorCode.InvalidName, "路径包含不安全的名称段。");
            return false;
        }

        if (full.Length >= 32760)
        {
            failure = new PathBoundaryFailure(PathBoundaryErrorCode.InvalidPath, "路径超过 Windows 支持的最大长度。");
            return false;
        }

        normalizedPath = full;
        failure = null;
        return true;
    }
}
