namespace EveryApp.Core.FileSystem;

/// <summary>An I/O failure translated to an <see cref="IoErrorKind"/>. The message never contains file contents.</summary>
public sealed class FileSystemException(string path, IoErrorKind kind, Exception? inner)
    : IOException($"File system operation failed ({kind}) for '{path}'.", inner)
{
    public string Path { get; } = path;

    public IoErrorKind Kind { get; } = kind;
}
