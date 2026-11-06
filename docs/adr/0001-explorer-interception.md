# ADR 0001: How Ferry catches copies in File Explorer

- **Status:** Proposed — awaiting manual test results
- **Date:** 2026-11-06
- **Spike:** `spikes/explorer-interception/` (throwaway, not in the solution or CI)
- **Manual checklist:** `spikes/explorer-interception/MANUAL-TEST.md`

## Context

Spec §5/§6/§12: Ferry takes over **Ctrl+V** and **drag-drop** copies and moves in File Explorer (always showing
its copy window), plus a quick-paste shortcut (**Ctrl+Shift+V** by default) that copies with no window. The
Explorer module must be thin, keep all logic out of Explorer's process, and fall back to the stock Windows copy
on any failure. Stability and speed come first (CLAUDE.md). Target: Windows 11 24H2, x64.

Three candidates were in scope (Task 4 brief):

- **(A)** Out-of-process paste catcher: low-level keyboard hook in the tray helper.
- **(B)** In-process left-drag interception: shell extension or hooking `IFileOperation` inside Explorer.
- **(C)** Right-drag `DragDropHandler` shell extension (documented API).

Controller ruling R12 for this spike: build (A) fully but log-only; build (C) but do not register it or restart
Explorer; evaluate (B) on paper only, with no code loaded into Explorer.

## What the spike built

