using EveryApp.Core.FileSystem;
using EveryApp.Core.Tests.Fakes;

namespace EveryApp.Core.Tests.FileSystem;

public sealed class WindowsFileSystemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "everyapp-fs-" + Guid.NewGuid().ToString("N"));

    public WindowsFileSystemTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best effort cleanup of our own unique temp directory
        }
    }

    [Theory]
    [InlineData(2, IoErrorKind.NotFound)]
    [InlineData(5, IoErrorKind.AccessDenied)]
    [InlineData(32, IoErrorKind.SharingViolation)]
    [InlineData(112, IoErrorKind.DiskFull)]
    [InlineData(23, IoErrorKind.BadBlock)]
    [InlineData(64, IoErrorKind.NetworkLost)]
    [InlineData(59, IoErrorKind.NetworkLost)]
    [InlineData(206, IoErrorKind.PathTooLong)]
    [InlineData(123, IoErrorKind.InvalidName)]
    public void ErrorKinds_MapsWin32Codes(int win32, IoErrorKind expected)
    {
        Assert.Equal(expected, ErrorKinds.FromHResult(win32));
        // The same code wrapped as an HRESULT must map identically.
        Assert.Equal(expected, ErrorKinds.FromHResult(unchecked((int)(0x80070000u | (uint)win32))));
    }

    [Fact]
    public async Task WindowsFileSystem_UnbufferedReadWrite_RoundTrips()
    {
        var fs = new WindowsFileSystem();
        var path = Path.Combine(_root, "data.bin");
        var data = new byte[(3 * 1024 * 1024) + 17];
        new Random(1234).NextBytes(data);

        await using (var w = fs.CreateWrite(path, data.Length, unbuffered: true))
        {
            await w.WriteAsync(data, 0, CancellationToken.None);
            await w.FlushAsync();
        }

        Assert.Equal(data.Length, fs.GetEntry(path).Length);

        var read = new byte[data.Length];
        await using (var r = fs.OpenRead(path, unbuffered: true))
        {
            Assert.Equal(data.Length, r.Length);
            var total = 0;
            while (total < read.Length)
            {
                var n = await r.ReadAsync(read.AsMemory(total), total, CancellationToken.None);
                if (n == 0)
                {
                    break;
                }

                total += n;
            }

            Assert.Equal(data.Length, total);
        }

        Assert.True(data.AsSpan().SequenceEqual(read));
        Assert.True(fs.GetSectorSize(path) >= 512);
        Assert.True(fs.GetFreeSpace(path) > 0);
    }

    [Fact]
    public async Task WindowsFileSystem_LongPath_Works()
    {
        var fs = new WindowsFileSystem();
        var dir = _root;
        while (dir.Length < 330)
        {
            dir = Path.Combine(dir, new string('d', 60));
        }

        var path = Path.Combine(dir, new string('f', 400 - dir.Length - 1));
        Assert.Equal(400, path.Length);

        fs.CreateDirectory(dir);
        await using (var w = fs.CreateWrite(path, 0, unbuffered: false))
        {
            await w.WriteAsync(new byte[] { 1, 2, 3 }, 0, CancellationToken.None);
        }

        var entry = fs.GetEntry(path);
        Assert.Equal(3, entry.Length);
        Assert.NotEqual(0UL, entry.FileId);
        Assert.NotEqual(0u, entry.VolumeSerial);
        Assert.Single(fs.EnumerateChildren(dir));

        fs.Delete(path);
        var ex = Assert.Throws<FileSystemException>(() => fs.GetEntry(path));
        Assert.Equal(IoErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public void WindowsFileSystem_MissingFile_TranslatesToNotFound()
    {
        var fs = new WindowsFileSystem();
        var ex = Assert.Throws<FileSystemException>(() => fs.OpenRead(Path.Combine(_root, "nope"), unbuffered: false));
        Assert.Equal(IoErrorKind.NotFound, ex.Kind);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task FaultInjecting_FailRead_ThrowsWithKind()
    {
        var mem = new InMemoryFileSystem();
        mem.AddFile(@"C:\src\a.bin", new byte[4096]);
        var fs = new FaultInjectingFileSystem(mem);
        fs.FailRead(@"C:\src\a.bin", 1024, IoErrorKind.BadBlock);

        await using var r = fs.OpenRead(@"C:\src\a.bin", unbuffered: false);
        var buf = new byte[512];
        Assert.Equal(512, await r.ReadAsync(buf, 0, CancellationToken.None));
        var ex = await Assert.ThrowsAsync<FileSystemException>(async () => await r.ReadAsync(buf, 1024, CancellationToken.None));
        Assert.Equal(IoErrorKind.BadBlock, ex.Kind);
    }

    [Fact]
    public async Task FaultInjecting_FailWrite_Times_AndFreeSpace()
    {
        var fs = new FaultInjectingFileSystem(new InMemoryFileSystem());
        fs.SetFreeSpace(@"C:\", 1234);
        Assert.Equal(1234, fs.GetFreeSpace(@"C:\dst\x"));

        fs.FailWrite(@"C:\dst\x", 0, IoErrorKind.DiskFull, times: 1);
        fs.CreateDirectory(@"C:\dst");
        await using var w = fs.CreateWrite(@"C:\dst\x", 0, unbuffered: false);
        var ex = await Assert.ThrowsAsync<FileSystemException>(async () => await w.WriteAsync(new byte[10], 0, CancellationToken.None));
        Assert.Equal(IoErrorKind.DiskFull, ex.Kind);
        await w.WriteAsync(new byte[10], 0, CancellationToken.None);
    }

    [Fact]
    public async Task FaultInjecting_MutateDuringRead_RunsOnce()
    {
        var mem = new InMemoryFileSystem();
        mem.AddFile(@"C:\a", new byte[100]);
        var fs = new FaultInjectingFileSystem(mem);
        var calls = 0;
        fs.MutateDuringRead(@"C:\a", 0, () => calls++);
        await using var r = fs.OpenRead(@"C:\a", unbuffered: false);
        var buf = new byte[10];
        await r.ReadAsync(buf, 0, CancellationToken.None);
        await r.ReadAsync(buf, 0, CancellationToken.None);
        Assert.Equal(1, calls);
    }
}
