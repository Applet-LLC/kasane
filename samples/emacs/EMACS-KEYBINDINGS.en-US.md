# Emacs-Style Key Operation Example

The `samples\emacs\` folder in the installation folder contains a setup that makes basic Emacs cursor movement and editing, operations that start with C-x, C-q (send the next key as it is), and the Ctrl+Space mark mode available in ordinary Windows apps. The basic setup is 3 layers, 36 macros, and 35 key overrides. It does not depend on the tray. It works even when the tray is not running.

The behavior was checked with the built-in keyboard by looking at the sequence of keys that reach Windows (representative movement and editing operations, the one key after C-x, the one key after C-q, and mark mode). The 32 movement and editing operations were also checked in Notepad. The actual behavior in apps was not checked for the C-x operations, C-q, and mark mode. Some apps may not behave as expected.

## What is included

| File | Contents |
|---|---|
| `emacs.kasane.json` | The setup itself. A kasane exchange file that holds the layers, macros, and key overrides |
| `apply-emacs.ps1` | A script that reads the file above and writes it to the keyboard with KeymapTool |

## Assigned keys

Either Ctrl works, and either Alt works.

| Key you press | Key sent | Emacs operation |
|---|---|---|
| Ctrl+F / Ctrl+B | → / ← | Forward / back one character |
| Ctrl+N / Ctrl+P | ↓ / ↑ | Next line / previous line |
| Ctrl+A / Ctrl+E | Home / End | Start / end of line |
| Ctrl+V / Alt+V | PageDown / PageUp | Forward / back one screen |
| Alt+F / Alt+B | Ctrl+→ / Ctrl+← | Forward / back one word |
| Alt+Shift+, / Alt+Shift+. | Ctrl+Home / Ctrl+End | Start / end of the document (M-< and M->) |
| Ctrl+D / Ctrl+H | Delete / BackSpace | Delete the next / previous character |
| Alt+D / Alt+BackSpace | Ctrl+Delete / Ctrl+BackSpace | Delete the next / previous word |
| Ctrl+K | Shift+End, then Ctrl+X | Cut to the end of the line |
| Ctrl+W / Alt+W | Ctrl+X / Ctrl+C | Cut / copy |
| Ctrl+Y | Ctrl+V | Paste |
| Ctrl+/ | Ctrl+Z | Undo |
| Ctrl+S | Ctrl+F | Search |
| Ctrl+G | Esc | Cancel |
| Ctrl+M / Ctrl+J | Enter | New line |
| Ctrl+O | Enter, then ← | Open an empty line below |
| Ctrl+Shift+F / B / N / P / A / E | Shift+→ / ← / ↓ / ↑ / Home / End | Extend the selection while moving |

### Extending the selection

The Ctrl+Shift combinations extend the selection without entering mark mode. Each time you press a movement key while holding Ctrl and Shift, the selection grows by one step. You do not need to set a starting point. Where the cursor is now is the starting point.

Example: select from the middle of a line to the end of the line.

1. Put the cursor where you want to start selecting.
2. Hold Ctrl and Shift and press E. The text from the cursor to the end of the line is selected.
3. To cut the range as it is, release Shift and press W while holding Ctrl.

Example: select down 3 lines. Hold Ctrl and Shift and press N 3 times. If you hold it down, auto-repeat keeps extending the selection.

- Starting a selection and continuing it work the same way. The selection extends only while both Ctrl and Shift are held.
- If you release Shift partway and press Ctrl+F or the like, the selection is cleared and only the cursor moves.
- To stop selecting, use movement without Shift (Ctrl+F and so on). Ctrl+G only sends Esc, so in some apps it does not clear the selection.
- Operations that select by word (M-S-f and so on) are not included.

## Operations that start with C-x

After you press Ctrl+X, only the one key you press next gets a different assignment. It is the same whether you keep holding Ctrl or release it first (C-x C-s and C-x s both save).

| Key you press | Key sent | Emacs operation |
|---|---|---|
| C-x C-s | Ctrl+S | Save |
| C-x C-f | Ctrl+O | Open a file |
| C-x C-w | F12 | Save as (F12 in Word and similar apps) |
| C-x b | Ctrl+Tab | Next tab (switch buffer) |
| C-x k | Ctrl+W | Close the tab (kill the buffer) |
| C-x h | Ctrl+A | Select all |
| C-x u | Ctrl+Z | Undo |

- If you press a key that is not in the table above after C-x, that key works normally (if you are holding Ctrl, it stays a Ctrl key). The effect of C-x ends after the next one key.
- If you press C-x twice in a row, the wait stays at one.
- Windows Ctrl+X (cut) becomes the C-x prefix. Use C-w to cut.

## C-q (send the next key as it is)

After you press Ctrl+Q, only the one key you press next is delivered as a Ctrl key as it is, without the Emacs-style replacement. It is used the same way as nodoka's `key C-Q = &Prefix(KeymapDefault)`. For example, C-q C-f sends Ctrl+F to the app as it is, instead of →.

- The targets are A to Z and Space (C-q C-Space does not enter mark mode; it sends Ctrl+Space). Other keys still get the normal replacement after C-q (for example, C-/ becomes Ctrl+Z).
- The effect of C-q ends after the next one key. If you press C-f again, it becomes →.
- The mechanism is the same as C-x: each key on layer 2 is assigned a macro that sends a modified key such as `LCTL(KC_F)`. This is because if a modified key were placed directly on a layer, its output would hit another replacement.

## Mark mode (Ctrl+Space)

Ctrl+Space enters mark mode. While you are in it, Shift is added to the movement keys (←↑→↓, Home, End, PageUp, PageDown), and the selection grows with every move. Shift is also added to assignments that send a movement key, such as Ctrl+F and Alt+F.

| Key you press during mark mode | Key sent |
|---|---|
| Ctrl+F / Ctrl+B / Ctrl+N / Ctrl+P | Shift+→ / ← / ↓ / ↑ |
| Ctrl+A / Ctrl+E | Shift+Home / Shift+End |
| Ctrl+V / Alt+V | Shift+PageDown / Shift+PageUp |
| Alt+F / Alt+B | Ctrl+Shift+→ / ← |
| The movement keys themselves, such as the arrow keys | The same key with Shift |

Mark mode ends when you press a key other than a movement key or a modifier key. It ends after that key has been sent, so C-w (cut) and Alt+W (copy) work as before, and then mark mode ends.

- **It ends with**: C-w, Alt+W, Ctrl+G (sends Esc and ends), Enter, typing characters, BackSpace, and so on. It also ends when the foreground window changes (while the tray is running).
- **Pressing Ctrl+Space again keeps mark mode on.** The selection does not change (the same behavior as nodoka).
- **Changing the selection with the mouse does not end mark mode.** To end it, press one key other than movement.
- The start of the range is where the cursor was when you started moving. Pressing Ctrl+Space does not change what you see.

## How to write it to the keyboard

### From the command line

In PowerShell with administrator rights, move to the `samples/emacs` folder and run it. `<ID>` is the 8-digit hexadecimal number shown by `KeymapTool --list`.

PowerShell often does not allow running scripts by default, so run it with `-ExecutionPolicy RemoteSigned`. This setting applies only to this command.

```
KeymapTool --list
PowerShell -ExecutionPolicy RemoteSigned .\apply-emacs.ps1 -Id <ID>
```

It takes effect right after you run it. To undo it, add `-Remove`.

```
PowerShell -ExecutionPolicy RemoteSigned .\apply-emacs.ps1 -Id <ID> -Remove
```

Files you downloaded and extracted are marked by Windows as "from the internet", and RemoteSigned may stop them from running. In that case, run `Unblock-File .\apply-emacs.ps1` once and try again.

If KeymapTool is in a different place, specify it with `-KeymapTool`.

If you want Ctrl+K to behave the same as in Emacs (joining successive cuts, and deleting the line break at the end of a line), add `-KillLine`. The tray must be running.

```
PowerShell -ExecutionPolicy RemoteSigned .\apply-emacs.ps1 -Id <ID> -KillLine
```

For details, see "Making Ctrl+K behave like Emacs" below.

If you see the message that the driver does not recognize the keyboard with that ID (`Error: 1168` in KeymapTool by itself), check the following.

- Whether that ID appears in the output of `KeymapTool --list`. Specify the 8 digits after `UniqId=`. It is not always the same as the ID in the editor or in the name of the settings folder.
- If the output is `DeviceCount=0`, the driver does not recognize the keyboard. Reconnect the keyboard or restart, and run `--list` again. Right after installation, also check that the license has been activated and that you have restarted.
- If you change the USB port or pair Bluetooth again, the ID changes. In that case, specify the new ID.

### From the editor

In the **Import** tab of **Import / Export...**, press **Emacs Example...** (this loads `samples\emacs\emacs.kasane.json` bundled with the installation. If you use a downloaded file, choose `emacs.kasane.json` with **Choose File...**). Choose the three items "Layers", "Macros", and "Key overrides", and choose **Replace** as the mode. Check the keyboard's assignments, import, and press **Apply to keyboard (ON)**. This way the settings remain in `project.json`, so you can edit them on the screen.

## Notes

- **It replaces the keyboard's layer map.** This setup writes 3 layers (0 is normal, 1 is C-x, 2 is C-q) covering only the 27 keys A to Z and Space. On a keyboard that already has a layer map, it is erased. Other keys are given no assignment and pass through as they are. Take a backup first.
- **Key overrides 0 to 34 and macros 92 to 127 are used.** Settings that used the same numbers are replaced. Other numbers do not change. `-Remove` erases the whole layer map and only these numbers.
- **What you wrote from the command line may disappear when you use the editor.** **Apply to keyboard (ON)** in the editor replaces the settings with what the screen holds. If you apply from the screen after writing from the command line, do the import above first.
- **It works in all apps.** App shortcuts such as Ctrl+F and Ctrl+S can no longer be used. In real Emacs, or in editors that have Emacs-style operation, it may act twice. If you want to stop it only while such an app is in front, make a **Turn remapping OFF** rule in the tray's per-app layers ([11. Helper Actions and Per-App Layers](../../docs/manual/src/en/11-helper-apprules.md)).
- **Ctrl+K behaves differently depending on the app.** With the default setup, it only sends Shift+End and then Ctrl+X. When pressed at the end of a line, Notepad and similar apps cut nothing. In apps such as VS Code that cut the whole line when there is no selection, the whole line is removed. It does not delete the line break at the end of a line as Emacs does. With `-KillLine`, it behaves the same as Emacs in ordinary apps such as Notepad (next section).
- Assignments are decided by key position (scan code). Alt+Shift+, and Alt+Shift+. are the keys that type "<" and ">" on either layout.

## Making Ctrl+K behave like Emacs

When you add `-KillLine` to `apply-emacs.ps1`, Ctrl+K becomes the tray's helper action `KillLine` (built the same way as nodoka's `&EmacsEditKillLine`). It adds the action with Id 100 to the keyboard's `helper.json`. It does not change other actions. `-Remove` also removes this action.

It behaves as follows.

- It cuts to the end of the line and puts the cut text on the clipboard.
- If you press Ctrl+K again in succession, the cut text is joined into one. Ctrl+Y (paste) brings it all back together.
- When pressed at the end of a line, it joins the line break and sends Delete (the next line comes up).
- The joining stops when you press a key other than Ctrl+K, when the foreground window changes, when something else goes onto the clipboard, or after 60 seconds.

Notes.

- **The tray must be running.** While it is stopped, Ctrl+K sends nothing.
- **It does not fit as it is in VS Code and similar apps.** In apps that cut the whole line when there is no selection, or that handle line breaks differently, the behavior is off. It was checked in ordinary apps such as Notepad. Support that changes the behavior per app is still to come.
- From the first time you use it, the tray watches actual key input (to know whether a key other than Ctrl+K was pressed). The contents of the keys are not recorded.

## What is not included

### Differences of mark mode from Emacs

It does not keep a mark position as Emacs does, so these points differ.

- Pressing Ctrl+Space again does not reset the mark position (the same as nodoka).
- There is no operation to return to the mark position (C-x C-x).
- It does not end with mouse operations.

### Other C-x operations

C-x C-c (exit), splitting and switching windows (C-x 0, 1, 2, 3, o), rectangles (C-x r ...), and C-x C-x are not included. You can add the key after C-x to layer 1 as `MACRO(n)` (if you put a modified key such as `LCTL(KC_S)` on layer 1, its output would hit the replacement of Ctrl+S (search)).

### Others

The kill ring (the operation that goes back through the cut history with M-y after C-y), rectangle selection, the repeat count of C-u, and nodoka's `&Emacs*` functions cannot be reproduced.
