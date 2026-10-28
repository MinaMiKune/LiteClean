namespace LiteClean.Core.Models;

public enum ItemKind
{
    File,
    Directory,
}

/// <summary>
/// Why an item was not put into the cleanup candidate set, or why a candidate
/// was not deleted. The set is deliberately explicit; unknown cases are
/// reported as <see cref="Unknown"/> and never treated as safe.
/// </summary>
public enum SkipReason
{
    Recent,
    ReparsePoint,
    InUse,
    AccessDenied,
    ReadOnly,
    StateChanged,
    OutsideBoundary,
    InvalidPath,
    Unreadable,
    NotFound,
    DirectoryNotEmpty,
    IoError,
    Unknown,
}

public readonly record struct FileIdentity(uint VolumeSerialNumber, ulong FileIndex)
{
    public bool IsEmpty => VolumeSerialNumber == 0 && FileIndex == 0;

    public override string ToString() => $"{VolumeSerialNumber:X8}:{FileIndex:X16}";
}

public sealed record SkippedItem(
    string RelativePath,
    ItemKind Kind,
    SkipReason Reason,
    int Win32Error = 0);

public sealed record ScanCandidate(
    string FullPath,
    string RelativePath,
    ItemKind Kind,
    long SizeBytes,
    long LastWriteFileTimeUtc,
    FileIdentity Identity)
{
    public DateTime LastWriteUtc =>
        LastWriteFileTimeUtc <= 0
            ? DateTime.MinValue
            : DateTime.FromFileTimeUtc(LastWriteFileTimeUtc);

    public bool IsFile => Kind == ItemKind.File;
}
