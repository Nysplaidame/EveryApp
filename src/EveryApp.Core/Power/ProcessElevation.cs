using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;

namespace EveryApp.Core.Power;

/// <summary>Whether this process runs with an elevated (administrator) token, read from TokenElevation.</summary>
public static class ProcessElevation
{
    public static unsafe bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var token = identity.AccessToken;
        var added = false;
        try
        {
            token.DangerousAddRef(ref added);
            TOKEN_ELEVATION elevation;
            uint returned;
            if (!PInvoke.GetTokenInformation(new HANDLE(token.DangerousGetHandle()), TOKEN_INFORMATION_CLASS.TokenElevation, &elevation, (uint)sizeof(TOKEN_ELEVATION), &returned))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return elevation.TokenIsElevated != 0;
        }
        finally
        {
            if (added)
            {
                token.DangerousRelease();
            }
        }
    }
}
