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
            39 or 112 => IoErrorKind.DiskFull,                     // handle disk full / disk full
            23 or 1117 => IoErrorKind.BadBlock,                    // CRC error / I/O device error
            53 or 55 or 59 or 64 or 1231 => IoErrorKind.NetworkLost,
            206 => IoErrorKind.PathTooLong,
            123 or 161 or 267 => IoErrorKind.InvalidName,
            _ => IoErrorKind.Other,
        };
    }

    /// <summary>Translates any exception thrown by the BCL or Win32 into a <see cref="FileSystemException"/>.</summary>
    public static FileSystemException Translate(string path, Exception ex)
    {
        if (ex is FileSystemException already)
        {
            return already;
        }

        var kind = FromHResult(ex is System.ComponentModel.Win32Exception w ? w.NativeErrorCode : ex.HResult);
        if (kind is IoErrorKind.Other or IoErrorKind.None)
        {
            kind = ex switch
            {
                FileNotFoundException or DirectoryNotFoundException => IoErrorKind.NotFound,
                UnauthorizedAccessException => IoErrorKind.AccessDenied,
                PathTooLongException => IoErrorKind.PathTooLong,
                ArgumentException or NotSupportedException => IoErrorKind.InvalidName,
                _ => IoErrorKind.Other,
            };
        }

        return new FileSystemException(path, kind, ex);
    }
}
