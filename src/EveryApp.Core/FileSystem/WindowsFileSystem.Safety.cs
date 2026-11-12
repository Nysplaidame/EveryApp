using System.Buffers;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using EveryApp.Core.Paths;
using EveryApp.Core.Scheduling;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;

namespace EveryApp.Core.FileSystem;

/// <summary>Handle-based safety checks: final paths, verified deletes, volume identity, filtered permissions and data streams.</summary>
public sealed partial class WindowsFileSystem
{
    private const uint DeleteAccess = 0x00010000;
    private const uint ReadControl = 0x00020000;
    private const uint WriteDac = 0x00040000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileReadData = 0x0001; // FILE_LIST_DIRECTORY for folders
    private const int ErrorDirNotEmpty = 145;
    private const string ZoneIdentifierStream = ":Zone.Identifier:$DATA";

    // Generic rights are stored raw in an entry (inherit-only folder entries often use them) and expand to change rights when applied.
    private const FileSystemRights GenericAll = (FileSystemRights)0x10000000;
    private const FileSystemRights GenericWriteRight = (FileSystemRights)0x40000000;
    private const FileSystemRights MaximumAllowed = (FileSystemRights)0x02000000;

    // Rights that let someone change a file or its permissions.
    private const FileSystemRights ChangeRights = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | GenericAll | GenericWriteRight | MaximumAllowed;

    private static readonly ConcurrentDictionary<SecurityIdentifier, bool> KnownSids = new();
    private static readonly SecurityIdentifier OwnerRights = new("S-1-3-4");
    private static readonly SecurityIdentifier[] BroadGroups =
    [
        new(WellKnownSidType.WorldSid, null),
        new(WellKnownSidType.BuiltinUsersSid, null),
        new(WellKnownSidType.AuthenticatedUserSid, null),
        new(WellKnownSidType.AnonymousSid, null),
        new("S-1-5-4"), // INTERACTIVE
        new("S-1-5-32-546"), // Guests
    ];

    public string GetFinalPath(string path)
    {
        try
        {
            using var handle = OpenQuery(Normalize(path), FileReadAttributes);
            return NativeFileApi.GetFinalPath(handle);
        }
        catch (Exception ex) when (IsTranslatable(ex))
        {
            throw ErrorKinds.Translate(path, ex);
        }
    }

    public string? GetVolumeId(string path)
    {
        try
        {
            var plain = StripPrefix(Normalize(path));
            if (plain.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return null; // network share: no volume GUID to compare
            }

            var api = new NativeVolumeApi();
            return api.GetVolumeGuidPath(api.GetVolumeRoot(plain));
        }
        catch (Exception ex) when (IsTranslatable(ex))
        {
            return null;
        }
    }

    public bool DeleteVerified(
        string path, UInt128 expectedFileId, ulong expectedVolumeSerial, long expectedLength, DateTime expectedLastWriteUtc, string? mustBeUnder) =>
        DeleteThroughHandle(
            path,
            folder: false,
            e => e.FileId == expectedFileId && e.VolumeSerial == expectedVolumeSerial && e.Length == expectedLength && e.LastWriteTimeUtc == expectedLastWriteUtc,
            mustBeUnder);

    public bool DeleteFileContained(string path, string mustBeUnder) =>
        DeleteThroughHandle(path, folder: false, static _ => true, mustBeUnder);

    public bool IsContained(string path, string root)
    {
        try
        {
            using var handle = OpenQuery(Normalize(path), FileReadAttributes, openReparsePoint: true);
            return PathSafety.IsSameOrUnderFinal(GetFinalPath(root), NativeFileApi.GetFinalPath(handle));
        }
        catch (Exception ex) when (IsTranslatable(ex))
        {
            throw ErrorKinds.Translate(path, ex);
        }
    }

    public bool DeleteEmptyFolderVerified(string path, UInt128 expectedFileId, ulong expectedVolumeSerial, string? mustBeUnder) =>
        DeleteThroughHandle(path, folder: true, e => e.FileId == expectedFileId && e.VolumeSerial == expectedVolumeSerial, mustBeUnder);

