using EveryApp.Core.FileSystem;

namespace Ferry.Engine;

/// <summary>Every plain-language message the copy engine shows to people. Keep wording short and free of jargon.</summary>
public static class CopyMessages
{
    public const string SourceChanged = "The file changed while it was being copied";
    public const string CouldNotRead = "The disk couldn't read part of this file";
    public const string CouldNotWrite = "The disk couldn't save part of this file";
    public const string WrongSize = "The copy didn't come out the same size as the original";
    public const string NetworkLost = "The network connection was lost";
    public const string SourceMissing = "The file couldn't be found";
    public const string DestinationFolderMissing = "The folder to copy into couldn't be found";
    public const string NoAccessToSource = "Windows didn't allow this file to be opened";
    public const string NoAccessToDestination = "Windows didn't allow saving to this place";
    public const string InUse = "Another program is using this file";
    public const string AlreadyExists = "A file with this name is already there";
    public const string PathTooLong = "The file's location is too long for Windows";
    public const string InvalidName = "This name can't be used here";
    public const string Unknown = "Something went wrong while copying this file";
    public const string DatesAndAttributesNotKept = "The file was copied, but its dates and attributes couldn't be kept";

    public static string NotEnoughSpace(string drive) => $"There isn't enough space on {drive}";

    /// <summary>Message for a disk failure. <paramref name="whileWriting"/> tells a failure at the destination from one at the source.</summary>
    public static string For(IoErrorKind kind, bool whileWriting, string destinationDrive) => kind switch
    {
        IoErrorKind.DiskFull => NotEnoughSpace(destinationDrive),
        IoErrorKind.BadBlock => whileWriting ? CouldNotWrite : CouldNotRead,
        IoErrorKind.NetworkLost => NetworkLost,
        IoErrorKind.NotFound => whileWriting ? DestinationFolderMissing : SourceMissing,
        IoErrorKind.AccessDenied => whileWriting ? NoAccessToDestination : NoAccessToSource,
        IoErrorKind.SharingViolation => InUse,
        IoErrorKind.AlreadyExists => AlreadyExists,
        IoErrorKind.PathTooLong => PathTooLong,
        IoErrorKind.InvalidName => InvalidName,
        _ => Unknown,
    };
}
