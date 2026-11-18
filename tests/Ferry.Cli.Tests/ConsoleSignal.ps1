# Run Ferry in a separate hidden console and send a real CTRL_C_EVENT to that console only.
# This helper is a separate process so the test runner's console and signal handlers are never changed.
param([string]$Executable, [string]$Source, [string]$Destination)
$ErrorActionPreference = 'Stop'
Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class SignalNative {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct Startup {
        public int Size; public string Reserved; public string Desktop; public string Title;
        public int X, Y, Width, Height, CharsX, CharsY, Fill, Flags;
        public short ShowWindow, ReservedSize; public IntPtr ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInfo { public IntPtr Process, Thread; public uint Id, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CreateProcess(string app, StringBuilder command, IntPtr pa, IntPtr ta,
        bool inherit, uint flags, IntPtr env, string cwd, ref Startup startup, out ProcessInfo process);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] public static extern bool FreeConsole();
    [DllImport("kernel32.dll")] public static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll")] public static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
    [DllImport("kernel32.dll")] public static extern bool GenerateConsoleCtrlEvent(uint signal, uint group);
    public static int Launch(string exe, string source, string destination) {
        var startup = new Startup { Size = Marshal.SizeOf(typeof(Startup)), Flags = 1, ShowWindow = 0 };
        ProcessInfo info;
        var command = new StringBuilder("\"" + exe + "\" copy \"" + source + "\" \"" + destination + "\" --if-exists skip --limit 1MB/s");
        if (!CreateProcess(exe, command, IntPtr.Zero, IntPtr.Zero, false, 0x10, IntPtr.Zero, null, ref startup, out info))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        CloseHandle(info.Thread); CloseHandle(info.Process);
        return (int)info.Id;
    }
}
'@
# An ignored Ctrl+C flag is inherited by children, even with CREATE_NEW_CONSOLE.
# Clear the helper's inherited flag before launching the child.
[void][SignalNative]::SetConsoleCtrlHandler([IntPtr]::Zero, $false)
$child = [Diagnostics.Process]::GetProcessById([SignalNative]::Launch($Executable, $Source, $Destination))
$null = $child.Handle # Keep a handle before the child exits, so its exit status remains available.
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    do {
        if ($child.HasExited) { throw 'Ferry exited before the cancellation checkpoint.' }
        $part = Get-ChildItem -LiteralPath $Destination -Filter '*.ferrypart' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($part) { break }
        if ([DateTime]::UtcNow -gt $deadline) { throw 'No copy progress before the deadline.' }
        Start-Sleep -Milliseconds 50
    } while ($true)
    [void][SignalNative]::FreeConsole()
    if (-not [SignalNative]::AttachConsole([uint32]$child.Id)) { throw 'Could not attach to the child console.' }
    if (-not [SignalNative]::SetConsoleCtrlHandler([IntPtr]::Zero, $true)) { throw 'Could not protect the signal helper.' }
    if (-not [SignalNative]::GenerateConsoleCtrlEvent(0, 0)) { throw 'Could not send Ctrl+C.' }
    if (-not $child.WaitForExit(30000)) { throw 'Ferry did not cancel within 30 seconds.' }
    exit $child.ExitCode
}
finally {
    [void][SignalNative]::FreeConsole()
    if (-not $child.HasExited) { $child.Kill() }
    $child.Dispose()
}
