using EveryApp.Core.Hashing;

namespace Ferry.Engine;

/// <summary>Settings for one copy job. Some fields are used by later stages (conflicts, retry, verify, extra streams).</summary>
public sealed record CopyOptions
{
    public bool Verify { get; init; } = true;

    public bool KeepDatesAndAttributes { get; init; } = true;

    public bool CopyPermissions { get; init; }

    public ConflictRule IfExists { get; init; } = ConflictRule.Ask;

    public FailureRule IfUnreadable { get; init; } = FailureRule.RetryThenSkipAndList;

    public bool RescueMode { get; init; }

    public bool FollowReparsePoints { get; init; }

    public long? SpeedLimitBytesPerSecond { get; init; }

    public IReadOnlyList<string> Exclude { get; init; } = [];

    public bool DryRun { get; init; }

    public HashAlgorithmKind HashAlgorithm { get; init; } = HashAlgorithmKind.XxHash3;

    /// <summary>Size of each read/write chunk. 0 = auto (<see cref="FileCopier.DefaultBufferSizeBytes"/>, tuned by benchmarks).</summary>
    public int BufferSizeBytes { get; init; } // 0 = auto (CA1805 forbids spelling out "= 0")

    /// <summary>NTFS extra data streams, opt-in.</summary>
    public bool CopyAlternateStreams { get; init; }

    /// <summary>Writes &lt;dest&gt;\ferry-checksums.&lt;algo&gt; (one "hash  relative-path" line per file).</summary>
    public bool ExportChecksums { get; init; }
}

public enum ConflictRule
{
    Ask,
    Overwrite,
    OverwriteIfNewer,
    Skip,
    Rename,
    KeepBoth,
}

public enum FailureRule
{
    RetryThenSkipAndList,
    Ask,
    Stop,
}
