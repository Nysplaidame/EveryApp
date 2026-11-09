using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using EveryApp.Core.FileSystem;
using EveryApp.Core.Power;
using EveryApp.Core.Tests.Fakes;

namespace EveryApp.Core.Tests.FileSystem;

/// <summary>Round-2 security checks on the real file system: final paths, verified deletes, renames that never copy, filtered permissions, streams.</summary>
public sealed class WindowsFileSystemSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "everyapp-fs-safety-" + Guid.NewGuid().ToString("N"));
    private readonly WindowsFileSystem _fs = new();

    public WindowsFileSystemSafetyTests() => Directory.CreateDirectory(_root);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A junction (no special rights needed, unlike symbolic links).</summary>
    internal static void MakeJunction(string link, string target)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        p.WaitForExit();
        Assert.True(Directory.Exists(link), "Could not create a junction for the test.");
    }

    private string Dir(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    [Fact]
    public void GetFinalPath_ThroughAJunction_IsTheTarget()
    {
        var target = Dir("target");
        File.WriteAllText(Path.Combine(target, "a.txt"), "a");
        MakeJunction(Path.Combine(_root, "link"), target);

        var final = _fs.GetFinalPath(Path.Combine(_root, "link", "a.txt"));

        Assert.Equal(_fs.GetFinalPath(target) + @"\a.txt", final, ignoreCase: true);
        Assert.StartsWith(@"\\?\", final, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PartHandle_FinalPath_ShowsWhereTheFileReallyIs_AndDeleteOnCloseRemovesIt()
    {
        var target = Dir("target");
        MakeJunction(Path.Combine(_root, "link"), target);

        var writer = _fs.CreateWrite(Path.Combine(_root, "link", "p.ferrypart"), 0, unbuffered: false);
        string final;
        try
        {
            await writer.WriteAsync(new byte[] { 1, 2, 3 }, 0, Ct);
            final = writer.GetFinalPath();
            writer.DeleteOnClose();
        }
        finally
        {
            await writer.DisposeAsync();
        }

        Assert.Equal(_fs.GetFinalPath(target) + @"\p.ferrypart", final, ignoreCase: true);
        Assert.False(File.Exists(Path.Combine(target, "p.ferrypart")));
    }

    [Fact]
    public async Task PartHandle_Unbuffered_StillWritesThroughTheDeleteCapableHandle()
    {
        var path = Path.Combine(_root, "big.ferrypart");
        var data = new byte[3 * 4096];
        new Random(1).NextBytes(data);
        var writer = _fs.CreateWrite(path, data.Length, unbuffered: true);
        await using (writer.ConfigureAwait(false))
        {
            await writer.WriteAsync(data, 0, Ct);
            await writer.FlushAsync();
        }

        Assert.Equal(data, File.ReadAllBytes(path));
    }

    [Fact]
    public void DeleteVerified_Matching_DeletesEvenReadOnly()
    {
        var path = Path.Combine(_root, "a.txt");
        File.WriteAllText(path, "abc");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        var e = _fs.GetEntry(path);

        Assert.True(_fs.DeleteVerified(path, e.FileId, e.VolumeSerial, e.Length, e.LastWriteTimeUtc, _root));
        Assert.False(File.Exists(path));
    }

    public static TheoryData<string> Mismatches() => ["id", "length", "time", "root"];

    [Theory]
    [MemberData(nameof(Mismatches))]
    public void DeleteVerified_AnyMismatch_KeepsTheFile(string what)
    {
        var path = Path.Combine(_root, "a.txt");
        File.WriteAllText(path, "abc");
        var e = _fs.GetEntry(path);
        var other = Dir("elsewhere");

        var deleted = _fs.DeleteVerified(
            path,
            what == "id" ? e.FileId + 1 : e.FileId,
            e.VolumeSerial,
            what == "length" ? e.Length + 1 : e.Length,
            what == "time" ? e.LastWriteTimeUtc.AddSeconds(1) : e.LastWriteTimeUtc,
            what == "root" ? other : _root);

        Assert.False(deleted);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void DeleteEmptyFolderVerified_OnlyEmptyAndSameFolder()
    {
        var folder = Dir("f");
        var e = _fs.GetEntry(folder);
        File.WriteAllText(Path.Combine(folder, "x"), "x");

        Assert.False(_fs.DeleteEmptyFolderVerified(folder, e.FileId, e.VolumeSerial, _root)); // not empty
        File.Delete(Path.Combine(folder, "x"));
        Assert.False(_fs.DeleteEmptyFolderVerified(folder, e.FileId + 1, e.VolumeSerial, _root)); // another folder
        Assert.True(Directory.Exists(folder));
        Assert.True(_fs.DeleteEmptyFolderVerified(folder, e.FileId, e.VolumeSerial, _root));
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void DeleteEmptyFolderVerified_Junction_RemovesTheLinkNotTheTarget()
    {
        var target = Dir("target");
        File.WriteAllText(Path.Combine(target, "keep.txt"), "keep");
        var link = Path.Combine(_root, "link");
        MakeJunction(link, target);
        var e = _fs.GetEntry(link);

        Assert.True(_fs.DeleteEmptyFolderVerified(link, e.FileId, e.VolumeSerial, _root));
        Assert.False(Directory.Exists(link));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "keep.txt")));
    }

    [Fact]
    public void Move_AcrossVolumes_NeverCopies()
    {
        var otherVolume = Path.Combine(AppContext.BaseDirectory, "move-probe-" + Guid.NewGuid().ToString("N"));
        Assert.SkipWhen(
            string.Equals(Path.GetPathRoot(otherVolume), Path.GetPathRoot(_root), StringComparison.OrdinalIgnoreCase),
            "The temp folder and the test folder are on the same volume.");
        var path = Path.Combine(_root, "a.txt");
        File.WriteAllText(path, "abc");

        var ex = Assert.Throws<FileSystemException>(() => _fs.Move(path, otherVolume, overwrite: false));

        Assert.Equal(IoErrorKind.NotSameDevice, ex.Kind);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(otherVolume));
    }

    [Fact]
    public void GetVolumeId_SameVolume_SameId_NetworkIsUnknown()
    {
        var a = _fs.GetVolumeId(_root);
        Assert.NotNull(a);
        Assert.StartsWith(@"\\?\Volume{", a, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(a, _fs.GetVolumeId(Path.Combine(_root, "not", "there", "yet.txt")));
        Assert.Null(_fs.GetVolumeId(@"\\server\share\x"));
    }

    [Fact]
    public void CopyAlternateStreams_MarkerOnly_CopiesJustZoneIdentifier()
    {
        var src = Path.Combine(_root, "src.txt");
        var dst = Path.Combine(_root, "dst.txt");
        File.WriteAllText(src, "main");
        File.WriteAllText(dst, "main");
        File.WriteAllText(src + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        File.WriteAllText(src + ":other", "x");

        _fs.CopyAlternateStreams(src, dst, allStreams: false, Ct);

        Assert.Equal("[ZoneTransfer]\r\nZoneId=3\r\n", File.ReadAllText(dst + ":Zone.Identifier"));
        Assert.False(File.Exists(dst + ":other"));
    }

    [Fact]
    public void CopyAlternateStreams_Cancelled_Throws()
    {
        var src = Path.Combine(_root, "src.txt");
        var dst = Path.Combine(_root, "dst.txt");
        File.WriteAllText(src, "main");
        File.WriteAllText(dst, "main");
        File.WriteAllText(src + ":Zone.Identifier", "z");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => _fs.CopyAlternateStreams(src, dst, allStreams: true, cts.Token));
    }

    [Theory]
    [InlineData(":Zone.Identifier:$DATA", true)]
    [InlineData(":a b:$DATA", true)]
    [InlineData("::$DATA", false)]
    [InlineData(":a:b:$DATA", false)]
    [InlineData(@":a\b:$DATA", false)]
    [InlineData(":a/b:$DATA", false)]
    [InlineData(":a:$INDEX_ALLOCATION", false)]
    [InlineData("a:$DATA", false)]
    public void StreamNames_OnlyPlainDataStreamsAccepted(string name, bool accepted) =>
        Assert.Equal(accepted, WindowsFileSystem.IsAcceptedStreamName(name));

    private static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, null);

    private static List<FileSystemAccessRule> Explicit(string path) =>
        new FileInfo(path).GetAccessControl().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();

    [Fact]
    public void CopySecurity_LeavesOutWideAndUnknownEntries()
    {
        var src = Path.Combine(_root, "src.txt");
        var dst = Path.Combine(_root, "dst.txt");
        File.WriteAllText(src, "a");
        File.WriteAllText(dst, "b");
        var info = new FileInfo(src);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(Everyone, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-21-1-2-3-4242"), FileSystemRights.Read, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        info.SetAccessControl(security);

        var result = _fs.CopySecurity(src, dst);

        Assert.Equal(SecurityCopyResult.Copied, result);
        var rules = Explicit(dst);
        Assert.DoesNotContain(rules, r => r.IdentityReference.Equals(Everyone));
        Assert.DoesNotContain(rules, r => r.IdentityReference.Value == "S-1-5-21-1-2-3-4242");
        Assert.Contains(rules, r => r.IdentityReference.Value == "S-1-5-32-545"); // read-only for Users is fine
    }

    [Fact]
    public void CopySecurity_WouldLockTheUserOut_IsReverted()
    {
        var src = Path.Combine(_root, "src.txt");
        var dst = Path.Combine(_root, "dst.txt");
        File.WriteAllText(src, "a");
        File.WriteAllText(dst, "b");
        var me = WindowsIdentity.GetCurrent().User!;
        var info = new FileInfo(src);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.ReadData, AccessControlType.Deny));
        info.SetAccessControl(security);

        var result = _fs.CopySecurity(src, dst);

        Assert.Equal(SecurityCopyResult.Reverted, result);
        Assert.Equal("b", File.ReadAllText(dst));
        Assert.Empty(Explicit(dst));
    }

    /// <summary>Adds an allow entry with a raw access mask (managed rules refuse generic masks, but the file system stores them).</summary>
    private static void AddRawAllowAce(FileSystemSecurity security, SecurityIdentifier sid, uint mask, AceFlags flags)
    {
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        var acl = descriptor.DiscretionaryAcl ?? new RawAcl(GenericAcl.AclRevision, 1);
        acl.InsertAce(0, new CommonAce(flags, AceQualifier.AccessAllowed, unchecked((int)mask), sid, false, null));
        descriptor.DiscretionaryAcl = acl;
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        security.SetSecurityDescriptorBinaryForm(bytes, AccessControlSections.Access);
    }

    [Theory]
    [InlineData(0x10000000u)] // GENERIC_ALL
    [InlineData(0x40000000u)] // GENERIC_WRITE
    [InlineData(0x02000000u)] // MAXIMUM_ALLOWED
    public void CopySecurity_GenericRightsForEveryone_AreLeftOut_OnAFile(uint mask)
    {
        var src = Path.Combine(_root, "g-src.txt");
        var dst = Path.Combine(_root, "g-dst.txt");
        File.WriteAllText(src, "a");
        File.WriteAllText(dst, "b");
        var info = new FileInfo(src);
        var security = info.GetAccessControl(AccessControlSections.Access);
        AddRawAllowAce(security, Everyone, mask, AceFlags.None);
        info.SetAccessControl(security);

        _fs.CopySecurity(src, dst);

        Assert.DoesNotContain(Explicit(dst), r => r.IdentityReference.Equals(Everyone));
    }

    [Theory]
    [InlineData(0x10000000u)]
    [InlineData(0x40000000u)]
    [InlineData(0x02000000u)]
    public void CopySecurity_GenericRightsForEveryone_AreLeftOut_OnAnInheritOnlyFolderEntry(uint mask)
    {
        var src = Path.Combine(_root, "g-srcdir");
        var dst = Path.Combine(_root, "g-dstdir");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        var info = new DirectoryInfo(src);
        var security = info.GetAccessControl(AccessControlSections.Access);
        AddRawAllowAce(security, Everyone, mask, AceFlags.ContainerInherit | AceFlags.ObjectInherit | AceFlags.InheritOnly);
        info.SetAccessControl(security);

        _fs.CopySecurity(src, dst);

        Assert.DoesNotContain(
            new DirectoryInfo(dst).GetAccessControl().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
            r => r.IdentityReference.Equals(Everyone));
    }

    [Theory]
    [InlineData("S-1-5-4")] // INTERACTIVE
    [InlineData("S-1-5-32-546")] // Guests
    [InlineData("S-1-5-21-1111-2222-3333-513")] // Domain Users
    public void BroadGroups_IncludeInteractiveGuestsAndDomainUsers(string sid) =>
        Assert.True(WindowsFileSystem.IsBroadGroup(new SecurityIdentifier(sid)));

    [Theory]
    [InlineData("S-1-5-21-1111-2222-3333-1001")]
    [InlineData("S-1-5-18")]
    [InlineData("S-1-5-32-544")]
    public void BroadGroups_DoNotIncludeOrdinaryAccounts(string sid) =>
        Assert.False(WindowsFileSystem.IsBroadGroup(new SecurityIdentifier(sid)));

    [Fact]
    public void CopySecurity_RevertStillLocksTheUserOut_ReturnsAWarning()
    {
        var folder = Path.Combine(_root, "denied-parent");
        Directory.CreateDirectory(folder);
        var dst = Path.Combine(folder, "dst.txt");
        File.WriteAllText(dst, "b");
        var src = Path.Combine(_root, "src2.txt");
        File.WriteAllText(src, "a");
        var me = WindowsIdentity.GetCurrent().User!;

        // The folder passes a deny-read entry down to files, so going back to inherited permissions cannot help.
        var dirInfo = new DirectoryInfo(folder);
        var dirSecurity = dirInfo.GetAccessControl();
        dirSecurity.AddAccessRule(new FileSystemAccessRule(
            me, FileSystemRights.ReadData, InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Deny));
        dirInfo.SetAccessControl(dirSecurity);
        var srcInfo = new FileInfo(src);
        var srcSecurity = srcInfo.GetAccessControl();
        srcSecurity.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.ReadData, AccessControlType.Deny));
        srcInfo.SetAccessControl(srcSecurity);

        var result = _fs.CopySecurity(src, dst);

        Assert.Equal(SecurityCopyResult.StillLockedOut, result);
    }

    [Fact]
    public void ProcessElevation_IsReadable()
    {
        var elevated = ProcessElevation.IsElevated();
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Assert.False(elevated); // a non-admin token is never elevated
        }
    }
}

