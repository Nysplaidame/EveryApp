using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Ferry.Bench;

/// <summary>
/// Empties the Windows standby list (cached file data) so each run reads from the drive, not from memory. Same call EmptyStandbyList makes.
/// Needs an administrator: the profiling privilege must be held and turned on. Never used by the app itself.
/// </summary>
internal static partial class StandbyCache
{
    private const int SystemMemoryListInformation = 80;
    private const int MemoryPurgeStandbyList = 4;
    private const uint TokenAdjustPrivileges = 0x20;
    private const uint TokenQuery = 0x8;
    private const uint SePrivilegeEnabled = 0x2;

    public static unsafe bool TryFlush(out string why)
    {
        if (!EnablePrivilege("SeProfileSingleProcessPrivilege", out why))
        {
            return false;
        }

        var command = MemoryPurgeStandbyList;
        var status = NtSetSystemInformation(SystemMemoryListInformation, &command, sizeof(int));
        why = status == 0 ? string.Empty : $"Could not clear the standby cache (status 0x{status:X8}).";
        return status == 0;
    }

    private static unsafe bool EnablePrivilege(string name, out string why)
    {
        why = string.Empty;
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
        {
            why = new Win32Exception().Message;
            return false;
        }

        try
        {
            if (!LookupPrivilegeValueW(null, name, out var luid))
            {
                why = new Win32Exception().Message;
                return false;
            }

            var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            if (!AdjustTokenPrivileges(token, false, &privileges, 0, null, null) || Marshal.GetLastPInvokeError() != 0)
            {
                why = "Clearing the standby cache needs an administrator.";
                return false;
            }

            return true;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    // TOKEN_PRIVILEGES with one entry. LUID is two 32-bit halves, so the native layout packs on 4 bytes (no gap after Count).
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivileges
    {
        public uint Count;
        public long Luid;
        public uint Attributes;
    }

    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtSetSystemInformation(int infoClass, void* info, int length);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValueW(string? system, string name, out long luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool AdjustTokenPrivileges(nint token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, TokenPrivileges* state, uint length, void* previous, uint* returned);
}
