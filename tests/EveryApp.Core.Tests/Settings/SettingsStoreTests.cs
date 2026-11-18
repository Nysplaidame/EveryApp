using System.Text.Json.Serialization;
using EveryApp.Core.Settings;

namespace EveryApp.Core.Tests.Settings;

public sealed class SampleSettings
{
    public string Name { get; set; } = "default";
    public int Count { get; set; } = 3;
}

[JsonSerializable(typeof(SampleSettings))]
internal sealed partial class SampleSettingsContext : JsonSerializerContext;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "everyapp-tests-" + Guid.NewGuid().ToString("N"));

    public SettingsStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private SettingsStore<SampleSettings> NewStore(out string file)
    {
        file = Path.Combine(_dir, "settings.json");
        return new SettingsStore<SampleSettings>(file, SampleSettingsContext.Default.SampleSettings);
    }

    [Fact]
    public void Settings_SaveLoad_RoundTrips()
    {
        var store = NewStore(out var file);
        store.Save(new SampleSettings { Name = "x", Count = 9 });

        var loaded = store.Load();

        Assert.Equal("x", loaded.Name);
        Assert.Equal(9, loaded.Count);
        Assert.Contains("\"schemaVersion\": 1", File.ReadAllText(file));
    }

    [Fact]
    public void Settings_MissingFile_ReturnsDefaults()
    {
        var store = NewStore(out _);
        Assert.Equal("default", store.Load().Name);
    }

    [Fact]
    public void Settings_CorruptFile_ReturnsDefaultsAndKeepsBackup()
    {
        var store = NewStore(out var file);
        File.WriteAllText(file, "{ not json at all");

        var loaded = store.Load();

        Assert.Equal("default", loaded.Name);
        Assert.False(File.Exists(file));
        var backups = Directory.GetFiles(_dir, "settings.corrupt-*.json");
        Assert.Single(backups);
        Assert.Equal("{ not json at all", File.ReadAllText(backups[0]));
    }

    [Fact]
    public void Settings_NewerSchemaVersion_IsKeptAsBackupAndDefaultsReturned()
    {
        var store = NewStore(out var file);
        File.WriteAllText(file, "{\"schemaVersion\": 99, \"data\": {\"name\": \"future\"}}");

        Assert.Equal("default", store.Load().Name);
        Assert.Single(Directory.GetFiles(_dir, "settings.corrupt-*.json"));
    }

    [Fact]
    public void Settings_Save_IsAtomic()
    {
        var store = NewStore(out var file);
        store.Save(new SampleSettings { Name = "old" });

        // A stale temp file from an earlier crash must not break or leak into a save.
        File.WriteAllText(file + ".tmp", "garbage from a crashed save");
        store.Save(new SampleSettings { Name = "new" });

        Assert.False(File.Exists(file + ".tmp"));
        Assert.Equal("new", store.Load().Name);
        Assert.Equal(["settings.json"], Directory.GetFiles(_dir).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void Settings_Save_FailureLeavesOldFileIntact()
    {
        var store = NewStore(out var file);
        store.Save(new SampleSettings { Name = "old" });

        // Block the temp path with a directory so the write step fails before the replace.
        Directory.CreateDirectory(file + ".tmp");
        Assert.ThrowsAny<Exception>(() => store.Save(new SampleSettings { Name = "new" }));

        Assert.Equal("old", store.Load().Name);
    }
}
