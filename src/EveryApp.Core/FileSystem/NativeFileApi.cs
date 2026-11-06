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