/// <summary>The in-memory fake follows the same rules (used by the engine tests).</summary>
public sealed class InMemorySafetyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CreateWrite_ThroughAJunction_LandsInTheTarget_AndFinalPathSaysSo()
    {
        var fs = new InMemoryFileSystem();
        fs.CreateDirectory(@"D:\outside");
        fs.AddJunction(@"D:\dst\link", @"D:\outside");

        var writer = fs.CreateWrite(@"D:\dst\link\p.ferrypart", 0, false);
        await writer.WriteAsync(new byte[] { 1 }, 0, Ct);
        Assert.Equal(@"\\?\D:\outside\p.ferrypart", writer.GetFinalPath());
        writer.DeleteOnClose();
        await writer.DisposeAsync();

        Assert.False(fs.Exists(@"D:\outside\p.ferrypart"));
    }

    [Fact]
    public void Move_AcrossVolumes_Refused()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(@"C:\a.txt", [1]);
        fs.CreateDirectory(@"D:\");

        Assert.Equal(IoErrorKind.NotSameDevice, Assert.Throws<FileSystemException>(() => fs.Move(@"C:\a.txt", @"D:\a.txt", false)).Kind);

        fs.VolumeIds['S'] = fs.GetVolumeId(@"C:\")!;
        fs.CreateDirectory(@"S:\");
        fs.Move(@"C:\a.txt", @"S:\a.txt", false); // same volume under another letter
        Assert.True(fs.Exists(@"S:\a.txt"));
    }

    [Fact]
    public void DeleteVerified_ChangedFile_Kept()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(@"C:\src\a.txt", [1]);
        var e = fs.GetEntry(@"C:\src\a.txt");
        fs.Delete(@"C:\src\a.txt");
        fs.AddFile(@"C:\src\a.txt", [1]); // same name, new file

        Assert.False(fs.DeleteVerified(@"C:\src\a.txt", e.FileId, e.VolumeSerial, e.Length, e.LastWriteTimeUtc, @"C:\src"));
        Assert.True(fs.Exists(@"C:\src\a.txt"));
    }
}
