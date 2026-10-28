using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using LiteClean.Core.Native;

namespace LiteClean.Core.Security;

internal static class Win32File
{
    public static SafeFileHandle OpenFileForInspection(string path, out int error) =>
        OpenHandle(
            path,
            NativeMethods.DeleteAccess | NativeMethods.FileReadAttributes | NativeMethods.Synchronize,
            shareMode: 0,
            NativeMethods.FileFlagOpenReparsePoint,
            out error);

    public static SafeFileHandle OpenFileForDelete(string path, out int error) =>
        OpenHandle(
            path,
            NativeMethods.DeleteAccess | NativeMethods.FileReadAttributes | NativeMethods.Synchronize,
            shareMode: 0,
            NativeMethods.FileFlagOpenReparsePoint,
            out error);

    public static SafeFileHandle OpenDirectoryForMetadata(string path, out int error) =>
        OpenHandle(
            path,
            NativeMethods.FileReadAttributes | NativeMethods.Synchronize,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite | NativeMethods.FileShareDelete,
            NativeMethods.FileFlagBackupSemantics | NativeMethods.FileFlagOpenReparsePoint,
            out error);

    /// <summary>Opens a directory and follows reparse points in the final component.</summary>
    public static SafeFileHandle OpenDirectoryResolved(string path, out int error) =>
        OpenHandle(
            path,
            NativeMethods.FileReadAttributes | NativeMethods.Synchronize,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite | NativeMethods.FileShareDelete,
            NativeMethods.FileFlagBackupSemantics,
            out error);

    public static SafeFileHandle OpenDirectoryForDelete(string path, out int error) =>
        OpenHandle(
            path,
            NativeMethods.DeleteAccess | NativeMethods.FileReadAttributes | NativeMethods.Synchronize,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite | NativeMethods.FileShareDelete,
            NativeMethods.FileFlagBackupSemantics | NativeMethods.FileFlagOpenReparsePoint,
            out error);

    private static SafeFileHandle OpenHandle(string path, uint access, uint shareMode, uint flags, out int error)
    {
        error = 0;
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0', StringComparison.Ordinal))
        {
            error = NativeMethods.ErrorInvalidParameter;
            return new SafeFileHandle(IntPtr.Zero, ownsHandle: true);
        }

        var handle = NativeMethods.CreateFileW(
            PathUtil.ToExtendedPath(path),
            access,
            shareMode,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            flags,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            error = Marshal.GetLastWin32Error();
        }

        return handle;
    }

    public static bool TryGetInfo(SafeFileHandle handle, out NativeMethods.ByHandleFileInformation info, out int error)
    {
        if (!NativeMethods.GetFileInformationByHandle(handle, out info))
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }

        error = 0;
        return true;
    }

    public static bool TryGetAttributeTag(
        SafeFileHandle handle,
        out NativeMethods.FileAttributeTagInfo info,
        out int error)
    {
        if (!NativeMethods.GetFileInformationByHandleExAttributeTag(
                handle,
                NativeMethods.FileAttributeTagInfoClass,
                out info,
                (uint)Marshal.SizeOf<NativeMethods.FileAttributeTagInfo>()))
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }

        error = 0;
        return true;
    }

    public static bool TryGetSize(SafeFileHandle handle, out long size, out int error)
    {
        if (!NativeMethods.GetFileSizeEx(handle, out size))
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }

        error = 0;
        return true;
    }

    public static bool TryMarkForDeletion(SafeFileHandle handle, out int error)
    {
        var disposition = new NativeMethods.FileDispositionInfo { DeleteFile = 1 };
        if (!NativeMethods.SetFileInformationByHandle(
                handle,
                NativeMethods.FileDispositionInfoClass,
                ref disposition,
                (uint)Marshal.SizeOf<NativeMethods.FileDispositionInfo>()))
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }

        error = 0;
        return true;
    }

    public static bool IsReparsePoint(uint attributes) =>
        (attributes & NativeMethods.FileAttributeReparsePoint) != 0;

    public static bool IsDirectory(uint attributes) =>
        (attributes & NativeMethods.FileAttributeDirectory) != 0;

    public static bool IsReadOnly(uint attributes) =>
        (attributes & NativeMethods.FileAttributeReadOnly) != 0;

    /// <summary>
    /// Returns true when a path still exists (including when it has been
    /// replaced by a reparse point). Errors other than "not found" are treated
    /// as "still exists" so that a deletion is never reported as successful on
    /// ambiguous evidence.
    /// </summary>
    public static bool PathStillExists(string path)
    {
        var attributes = NativeMethods.GetFileAttributesW(PathUtil.ToExtendedPath(path));
        if (attributes != NativeMethods.InvalidFileAttributes)
        {
            return true;
        }

        var error = Marshal.GetLastWin32Error();
        return error is not (NativeMethods.ErrorFileNotFound or NativeMethods.ErrorPathNotFound);
    }
}