    public SecurityCopyResult CopySecurity(string source, string target)
    {
        try
        {
            var f = Normalize(source);
            var t = Normalize(target);
            var isDirectory = Directory.Exists(f);
            var original = ReadSecurity(f, isDirectory, AccessControlSections.Access | AccessControlSections.Owner);
            var owner = original.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            var currentOwner = ReadSecurity(t, isDirectory, AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier));

            var result = SecurityCopyResult.Copied;
            if (owner is not null && !owner.Equals(currentOwner))
            {
                if (IsKnown(owner))
                {
                    try
                    {
                        WriteSecurity(t, isDirectory, Filtered(original, isDirectory, owner)); // owner and access list in one call
                    }
                    catch (Exception ex) when (ex is PrivilegeNotHeldException or InvalidOperationException or UnauthorizedAccessException)
                    {
                        result = SecurityCopyResult.OwnerNotKept; // nothing was applied; write the access list alone below
                    }
                }
                else
                {
                    result = SecurityCopyResult.OwnerNotKept;
                }
            }

            if (result != SecurityCopyResult.Copied || owner is null || owner.Equals(currentOwner))
            {
                WriteSecurity(t, isDirectory, Filtered(original, isDirectory, owner: null));
            }

            if (!CanStillUse(t, isDirectory))
            {
                // The copied list would lock the user out of their own copy: go back to what the folder passes down.
                var reset = isDirectory ? (FileSystemSecurity)new DirectorySecurity() : new FileSecurity();
                reset.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                WriteSecurity(t, isDirectory, reset);
                return CanStillUse(t, isDirectory) ? SecurityCopyResult.Reverted : SecurityCopyResult.StillLockedOut;
            }

            return result;
        }
        catch (Exception ex) when (IsTranslatable(ex))
        {
            throw ErrorKinds.Translate(target, ex);
        }
    }

    public void CopyAlternateStreams(string source, string target, bool allStreams, CancellationToken ct)
    {
        try
        {
            var f = Normalize(source);
            var t = Normalize(target);
            foreach (var stream in NamedStreams(f))
            {
                if (!IsAcceptedStreamName(stream) || (!allStreams && !stream.Equals(ZoneIdentifierStream, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                ct.ThrowIfCancellationRequested();
                using var reader = File.OpenHandle(f + stream, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var writer = File.OpenHandle(t + stream, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                try
                {
                    long offset = 0;
                    int n;
                    while ((n = RandomAccess.Read(reader, buffer, offset)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        RandomAccess.Write(writer, buffer.AsSpan(0, n), offset);
                        offset += n;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
        catch (Exception ex) when (IsTranslatable(ex))
        {
            throw ErrorKinds.Translate(target, ex);
        }
    }

    /// <summary>Only <c>:name:$DATA</c> with a name free of colons and slashes.</summary>
    internal static bool IsAcceptedStreamName(string stream) => StreamName().IsMatch(stream);

    [GeneratedRegex(@"^:[^:\\/]+:\$DATA$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StreamName();

    /// <summary>A part file: CREATE_NEW, write and delete access, no sharing, overlapped (for RandomAccess), optionally unbuffered.</summary>
    private static SafeFileHandle CreatePartHandle(string longForm, bool unbuffered)
    {
        var flags = FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OVERLAPPED;
        if (unbuffered)
        {
            flags |= FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_NO_BUFFERING | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_WRITE_THROUGH;
        }

        var handle = PInvoke.CreateFile(longForm, GenericWrite | DeleteAccess | FileReadAttributes, 0, null, FILE_CREATION_DISPOSITION.CREATE_NEW, flags, null);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }

        return handle;
    }

    private bool DeleteThroughHandle(string path, bool folder, Func<FileEntry, bool> matches, string? mustBeUnder)
    {
        try
        {
            var p = Normalize(path);

            // Opened without following links (a link is checked and removed as itself) and with delete access, so the checks and the delete
            // are about the very same object.
            using var handle = OpenQuery(p, DeleteAccess | FileReadAttributes, openReparsePoint: true);
            var entry = EntryFromHandle(handle, p);
            if (entry.Attributes.HasFlag(FileAttributes.Directory) != folder || !matches(entry))
            {
                return false;
            }

            if (mustBeUnder is not null && !PathSafety.IsSameOrUnderFinal(GetFinalPath(mustBeUnder), NativeFileApi.GetFinalPath(handle)))
            {
                return false;
            }

            if (!NativeFileApi.TrySetDeleteDisposition(handle, out var error))
            {
                return folder && error == ErrorDirNotEmpty ? false : throw new Win32Exception(error);
            }

            return true;
        }
        catch (Exception ex) when (IsTranslatable(ex))
        {
            throw ErrorKinds.Translate(path, ex);
        }
    }

    /// <summary>The source's explicit entries minus anything unsafe (see <see cref="IFileSystem.CopySecurity"/>), plus the owner if given.</summary>
    private static FileSystemSecurity Filtered(FileSystemSecurity original, bool isDirectory, SecurityIdentifier? owner)
    {
        var copy = isDirectory ? (FileSystemSecurity)new DirectorySecurity() : new FileSecurity();
        copy.SetAccessRuleProtection(original.AreAccessRulesProtected, preserveInheritance: false);
        foreach (FileSystemAccessRule rule in original.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)))
        {
            if (IsSafeToCopy(rule))
            {
                copy.AddAccessRule(rule);
            }
        }

        if (owner is not null)
        {
            copy.SetOwner(owner);
        }

        return copy;
    }

    /// <summary>Groups nearly everyone belongs to, plus Domain Users (any domain's RID 513).</summary>
    internal static bool IsBroadGroup(SecurityIdentifier sid) =>
        Array.Exists(BroadGroups, g => g.Equals(sid))
        || (sid.Value.StartsWith("S-1-5-21-", StringComparison.Ordinal) && sid.Value.EndsWith("-513", StringComparison.Ordinal));

    private static bool IsSafeToCopy(FileSystemAccessRule rule)
    {
        if (rule.IdentityReference is not SecurityIdentifier sid || sid.Equals(OwnerRights) || !IsKnown(sid))
        {
            return false;
        }

        var widens = rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & ChangeRights) != 0;
        return !(widens && IsBroadGroup(sid));
    }

    /// <summary>An account this PC can name (local, domain it trusts, or well known). Unknown and foreign SIDs are left out.</summary>
    private static bool IsKnown(SecurityIdentifier sid) => KnownSids.GetOrAdd(sid, static s =>
    {
        try
        {
            _ = s.Translate(typeof(NTAccount));
            return true;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            return false;
        }
    });

    /// <summary>Whether the current user can still read the item and change its permissions.</summary>
    private static bool CanStillUse(string longForm, bool isDirectory)
    {
        try
        {
            using var handle = OpenQuery(longForm, ReadControl | WriteDac | FileReadData | (isDirectory ? 0 : FileReadAttributes), openReparsePoint: true);
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}
