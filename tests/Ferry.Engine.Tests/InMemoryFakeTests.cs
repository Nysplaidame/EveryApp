using System.Security.Cryptography;
using System.Text;
using EveryApp.Core.FileSystem;
using EveryApp.Core.Hashing;
using EveryApp.Core.Jobs;
using EveryApp.Core.Preflight;

namespace Ferry.Engine.Tests;

/// <summary>Regression test for the shared in-memory fake.</summary>
public sealed class InMemoryFakeTests
{
    [Fact]
    public void Move_Folder_MovesTheWholeTree()
    {
        var m = new EveryApp.Core.Tests.Fakes.InMemoryFileSystem();
        m.AddFile(@"C:\src\a.bin", [1]);
        m.AddFile(@"C:\src\sub\b.bin", [2]);
        m.CreateDirectory(@"C:\dst");

        m.Move(@"C:\src", @"C:\dst\src", overwrite: false);

        Assert.Equal([1], m.ReadAllBytes(@"C:\dst\src\a.bin"));
        Assert.Equal([2], m.ReadAllBytes(@"C:\dst\src\sub\b.bin"));
        Assert.False(m.Exists(@"C:\src"));
    }
}
