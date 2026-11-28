using System.Security.Cryptography;
using System.Text;
using EveryApp.Core.FileSystem;
using EveryApp.Core.Hashing;
using EveryApp.Core.Jobs;
using EveryApp.Core.Preflight;

namespace Ferry.Engine.Tests;

/// <summary>Shared rig and helpers for the tree copier tests (in-memory C: and D: drives behind fault injection and a log).</summary>
public abstract class TreeCopierTestBase : IDisposable
{
    private protected readonly TreeCopierRig _rig = new();

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _rig.Dispose();
        GC.SuppressFinalize(this);
    }

    protected static byte[] Bytes(int size, int seed)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    protected static bool IsPartOf(string path, string name) =>
        RecordingFileSystem.Plain(path).Contains(name + ".", StringComparison.OrdinalIgnoreCase) && path.EndsWith(FileCopier.PartSuffix, StringComparison.OrdinalIgnoreCase);

    protected void Add(string path, int size, int seed = 1) => _rig.Mem.AddFile(path, Bytes(size, seed));

    protected void AddText(string path, string text) => _rig.Mem.AddFile(path, Encoding.UTF8.GetBytes(text));

    protected string Text(string path) => Encoding.UTF8.GetString(_rig.Mem.ReadAllBytes(path));

    protected void SetTimes(string path, DateTime lastWriteUtc)
    {
        var e = _rig.Mem.GetEntry(path);
        _rig.Mem.SetMetadata(path, e.Attributes, lastWriteUtc, lastWriteUtc);
    }
}
