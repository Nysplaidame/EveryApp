using System.Security.AccessControl;
using System.Security.Principal;
using EveryApp.Core.FileSystem;
using EveryApp.Core.Paths;
using EveryApp.Core.Power;
using EveryApp.Core.Tests.Fakes;

namespace EveryApp.Core.Tests.FileSystem;

/// <summary>Permissions, extra data streams and block cloning on the real file system (ruling R1), plus the keep-awake hold and walker links.</summary>
public sealed class WindowsFileSystemExtrasTests : IDisposable
{
    private static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, null);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "everyapp-fs-extras-" + Guid.NewGuid().ToString("N"));
    private readonly WindowsFileSystem _fs = new();

    public WindowsFileSystemExtrasTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static bool HasExplicitEveryoneRule(FileSystemSecurity security) =>
        security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Any(r => r.IdentityReference.Equals(Everyone) && r.FileSystemRights.HasFlag(FileSystemRights.ReadExtendedAttributes));

    private static void AddEveryoneRule(FileInfo file)
    {
        var security = file.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(Everyone, FileSystemRights.ReadExtendedAttributes, AccessControlType.Allow));
        file.SetAccessControl(security);
    }

    [Fact]
    public void CopySecurity_File_CopiesExplicitEntries()
    {
        var src = Path.Combine(_root, "src.txt");
        var dst = Path.Combine(_root, "dst.txt");
        File.WriteAllText(src, "a");
        File.WriteAllText(dst, "b");
        AddEveryoneRule(new FileInfo(src));
        Assert.False(HasExplicitEveryoneRule(new FileInfo(dst).GetAccessControl()));

        _fs.CopySecurity(src, dst);

        Assert.True(HasExplicitEveryoneRule(new FileInfo(dst).GetAccessControl()));
    }

    [Fact]
    public void CopySecurity_Directory_CopiesExplicitEntries()
    {
        var src = Directory.CreateDirectory(Path.Combine(_root, "srcdir"));
        var dst = Directory.CreateDirectory(Path.Combine(_root, "dstdir"));
        var security = src.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(Everyone, FileSystemRights.ReadExtendedAttributes, AccessControlType.Allow));
        src.SetAccessControl(security);

        _fs.CopySecurity(src.FullName, dst.FullName);

        Assert.True(HasExplicitEveryoneRule(dst.GetAccessControl()));
    }

    [Fact]
    public void CopySecurity_MissingSource_IsNotFound()
    {
        var dst = Path.Combine(_root, "dst.txt");
        File.WriteAllText(dst, "b");
        var ex = Assert.Throws<FileSystemException>(() => _fs.CopySecurity(Path.Combine(_root, "missing.txt"), dst));
        Assert.Equal(IoErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public void CopyAlternateStreams_CopiesEveryNamedStream()
    {
        var src = Path.Combine(_root, "src.txt");
        var dst = Path.Combine(_root, "dst.txt");
        File.WriteAllText(src, "main");
        File.WriteAllText(dst, "main");
        File.WriteAllText(src + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        File.WriteAllBytes(src + ":big", Enumerable.Range(0, 200_000).Select(i => (byte)i).ToArray());

        _fs.CopyAlternateStreams(src, dst, allStreams: true, TestContext.Current.CancellationToken);

        Assert.Equal("[ZoneTransfer]\r\nZoneId=3\r\n", File.ReadAllText(dst + ":Zone.Identifier"));
        Assert.Equal(File.ReadAllBytes(src + ":big"), File.ReadAllBytes(dst + ":big"));
        Assert.Equal("main", File.ReadAllText(dst));
    }

    [Fact]
    public void CopyAlternateStreams_NoStreams_DoesNothing()
    {
        var src = Path.Combine(_root, "src.txt");
        var dst = Path.Combine(_root, "dst.txt");
        File.WriteAllText(src, "main");
        File.WriteAllText(dst, "other");

        _fs.CopyAlternateStreams(src, dst, allStreams: true, TestContext.Current.CancellationToken);

        Assert.Equal("other", File.ReadAllText(dst));
    }

    [Fact]
    public void TryCloneBlocks_NotReFS_ReturnsFalse_AndCreatesNothing()
    {
        Assert.SkipWhen(IsReFS(_root), "The temp folder is on ReFS, where cloning works.");
        var src = Path.Combine(_root, "src.bin");
        var dst = Path.Combine(_root, "dst.bin");
        File.WriteAllBytes(src, new byte[10_000]);

        Assert.False(_fs.TryCloneBlocks(src, dst));
        Assert.False(File.Exists(dst));
    }

    [Fact]
    public void TryCloneBlocks_TargetExists_ReturnsFalse_AndKeepsIt()
    {
        var src = Path.Combine(_root, "src.bin");
        var dst = Path.Combine(_root, "dst.bin");
        File.WriteAllBytes(src, new byte[10_000]);
        File.WriteAllText(dst, "keep me");

        Assert.False(_fs.TryCloneBlocks(src, dst));
        Assert.Equal("keep me", File.ReadAllText(dst));
    }

    [Fact]
    public void TryCloneBlocks_MissingSource_ReturnsFalse()
    {
        Assert.False(_fs.TryCloneBlocks(Path.Combine(_root, "missing.bin"), Path.Combine(_root, "dst.bin")));
        Assert.False(File.Exists(Path.Combine(_root, "dst.bin")));
    }

    [Fact]
    public void KeepAwake_HoldsUntilEveryLeaseIsReleased()
    {
        var before = KeepAwake.ActiveHolds;
        var a = KeepAwake.Acquire();
        var b = KeepAwake.Acquire();
        Assert.Equal(before + 2, KeepAwake.ActiveHolds);
        a.Dispose();
        a.Dispose(); // twice is harmless
        Assert.Equal(before + 1, KeepAwake.ActiveHolds);
        b.Dispose();
        Assert.Equal(before, KeepAwake.ActiveHolds);
    }

    [Fact]
    public void Walker_OnLink_ReceivesFolderLinksInsteadOfSkips()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(@"C:\other\x.txt", [1]);
        fs.AddFile(@"C:\src\a.txt", [1]);
        fs.AddJunction(@"C:\src\link", @"C:\other");
        var links = new List<WalkItem>();
        var skips = new List<WalkSkip>();

        var items = new TreeWalker(fs, followReparsePoints: false, []).Walk(@"C:\src", skips.Add, links.Add).ToList();

        Assert.Equal(["a.txt"], items.Select(i => i.RelativePath));
        Assert.Equal(["link"], links.Select(l => l.RelativePath));
        Assert.Empty(skips);
    }

    private static bool IsReFS(string path)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(path)!).DriveFormat.Equals("ReFS", StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }
}
