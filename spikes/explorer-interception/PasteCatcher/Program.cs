using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PasteCatcher;

/// <summary>
/// Spike (A): out-of-process paste catcher. A low-level keyboard hook spots Ctrl+V, Ctrl+Shift+V and Shift+Insert
/// while File Explorer or the desktop has focus, and logs what Ferry WOULD do. It never copies anything.
/// The hook callback only does cheap checks and queues an event; all clipboard and COM work happens on a worker thread.
/// </summary>
internal static class Program
{
    private enum Shortcut { None, CtrlV, CtrlShiftV, ShiftInsert }

    private sealed record HookEvent(
        Shortcut Shortcut, string Where, IntPtr TopLevel, IntPtr ActiveTab, uint Pid,
        bool Candidate, string? PassReason, bool Swallowed, bool Injected, double HookMs, DateTime At, long QueuedTicks,
        ClipboardSnapshot? Snapshot);

    private static bool _swallow;
    private static ClipboardWatcher _clipboard = null!;
    private static uint _mainThreadId;
    private static IntPtr _hook;
    private static readonly Native.LowLevelKeyboardProc HookProc = HookCallback; // kept alive for the hook's lifetime
    private static readonly BlockingCollection<HookEvent> Queue = new(boundedCapacity: 256);

    // Hook-thread-only state.
    private static uint _heldKey;      // suppresses auto-repeat
    private static uint _swallowedKey; // the matching key-up must be swallowed too
    private static long _hookCalls;
    private static double _hookMaxMs;
    private static double _hookTotalMs;

    private static int Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h") || args.Contains("/?"))
        {
            PrintHelp();
            return 0;
        }

        _swallow = args.Contains("--swallow");
        int exitAfter = 0;
        int idx = Array.IndexOf(args, "--exit-after");
        if (idx >= 0 && (idx + 1 >= args.Length || !int.TryParse(args[idx + 1], out exitAfter) || exitAfter <= 0))
        {
            Console.Error.WriteLine("--exit-after needs a number of seconds.");
            return 2;
        }

        if (args.Contains("--probe")) return Probe();

        _mainThreadId = Native.GetCurrentThreadId();

        // The clipboard is read only when it changes, never at paste time (see ClipboardWatcher).
        _clipboard = new ClipboardWatcher(Log);
        if (!_clipboard.Start())
        {
            Console.Error.WriteLine("Could not start the clipboard watcher.");
            return 1;
        }

        var worker = new Thread(WorkerLoop) { IsBackground = true, Name = "PasteCatcher worker" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();

        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, HookProc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            Console.Error.WriteLine($"Could not install the keyboard hook (error {Marshal.GetLastWin32Error()}).");
            return 1;
        }

        Log($"Keyboard hook installed. Mode: {(_swallow ? "SWALLOW (Explorer will NOT paste when the catcher takes the shortcut)" : "pass-through (Explorer still pastes normally)")}.");
        Log("Watching Ctrl+V, Ctrl+Shift+V and Shift+Insert in File Explorer and on the desktop. Nothing is copied. Press Ctrl+C here to stop.");

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Native.PostThreadMessage(_mainThreadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        };

        using var timer = exitAfter > 0
            ? new Timer(_ => Native.PostThreadMessage(_mainThreadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero), null, exitAfter * 1000, Timeout.Infinite)
            : null;

        var cpuStart = Process.GetCurrentProcess().TotalProcessorTime;
        var wall = Stopwatch.StartNew();

