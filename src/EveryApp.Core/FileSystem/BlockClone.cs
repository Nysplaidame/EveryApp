using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace EveryApp.Core.FileSystem;

/// <summary>
/// ReFS block cloning (FSCTL_DUPLICATE_EXTENTS_TO_FILE): the target shares the source's data blocks, so a same-volume copy costs
/// almost no I/O. Every call reports failure as false; the caller falls back to a normal copy. Raw handle values are read only inside
/// DangerousAddRef/DangerousRelease pairs.
/// </summary>
internal static class BlockClone
{
    /// <summary>The largest single clone request: under the 4 GB limit and a multiple of every cluster size.</summary>
    private const long MaxChunk = 1L << 31;

    public readonly record struct Integrity(ushort ChecksumAlgorithm, uint Flags, uint ClusterSize);

    /// <summary>Succeeds only on file systems that support integrity streams (ReFS), which is also where block cloning lives.</summary>
    public static unsafe bool TryGetIntegrity(SafeFileHandle handle, out Integrity integrity)
    {
        // FSCTL_GET_INTEGRITY_INFORMATION_BUFFER: ChecksumAlgorithm (2), Reserved (2), Flags (4), ChecksumChunkSizeInBytes (4), ClusterSizeInBytes (4).
        byte* buffer = stackalloc byte[16];
        integrity = default;
        if (!Control(handle, PInvoke.FSCTL_GET_INTEGRITY_INFORMATION, null, 0, buffer, 16))
        {
            return false;
        }

        var cluster = *(uint*)(buffer + 12);
        if (cluster == 0 || !uint.IsPow2(cluster))
        {
            return false;
        }

        integrity = new Integrity(*(ushort*)buffer, *(uint*)(buffer + 4), cluster);
        return true;
    }

    public static unsafe bool SetIntegrity(SafeFileHandle handle, Integrity integrity)
    {
        // FSCTL_SET_INTEGRITY_INFORMATION_BUFFER: ChecksumAlgorithm (2), Reserved (2), Flags (4).
        byte* buffer = stackalloc byte[8];
        *(ushort*)buffer = integrity.ChecksumAlgorithm;
        *(ushort*)(buffer + 2) = 0;
        *(uint*)(buffer + 4) = integrity.Flags;
        return Control(handle, PInvoke.FSCTL_SET_INTEGRITY_INFORMATION, buffer, 8, null, 0);
    }

    public static unsafe bool SetSparse(SafeFileHandle handle) => Control(handle, PInvoke.FSCTL_SET_SPARSE, null, 0, null, 0);

    /// <summary>Clones the whole source onto the target (whose length is already set). The last request is rounded up to a whole cluster, as ReFS requires.</summary>
    public static unsafe bool Duplicate(SafeFileHandle source, SafeFileHandle target, long length, uint clusterSize)
    {
        var added = false;
        try
        {
            source.DangerousAddRef(ref added);

            // DUPLICATE_EXTENTS_DATA (x64): FileHandle (8), SourceFileOffset (8), TargetFileOffset (8), ByteCount (8).
            byte* data = stackalloc byte[32];
            *(nint*)data = source.DangerousGetHandle();
            for (long offset = 0; offset < length; offset += MaxChunk)
            {
                var count = Math.Min(MaxChunk, length - offset);
                count = (count + clusterSize - 1) / clusterSize * clusterSize;
                *(long*)(data + 8) = offset;
                *(long*)(data + 16) = offset;
                *(long*)(data + 24) = count;
                if (!Control(target, PInvoke.FSCTL_DUPLICATE_EXTENTS_TO_FILE, data, 32, null, 0))
                {
                    return false;
                }
            }

            return true;
        }
        finally
        {
            if (added)
            {
                source.DangerousRelease();
            }
        }
    }

    public static bool Flush(SafeFileHandle handle) => PInvoke.FlushFileBuffers(handle);

    private static unsafe bool Control(SafeFileHandle handle, uint code, void* input, uint inputSize, void* output, uint outputSize)
    {
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            uint returned = 0;
            return PInvoke.DeviceIoControl(new HANDLE(handle.DangerousGetHandle()), code, input, inputSize, output, outputSize, &returned, null);
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }
}
