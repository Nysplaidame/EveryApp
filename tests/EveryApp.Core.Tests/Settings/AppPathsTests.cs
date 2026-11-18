using EveryApp.Core.Settings;

namespace EveryApp.Core.Tests.Settings;

public sealed class AppPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "everyapp-tests-" + Guid.NewGuid().ToString("N"));

    public AppPathsTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void AppPaths_PortableFlag_UsesExeFolder()
    {
        var exeDir = Path.Combine(_root, "exe");
        Directory.CreateDirectory(exeDir);
        File.WriteAllText(Path.Combine(exeDir, "portable.flag"), "");

        var paths = new AppPathLayout(exeDir, Path.Combine(_root, "local"));

        Assert.True(paths.IsPortable);
        Assert.Equal(Path.Combine(exeDir, "data"), paths.DataRoot);
        Assert.Equal(Path.Combine(exeDir, "data", "Logs"), paths.Logs);
        Assert.True(Directory.Exists(paths.Logs));
        Assert.True(Directory.Exists(paths.Jobs));
        Assert.False(Directory.Exists(Path.Combine(_root, "local")));
    }

    [Fact]
    public void AppPaths_NoFlag_UsesLocalAppData()
    {
        var exeDir = Path.Combine(_root, "exe");
        Directory.CreateDirectory(exeDir);
        var local = Path.Combine(_root, "local");

        var paths = new AppPathLayout(exeDir, local);

        Assert.False(paths.IsPortable);
        Assert.Equal(Path.Combine(local, "EveryApp"), paths.DataRoot);
        Assert.Equal(Path.Combine(local, "EveryApp", "Jobs"), paths.Jobs);
        Assert.True(Directory.Exists(paths.Jobs));
    }

    [Fact]
    public void AppPaths_Public_PointsSomewhereReal()
    {
        Assert.False(string.IsNullOrEmpty(AppPaths.DataRoot));
    }
}
