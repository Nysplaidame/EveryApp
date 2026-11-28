namespace Ferry.Engine;

/// <summary>
/// Shared by every copy of one job so large files take turns: one large file copies at a time (<see cref="Transfer"/>) and one large file is
/// checked at a time (<see cref="Check"/>). A large file lets go of its turn to copy as soon as its data is written and closed, so its check
/// can run while the next large file copies. Small files, block clones and rescue copies never use them.
/// </summary>
internal sealed class LargeFileGates : IDisposable
{
    /// <summary>Held by a large file from just before its part file is created until the part is flushed and closed.</summary>
    public SemaphoreSlim Transfer { get; } = new(1, 1);

    /// <summary>Held by a large file only while its closed part is read back and compared with the source.</summary>
    public SemaphoreSlim Check { get; } = new(1, 1);

    /// <inheritdoc/>
    public void Dispose()
    {
        Transfer.Dispose();
        Check.Dispose();
    }
}
