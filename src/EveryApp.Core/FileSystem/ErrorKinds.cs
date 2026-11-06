using System.ComponentModel;

namespace EveryApp.Core.FileSystem;

/// <summary>Maps Win32 error codes and HRESULTs to <see cref="IoErrorKind"/>.</summary>
public static class ErrorKinds
{
    /// <summary>Accepts a plain Win32 error code (0..65535) or an HRESULT wrapping one (0x8007xxxx).</summary>
    public static IoErrorKind FromHResult(int hresult)
    {
        var code = hresult;
        if (((uint)hresult & 0xFFFF0000u) == 0x80070000u)
        {
            code = hresult & 0xFFFF;
        }

        return code switch
        {
            0 => IoErrorKind.None,
            2 or 3 => IoErrorKind.NotFound,                        // file / path not found
            5 => IoErrorKind.AccessDenied,
            32 or 33 => IoErrorKind.SharingViolation,              // sharing / lock violation
            39 or 112 or 1295 or 1816 => IoErrorKind.DiskFull,     // handle disk full / disk full / quota / not enough quota
            23 or 27 or 29 or 30 or 483 or 1117 or 1392 or 1393 => IoErrorKind.BadBlock, // CRC, sector, write/read fault, device errors, corrupt file/disk
            53 or 55 or 59 or 64 or 67 or 121 or 1231 or 1236 => IoErrorKind.NetworkLost,
            80 or 183 => IoErrorKind.AlreadyExists,                // file exists / already exists
            206 => IoErrorKind.PathTooLong,
            123 or 161 or 267 => IoErrorKind.InvalidName,
            _ => IoErrorKind.Other,
        };
    }

    /// <summary>
    /// Translates any exception thrown by the BCL or Win32 into a <see cref="FileSystemException"/>, keeping the original as inner exception.
    /// Anything that does not clearly map stays <see cref="IoErrorKind.Other"/>; name problems we detect ourselves are raised directly as InvalidName.
    /// </summary>
    public static FileSystemException Translate(string path, Exception ex)
    {
        if (ex is FileSystemException already)
        {
            return already;
        }

        var kind = FromHResult(ex is Win32Exception w ? w.NativeErrorCode : ex.HResult);
        if (kind is IoErrorKind.Other or IoErrorKind.None)
        {
            kind = ex switch
            {
                FileNotFoundException or DirectoryNotFoundException => IoErrorKind.NotFound,
                UnauthorizedAccessException => IoErrorKind.AccessDenied,
                PathTooLongException => IoErrorKind.PathTooLong,
                _ => IoErrorKind.Other,
            };
        }

        return new FileSystemException(path, kind, ex);
    }
}
