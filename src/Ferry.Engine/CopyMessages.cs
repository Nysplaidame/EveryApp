using EveryApp.Core.FileSystem;

namespace Ferry.Engine;

/// <summary>Every plain-language message the copy engine shows to people. Keep wording short and free of jargon.</summary>
public static class CopyMessages
{
    public const string SourceChanged = "The file changed while it was being copied";
    public const string CouldNotRead = "The disk couldn't read part of this file";
    public const string CouldNotWrite = "The disk couldn't save part of this file";
    public const string PartFileVanished = "The temporary copy disappeared before it could be finished";
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

    // Tree copy (job level).
    public const string NeedsAdministrator = "Needs administrator: restart this job as administrator";
    public const string PermissionsNotCopied = "The file was copied, but its permissions couldn't be copied";
    public const string StreamsNotCopied = "The file was copied, but its hidden extra data couldn't be copied";
    public const string VerifyMismatch = "The copy didn't match the original when it was checked, so it wasn't kept";
    public const string VerifyCouldNotRead = "The copy couldn't be read back to check it, so it wasn't kept";
    public const string OriginalNotRemoved = "The file was moved, but the original couldn't be removed";
    public const string FolderInTheWay = "A folder with this name is already there";
    public const string FileInTheWay = "A file with this name is already there, so the folder couldn't be created";
    public const string LinkNotMovedAcrossDrives = "This is a link to another folder. Links can't be moved to another drive, so it was left where it is";
    public const string FileLinkNotMovedAcrossDrives = "This is a link to a file. Links can't be moved to another drive, so it was left where it is";
    public const string LinkSkipped = "Skipped: this is a link to another folder.";
    public const string DestinationInsideSource = "The destination is inside the folder being copied, which would copy it into itself";
    public const string SameAsSource = "This is already in the folder you chose";
    public const string StoppedAfterProblem = "Stopped after a problem, as the settings ask";
    public const string ChecksumsNotWritten = "The list of checksums couldn't be saved";
    public const string NoFreeName = "Couldn't find a free name for the copy";

    /// <summary>Destination drive (or share) of a path, as people know it, e.g. "D:".</summary>
    public static string DriveOf(string path)
    {
        try
        {
            var root = Path.GetPathRoot(PathDisplay.ToDisplayPath(path));
            return string.IsNullOrEmpty(root) ? path : root.TrimEnd('\\');
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

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
