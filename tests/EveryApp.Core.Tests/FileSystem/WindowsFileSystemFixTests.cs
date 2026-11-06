using EveryApp.Core.FileSystem;
using EveryApp.Core.Tests.Fakes;

namespace EveryApp.Core.Tests.FileSystem;

public sealed class WindowsFileSystemFixTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "everyapp-fix-" + Guid.NewGuid().ToString("N"));

    public WindowsFileSystemFixTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            // Names with trailing dots/spaces need the long-path form to be removed.
            Directory.Delete(@"\\?\" + _root, recursive: true);
        }
        catch (IOException)
        {
            // best effort cleanup of our own unique temp directory
        }
    }

    private string Raw(string name) => @"\\?\" + _root + @"\" + name;

    [Fact]
    public async Task TrailingDotAndSpace_NamesStayDistinct_ThroughCreateEnumerateEntryDelete()
    {
        var fs = new WindowsFileSystem();
        foreach (var (name, content) in new[] { ("a", 1), ("b", 2), ("a.", 3), ("b ", 4) })
        {
            await using var w = fs.CreateWrite(Raw(name), 0, unbuffered: false);
            await w.WriteAsync(new byte[content], 0, CancellationToken.None);
        }

        var children = fs.EnumerateChildren(_root).ToList();
        Assert.Equal(4, children.Count);
        var lengths = children.ToDictionary(c => c.FullPath, c => c.Length);

        var dotted = children.Single(c => c.Length == 3);
        var spaced = children.Single(c => c.Length == 4);
        Assert.Equal(3, fs.GetEntry(dotted.FullPath).Length);
        Assert.Equal(4, fs.GetEntry(spaced.FullPath).Length);

        fs.Delete(dotted.FullPath);
        fs.Delete(spaced.FullPath);

        var left = fs.EnumerateChildren(_root).Select(c => c.Length).Order().ToList();
        Assert.Equal([1L, 2L], left);
        Assert.Equal(1, fs.GetEntry(Path.Combine(_root, "a")).Length);
        Assert.Equal(2, fs.GetEntry(Path.Combine(_root, "b")).Length);
        Assert.NotEmpty(lengths);
    }

    [Fact]
    public async Task PlainPaths_WithTrailingDotOrSpace_ActOnTheRightFile_NeverTheSibling()
    {
        var fs = new WindowsFileSystem();
        foreach (var (name, content) in new[] { ("foo", 1), ("foo.", 2), ("foo ", 3) })
        {
            await using var w = fs.CreateWrite(Raw(name), 0, unbuffered: false);
            await w.WriteAsync(new byte[content], 0, CancellationToken.None);
        }

        var plainDot = _root + @"\foo.";
        var plainSpace = _root + @"\foo ";
        Assert.Equal(2, fs.GetEntry(plainDot).Length);
        Assert.Equal(3, fs.GetEntry(plainSpace).Length);
        await using (var r = fs.OpenRead(plainDot, unbuffered: false))
        {
            Assert.Equal(2, r.Length);
        }

        fs.Delete(plainDot);
        fs.Delete(plainSpace);
        Assert.Equal(1, fs.GetEntry(_root + @"\foo").Length);
        Assert.Single(fs.EnumerateChildren(_root));
    }

    [Theory]
    [InlineData(@"C:\a\b", @"\\?\C:\a\b")]
    [InlineData(@"C:\a\b.", @"\\?\C:\a\b.")]
    [InlineData(@"\\srv\share\x ", @"\\?\UNC\srv\share\x ")]
    [InlineData(@"C:\a\..\b", @"\\?\C:\b")]
    [InlineData(@"C:\a\.\b", @"\\?\C:\a\b")]
    public void Normalize_PrefixesQualifiedPathsDirectly_AndResolvesDottedOnes(string input, string expected) =>
        Assert.Equal(expected, WindowsFileSystem.Normalize(input));

    [Fact]
    public void ToDisplayPath_StripsPrefixOnlyWhenSafe()
    {
        Assert.Equal(@"C:\a\b", PathDisplay.ToDisplayPath(@"\\?\C:\a\b"));
        Assert.Equal(@"\\srv\share\x", PathDisplay.ToDisplayPath(@"\\?\UNC\srv\share\x"));
        Assert.Equal(@"\\?\C:\a\b.", PathDisplay.ToDisplayPath(@"\\?\C:\a\b."));
        Assert.Equal(@"\\?\C:\a\b ", PathDisplay.ToDisplayPath(@"\\?\C:\a\b "));
        Assert.Equal(@"C:\plain", PathDisplay.ToDisplayPath(@"C:\plain"));
    }

    [Fact]
    public void AllEntries_UseThePrefixedForm_EvenForPlainInput()
    {
        var fs = new WindowsFileSystem();
        File.WriteAllBytes(Path.Combine(_root, "p.txt"), [1]);
        Assert.StartsWith(@"\\?\", fs.GetEntry(Path.Combine(_root, "p.txt")).FullPath, StringComparison.Ordinal);
        Assert.StartsWith(@"\\?\", fs.EnumerateChildren(_root).Single().FullPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispose_WithWritesInFlight_CompletesPromptly_AndNeverCorruptsMemory()
    {
        using var pool = new AlignedBufferPool();
        var fs = new WindowsFileSystem(pool);
        var path = Path.Combine(_root, "inflight.bin");
        var w = fs.CreateWrite(path, 0, unbuffered: true);
        var data = new byte[1024 * 1024];
        var writes = Enumerable.Range(0, 32)
            .Select(i => Task.Run(async () =>
            {
                try
                {
                    await w.WriteAsync(data, (long)i * data.Length, CancellationToken.None);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
                {
                    // allowed once dispose has started
                }
            }))
            .ToList();
        var dispose = w.DisposeAsync().AsTask();
        Assert.Same(dispose, await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken)));
        await dispose;
        await Task.WhenAll(writes);
    }

    [Fact]
    public void Pool_DoubleReturn_Throws()
    {
        using var pool = new AlignedBufferPool();
        var buffer = pool.Rent(4096);
        pool.Return(buffer);
        Assert.Throws<InvalidOperationException>(() => pool.Return(buffer));
    }

    [Fact]
    public void Pool_ReturnAfterDispose_ThrowsObjectDisposed()
    {
        var pool = new AlignedBufferPool();
        var buffer = pool.Rent(4096);
        pool.Dispose();
        Assert.Throws<ObjectDisposedException>(() => pool.Return(buffer));
    }

    [Theory]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume1\x")]
    [InlineData("")]
    public void DevicePathsAndEmptyNames_AreRejected_AsInvalidName(string path)
    {
        var fs = new WindowsFileSystem();
        Assert.Equal(IoErrorKind.InvalidName, Assert.Throws<FileSystemException>(() => fs.GetEntry(path)).Kind);
    }

    [Fact]
    public async Task AlignedPoolMemory_RoundTrips_WithoutBounceBuffers()
    {
        using var pool = new AlignedBufferPool();
        var fs = new WindowsFileSystem(pool);
        var path = Path.Combine(_root, "aligned.bin");
        var sector = fs.GetSectorSize(_root);
        var size = Math.Max(1024 * 1024, sector * 16);

        var src = pool.Rent(size);
        var dst = pool.Rent(size);
        new Random(5).NextBytes(src.Memory.Span);
        var rents = pool.RentCount;

        await using (var w = fs.CreateWrite(path, size, unbuffered: true))
        {
            await w.WriteAsync(src.Memory[..size], 0, CancellationToken.None);
            await w.FlushAsync();
        }

        await using (var r = fs.OpenRead(path, unbuffered: true))
        {
            Assert.Equal(size, await r.ReadAsync(dst.Memory[..size], 0, CancellationToken.None));
        }

        Assert.Equal(rents, pool.RentCount); // no bounce buffer was rented
        Assert.True(src.Memory.Span[..size].SequenceEqual(dst.Memory.Span[..size]));
        pool.Return(src);
        pool.Return(dst);
    }

    [Fact]
    public async Task UnalignedInput_StillWorks_ViaBounceBuffer()
    {
        using var pool = new AlignedBufferPool();
        var fs = new WindowsFileSystem(pool);
        var path = Path.Combine(_root, "unaligned.bin");
        var data = new byte[200_000 + 3];
        new Random(6).NextBytes(data);
        var before = pool.RentCount;

        await using (var w = fs.CreateWrite(path, 0, unbuffered: true))
        {
            await w.WriteAsync(data, 0, CancellationToken.None);
        }

        var back = new byte[data.Length];
        await using (var r = fs.OpenRead(path, unbuffered: true))
        {
            var total = 0;
            while (total < back.Length)
            {
                var n = await r.ReadAsync(back.AsMemory(total), total + 0, CancellationToken.None);
                Assert.True(n > 0);
                total += n;
            }
        }

        Assert.True(pool.RentCount > before);
        Assert.Equal(data, back);
    }

    [Fact]
    public async Task ConcurrentAlignedReads_AtDifferentOffsets_Work()
    {
        using var pool = new AlignedBufferPool();
        var fs = new WindowsFileSystem(pool);
        var path = Path.Combine(_root, "conc.bin");
        var sector = fs.GetSectorSize(_root);
        var chunk = sector * 64;
        var data = new byte[chunk * 8];
        new Random(9).NextBytes(data);
        File.WriteAllBytes(path, data);

        await using var r = fs.OpenRead(path, unbuffered: true);
        var tasks = Enumerable.Range(0, 8).Select(async i =>
        {
            var buf = pool.Rent(chunk);
            try
            {
                var n = await r.ReadAsync(buf.Memory[..chunk], (long)i * chunk, CancellationToken.None);
                Assert.Equal(chunk, n);
                Assert.True(buf.Memory.Span[..chunk].SequenceEqual(data.AsSpan(i * chunk, chunk)));
            }
            finally
            {
                pool.Return(buf);
            }
        });
        await Task.WhenAll(tasks);
    }

    [Fact]
    public void Enumerate_DirectoryWithLockedFile_ListsEverything()
    {
        var fs = new WindowsFileSystem();
        File.WriteAllBytes(Path.Combine(_root, "free.txt"), [1]);
        var locked = Path.Combine(_root, "locked.txt");
        using var hold = new FileStream(locked, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        hold.WriteByte(1);
        hold.Flush();

        var names = fs.EnumerateChildren(_root).Select(e => Path.GetFileName(e.FullPath)).Order().ToList();
        Assert.Equal(["free.txt", "locked.txt"], names);
    }

    [Fact]
    public void Enumerate_TenThousandChildren_ReturnsIdsFromOneDirectoryHandle()
    {
        var fs = new WindowsFileSystem();
        var dir = Path.Combine(_root, "many");
        Directory.CreateDirectory(dir);
        for (var i = 0; i < 10_000; i++)
        {
            using var f = File.Create(Path.Combine(dir, "f" + i));
        }

        var entries = fs.EnumerateChildren(dir).ToList();
        Assert.Equal(10_000, entries.Count);
        Assert.Equal(10_000, entries.Select(e => e.FileId).Distinct().Count());
        Assert.Single(entries.Select(e => e.VolumeSerial).Distinct());
        Assert.All(entries, e => Assert.NotEqual(0UL, e.VolumeSerial));
    }

    [Fact]
    public void Enumerate_MissingDirectory_ThrowsNotFound_Immediately()
    {
        var fs = new WindowsFileSystem();
        Assert.Equal(IoErrorKind.NotFound, Assert.Throws<FileSystemException>(() => fs.EnumerateChildren(Path.Combine(_root, "nodir"))).Kind);
    }

    [Fact]
    public async Task OpenRead_WorksWhileFileIsOpenForWritingElsewhere()
    {
        var fs = new WindowsFileSystem();
        var path = Path.Combine(_root, "shared.txt");
        using var other = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        other.Write([1, 2, 3]);
        other.Flush();
        await using var r = fs.OpenRead(path, unbuffered: false);
        var buf = new byte[3];
        Assert.Equal(3, await r.ReadAsync(buf, 0, CancellationToken.None));
    }

    [Fact]
    public async Task FaultInjecting_FreeSpace_FailsWritesOnceCumulativeBytesExceedLimit()
    {
        var fs = new FaultInjectingFileSystem(new InMemoryFileSystem());
        fs.CreateDirectory(@"C:\d");
        fs.SetFreeSpace(@"C:\", 100);
        await using var w = fs.CreateWrite(@"C:\d\f", 0, unbuffered: false);
        await w.WriteAsync(new byte[60], 0, CancellationToken.None);
        Assert.Equal(40, fs.GetFreeSpace(@"C:\d\f"));
        var ex = await Assert.ThrowsAsync<FileSystemException>(async () => await w.WriteAsync(new byte[60], 60, CancellationToken.None));
        Assert.Equal(IoErrorKind.DiskFull, ex.Kind);
        var pre = Assert.Throws<FileSystemException>(() => fs.CreateWrite(@"C:\d\g", 41, unbuffered: false));
        Assert.Equal(IoErrorKind.DiskFull, pre.Kind);
        Assert.Throws<FileSystemException>(() => fs.GetEntry(@"C:\d\g")); // nothing left behind
    }
}