        // A low-level hook needs a message loop on the thread that installed it.
        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            _ = msg;
        }

        bool unhooked = Native.UnhookWindowsHookEx(_hook);
        Queue.CompleteAdding();
        worker.Join(TimeSpan.FromSeconds(3));
        _clipboard.Dispose();

        using var self = Process.GetCurrentProcess();
        self.Refresh();
        var cpu = self.TotalProcessorTime - cpuStart;
        Log($"Stopped. Hook removed: {unhooked}. Ran {wall.Elapsed.TotalSeconds:F1} s, CPU used {cpu.TotalMilliseconds:F0} ms " +
            $"({cpu.TotalMilliseconds / Math.Max(1, wall.Elapsed.TotalMilliseconds) * 100:F3} %), working set {self.WorkingSet64 / (1024 * 1024)} MB, " +
            $"private {self.PrivateMemorySize64 / (1024 * 1024)} MB.");
        Log($"Hook calls: {_hookCalls}, average {(_hookCalls > 0 ? _hookTotalMs / _hookCalls : 0):F4} ms, max {_hookMaxMs:F4} ms.");
        Log(_clipboard.Stats());
        return 0;
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

        long t0 = Stopwatch.GetTimestamp();
        bool eat = false;
        try
        {
            var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            int msg = (int)wParam;
            bool down = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
            bool up = msg is Native.WM_KEYUP or Native.WM_SYSKEYUP;

            if (k.vkCode is Native.VK_V or Native.VK_INSERT)
            {
                if (up)
                {
                    if (_swallowedKey == k.vkCode) { eat = true; _swallowedKey = 0; }
                    if (_heldKey == k.vkCode) _heldKey = 0;
                }
                else if (down)
                {
                    if (_heldKey == k.vkCode)
                    {
                        eat = _swallowedKey == k.vkCode; // auto-repeat: same decision as the first press
                    }
                    else
                    {
                        _heldKey = k.vkCode;
                        var shortcut = Classify(k.vkCode);
                        if (shortcut != Shortcut.None)
                        {
                            eat = Decide(shortcut, (k.flags & Native.LLKHF_INJECTED) != 0, t0);
                            if (eat) _swallowedKey = k.vkCode;
                        }
                    }
                }
            }
        }
        catch
        {
            eat = false; // never break the user's keyboard
        }

        double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        _hookCalls++;
        _hookTotalMs += ms;
        if (ms > _hookMaxMs) _hookMaxMs = ms;

        return eat ? new IntPtr(1) : Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private static Shortcut Classify(uint vk)
    {
        bool ctrl = Native.IsDown(Native.VK_CONTROL);
        bool shift = Native.IsDown(Native.VK_SHIFT);
        bool alt = Native.IsDown(Native.VK_MENU); // AltGr arrives as Ctrl+Alt: never treat it as Ctrl
        bool win = Native.IsDown(Native.VK_LWIN) || Native.IsDown(Native.VK_RWIN);
        if (alt || win) return Shortcut.None;
        if (vk == Native.VK_V && ctrl) return shift ? Shortcut.CtrlShiftV : Shortcut.CtrlV;
        if (vk == Native.VK_INSERT && shift && !ctrl) return Shortcut.ShiftInsert;
        return Shortcut.None;
    }

    /// <summary>Cheap checks only (no COM, no clipboard open). Returns true to swallow the key.</summary>
    private static bool Decide(Shortcut shortcut, bool injected, long t0)
    {
        IntPtr fg = Native.GetForegroundWindow();
        string fgClass = Native.ClassOf(fg);
        string where = fgClass switch
        {
            "CabinetWClass" or "ExploreWClass" => "File Explorer",
            "Progman" or "WorkerW" => "Desktop",
            _ => "",
        };
        if (where.Length == 0) return false; // other apps: not logged, not touched

        uint thread = Native.GetWindowThreadProcessId(fg, out uint pid);
        var gti = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf<Native.GUITHREADINFO>() };
        IntPtr focus = Native.GetGUIThreadInfo(thread, ref gti) ? gti.hwndFocus : IntPtr.Zero;

        // Walk up from the focused control: it must sit inside the folder view (SHELLDLL_DefView),
        // not the address bar, search box, navigation pane or a rename box.
        bool inView = false;
        IntPtr activeTab = IntPtr.Zero;
        string focusClass = Native.ClassOf(focus);
        for (IntPtr h = focus; h != IntPtr.Zero && h != fg; h = Native.GetParent(h))
        {
            string c = Native.ClassOf(h);
            if (c == "SHELLDLL_DefView") inView = true;
            else if (c == "ShellTabWindowClass") { activeTab = h; break; }
        }

        // Clipboard state comes ONLY from the snapshot taken when the clipboard last changed. Never open the
        // clipboard here or later for this key press: Explorer is opening it right now to paste.
        ClipboardSnapshot? snap = _clipboard.Current;
        uint seq = Native.GetClipboardSequenceNumber();

        string? reason = null;
        if (focus == IntPtr.Zero) reason = "could not see which control has focus";
        else if (focusClass == "Edit") reason = "focus is in a text box (rename, address bar or search)";
        else if (!inView) reason = $"focus is not in the file list (focused control: {focusClass})";
        else if (snap is null || snap.Sequence != seq) reason = $"clipboard changed, snapshot stale (snapshot {snap?.Sequence.ToString() ?? "none"}, now {seq}); Windows pastes";
        else if (!snap.HasFiles) reason = snap.Problem ?? "clipboard has no file list";
        else if (injected) reason = "key was injected by software, not typed";

        bool candidate = reason is null;
        bool swallow = candidate && _swallow;
        double hookMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        Queue.TryAdd(new HookEvent(shortcut, where, fg, activeTab, pid, candidate, reason, swallow, injected, hookMs, DateTime.Now, Stopwatch.GetTimestamp(), snap));
        return swallow;
    }

    private static void WorkerLoop()
    {
        foreach (var e in Queue.GetConsumingEnumerable())
        {
            try
            {
                Handle(e);
            }
            catch (Exception ex)
            {
                Log($"  worker error (ignored): {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static void Handle(HookEvent e)
    {
        string name = e.Shortcut switch
        {
            Shortcut.CtrlV => "Ctrl+V",
            Shortcut.CtrlShiftV => "Ctrl+Shift+V (quick paste)",
            Shortcut.ShiftInsert => "Shift+Insert",
            _ => "?",
        };
        string proc = ShellTarget.ProcessNameOf(e.Pid);
        string keyFate = e.Swallowed ? "key SWALLOWED (Explorer did not see it)" : "key passed to Explorer";

        if (!e.Candidate)
        {
            Log($"{name} in {e.Where} [{proc}] -> ignored: {e.PassReason}. {keyFate}. Hook {e.HookMs:F3} ms.");
            return;
        }

        // Candidate events always carry a fresh snapshot with files (checked in the hook). No clipboard access here.
        ClipboardSnapshot clip = e.Snapshot!;
        var sw = Stopwatch.StartNew();
        TargetInfo target = e.Where == "Desktop" ? ShellTarget.ForDesktop() : ShellTarget.ForExplorer(e.TopLevel, e.ActiveTab);
        double targetMs = sw.Elapsed.TotalMilliseconds;
        double sinceKeyMs = Stopwatch.GetElapsedTime(e.QueuedTicks).TotalMilliseconds + e.HookMs;

        string op = clip.DropEffect is null ? "COPY (no Preferred DropEffect; Explorer would copy)"
            : clip.IsMove ? $"MOVE (cut; Preferred DropEffect={clip.DropEffect})"
            : $"COPY (Preferred DropEffect={clip.DropEffect})";

        Log($"{name} in {e.Where} [{proc}]. {keyFate}.");
        if (proc != "explorer") Log($"  WARNING: the window belongs to '{proc}', not explorer. Production must ignore it.");
        if (clip.Problem is not null)
        {
            Log($"  Ferry would NOT take this: {clip.Problem}. Fallback: stock Explorer paste.");
        }
        else if (target.FileSystemPath is null)
        {
            Log($"  Ferry would NOT take this: target '{target.DisplayName}' is not a normal folder ({target.How}). Fallback: stock Explorer paste.");
        }
        else
        {
            Log($"  WOULD {op}");
            Log($"  into: {target.FileSystemPath}   ({target.How})");
            Log($"  {clip.Paths.Count} item(s):");
            foreach (var p in clip.Paths.Take(15)) Log($"    {p}");
            if (clip.Paths.Count > 15) Log($"    ... and {clip.Paths.Count - 15} more");
            if (e.Shortcut == Shortcut.CtrlShiftV) Log("  (quick paste: Ferry would copy with no window, tray progress only)");
        }
        Log($"  clipboard: snapshot from {clip.TakenAt:HH:mm:ss.fff} (sequence {clip.Sequence}), not opened at paste time.");
        Log($"  timings: hook {e.HookMs:F3} ms, target {targetMs:F1} ms, key-to-decision {sinceKeyMs:F1} ms.");
    }

    private static int Probe()
    {
        // Non-interactive self-check: no hook, nothing touched. Counts only (no paths printed).
        var t = new Thread(() =>
        {
            var sw = Stopwatch.StartNew();
            var type = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"), true)!;
            dynamic shellWindows = Activator.CreateInstance(type)!;
            int count = shellWindows.Count;
            int fileSystem = 0, tabsResolved = 0;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    dynamic item = shellWindows.Item(i);
                    if (item is null) continue;
                    if (item is IServiceProviderCom sp &&
                        sp.QueryService(new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837"), new Guid("000214E2-0000-0000-C000-000000000046"), out object? b) == 0 &&
                        b is IShellBrowser browser && browser.GetWindow(out IntPtr tab) == 0 && Native.ClassOf(tab) == "ShellTabWindowClass")
                    {
                        tabsResolved++;
                    }
                    bool fs = item.Document.Folder.Self.IsFileSystem;
                    if (fs) fileSystem++;
                }
                catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException)
                {
                }
            }
            Log($"Probe: IShellWindows has {count} entr(ies); {tabsResolved} resolved to an Explorer tab via IShellBrowser; {fileSystem} show a normal folder. {sw.Elapsed.TotalMilliseconds:F1} ms.");
            Log($"Probe: clipboard has a file list: {Native.IsClipboardFormatAvailable(Native.CF_HDROP)}.");
            Log($"Probe: foreground window class: '{Native.ClassOf(Native.GetForegroundWindow())}'.");
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            PasteCatcher (Ferry spike). Logs what Ferry would do when you paste files in File Explorer. Never copies anything.

              PasteCatcher                 Watch, and let Explorer paste as normal (safe default).
              PasteCatcher --swallow       Watch, and stop Explorer from pasting when the catcher would take over.
              PasteCatcher --exit-after N  Stop by itself after N seconds.
              PasteCatcher --probe         Quick self-check (no keyboard hook): can it see Explorer windows?

            Press Ctrl+C in this window to stop.
            """);
    }

    private static readonly object LogLock = new();

    private static void Log(string text)
    {
        lock (LogLock) Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {text}");
    }
}