| Part | Location | What it does |
|---|---|---|
| (A) PasteCatcher | `spikes/explorer-interception/PasteCatcher/` (C#, .NET 10 console) | `WH_KEYBOARD_LL` hook. On Ctrl+V, Ctrl+Shift+V or Shift+Insert, checks (cheaply, inside the hook) that the foreground window is `CabinetWClass`/`ExploreWClass` or the desktop (`Progman`/`WorkerW`), that keyboard focus is inside the folder view (`SHELLDLL_DefView`, not a rename box, the address bar or the navigation pane), that the clipboard has `CF_HDROP`, that AltGr/Win are not down and the key was not injected. A worker thread (STA) then reads `CF_HDROP` + `Preferred DropEffect` and finds the target folder through `IShellWindows` → `IServiceProvider(SID_STopLevelBrowser)` → `IShellBrowser::GetWindow`, matched to the **active tab** (the `ShellTabWindowClass` ancestor of the focused control). Logs what Ferry would do. Never copies. Passes keys through unless `--swallow`. |
| (C) DragDropHandler | `spikes/explorer-interception/DragDropHandler/` (C++, no ATL, static CRT, CFG + CET-compatible) | `IShellExtInit` + `IContextMenu`. Adds "Copy/Move here with Ferry (test)" to the right-drag menu when the target is a real file-system folder (`SFGAO_FILESYSTEM`+`SFGAO_FOLDER`, not `SFGAO_STREAM`, so zip files are excluded) and the data has `CF_HDROP`. On choose: sends op/target/sources to a named pipe with overlapped I/O inside a 250 ms total budget (100 ms connect wait + the rest for the write; `SECURITY_IDENTIFICATION` client), logs, returns. Never copies. Class-factory references and `LockServer` count towards `DllCanUnloadNow`. HKCU-only `register.ps1`/`unregister.ps1` (unregister also removes parent keys that register created, if empty), `watch-counter.ps1` (pipe listener + Explorer crash/handle watcher), `hung-server.ps1` (a helper that accepts and never reads), and `harness.exe` (drives the DLL in its own process, never Explorer). |

## Findings so far (automated, non-interactive)

- **(A) starts and stops cleanly.** Hook installs; both `--exit-after` and Ctrl+C unhook (`Hook removed: True`).
  Ctrl+C was checked by `PasteCatcher/test-ctrlc.ps1`, which runs the catcher in its own hidden console and raises
  `CTRL_C_EVENT` on that console with `GenerateConsoleCtrlEvent` (a console signal, no keystrokes): exit code 0,
  clean unhook line.
- **(A) idle cost:** 15 s idle with the hook installed used 16 ms CPU (one scheduler tick, ≈0.1 %), working set
  29 MB, private memory 9 MB. Two reasons to build the production helper with Native AOT: memory (the .NET
  runtime is most of that 29 MB against a 40 MB budget for the whole helper), and latency inside the hook
  callback, where a JIT compile on first use or a garbage-collection pause would delay every key press on the PC
  and could exceed `LowLevelHooksTimeout`. Under AOT the callback is precompiled; it must also allocate nothing.
- **(A) target lookup works on this PC:** `--probe` found 5 `IShellWindows` entries, all 5 resolved to an Explorer
  tab window through `IShellBrowser`, 4 of 5 showed a normal folder (the 5th is a virtual location such as Home).
  Enumeration took 144 ms cold (first COM activation); this runs on the worker, never in the hook.
- **(C) compiles** with both MSVC 14.29 (VS 2019 Build Tools) and the newest installed toolset, at `/W4 /WX`,
  exporting only `DllGetClassObject` and `DllCanUnloadNow`, importing only kernel32/user32/ole32/shell32.
- **(C) harness, 200 runs** of create → Initialize → QueryContextMenu → InvokeCommand → Release in a test process:
  200/200 menus, 200/200 invokes, handle delta 0, working set +96 KB. `DllCanUnloadNow` = S_FALSE while the
  harness still holds the class factory and S_OK once it is released. Bad verb refused; "This PC" and a `.zip`
  target refused. Pipe down: every invoke logged the fallback, worst case 16 ms. Pipe up (counter listening,
  400 ms between drags): 20/20 jobs received, 0 fallbacks, 0 ms each. Back-to-back with no gap, the
  single-instance test listener missed 19 of 20 (fallback taken cleanly): the production helper must keep
  several pipe instances listening.
- **(C) hung helper** (`hung-server.ps1`: accepts the connection, never reads, 1-byte buffer): the first invoke
  timed out at 250 ms and fell back; the next four found the pipe busy and fell back after 109–110 ms. All under
  the 350 ms target. (Before the fix the worst case was ~600 ms: 300 ms connect wait + 300 ms write timeout.)
- **Ctrl+Shift+V:** Microsoft's Windows 11 keyboard-shortcut list has no Ctrl+Shift+V entry for File Explorer.
  It lists Ctrl+Shift+V only under text editing ("paste as plain text"), which can apply in Explorer's text boxes
  (rename, address bar, search). Ferry only acts when focus is in the file list, so no clash is expected.
  Confirm with R1/R2. Source: <https://support.microsoft.com/en-us/windows/keyboard-shortcuts-in-windows-dcc61a57-8ff0-cffe-9796-cb9706c75eec>.

## Manual test results

Fill in from `MANUAL-TEST.md`. Leave "TBD" where a step was skipped.

| Code | Check | Result |
|---|---|---|
| R1 | Ctrl+Shift+V in a folder, no spike running | TBD |
| R2 | Ctrl+Shift+V on the desktop, no spike running | TBD |
| R3 | Ctrl+V: right target, all items, COPY | TBD |
| R4 | Cut then Ctrl+V reported as MOVE | TBD |
| R5 | Correct tab with two tabs open | TBD |
| R6 | Desktop paste target | TBD |
| R7 | Ctrl+Shift+V logged as quick paste | TBD |
| R8 | Shift+Insert | TBD |
| R9 | Rename box, address bar, nav pane, text clipboard, other apps, AltGr ignored | TBD |
| R10 | Largest hook time / key-to-decision time | TBD |
| R11 | CPU / memory / hook-call line on exit | TBD |
| R12 | `--swallow`: Explorer did not paste | TBD |
| R13 | `--swallow`: rename/address bar/nav pane/text paste still normal | TBD |
| R14 | Helper killed (End task): stock paste works at once, no stuck keys | TBD |
| R15 | Terminal closed: stock paste works | TBD |
| R16 | Right-drag items show (directly in the Win11 menu?) | TBD |
| R17 | Ferry item sends a job to the pipe | TBD |
| R18 | Pipe down: no felt delay | TBD |
| R19 | 200-drag counter final line + starting handles/memory | TBD |
| R20 | Any Explorer freeze/restart/red warning | TBD |
| R21 | Ferry items wrongly shown when dropping onto a zip file | TBD |

## Option (B) on paper: in-process left-drag interception

**How it would work.** Two routes exist, neither through a documented "replace the copy engine" API:

1. *Hook the copy call.* Get a DLL into `explorer.exe` (in practice, by registering it as some shell extension so
   Explorer loads it), then patch `IFileOperation::PerformOperations` (or the older `SHFileOperation`, or the
   `IDropTarget::Drop` of the folder view) with a detour. The detour reads the queued items and target, sends them
   to the helper over a pipe, and either cancels the original or calls through to it on any failure.
2. *Replace the drop target.* Register a drop handler for folders/drives so Explorer calls our `IDropTarget` on
   left-drop. Windows documents `DropHandler` for file types (dropping onto a file), not as a way to replace the
   drop behaviour of folders, drives or the folder background; using it there is unsupported territory.

The only documented, related hook, `ICopyHook`, cannot do the job: it is called for folders (not files) and can
only allow or cancel, not redirect.

**What existing tools are publicly known to do.** Several third-party copiers advertise "replace Explorer's copy"
for drag-drop and paste (TeraCopy's "default copy handler" option is the best known; Ultracopier/Supercopier use
an Explorer plugin that forwards copies to the app). The vendors do not document the mechanism, but it necessarily
means their code runs inside Explorer and intercepts Explorer's own operation. Tools that stay on documented APIs
(FastCopy's shell extension, Copy Handler's drag menu items) add right-drag and context-menu entries, i.e. option
(C). Public user reports for "replace copy" integrations include Explorer crashes or copy failures after Windows
updates. We did not verify any vendor's internals and do not rely on them.

**Stability risks.**
- A hook bug, or a Windows update that changes the hooked function's behaviour or calling pattern, crashes or hangs
  Explorer, which takes the taskbar and desktop with it. Explorer's internals change in feature updates (24H2 moved
  more of the UI to XAML/WinUI), and there is no compatibility promise for private behaviour.
