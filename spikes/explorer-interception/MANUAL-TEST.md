# Explorer interception spike: manual test checklist

> **Read this first: keep your real files safe**
>
> - **Only ever use the `FerryTest` folder** that step 3 makes on your desktop. Every file you copy, cut or drag,
>   and every folder you paste or drop into, must be inside `FerryTest` (the two desktop steps only *copy* a
>   FerryTest file, see their notes).
> - **In the right-drag menu, never choose Windows' own "Move here" or "Copy here".** Choose only the
>   "... with Ferry (test)" items, or press **Esc**.
> - Never drag anything onto a drive, "This PC", "Home", or a folder outside `FerryTest`.
> - If you are unsure about a step, skip it and write "skipped" for its result.

This checklist is for you to run by hand. The test tools never copy, move or delete anything. Where a step says
Explorer pastes, that is normal Windows doing it, inside `FerryTest`.

Each result has a code (R1, R2, ...). Write what you saw next to the same code in
`docs/adr/0001-explorer-interception.md`, section "Manual test results".

Time needed: about 20 minutes for Part A. Part C is optional and takes about 30 more minutes (200 drags).

---

## Part 0: Get ready

1. Open **Terminal** (PowerShell) in the repo folder.
2. Build the paste catcher:
   ```powershell
   dotnet build spikes\explorer-interception\PasteCatcher -c Release
   ```
   It should end with "Build succeeded".
3. Make the test folder (dummy files only; it opens in Explorer when done):
   ```powershell
   powershell -ExecutionPolicy Bypass -File spikes\explorer-interception\make-test-files.ps1
   ```
   You get `Desktop\FerryTest` with: `From` (5 files), `To`, `Empty`, `Drop1`, `Drop2`, `Many` (300 files) and
   `Archive.zip`. All test files are named `FerryTest-...`.

## Part 1: Is Ctrl+Shift+V free in Explorer? (no spike running)

4. Make sure the paste catcher is **not** running.
5. In `FerryTest\From`, select `FerryTest-file1.txt` and press **Ctrl+C** (copy, not cut).
6. Open `FerryTest\To`, click an empty spot in the file list, press **Ctrl+Shift+V**.
   - Expected: nothing happens (no file appears, no menu, no sound).
   - **R1:** what happened?
7. Desktop: click an empty spot on the desktop and press **Ctrl+Shift+V**.
   - Expected: nothing happens.
   - The clipboard holds only a *copy* of a FerryTest file, so the worst case is that a copy of
     `FerryTest-file1.txt` appears on your desktop. If it does, note it and delete that one copy.
   - **R2:** what happened?

## Part A: Paste catcher, safe mode (Explorer still pastes)

8. **Rebuild first** (the paste catcher was fixed after your first try: it no longer makes the error sound on the
   first Ctrl+V). If it is still running from before, press Ctrl+C in its window. Then:
   ```powershell
   dotnet build spikes\explorer-interception\PasteCatcher\PasteCatcher.csproj -c Release
   ```
   It should end with "Build succeeded". Then start it:
   ```powershell
   spikes\explorer-interception\PasteCatcher\bin\Release\net10.0-windows\PasteCatcher.exe
   ```
   Expected first lines: `Clipboard snapshot (start, ...)`, then
   `Keyboard hook installed. Mode: pass-through (Explorer still pastes normally).`
   Put the Terminal window next to Explorer so you can see both.
   From now on, each time you copy or cut something, a `Clipboard snapshot (change, ...)` line appears. That is
   the catcher reading the clipboard when it changes, so it never has to touch it when you paste.
9. **Copy:** in `FerryTest\From`, select `FerryTest-file1.txt` and `FerryTest-file2.txt`, press **Ctrl+C**.
   - Expected in Terminal straight away: `Clipboard snapshot (change, sequence N): 2 file(s), copy, ...`.
   Go to `FerryTest\To`, click an empty spot, press **Ctrl+V** once.
   - Expected in Explorer: the two files are pasted into `To` **on the first press**, with **no error sound**.
   - Expected in Terminal: `Ctrl+V in File Explorer [explorer]. key passed to Explorer.`, then `WOULD COPY`, then
     `into:` with the full path of `To`, the two file names, and `clipboard: snapshot from ... not opened at paste time.`
   - If you press Ctrl+V within a split second of Ctrl+C, it may say `clipboard changed, snapshot stale ...;
     Windows pastes`. That is expected and safe: Windows pastes as normal.
   - **R3:** was the target folder right? Were both files listed? Did it say COPY? First press pasted, no sound?
