using System.Security.Cryptography;
using System.Text;
using EveryApp.Core.FileSystem;
using EveryApp.Core.Hashing;
using EveryApp.Core.Tests.Fakes;

namespace EveryApp.Core.Tests.Hashing;

public sealed class HashingTests
{
    private const string Dir = @"\\?\C:\hash";

    private static string Hex(byte[] b) => Convert.ToHexString(b);

    [Fact]
    public void XxHash3_KnownVector()
    {
        using var h = new StreamingHasher(HashAlgorithmKind.XxHash3);
        Assert.Equal("2D06800538D394C2", Hex(h.Finish()));
    }

    [Fact]
    public void Sha256_KnownVector()
    {
        using var h = new StreamingHasher(HashAlgorithmKind.Sha256);
        h.Append("abc"u8);
        Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", Hex(h.Finish()));
    }

    [Fact]
    public void Md5_KnownVector()
    {
        using var h = new StreamingHasher(HashAlgorithmKind.Md5);
        h.Append("abc"u8);
        Assert.Equal("900150983CD24FB0D6963F7D28E17F72", Hex(h.Finish()));
    }

    [Theory]
    [InlineData(HashAlgorithmKind.XxHash3)]
    [InlineData(HashAlgorithmKind.Sha256)]
    [InlineData(HashAlgorithmKind.Md5)]
    public void StreamingHasher_ChunkedEqualsWhole(HashAlgorithmKind kind)
    {
        var data = new byte[1 << 20];
        new Random(42).NextBytes(data);

        using var whole = new StreamingHasher(kind);
        whole.Append(data);
        var expected = whole.Finish();

        using var chunked = new StreamingHasher(kind);
        int[] sizes = [1, 4093, 65536, 100_003, 7, 300_000];
        var pos = 0;
        foreach (var s in sizes)
        {
            chunked.Append(data.AsSpan(pos, s));
            pos += s;
        }

        chunked.Append(data.AsSpan(pos));
        Assert.Equal(expected, chunked.Finish());
    }

    [Fact]
    public void StreamingHasher_AppendOrFinishAfterFinish_Throws()
    {
        using var h = new StreamingHasher(HashAlgorithmKind.Sha256);
        h.Append("abc"u8);
        h.Finish();
        Assert.Throws<InvalidOperationException>(() => h.Append("x"u8));
        Assert.Throws<InvalidOperationException>(() => h.Finish());
    }

    [Fact]
    public void StreamingHasher_UseAfterDispose_Throws()
    {
        var h = new StreamingHasher(HashAlgorithmKind.XxHash3);
        h.Dispose();
        h.Dispose(); // idempotent
        Assert.Throws<ObjectDisposedException>(() => h.Append("x"u8));
        Assert.Throws<ObjectDisposedException>(() => h.Finish());
    }

