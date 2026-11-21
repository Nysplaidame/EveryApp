using BenchmarkDotNet.Attributes;
using EveryApp.Core.FileSystem;
using EveryApp.Core.Hashing;
using EveryApp.Core.Jobs;
using Ferry.Engine;

namespace Ferry.Bench;

/// <summary>Renting and returning one copy buffer. Should be allocation free and well under a microsecond.</summary>
[MemoryDiagnoser]
public class BufferPoolBenchmarks
{
    private BufferPool _pool = null!;

    [Params(1024 * 1024, 4 * 1024 * 1024)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _pool = new BufferPool();
        _pool.Return(_pool.Rent(Size)); // first rent allocates the native memory
    }

    [GlobalCleanup]
    public void Cleanup() => _pool.Dispose();

    [Benchmark]
    public void RentReturn() => _pool.Return(_pool.Rent(Size));
}

/// <summary>Hash throughput on one core over data already in memory. Plan target: XxHash3 at 10 GB/s or more.</summary>
public class HashBenchmarks
{
    private byte[] _data = null!;

    [Params(HashAlgorithmKind.XxHash3, HashAlgorithmKind.Sha256)]
    public HashAlgorithmKind Kind { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _data = new byte[64 * 1024 * 1024];
        new Random(16).NextBytes(_data);
    }

    /// <summary>Hashes 64 MiB in 4 MiB pieces, as the copier does.</summary>
    [Benchmark]
    public byte[] Hash64MiB()
    {
        using var hasher = new StreamingHasher(Kind);
        for (var offset = 0; offset < _data.Length; offset += 4 * 1024 * 1024)
        {
            hasher.Append(_data.AsSpan(offset, 4 * 1024 * 1024));
        }

        return hasher.Finish();
    }
}

/// <summary>
/// The full FileCopier path for one 256 MiB file inside the temp folder (verify on and off). Uses whatever drive holds the temp folder,
/// so compare runs on the same PC only.
/// </summary>
public class FileCopierBenchmarks
{
    private string _folder = null!;
    private string _source = null!;
    private FileCopier _copier = null!;
    private BufferPool _pool = null!;
    private int _run;

    [Params(true, false)]
    public bool Verify { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _folder = Path.Combine(Path.GetTempPath(), "ferry-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        _source = Path.Combine(_folder, "source.bin");
        var data = new byte[4 * 1024 * 1024];
        var random = new Random(16);
        using (var file = File.Create(_source))
        {
            for (var i = 0; i < 64; i++)
            {
                random.NextBytes(data);
                file.Write(data);
            }
        }

        _pool = new BufferPool();
        _copier = new FileCopier(new WindowsFileSystem(), _pool);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _pool.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    [Benchmark]
    public async Task<bool> Copy256MiB()
    {
        var target = Path.Combine(_folder, $"copy-{_run++}.bin");
        var outcome = await _copier.CopyAsync(_source, target, new CopyOptions { Verify = Verify }, new PauseGate(), new Progress<FileProgress>(), CancellationToken.None).ConfigureAwait(false);
        File.Delete(target);
        return outcome.Success;
    }
}
