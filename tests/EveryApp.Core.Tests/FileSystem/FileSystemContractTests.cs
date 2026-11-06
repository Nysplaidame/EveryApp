using EveryApp.Core.FileSystem;
using EveryApp.Core.Tests.Fakes;

namespace EveryApp.Core.Tests.FileSystem;

/// <summary>The same scenarios run against the real and the in-memory implementation so the fake cannot drift from reality.</summary>
public abstract class FileSystemContractTests : IDisposable
{
    protected abstract IFileSystem Fs { get; }

    protected abstract string Root { get; }

    public virtual void Dispose() => GC.SuppressFinalize(this);

    private string P(string name) => Root + @"\" + name;

    private static async Task Write(IFileSystem fs, string path, byte[] data, bool unbuffered = false)
    {
        await using var w = fs.CreateWrite(path, 0, unbuffered);
        await w.WriteAsync(data, 0, CancellationToken.None);
    }

    private static async Task<byte[]> ReadAll(IFileSystem fs, string path)
    {
        await using var r = fs.OpenRead(path, unbuffered: false);
        var buf = new byte[r.Length];
        var total = 0;
        while (total < buf.Length)
        {
            var n = await r.ReadAsync(buf.AsMemory(total), total, CancellationToken.None);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return buf;
    }

    [Fact]
    public async Task Contract_WriteThenRead_RoundTrips()
    {
        var data = new byte[5000];
        new Random(7).NextBytes(data);
        await Write(Fs, P("rt.bin"), data);
        Assert.Equal(data, await ReadAll(Fs, P("rt.bin")));
        Assert.Equal(5000, Fs.GetEntry(P("rt.bin")).Length);
    }

    [Fact]
    public async Task Contract_CreateWrite_OnExistingPath_ThrowsAlreadyExists_AndKeepsContent()
    {
        await Write(Fs, P("keep.bin"), [1, 2, 3, 4]);
        var ex = Assert.Throws<FileSystemException>(() => Fs.CreateWrite(P("keep.bin"), 100, unbuffered: false));
        Assert.Equal(IoErrorKind.AlreadyExists, ex.Kind);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await ReadAll(Fs, P("keep.bin")));
    }

    [Fact]
    public async Task Contract_Preallocate_DoesNotMoveEndOfFile()
    {
        await using var w = Fs.CreateWrite(P("pre.bin"), 1024 * 1024, unbuffered: false);
        Assert.Equal(0, Fs.GetEntry(P("pre.bin")).Length);
        await w.WriteAsync(new byte[10], 0, CancellationToken.None);
        await w.FlushAsync();
        Assert.Equal(10, Fs.GetEntry(P("pre.bin")).Length);
    }

    [Fact]
    public void Contract_MissingPaths_ThrowNotFound()
    {
        Assert.Equal(IoErrorKind.NotFound, Assert.Throws<FileSystemException>(() => Fs.GetEntry(P("nope"))).Kind);
        Assert.Equal(IoErrorKind.NotFound, Assert.Throws<FileSystemException>(() => Fs.Delete(P("nope"))).Kind);
        Assert.Equal(IoErrorKind.NotFound, Assert.Throws<FileSystemException>(() => Fs.OpenRead(P("nope"), false)).Kind);
    }

    [Fact]
    public async Task Contract_Move_ToExistingWithoutOverwrite_ThrowsAlreadyExists()
    {
        await Write(Fs, P("m1"), [1]);
        await Write(Fs, P("m2"), [2]);
        Assert.Equal(IoErrorKind.AlreadyExists, Assert.Throws<FileSystemException>(() => Fs.Move(P("m1"), P("m2"), overwrite: false)).Kind);
        Fs.Move(P("m1"), P("m2"), overwrite: true);
        Assert.Equal(new byte[] { 1 }, await ReadAll(Fs, P("m2")));
        Assert.Throws<FileSystemException>(() => Fs.GetEntry(P("m1")));
    }

    [Fact]
    public async Task Contract_EnumerateChildren_ListsDirectChildrenOnly()
    {
        Fs.CreateDirectory(P("dir\\sub"));
        await Write(Fs, P("dir\\f1"), [1]);
        await Write(Fs, P("dir\\sub\\deep"), [1]);
        var names = Fs.EnumerateChildren(P("dir")).Select(e => e.FullPath[(e.FullPath.LastIndexOf('\\') + 1)..]).Order().ToList();
        Assert.Equal(["f1", "sub"], names);
    }

    [Fact]
    public async Task Contract_Unbuffered_MisalignedOffset_ThrowsArgumentException()
    {
        var sector = Fs.GetSectorSize(Root);
        await using var w = Fs.CreateWrite(P("u1"), 0, unbuffered: true);
        await Assert.ThrowsAsync<ArgumentException>(async () => await w.WriteAsync(new byte[sector], 1, CancellationToken.None));
    }

    [Fact]
    public async Task Contract_Unbuffered_ShortNonFinalWrite_Throws_AndKeepsExistingData()
    {
        var sector = Fs.GetSectorSize(Root);
        var data = Enumerable.Repeat((byte)0xAB, sector * 2).ToArray();
        await using (var w = Fs.CreateWrite(P("u2"), 0, unbuffered: true))
        {
            await w.WriteAsync(data, 0, CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await w.WriteAsync(new byte[sector / 2], 0, CancellationToken.None));
        }

        Assert.Equal(data, await ReadAll(Fs, P("u2")));
    }

    [Fact]
    public async Task Contract_Unbuffered_ShortFinalWrite_KeepsExactLength()
    {
        var sector = Fs.GetSectorSize(Root);
        var data = new byte[sector + 17];
        new Random(3).NextBytes(data);
        await Write(Fs, P("u3"), data, unbuffered: true);
        Assert.Equal(data, await ReadAll(Fs, P("u3")));
    }

    [Fact]
    public async Task Contract_UseAfterDispose_ThrowsObjectDisposed()
    {
        var w = Fs.CreateWrite(P("d1"), 0, unbuffered: false);
        await w.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await w.WriteAsync(new byte[1], 0, CancellationToken.None));
        await w.DisposeAsync(); // idempotent
    }
}

public sealed class InMemoryFileSystemContractTests : FileSystemContractTests
{
    private readonly InMemoryFileSystem _fs = new();

    protected override IFileSystem Fs => _fs;

    protected override string Root => @"C:\contract";

    public InMemoryFileSystemContractTests() => _fs.CreateDirectory(Root);
}

public sealed class WindowsFileSystemContractTests : FileSystemContractTests
{
    private readonly WindowsFileSystem _fs = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "everyapp-contract-" + Guid.NewGuid().ToString("N"));

    protected override IFileSystem Fs => _fs;

    protected override string Root => _root;

    public WindowsFileSystemContractTests() => Directory.CreateDirectory(_root);

    public override void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best effort cleanup of our own unique temp directory
        }

        base.Dispose();
    }
}