    [Fact]
    public void StreamingHasher_UnknownKind_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StreamingHasher((HashAlgorithmKind)99));
    }

    private static InMemoryFileSystem WithFiles(params (string Name, byte[] Data)[] files)
    {
        var fs = new InMemoryFileSystem();
        foreach (var (name, data) in files)
        {
            fs.AddFile(Dir + @"\" + name, data);
        }

        return fs;
    }

    private static ValueTask<ulong> Compute(IFileSystem fs, string path, int htb = 65536) =>
        PartialHash.ComputeAsync(fs, path, htb, TestContext.Current.CancellationToken);

    private static byte[] Random(int length, int seed = 1)
    {
        var b = new byte[length];
        new Random(seed).NextBytes(b);
        return b;
    }

    [Fact]
    public async Task PartialHash_FilesDifferingOnlyInMiddle_Equal()
    {
        var a = Random(1000);
        var b = (byte[])a.Clone();
        b[500] ^= 0xFF;
        var fs = WithFiles(("a", a), ("b", b));

        var ha = await Compute(fs, Dir + @"\a", 100);
        var hb = await Compute(fs, Dir + @"\b", 100);
        Assert.Equal(ha, hb);
    }

    [Fact]
    public async Task PartialHash_FilesDifferingInTail_Differ()
    {
        var a = Random(1000);
        var b = (byte[])a.Clone();
        b[^1] ^= 0xFF;
        var fs = WithFiles(("a", a), ("b", b));

        Assert.NotEqual(
            await Compute(fs, Dir + @"\a", 100),
            await Compute(fs, Dir + @"\b", 100));
    }

    [Fact]
    public async Task PartialHash_FilesDifferingInHead_Differ()
    {
        var a = Random(1000);
        var b = (byte[])a.Clone();
        b[0] ^= 0xFF;
        var fs = WithFiles(("a", a), ("b", b));

        Assert.NotEqual(
            await Compute(fs, Dir + @"\a", 100),
            await Compute(fs, Dir + @"\b", 100));
    }

    [Fact]
    public async Task PartialHash_DifferentLengthSameEnds_Differ()
    {
        var head = Random(100, 1);
        var tail = Random(100, 2);
        var a = head.Concat(new byte[300]).Concat(tail).ToArray();
        var b = head.Concat(new byte[301]).Concat(tail).ToArray();
        var fs = WithFiles(("a", a), ("b", b));

        Assert.NotEqual(
            await Compute(fs, Dir + @"\a", 100),
            await Compute(fs, Dir + @"\b", 100));
    }

    [Fact]
    public async Task PartialHash_SmallFile_HashedWhole()
    {
        // Length <= 2 * headTailBytes: a difference anywhere counts, including the middle.
        var a = Random(200);
        var b = (byte[])a.Clone();
        b[100] ^= 0xFF;
        var fs = WithFiles(("a", a), ("b", b));

        Assert.NotEqual(
            await Compute(fs, Dir + @"\a", 100),
            await Compute(fs, Dir + @"\b", 100));
    }

    [Fact]
    public async Task PartialHash_EmptyFile_Works()
    {
        var fs = WithFiles(("e", []), ("e2", []));
        Assert.Equal(
            await Compute(fs, Dir + @"\e"),
            await Compute(fs, Dir + @"\e2"));
    }

    [Fact]
    public async Task PartialHash_SurvivesShortReads()
    {
        var a = Random(5000);
        var b = (byte[])a.Clone();
        b[2500] ^= 0xFF;
        var full = WithFiles(("a", a));
        var expectedLarge = await Compute(full, Dir + @"\a", 100);
        var expectedSmall = await Compute(full, Dir + @"\a", 4000);

        var trickle = new TrickleFileSystem(full, maxPerRead: 7);
        Assert.Equal(expectedLarge, await Compute(trickle, Dir + @"\a", 100));
        Assert.Equal(expectedSmall, await Compute(trickle, Dir + @"\a", 4000));
    }

    [Fact]
    public async Task PartialHash_AtMostTwoReads()
    {
        var fs = WithFiles(("big", Random(10_000)), ("small", Random(150)));
        var counting = new TrickleFileSystem(fs, maxPerRead: int.MaxValue);

        await Compute(counting, Dir + @"\big", 100);
        Assert.Equal(2, counting.Reads);

        counting.Reads = 0;
        await Compute(counting, Dir + @"\small", 100);
        Assert.Equal(1, counting.Reads);
    }

    [Fact]
    public async Task PartialHash_InvalidHeadTail_Throws()
    {
        var fs = WithFiles(("a", Random(10)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await Compute(fs, Dir + @"\a", 0));
    }

    [Fact]
    public async Task PartialHash_Cancelled_Throws()
    {
        var fs = WithFiles(("a", Random(10)));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await PartialHash.ComputeAsync(fs, Dir + @"\a", ct: cts.Token));
    }

    [Fact]
    public void Sha256_MatchesFramework()
    {
        var data = Random(10_000);
        using var h = new StreamingHasher(HashAlgorithmKind.Sha256);
        h.Append(data);
        Assert.Equal(SHA256.HashData(data), h.Finish());
        Assert.Equal(64, Encoding.ASCII.GetBytes(Hex(SHA256.HashData(data))).Length);
    }
}
