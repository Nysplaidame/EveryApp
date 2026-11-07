using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace EveryApp.Core.FileSystem;

/// <summary>
/// Thin wrappers over the Win32 calls that only accept a raw handle. The raw value is read only inside a
/// DangerousAddRef/DangerousRelease pair, so the handle cannot be closed or recycled while a call uses it.
/// </summary>
internal static class NativeFileApi
{
    public static unsafe bool GetInfoEx(SafeFileHandle handle, FILE_INFO_BY_HANDLE_CLASS infoClass, void* buffer, uint size, out int error)
    {
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            var ok = PInvoke.GetFileInformationByHandleEx(new HANDLE(handle.DangerousGetHandle()), infoClass, buffer, size);
            error = ok ? 0 : Marshal.GetLastWin32Error();
            return ok;
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }

    /// <summary>Cancels every pending I/O on the handle. Safe to call on a closed handle.</summary>
    public static unsafe void CancelAll(SafeFileHandle handle)
    {
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            _ = PInvoke.CancelIoEx(new HANDLE(handle.DangerousGetHandle()), null);
        }
        catch (ObjectDisposedException)
        {
            // already closed: nothing in flight
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }

    private const int FileDispositionInfoClass = 4;
    private const int FileDispositionInfoExClass = 21;
    private const uint DispositionDelete = 0x1;
    private const uint DispositionPosixSemantics = 0x2;
    private const uint DispositionIgnoreReadOnly = 0x10;

    /// <summary>The handle's path as Windows resolved it (links followed), in the <c>\\?\</c> form (<c>\\?\UNC\</c> for shares).</summary>
    public static unsafe string GetFinalPath(SafeFileHandle handle)
    {
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            var raw = new HANDLE(handle.DangerousGetHandle());
            var size = 512u;
            while (true)
            {
                var buffer = new char[size];
                uint length;
                fixed (char* p = buffer)
                {
                    length = PInvoke.GetFinalPathNameByHandle(raw, p, size, 0); // FILE_NAME_NORMALIZED | VOLUME_NAME_DOS
                }

                if (length == 0)
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }

                if (length < size)
                {
                    return new string(buffer, 0, (int)length);
                }

                size = length + 1;
            }
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Marks the object open on <paramref name="handle"/> for deletion (it goes when the last handle closes). POSIX semantics where supported,
    /// so the name is freed at once; ReadOnly does not block it. Needs a handle opened with DELETE access.
    /// </summary>
    public static unsafe bool TrySetDeleteDisposition(SafeFileHandle handle, out int error)
    {
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            var raw = new HANDLE(handle.DangerousGetHandle());
            var flags = DispositionDelete | DispositionPosixSemantics | DispositionIgnoreReadOnly;
            if (PInvoke.SetFileInformationByHandle(raw, (FILE_INFO_BY_HANDLE_CLASS)FileDispositionInfoExClass, &flags, sizeof(uint)))
            {
                error = 0;
                return true;
            }

            error = Marshal.GetLastWin32Error();
            if (error is 145)
            {
                return false; // folder not empty: the plain call would say the same
            }

            // Older Windows or a file system without the extended call (FAT): the classic disposition.
            byte deleteFile = 1;
            if (PInvoke.SetFileInformationByHandle(raw, (FILE_INFO_BY_HANDLE_CLASS)FileDispositionInfoClass, &deleteFile, sizeof(byte)))
            {
                error = 0;
                return true;
            }

            error = Marshal.GetLastWin32Error();
            return false;
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }

    /// <summary>Best effort <see cref="TrySetDeleteDisposition(SafeFileHandle, out int)"/> for cleanup paths.</summary>
    public static void TrySetDeleteDisposition(SafeFileHandle handle) => _ = TrySetDeleteDisposition(handle, out _);

    /// <summary>
    /// 128-bit file id plus 64-bit volume serial (FileIdInfo). On file systems without FileIdInfo it falls back to the 64-bit index and 32-bit
    /// serial from GetFileInformationByHandle, widened, so every source of ids on one volume agrees.
    /// </summary>
    public static unsafe bool TryGetFileId(SafeFileHandle handle, out ulong volumeSerial, out UInt128 fileId)
    {
        // FILE_ID_INFO: VolumeSerialNumber (8), FileId (16 bytes, little endian).
        byte* buffer = stackalloc byte[24];
        if (GetInfoEx(handle, FILE_INFO_BY_HANDLE_CLASS.FileIdInfo, buffer, 24, out _))
        {
            volumeSerial = *(ulong*)buffer;
            fileId = new UInt128(*(ulong*)(buffer + 16), *(ulong*)(buffer + 8));
            return true;
        }

        if (PInvoke.GetFileInformationByHandle(handle, out var info))
        {
            volumeSerial = info.dwVolumeSerialNumber;
            fileId = ((ulong)info.nFileIndexHigh << 32) | info.nFileIndexLow;
            return true;
        }

        volumeSerial = 0;
        fileId = UInt128.Zero;
        return false;
    }
}
