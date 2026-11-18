using EveryApp.Core.FileSystem;

namespace Ferry.Engine;

/// <summary>Something that went wrong with one item. <paramref name="Message"/> is plain language, ready to show to people.</summary>
public sealed record Problem(string Path, IoErrorKind Kind, string Message);
