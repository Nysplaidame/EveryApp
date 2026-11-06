using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PasteCatcher;

/// <summary>Files + copy/move intent read from the clipboard when it last changed.</summary>
internal sealed record ClipboardSnapshot(uint Sequence, IReadOnlyList<string> Paths, uint? DropEffect, string? Problem, DateTime TakenAt)
{
    public bool HasFiles => Problem is null && Paths.Count > 0;
    public bool IsMove => DropEffect is uint e && (e & 2) != 0 && (e & 1) == 0;
}

/// <summary>
/// Reads the clipboard ONLY when it changes (WM_CLIPBOARDUPDATE), on its own thread, and caches the result.
/// The paste path never opens the clipboard: opening it at paste time races Explorer's own paste, which then
/// fails with an error sound (found in the user's manual test, Part A step 8).
/// </summary>
internal sealed class ClipboardWatcher : IDisposable
{
    private const int OpenAttempts = 5;
    private const int OpenRetryMs = 30;

    private static readonly uint PreferredDropEffectFormat = Native.RegisterClipboardFormat("Preferred DropEffect");

    private readonly Action<string> _log;
    private readonly Native.WndProc _wndProc; // kept alive while the window exists
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private uint _threadId;
    private IntPtr _hwnd;
    private volatile ClipboardSnapshot? _snapshot;

    // Proof counters: every OpenClipboard call in this process goes through TryOpen, which records the thread.
    private static long _opensOnWatcherThread;
    private static long _opensOnOtherThreads;
    private static long _openFailures;
    private long _snapshots;
    private double _maxHeldMs;

    public ClipboardWatcher(Action<string> log)
    {
        _log = log;
        _wndProc = WndProc;
        _thread = new Thread(Run) { IsBackground = true, Name = "PasteCatcher clipboard watcher" };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    /// <summary>Latest snapshot, or null if none yet. Lock-free read; safe to call from the keyboard hook.</summary>
    public ClipboardSnapshot? Current => _snapshot;

    public static long OpensOnWatcherThread => Interlocked.Read(ref _opensOnWatcherThread);
    public static long OpensOnOtherThreads => Interlocked.Read(ref _opensOnOtherThreads);

    public bool Start()
    {
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
        return _hwnd != IntPtr.Zero;
    }

    public string Stats() =>
        $"Clipboard: {_snapshots} snapshot(s), longest hold {_maxHeldMs:F1} ms. OpenClipboard calls: watcher thread {OpensOnWatcherThread}, " +
        $"any other thread (paste path) {OpensOnOtherThreads}, busy retries {Interlocked.Read(ref _openFailures)}.";

    public void Dispose()
    {
        if (_threadId != 0) Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(3));
        _ready.Dispose();
    }

    private void Run()
    {
        _threadId = Native.GetCurrentThreadId();
        IntPtr instance = Native.GetModuleHandle(null);
        string className = "FerrySpikeClipboardWatcher" + Environment.ProcessId;
        var wc = new Native.WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = instance,
            lpszClassName = className,
        };
        if (Native.RegisterClassEx(ref wc) == 0)
        {
            _log($"Clipboard watcher: RegisterClassEx failed ({Marshal.GetLastWin32Error()}).");
            _ready.Set();
            return;
        }
        IntPtr hwnd = Native.CreateWindowEx(0, className, null, 0, 0, 0, 0, 0, Native.HWND_MESSAGE, IntPtr.Zero, instance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero || !Native.AddClipboardFormatListener(hwnd))
        {
            _log($"Clipboard watcher: could not listen for clipboard changes ({Marshal.GetLastWin32Error()}).");
            if (hwnd != IntPtr.Zero) Native.DestroyWindow(hwnd);
            _ready.Set();
            return;
        }
        _hwnd = hwnd;
        TakeSnapshot("start"); // the clipboard may already hold files copied before the catcher started
        _ready.Set();

        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }

        Native.RemoveClipboardFormatListener(hwnd);
        Native.DestroyWindow(hwnd);
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Native.WM_CLIPBOARDUPDATE)
        {
            try { TakeSnapshot("change"); }
            catch (Exception ex) { _log($"Clipboard watcher error (ignored): {ex.GetType().Name}: {ex.Message}"); }
            return IntPtr.Zero;
        }
        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void TakeSnapshot(string why)
    {
        uint seq = Native.GetClipboardSequenceNumber();

        // No file list: record that without opening the clipboard at all.
        if (!Native.IsClipboardFormatAvailable(Native.CF_HDROP))
        {
            Publish(new ClipboardSnapshot(seq, [], null, "clipboard has no file list", DateTime.Now), why, 0);
            return;
        }

        bool opened = false;
        for (int attempt = 0; attempt < OpenAttempts && !opened; attempt++)
        {
            opened = TryOpen();
            if (!opened)
            {
                Interlocked.Increment(ref _openFailures);
                Thread.Sleep(OpenRetryMs);
            }
        }
        if (!opened)
        {
            Publish(new ClipboardSnapshot(seq, [], null, "clipboard was busy when it changed; could not read it", DateTime.Now), why, 0);
            return;
        }

        // Hold the clipboard only while copying the data out.
        var held = Stopwatch.StartNew();
        var paths = new List<string>();
        uint? effect = null;
        string? problem = null;
        try
        {
            seq = Native.GetClipboardSequenceNumber(); // the data we read belongs to this sequence number
            IntPtr hDrop = Native.GetClipboardData(Native.CF_HDROP);
            if (hDrop == IntPtr.Zero)
            {
                problem = "no file list on the clipboard (maybe items from a phone, zip or other virtual folder)";
            }
            else
            {
                uint n = Native.DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
                for (uint i = 0; i < n; i++)
                {
                    uint len = Native.DragQueryFile(hDrop, i, null, 0);
                    var sb = new StringBuilder((int)len + 1);
                    Native.DragQueryFile(hDrop, i, sb, (uint)sb.Capacity);
                    paths.Add(sb.ToString());
                }
                IntPtr hEffect = Native.GetClipboardData(PreferredDropEffectFormat);
                if (hEffect != IntPtr.Zero && (ulong)Native.GlobalSize(hEffect) >= 4)
                {
                    IntPtr p = Native.GlobalLock(hEffect);
                    if (p != IntPtr.Zero)
                    {
                        effect = (uint)Marshal.ReadInt32(p);
                        Native.GlobalUnlock(hEffect);
                    }
                }
            }
        }
        finally
        {
            Native.CloseClipboard();
        }
        Publish(new ClipboardSnapshot(seq, paths, effect, problem, DateTime.Now), why, held.Elapsed.TotalMilliseconds);
    }

    private void Publish(ClipboardSnapshot s, string why, double heldMs)
    {
        _snapshot = s;
        _snapshots++;
        if (heldMs > _maxHeldMs) _maxHeldMs = heldMs;
        string what = s.HasFiles
            ? $"{s.Paths.Count} file(s), {(s.IsMove ? "cut (move)" : "copy")}, Preferred DropEffect={(s.DropEffect?.ToString() ?? "none")}"
            : s.Problem ?? "empty";
        _log($"Clipboard snapshot ({why}, sequence {s.Sequence}): {what}. Held {heldMs:F1} ms.");
    }

    /// <summary>The only place this process opens the clipboard. Records which thread did it.</summary>
    private bool TryOpen()
    {
        if (Native.GetCurrentThreadId() == _threadId) Interlocked.Increment(ref _opensOnWatcherThread);
        else Interlocked.Increment(ref _opensOnOtherThreads);
        return Native.OpenClipboard(_hwnd);
    }
}
