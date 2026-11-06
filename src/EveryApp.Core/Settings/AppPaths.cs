namespace EveryApp.Core.Settings;

/// <summary>Where an EveryApp install keeps its data. Portable when a <c>portable.flag</c> file sits next to the program.</summary>
internal sealed class AppPathLayout
{
    internal const string PortableFlagFileName = "portable.flag";

    private readonly string _logs;
    private readonly string _jobs;

    public AppPathLayout(string baseDirectory, string localAppData)
    {
        IsPortable = File.Exists(Path.Combine(baseDirectory, PortableFlagFileName));
        DataRoot = IsPortable
            ? Path.Combine(baseDirectory, "data")
            : Path.Combine(localAppData, "EveryApp");
        _logs = Path.Combine(DataRoot, "Logs");
        _jobs = Path.Combine(DataRoot, "Jobs");
    }

    public bool IsPortable { get; }

    public string DataRoot { get; }

    /// <summary>Log folder. Created the first time it is read.</summary>
    public string Logs => Ensure(_logs);

    /// <summary>Job folder. Created the first time it is read.</summary>
    public string Jobs => Ensure(_jobs);

    private static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}

/// <summary>The real locations for the running program.</summary>
public static class AppPaths
{
    private static readonly Lazy<AppPathLayout> Layout = new(() => new AppPathLayout(
        AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));

    /// <summary>True when a <c>portable.flag</c> file exists next to the executable.</summary>
    public static bool IsPortable => Layout.Value.IsPortable;

    /// <summary>Root folder for settings, logs and jobs.</summary>
    public static string DataRoot => Layout.Value.DataRoot;

    /// <summary>Folder for log files. Created on first access.</summary>
    public static string Logs => Layout.Value.Logs;

    /// <summary>Folder for saved copy jobs. Created on first access.</summary>
    public static string Jobs => Layout.Value.Jobs;
}
