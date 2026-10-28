using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LiteClean.Core.Native;

/// <summary>
/// Minimal, audited Win32 interop used by the scanner and cleaner.
/// All handles are opened with FILE_FLAG_OPEN_REPARSE_POINT and all deletes are
/// performed through the handle rather than by path, which closes the usual
/// check-then-delete race against symbolic links/junctions.
/// </summary>
internal static class NativeMethods
{
    public const uint DeleteAccess = 0x00010000;
    public const uint FileReadAttributes = 0x00000080;
    public const uint Synchronize = 0x00100000;

    public const uint FileShareRead = 0x00000001;
    public const uint FileShareWrite = 0x00000002;
    public const uint FileShareDelete = 0x00000004;

    public const uint OpenExisting = 3;
    public const uint FileFlagBackupSemantics = 0x02000000;
    public const uint FileFlagOpenReparsePoint = 0x00200000;

    public const uint FileAttributeReadOnly = 0x00000001;
    public const uint FileAttributeDirectory = 0x00000010;
    public const uint FileAttributeReparsePoint = 0x00000400;
    public const uint InvalidFileAttributes = 0xFFFFFFFF;

    public const int FileDispositionInfoClass = 4;
    public const int FileAttributeTagInfoClass = 9;

    public const int ErrorFileNotFound = 2;
    public const int ErrorPathNotFound = 3;
    public const int ErrorAccessDenied = 5;
    public const int ErrorInvalidHandle = 6;
    public const int ErrorNotSameDevice = 17;
    public const int ErrorSharingViolation = 32;
    public const int ErrorLockViolation = 33;
    public const int ErrorFileExists = 80;
    public const int ErrorInvalidParameter = 87;
    public const int ErrorDirectoryNotEmpty = 145;
    public const int ErrorBusy = 170;
    public const int ErrorNotAReparsePoint = 4390;

    public const uint FinalPathNameNormalized = 0x0;
    public const uint FinalPathVolumeNameDos = 0x0;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out ByHandleFileInformation lpFileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileInformationByHandleExAttributeTag(
        SafeFileHandle hFile,
        int fileInformationClass,
        out FileAttributeTagInfo lpFileInformation,
        uint dwBufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetFileInformationByHandle(
        SafeFileHandle hFile,
        int fileInformationClass,
        ref FileDispositionInfo lpFileInformation,
        uint dwBufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle hFile,
        [Out] char[] lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileSizeEx(SafeFileHandle hFile, out long lpFileSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern uint GetFileAttributesW(string lpFileName);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;

        public readonly long FileSize => ((long)FileSizeHigh << 32) | FileSizeLow;
        public readonly ulong FileIndex => ((ulong)FileIndexHigh << 32) | FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileDispositionInfo
    {
        public int DeleteFile;
    }
}
