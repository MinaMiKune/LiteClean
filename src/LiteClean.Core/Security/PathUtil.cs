using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using LiteClean.Core.Models;
using LiteClean.Core.Native;

namespace LiteClean.Core.Security;

internal static class PathUtil
{
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();
    private static readonly char[] Separators = ['\\', '/'];

    public static string NormalizeFullPath(string path)
    {
        var full = Path.GetFullPath(path);
        full = StripExtendedPrefix(full);
        return TrimTrailingSeparators(full);
    }

    public static string StripExtendedPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }

        if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            return path[4..];
        }

        return path;
    }

    public static string ToExtendedPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return @"\\?\UNC\" + path[2..];
        }

        return @"\\?\" + path;
    }

    public static string TrimTrailingSeparators(string path)
    {
        var root = Path.GetPathRoot(path);
        if (!string.IsNullOrEmpty(root) && path.Length <= root.Length)
        {
            return path;
        }

        return path.TrimEnd(Separators);
    }

    public static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    public static bool IsDescendantOrSelf(string candidate, string root)
    {
        if (PathsEqual(candidate, root))
        {
            return true;
        }

        var prefix = root.EndsWith('\\') || root.EndsWith('/')
            ? root
            : root + Path.DirectorySeparatorChar;

        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDriveRoot(string path)
    {
        var root = Path.GetPathRoot(path);
        return !string.IsNullOrEmpty(root) &&
               string.Equals(TrimTrailingSeparators(path), TrimTrailingSeparators(root), StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsUncOrDevicePath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) ||
        path.StartsWith("//", StringComparison.Ordinal);

    public static bool TryGetRelativeSegments(string path, string root, [NotNullWhen(true)] out string[]? segments)
    {
        segments = null;
        if (!IsDescendantOrSelf(path, root) || PathsEqual(path, root))
        {
            return false;
        }

        var remainder = path[root.Length..].TrimStart(Separators);
        if (remainder.Length == 0)
        {
            return false;
        }

        segments = remainder.Split(Separators, StringSplitOptions.None);
        return segments.Length > 0;
    }

    public static bool HasUnsafeNameSegments(string[] segments)
    {
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment.Length > 255)
            {
                return true;
            }

            if (segment is "." or "..")
            {
                return true;
            }

            if (segment.EndsWith(' ') || segment.EndsWith('.'))
            {
                return true;
            }

            if (segment.IndexOfAny(InvalidFileNameChars) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Validates the raw string before Path.GetFullPath, because Windows path
    /// normalization silently trims trailing dots and spaces and could otherwise
    /// alias two different-looking names.
    /// </summary>
    public static bool HasUnsafeRawSegments(string path)
    {
        var stripped = StripExtendedPrefix(path);
        if (stripped.Length >= 2 && char.IsLetter(stripped[0]) && stripped[1] == ':')
        {
            stripped = stripped[2..];
        }

        var segments = stripped.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment is "." or "..")
            {
                continue;
            }

            if (segment.Length > 255)
            {
                return true;
            }

            if (segment.EndsWith(' ') || segment.EndsWith('.'))
            {
                return true;
            }

            if (segment.IndexOfAny(InvalidFileNameChars) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    public static FileIdentity GetIdentity(in NativeMethods.ByHandleFileInformation info) =>
        new(info.VolumeSerialNumber, info.FileIndex);

    public static bool TryGetFinalPath(SafeFileHandle handle, out string finalPath, out int error)
    {
        finalPath = string.Empty;
        error = 0;
        var buffer = new char[32768];
        var length = NativeMethods.GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, NativeMethods.FinalPathNameNormalized);
        if (length == 0)
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }

        if (length >= buffer.Length)
        {
            error = 206; // ERROR_FILENAME_EXCED_RANGE
            return false;
        }

        var raw = new string(buffer, 0, (int)length);
        var stripped = StripExtendedPrefix(raw);
        if (stripped.StartsWith("UNC\\", StringComparison.OrdinalIgnoreCase))
        {
            stripped = @"\\" + stripped[4..];
        }

        try
        {
            finalPath = NormalizeFullPath(stripped);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = 161; // ERROR_BAD_PATHNAME
            return false;
        }
    }
}
