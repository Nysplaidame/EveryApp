# Explorer interception spike: manual test checklist

This is for you to run by hand. Nothing here copies, moves or deletes your files except where a step says
Windows itself will (normal Explorer pasting). Use the small test folders from step 1 only.

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
3. Make test folders with a few small files (on your desktop, in a folder called `FerryTest`):
   ```powershell
   $t = Join-Path ([Environment]::GetFolderPath('Desktop')) 'FerryTest'
   New-Item -ItemType Directory -Force "$t\From", "$t\To" | Out-Null
   1..5 | ForEach-Object { Set-Content "$t\From\file$_.txt" "test $_" }
   explorer $t
   ```

## Part 1: Is Ctrl+Shift+V free in Explorer? (no spike running)

4. Make sure the paste catcher is **not** running.
5. In `FerryTest\From`, select `file1.txt` and press **Ctrl+C**.
6. Open `FerryTest\To`, click an empty spot in the file list, press **Ctrl+Shift+V**.
   - Expected: nothing happens (no file appears, no menu, no sound).
   - **R1:** what happened?
7. Click an empty spot on the desktop and press **Ctrl+Shift+V**.
   - Expected: nothing happens.
   - **R2:** what happened?

## Part A: Paste catcher, safe mode (Explorer still pastes)

8. Start it:
   ```powershell
   spikes\explorer-interception\PasteCatcher\bin\Release\net10.0-windows\PasteCatcher.exe
   ```
   Expected first line: `Keyboard hook installed. Mode: pass-through (Explorer still pastes normally).`
   Put the Terminal window next to Explorer so you can see both.
9. **Copy:** in `From`, select `file1.txt` and `file2.txt`, press **Ctrl+C**. Go to `To`, click an empty spot, press **Ctrl+V**.
   - Expected in Explorer: the two files are pasted as usual.
   - Expected in Terminal: `Ctrl+V in File Explorer [explorer]. key passed to Explorer.`, then `WOULD COPY`, then
     `into:` with the full path of `To`, then the two file names.
   - **R3:** was the target folder right? Were both files listed? Did it say COPY?
10. **Cut:** in `To`, select the two pasted files and press **Ctrl+X**. Go up to the `FerryTest` folder itself,
    click an empty spot and press **Ctrl+V**.
    - Expected in Terminal: `WOULD MOVE (cut; Preferred DropEffect=2)` and target `FerryTest`.
    - Explorer really moves them (that is normal Windows). Delete those two files afterwards if you like.
    - **R4:** did it say MOVE?
11. **Tabs:** open a second tab in the same Explorer window (Ctrl+T) and go to `From`. Copy `file3.txt`, switch to
    a tab showing `To`, press **Ctrl+V**.
    - Expected: `into:` shows the `To` path (the tab you are looking at, not the other tab).
    - **R5:** right tab?
12. **Desktop:** copy `file4.txt`, click an empty spot on the desktop, press **Ctrl+V**.
    - Expected: `Ctrl+V in Desktop`, `into:` your Desktop folder. Windows pastes the file on the desktop; delete it after.
    - **R6:** right?
13. **Quick paste:** copy `file5.txt`, click in `To`, press **Ctrl+Shift+V**.
    - Expected: `Ctrl+Shift+V (quick paste)` and the "Ferry would copy with no window" line. Explorer does nothing.
    - **R7:** right?
14. **Shift+Insert:** copy a file, click in `To`, press **Shift+Insert**.
    - Expected: `Shift+Insert` logged, Explorer pastes.
    - **R8:** did Explorer paste, and was it logged?
15. **Places it must ignore.** For each one, the Terminal should show `ignored: ...` (or nothing), and Explorer
    must behave exactly as normal:
    - a. Select a file, press **F2** to rename, press **Ctrl+V** (text from the clipboard goes into the name box,
      or nothing if the clipboard holds files). Press Esc.
    - b. Press **Alt+D** (address bar), press **Ctrl+V**. Press Esc.
    - c. Click a folder in the left pane (navigation pane), press **Ctrl+V**.
    - d. Copy some text in Notepad, go to `To`, press **Ctrl+V** (expected: `clipboard has no file list`).
    - e. Press **Ctrl+V** in Notepad or a browser (expected: nothing logged at all).
    - f. If you use a keyboard layout with **AltGr**, press **AltGr+V** in Explorer (expected: nothing logged).
    - **R9:** any case where it did NOT ignore, or where Explorer behaved differently from normal?
16. **Speed:** look at the `timings:` lines from steps 9 to 14.
    - **R10:** the largest `hook` value and the largest `key-to-decision` value you saw.
17. Press **Ctrl+C** in the Terminal.
    - Expected: `Stopped. Hook removed: True.` and a line with CPU and memory.
    - **R11:** copy that line and the `Hook calls:` line.

