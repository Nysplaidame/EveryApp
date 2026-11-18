using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace EveryApp.Core.Settings;

/// <summary>
/// Loads and saves one settings file. The file looks like <c>{"schemaVersion": 1, "data": { ... }}</c>.
/// Saves never leave a half-written file; an unreadable file is kept as a backup and defaults are used.
/// </summary>
/// <typeparam name="T">The settings type. Use a source-generated <see cref="JsonTypeInfo{T}"/> so no reflection is needed.</typeparam>
public sealed class SettingsStore<T> where T : class, new()
{
    private const int SchemaVersion = 1;
    private const string VersionProperty = "schemaVersion";
    private const string DataProperty = "data";

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true };

    private readonly string _fileName;
    private readonly JsonTypeInfo<T> _typeInfo;

    public SettingsStore(string fileName, JsonTypeInfo<T> typeInfo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(typeInfo);
        _fileName = Path.GetFullPath(fileName);
        _typeInfo = typeInfo;
    }

    /// <summary>Reads the settings. Missing file: defaults. Unreadable file: it is renamed to a backup and defaults are returned.</summary>
    public T Load()
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(_fileName);
        }
        catch (FileNotFoundException)
        {
            return new T();
        }
        catch (DirectoryNotFoundException)
        {
            return new T();
        }

        if (TryParse(bytes, out var value))
        {
            return value;
        }

        KeepAsBackup();
        return new T();
    }

    /// <summary>Writes the settings to a temporary file, flushes it to disk, then swaps it in.</summary>
    public void Save(T value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var directory = Path.GetDirectoryName(_fileName);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = _fileName + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (var writer = new Utf8JsonWriter(stream, WriterOptions))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber(VersionProperty, SchemaVersion);
                    writer.WritePropertyName(DataProperty);
                    JsonSerializer.Serialize(writer, value, _typeInfo);
                    writer.WriteEndObject();
                }

                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_fileName))
            {
                File.Replace(temp, _fileName, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temp, _fileName, overwrite: true);
            }
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private bool TryParse(byte[] bytes, out T value)
    {
        value = null!;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(VersionProperty, out var version)
                || !version.TryGetInt32(out var number)
                || number is < 1 or > SchemaVersion
                || !root.TryGetProperty(DataProperty, out var data))
            {
                return false;
            }

            var parsed = data.Deserialize(_typeInfo);
            if (parsed is null)
            {
                return false;
            }

            value = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void KeepAsBackup()
    {
        var directory = Path.GetDirectoryName(_fileName) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(_fileName);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
        var backup = Path.Combine(directory, $"{stem}.corrupt-{stamp}.json");
        try
        {
            File.Move(_fileName, backup, overwrite: false);
        }
        catch (IOException)
        {
            // Could not keep a backup (for example the name is taken). Defaults still apply; the next Save replaces the file.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                return;
            }

            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort cleanup only.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort cleanup only.
        }
    }
}