- Shell extensions are loaded into **every** process that shows a shell view, including any app's Open/Save dialog.
  A hooking DLL either hooks those apps too or must detect and stay inert in them.
- Hooking `IFileOperation` also catches copies that Explorer performs for other reasons (and, inside other apps,
  their own programmatic copies), which would wrongly route them through Ferry.
- Hardware-enforced stack protection (CET shadow stacks) and Control Flow Guard are increasingly enforced for
  Windows processes; detour trampolines must be compatible, or Explorer is terminated.
- Re-entrancy: the detour runs on Explorer's UI thread; any wait on the helper blocks Explorer's UI.

**Security risks.** Code injected into Explorer runs with the user's full rights in a process every other app
trusts; a flaw there is an easy privilege-of-position target. Anti-malware and Attack Surface Reduction rules
treat in-process API patching as suspicious, so Ferry could be flagged or blocked, and users may be told to
disable it. A pipe client inside Explorer must also authenticate the server, or any process could impersonate
Ferry and receive file lists.

**Mitigations it would need.** Load only in `explorer.exe` (check in `DllMain`, inert elsewhere); a transactional,
CET/CFG-compatible detour library; call-through to the original on every error, timeout or unknown Windows build;
an allow-list of tested Windows builds with auto-disable on others; a registry kill switch; auto-disable after any
Explorer crash with our module on the stack; strict pipe server checks (process ID → signed Ferry image path);
non-blocking hand-off with a hard timeout; a soak test on every Windows feature update before release.

**Recommendation: reject (B) for v1.** Even with every mitigation it trades Explorer's stability, the top
priority, for a feature, and breaks on Windows updates outside our control. Revisit only if Microsoft ships a
supported copy-handler API.

## Decision (proposed)

- **Paste: adopt (A).** The tray helper owns a low-level keyboard hook on its own thread. Ctrl+V (and
  Shift+Insert) start Ferry with its window; Ctrl+Shift+V (configurable) is quick paste. Nothing runs inside
  Explorer. Pending R1–R15.
- **Drag: adopt (C) and leave left-drag to Windows.** Ferry adds "Copy here with Ferry" / "Move here with Ferry"
  to the right-drag menu through a thin native `DragDropHandler` that only hands the job to the helper. Left-drag
  stays the stock Windows copy. Pending R16–R21.
- This changes spec §12 ("always show the copy window for Ctrl+V **and drag-drop**"): left-drag is not taken over.
  **Needs the user's decision before M2** (Task 4 Step 4: "Ctrl+V auto, drag via right-drag menu").

## Production requirements

### Paste catcher (A)

1. Hook on a dedicated thread with its own message loop; the callback does only cheap user32 checks and queues
   work. Windows silently removes a low-level hook whose callback exceeds `LowLevelHooksTimeout`, so the callback
   must stay well under 1 ms, and a watchdog must detect removal and reinstall.
2. Confirm the foreground window really belongs to `explorer.exe` (cache Explorer process IDs); window class names
   alone can be faked by any app.
3. Act only when focus is inside `SHELLDLL_DefView` and not an edit control; never on injected keys; never with
   Alt/AltGr or Win held. Swallow the key-down and its matching key-up, and auto-repeat.
4. **Swallow-then-fallback. This changes the Task 4 brief**, which said "swallow the key only if Ferry accepts
   the job". That is not possible safely: the hook must return its swallow/pass decision within a fraction of a
   millisecond, and finding out whether Ferry accepts (reading the clipboard, asking Explorer for the target over
   COM, asking the helper) takes 10–150 ms and calls into Explorer, which inside the hook risks the
   `LowLevelHooksTimeout` removal and delays every key press on the PC. So the hook swallows when the cheap checks
   pass, and acceptance is decided afterwards. If Ferry then declines the job (virtual target such as Home/This
   PC/phone, clipboard without `CF_HDROP`, helper busy or failing), the helper performs the stock operation itself
   through `IFileOperation` with the clipboard's data object (`OleGetClipboard` →
   `SHCreateShellItemArrayFromDataObject`), which shows Windows' normal copy UI. The user sees the same result as
   a stock paste. Do not re-inject keystrokes.
