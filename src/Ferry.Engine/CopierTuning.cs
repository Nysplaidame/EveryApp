namespace Ferry.Engine;

/// <summary>Pipeline settings the benchmarks sweep. The app only ever uses <see cref="Default"/>.</summary>
internal readonly record struct CopierTuning(int PipelineDepth, long UnbufferedThresholdBytes)
{
    public static CopierTuning Default { get; } = new(FileCopier.PipelineDepth, FileCopier.UnbufferedThresholdBytes);
}