10. **Cut:** in `FerryTest\To`, select the two pasted files and press **Ctrl+X**. Open `FerryTest\Drop1`, click
    an empty spot and press **Ctrl+V**.
    - Expected in Terminal: `WOULD MOVE (cut; Preferred DropEffect=2)` and target `Drop1`.
    - Explorer really moves the two test files from `To` to `Drop1` (normal Windows, all inside FerryTest).
    - **R4:** did it say MOVE?
11. **Tabs:** in the same Explorer window press **Ctrl+T** for a second tab and open `FerryTest\From` there.
    Copy `FerryTest-file3.txt`. Switch to the other tab, open `FerryTest\To`, press **Ctrl+V**.
    - Expected: `into:` shows the `To` path (the tab you are looking at, not the other tab).
    - **R5:** right tab?
12. **Quick paste:** copy `FerryTest-file5.txt`, click in `FerryTest\To`, press **Ctrl+Shift+V**.
    - Expected: `Ctrl+Shift+V (quick paste)` and the "Ferry would copy with no window" line. Explorer does nothing.
    - **R7:** right?
13. **Shift+Insert:** copy `FerryTest-file4.txt`, click in `FerryTest\Drop2`, press **Shift+Insert**.
    - Expected: `Shift+Insert` logged, Explorer pastes into `Drop2`.
    - **R8:** did Explorer paste, and was it logged?
14. **Places it must ignore.** First copy `FerryTest-file1.txt` (Ctrl+C, not cut). For each case the Terminal
    should show `ignored: ...` (or nothing), and Explorer must behave exactly as normal:
    - a. In `FerryTest\From`, select a file, press **F2** to rename, press **Ctrl+V**, then press **Esc**
      (Esc cancels the rename).
    - b. Press **Alt+D** (address bar), press **Ctrl+V**, then **Esc**.
    - c. In the left pane, expand `FerryTest` and click **`Drop2`** there (only that folder), press **Ctrl+V**.
      Explorer may paste the copy into `Drop2`; that is fine.
    - d. Copy some text in Notepad, go to `FerryTest\To`, press **Ctrl+V** (expected: `clipboard has no file list`).
    - e. Press **Ctrl+V** in Notepad (expected: nothing logged at all).
    - f. If you use a keyboard layout with **AltGr**, press **AltGr+V** in `FerryTest\To` (expected: nothing logged).
    - **R9:** any case where it did NOT ignore, or where Explorer behaved differently from normal?
15. **Speed:** look at the `timings:` lines from steps 9 to 13.
    - **R10:** the largest `hook` value and the largest `key-to-decision` value you saw.
16. Press **Ctrl+C** in the Terminal.
    - Expected: `Stopped. Hook removed: True.` and a line with CPU and memory.
    - **R11:** copy that line and the `Hook calls:` line.

## Part A2: Paste catcher, "take over" mode (Explorer does NOT paste)

17. Start it in take-over mode:
    ```powershell
    spikes\explorer-interception\PasteCatcher\bin\Release\net10.0-windows\PasteCatcher.exe --swallow
    ```
18. Copy `FerryTest-file1.txt` (Ctrl+C), open `FerryTest\Empty`, click an empty spot, press **Ctrl+V**.
    - Expected: Terminal says `key SWALLOWED`, and **no file appears** in `Empty` (this is what Ferry will do,
      except Ferry will then copy it itself).
    - **R12:** did `Empty` stay empty?
19. **Desktop** (copy only): with `FerryTest-file1.txt` still copied, click an empty spot on the desktop and
    press **Ctrl+V**.
    - Expected: Terminal shows `Ctrl+V in Desktop`, `key SWALLOWED`, `into:` your Desktop folder, and **nothing
      appears** on the desktop.
    - If a copy of `FerryTest-file1.txt` does appear, the desktop was not recognised: note it and delete that copy.
    - **R6:** right?
20. Repeat step 14 a to d in take-over mode. Rename box, address bar, left pane (`Drop2` only) and text paste
    must all behave exactly as normal.
    - **R13:** all normal?
21. **Kill test:** leave it running. Open Task Manager, find `PasteCatcher`, choose **End task**.
    Straight away, copy `FerryTest-file2.txt` and press **Ctrl+V** in `FerryTest\Empty`.
    - Expected: Explorer pastes normally at once (no delay, no stuck keys).
    - **R14:** did it paste normally? Any stuck Ctrl key or delay?
22. Start it again with `--swallow`, then **close the Terminal window** with the X. Copy `FerryTest-file3.txt`
    and press **Ctrl+V** in `FerryTest\Empty`.
    - **R15:** normal paste?

Part A is done. Keep `FerryTest` if you will do Part C; otherwise go to "Clean up".

---

## Part C (optional): Right-drag menu handler

This part loads a small test add-on into File Explorer, for your user account only. It never copies anything.
You remove it at the end (step 33). If anything goes wrong with Explorer, run step 33 and sign out and back in.

