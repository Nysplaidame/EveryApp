using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PasteCatcher;

/// <summary>Result of resolving the folder Explorer would paste into.</summary>
internal sealed record TargetInfo(string? FileSystemPath, string DisplayName, string How);

/// <summary>
/// Runs on the STA worker thread only. Never call from the keyboard hook. Never touches the clipboard:
/// clipboard data comes from <see cref="ClipboardWatcher"/>'s snapshot.
/// </summary>
internal static class ShellTarget
{
    private static readonly Guid ClsidShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid SidSTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IidIShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid IidIPersistFolder2 = new("1AC3D9F0-175C-11d1-95BE-00609797EA4F");

    public static TargetInfo ForDesktop()
    {
        string path = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return new TargetInfo(path, "Desktop", "known folder (desktop has no IShellWindows tab)");
    }

    /// <summary>
    /// Finds the IShellWindows entry whose IShellBrowser window is the active tab of the foreground Explorer window.
    /// </summary>
    public static TargetInfo ForExplorer(IntPtr topLevel, IntPtr activeTab)
    {
        var type = Type.GetTypeFromCLSID(ClsidShellWindows, throwOnError: true)!;
        dynamic shellWindows = Activator.CreateInstance(type)!;
        int count = shellWindows.Count;
        int sameTopLevel = 0;
        try
        {
            for (int i = 0; i < count; i++)
            {
                dynamic? item = null;
                try
                {
                    item = shellWindows.Item(i);
                    if (item is null) continue;

                    long hwndValue = item.HWND;
                    if (new IntPtr(hwndValue) != topLevel) continue;
                    sameTopLevel++;

                    IntPtr tabHwnd = IntPtr.Zero;
                    object? browserObj = null;
                    if (item is IServiceProviderCom sp &&
                        sp.QueryService(SidSTopLevelBrowser, IidIShellBrowser, out browserObj) == 0 &&
                        browserObj is IShellBrowser browser)
                    {
                        browser.GetWindow(out tabHwnd);
                    }

                    // Win11 tabs: several entries share the top-level HWND; only the active tab matches.
                    if (activeTab != IntPtr.Zero && tabHwnd != activeTab) continue;

                    string how = $"IShellWindows item {i}/{count}, tab match ({sameTopLevel} entr(ies) share this window)";
                    TargetInfo? fromDispatch = TryPathFromDispatch((object)item);
                    if (fromDispatch is not null) return fromDispatch with { How = how + ", via Document.Folder.Self" };

                    if (browserObj is IShellBrowser b2)
                    {
                        var fromView = TryPathFromShellView(b2);
                        if (fromView is not null) return fromView with { How = how + ", via IFolderView" };
                    }

                    return new TargetInfo(null, "(unknown)", how + ", but the folder could not be read");
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
                {
                    // An Explorer tab closing mid-enumeration is normal; skip it.
                }
                finally
                {
                    if (item is not null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(shellWindows);
        }

        return new TargetInfo(null, "(not found)", $"no IShellWindows entry matched ({count} entries, {sameTopLevel} for this window)");
    }

    private static TargetInfo? TryPathFromDispatch(object window)
    {
        try
        {
            dynamic item = window;
            dynamic self = item.Document.Folder.Self;
            string path = self.Path;
            string name = self.Name;
            bool isFileSystem = self.IsFileSystem;
            return new TargetInfo(isFileSystem ? path : null, name, "");
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return null;
        }
    }

    private static TargetInfo? TryPathFromShellView(IShellBrowser browser)
    {
        if (browser.QueryActiveShellView(out object? view) != 0 || view is not IFolderView folderView) return null;
        if (folderView.GetFolder(IidIPersistFolder2, out object? pf) != 0 || pf is not IPersistFolder2 persist) return null;
        if (persist.GetCurFolder(out IntPtr pidl) != 0 || pidl == IntPtr.Zero) return null;
        try
        {
            string? fsPath = NameOf(pidl, Native.SIGDN_FILESYSPATH);
            string display = NameOf(pidl, Native.SIGDN_NORMALDISPLAY) ?? "(unnamed)";
            return new TargetInfo(fsPath, display, "");
        }
        finally
        {
            Native.CoTaskMemFree(pidl);
        }
    }

    private static string? NameOf(IntPtr pidl, uint sigdn)
    {
        if (Native.SHGetNameFromIDList(pidl, sigdn, out IntPtr p) != 0 || p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Native.CoTaskMemFree(p); }
    }

    public static string ProcessNameOf(uint pid)
    {
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName; }
        catch (ArgumentException) { return "(gone)"; }
        catch (InvalidOperationException) { return "(gone)"; }
    }
}