5. If the helper process dies, the hook dies with it and Windows pastes as normal (R14/R15 confirm).
6. Not covered by the hook: Paste from Explorer's right-click menu or command bar. Cover with a "Paste with Ferry"
   context-menu command (the planned `IExplorerCommand` submenu), not by hooking Explorer.
7. Elevated Explorer windows (rare) are out of reach of a non-elevated hook (UIPI); stock paste applies.
8. Settings: the quick-paste shortcut is changeable and Ferry warns if the user picks one Explorer uses.
9. Native AOT for the helper, and no allocations in the hook callback, so no JIT or GC pause can land inside it.

### Right-drag handler (C)

1. Native C++, no exceptions escape any COM method, no blocking without a timeout, no work beyond collecting paths.
2. Pipe: overlapped I/O with **one total time budget for the whole hand-off** (connect wait + write + cancel),
   under 350 ms; the spike uses 250 ms (100 ms connect wait, the rest for the write). Client uses
   `SECURITY_IDENTIFICATION`; verify the server process (`GetNamedPipeServerProcessId` → image path under Ferry's
   install folder → Authenticode signature with the pinned certificate) before sending; per-user, per-session
   pipe name; the helper keeps several instances listening. Versioned, length-prefixed message; large selections
   streamed, not built as one string.
3. On any failure: perform the stock copy/move through `IFileOperation` (in Explorer, the same call Windows
   uses), never silently drop the job.
4. Show items only for real file-system folders (`SFGAO_FILESYSTEM` + `SFGAO_FOLDER`, not `SFGAO_STREAM`) with
   `CF_HDROP` (the harness checks "This PC" and a `.zip`).
5. **COM unload rules:** every class-factory reference (`AddRef`/`Release`) and every `LockServer(TRUE)` counts
   towards `DllCanUnloadNow`, as do live objects. Otherwise COM can unload the DLL while Explorer still holds the
   factory, and the next call crashes Explorer. The harness checks S_FALSE while the factory is held and S_OK
   after release.
6. **The hung-helper path must be tested** for every build: a helper that accepts the connection and never reads
   (spike: `hung-server.ps1`), and a helper that is not listening. Both must fall back within the budget.
7. Signed with the project certificate; CFG + CET-compatible; static CRT; registered per user; unregistering
   removes every key it created.

### Stability test the production build must pass (before M2 ships)

- **Drag:** 1,000 right-drag operations on Windows 11 24H2 (spike target was 200), mixing Ferry items, Esc, stock
  items, multi-thousand-file selections, long paths (>260 chars), Unicode names, network-share targets and
  virtual targets. Pass = Explorer process ID unchanged, zero Application Error/Hang events (1000/1002) for
  `explorer.exe`, Explorer handle count back within ±200 of the start after settling, every invoke under 50 ms
  with the helper up and under 350 ms with the helper down or hung (a test server that accepts but never reads).
- **Paste:** 500 Ctrl+V / Ctrl+Shift+V presses across folders, tabs and the desktop; hook callback max < 1 ms,
  average < 0.1 ms; the hook survives sleep/resume, lock screen, UAC prompts and Explorer restarts; killing the
  helper restores stock paste at once with no stuck modifier keys; rename box, address bar, search box and
  navigation pane behave exactly as stock.
- **Both:** helper idle ≈0 % CPU within the spec budget; automated parts run in CI where possible (the harness
  pattern for the handler), the rest as a scripted manual checklist like `MANUAL-TEST.md`.

## Rejected options

| Option | Why rejected |
|---|---|
| (B) Hooking `IFileOperation`/`SHFileOperation`/drop target inside Explorer | Unsupported, breaks on Windows updates, loads into every app's file dialogs, flagged by security tools; risks Explorer itself (see above). |
| `DropHandler` registered for folders/drives | Documented for file types only; replacing folder drop behaviour is unsupported and in-process. |
| `ICopyHook` | Folders only, can only allow/cancel, cannot redirect. |
| Clipboard listener (`AddClipboardFormatListener`) | Sees copy/cut, never the paste or its target. |
| Low-level mouse hook for left-drag | Out of process it cannot read the dragged data object or the drop result. |
| Sending Explorer's internal "paste" command ID as fallback | Undocumented command IDs; use `IFileOperation` from the helper instead. |

## Consequences

- No Ferry code runs in Explorer for paste; Explorer's stability is unaffected by the paste path.
- Right-drag adds a small, documented, in-process handler: low risk, but it must meet the requirements above and
  goes through `/security-review` and the threat model (new trust boundary: Explorer → helper pipe).
- Left-drag copies stay with Windows. Users who want Ferry for drag use right-drag (or Ctrl+C/Ctrl+V).