## Part A2: Paste catcher, "take over" mode (Explorer does NOT paste)

18. Start it in take-over mode:
    ```powershell
    spikes\explorer-interception\PasteCatcher\bin\Release\net10.0-windows\PasteCatcher.exe --swallow
    ```
19. Copy `file1.txt`, go to an empty folder (make `FerryTest\Empty`), press **Ctrl+V**.
    - Expected: Terminal says `key SWALLOWED`, and **no file appears** in Explorer (this is what Ferry will do,
      except Ferry will then copy it itself).
    - **R12:** did Explorer stay empty?
20. Repeat step 15 a to d in take-over mode. Rename box, address bar, navigation pane and text paste must all work
    exactly as normal.
    - **R13:** all normal?
21. **Kill test:** leave it running. Open Task Manager, find `PasteCatcher`, choose **End task**.
    Straight away, copy a file and press **Ctrl+V** in `Empty`.
    - Expected: Explorer pastes normally at once (no delay, no stuck keys).
    - **R14:** did it paste normally? Any stuck Ctrl key or delay?
22. Start it again with `--swallow`, then **close the Terminal window** with the X. Try Ctrl+V in Explorer again.
    - **R15:** normal paste?

Part A is done. You can delete the `FerryTest` folder.

---

## Part C (optional): Right-drag menu handler

Read first: this part loads a small test add-on into File Explorer, for your user account only. It never copies
anything. You can remove it at the end (step 36). If anything goes wrong with Explorer, run step 36 and sign out
and back in.

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
26. Open a **new** File Explorer window. Make `FerryTest\From` and `FerryTest\To` again (step 3).
27. Open a **second** Terminal and start the counter:
    ```powershell
    powershell -ExecutionPolicy Bypass -File spikes\explorer-interception\DragDropHandler\watch-counter.ps1
    ```
28. With the **right** mouse button, drag `file1.txt` onto the `To` folder and let go.
    - Expected: the menu shows **Copy here with Ferry (test)** and **Move here with Ferry (test)** next to Windows'
      own Copy here / Move here.
    - If they are missing: close all Explorer windows and open a new one; if still missing, sign out and back in.
    - **R16:** do the items show? Do they show in the Windows 11 menu directly (not under "Show more options")?
29. Choose **Copy here with Ferry (test)**.
    - Expected: no file is copied (correct, it is a test). The counter shows `job #1 received: op=copy, count=1`.
    - **R17:** right?
30. **Ferry not running:** press Ctrl+C in the counter, then start it with `-NoPipe`:
    ```powershell
    powershell -ExecutionPolicy Bypass -File spikes\explorer-interception\DragDropHandler\watch-counter.ps1 -NoPipe
    ```
    Right-drag a file again and choose a Ferry item.
    - Expected: Explorer stays quick, `fallbacks` goes up by 1. (The real Ferry would do a normal Windows copy here.)
    - **R18:** any delay you could feel?
31. Stop it with Ctrl+C and start it again **without** `-NoPipe` (step 27).
32. **Stability check: 200 right-drags.** The counter counts for you; watch the "Drags (menus shown): N/200" number.
    Mix these up, in any order:
    - about 100 times: choose a Ferry item (copy or move);
    - about 60 times: press **Esc** to cancel the menu;
    - about 20 times: drag **many** files at once (select all in a folder with a few hundred files, e.g. a photos
      folder) and choose a Ferry item. Nothing is copied;
    - about 20 times: drag onto other places: a drive in "This PC", the desktop, a folder in the left pane, and
      onto "This PC" or "Home" themselves (Ferry items should NOT show there).
    - Also do a few normal **left** drags between `From` and `To`: they must work exactly as before.
    - **R19:** the final counter line (drags, Ferry items chosen, jobs received, fallbacks, Explorer handles, memory,
      crashes). Also the handles/memory numbers from the counter's first line.
    - **R20:** did Explorer ever freeze, flicker, restart, or the taskbar disappear? Did the counter print any red
      WARNING?
    - **R21:** did the Ferry items wrongly show on This PC / Home / phones / zip files?
33. Stop the counter with Ctrl+C.

## Undo everything

34. Paste catcher: it is already stopped (nothing to undo; it installs nothing).
35. Delete the `FerryTest` folder from your desktop.
36. Right-drag handler (only if you did Part C):
    ```powershell
    powershell -ExecutionPolicy Bypass -File spikes\explorer-interception\DragDropHandler\unregister.ps1
    ```
    Then sign out and back in (this unloads it from Explorer). Finally delete the log folder:
    ```powershell
    Remove-Item -Recurse -Force "$env:LOCALAPPDATA\FerrySpike"
    ```
37. Fill in R1 to R21 in `docs/adr/0001-explorer-interception.md` and tell Claude you are done.