**Reminder: only choose the "with Ferry (test)" items or Esc. Never Windows' own "Move here" / "Copy here".
Only drag FerryTest files onto FerryTest folders.**

23. Build it (needs Visual Studio's C++ tools, which this PC has):
    ```powershell
    spikes\explorer-interception\DragDropHandler\build.cmd
    ```
    Expected last line: `Built ...\bin\FerrySpikeDragDrop.dll`.
24. Optional self-test outside Explorer (runs it 200 times in a test program):
    ```powershell
    spikes\explorer-interception\DragDropHandler\bin\harness.exe spikes\explorer-interception\DragDropHandler\bin\FerrySpikeDragDrop.dll 200
    ```
    Expected last line: `PASS`.
25. Register it for your account:
    ```powershell
    powershell -ExecutionPolicy Bypass -File spikes\explorer-interception\DragDropHandler\register.ps1
    ```
26. Close all File Explorer windows, then open `FerryTest` again in a **new** window.
27. Open a **second** Terminal and start the counter:
    ```powershell
    powershell -ExecutionPolicy Bypass -File spikes\explorer-interception\DragDropHandler\watch-counter.ps1
    ```
28. With the **right** mouse button, drag `FerryTest\From\FerryTest-file1.txt` onto the `To` folder and let go.
    - Expected: the menu shows **Copy here with Ferry (test)** and **Move here with Ferry (test)** next to Windows'
      own items.
    - If they are missing: press **Esc**, close all Explorer windows and open a new one; if still missing, sign out
      and back in.
    - **R16:** do the items show? Do they show in the Windows 11 menu directly (not under "Show more options")?
29. Right-drag the same file onto `To` again and choose **Copy here with Ferry (test)**.
    - Expected: no file is copied (correct, it is a test). The counter shows `job #1 received: op=copy, count=1`.
    - **R17:** right?
30. **Ferry not running:** press Ctrl+C in the counter, then start it with `-NoPipe`:
    ```powershell
    powershell -ExecutionPolicy Bypass -File spikes\explorer-interception\DragDropHandler\watch-counter.ps1 -NoPipe
    ```
    Right-drag a file from `From` onto `To` and choose a Ferry item.
    - Expected: Explorer stays quick, `fallbacks` goes up by 1. (The real Ferry would do a normal Windows copy here.)
    - **R18:** any delay you could feel?
31. Stop it with Ctrl+C and start it again **without** `-NoPipe` (step 27).
32. **Stability check: 200 right-drags.** The counter counts for you; watch "Drags (menus shown): N/200".
    Every drag starts in `FerryTest` and ends on a `FerryTest` folder. Mix these, in any order:
    - about 100 times: drag one or a few files from `From` onto `To`, `Drop1` or `Drop2` (in the file list or in
      the left pane under `FerryTest`) and choose a **Ferry** item (copy or move);
    - about 60 times: the same drags, but press **Esc** to close the menu;
    - about 30 times: open `FerryTest\Many`, press **Ctrl+A** (all 300 files), right-drag them onto
      `FerryTest\Drop1` in the left pane and choose a **Ferry** item. Nothing is copied;
    - about 10 times: right-drag a file onto `FerryTest\Archive.zip` and press **Esc**. The Ferry items should
      **not** be in that menu.
    - A few times: hold **Ctrl** and **left**-drag a file from `From` to `To` (a normal Windows copy, inside
      FerryTest). It must work exactly as before.
    - **R19:** the final counter line (drags, Ferry items chosen, jobs received, fallbacks, Explorer handles, memory,
      crashes). Also the handles/memory numbers from the counter's first line.
    - **R20:** did Explorer ever freeze, flicker, restart, or the taskbar disappear? Did the counter print any red
      WARNING?
    - **R21:** did the Ferry items wrongly show when dropping onto `Archive.zip`?
33. Stop the counter with Ctrl+C. Remove the add-on:
    ```powershell
    powershell -ExecutionPolicy Bypass -File spikes\explorer-interception\DragDropHandler\unregister.ps1
    ```
    Then sign out and back in (this unloads it from Explorer).

## Clean up

34. The paste catcher installs nothing; once its window is closed it is gone.
35. Delete the test folder (it asks you to type YES; it only deletes the `FerryTest` folder it made):
    ```powershell
    powershell -ExecutionPolicy Bypass -File spikes\explorer-interception\make-test-files.ps1 -Remove
    ```
36. If you did Part C, delete its log folder:
    ```powershell
    Remove-Item -Recurse -Force "$env:LOCALAPPDATA\FerrySpike"
    ```
37. If a `FerryTest-file1.txt` copy appeared on your desktop in step 7 or 19, delete it.
38. Fill in R1 to R21 in `docs/adr/0001-explorer-interception.md` and tell Claude you are done.
