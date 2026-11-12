# 10. IME Linking and NICOLA

In the **IME link** tab of the settings dialog, layers and thumb-shift (NICOLA) are switched automatically to match the state of the Japanese input method (IME).

![The IME link tab](../img/ja/ime-tab.png)

The upper part of the tab is switching layers to match the IME. The lower part is NICOLA (thumb-shift). The contents of the former "NICOLA" tab are also gathered in this tab.

Both work only while kasane's tray is running.

<a id="ime-layer"></a>
## Switching layers to match the IME

Layers you choose are enabled automatically to match the IME being ON or OFF. When the IME goes OFF, they are disabled. You can use this even if you do not use NICOLA.

| Item | Contents |
|---|---|
| Target | Choose **This keyboard** or **All keyboards (shared)**. The per-keyboard setting takes priority. If you set "This keyboard" to not link, that keyboard does not link even if there is a shared setting |
| **Enable layers to follow IME on/off** | When checked, linking is on |
| Layers to enable | The layers to enable (check boxes for 0 to 15). At least one |
| Condition for enabling | **While the IME is on** (with Microsoft IME, half-width alphanumeric mode counts as off), or **Only in Japanese input mode** (the IME is on and not in half-width alphanumeric mode; romaji input and kana input are not distinguished) |
| **Clear this scope** | Clears the setting of the selected target. If that keyboard has no setting, it follows the shared setting |

The layers you enable are combined by OR with layers such as `TG` on the keyboard side.

When the IME state of the foreground window cannot be read (for example, a window running as administrator), the previous state is kept.

### How to get the IME state

You choose how the IME state is read with **How to get the IME state**. Besides IME linking, the IME operations of helper actions ([IME on/off](11-helper-apprules.html#helper-ime)) also read the state this way.

| Method | Contents |
|---|---|
| IMM32 only (default) | Reads with the standard Windows method. No extra DLL is loaded |
| IMM32 first, then TSF | Only when IMM32 cannot read it, also reads with the Text Services Framework (TSF). A small DLL (the hook of Tenro sirius) is loaded into all apps |
| TSF first, then IMM32 | Reads with TSF first, and with IMM32 when that fails. The DLL is loaded as above |

With IMM32, the state cannot be read, or does not follow, in the following apps. While such an app is in front, the previous state is kept.

- Some Store apps, such as the Settings app (the value is fixed)
- The new Notepad (ON may not be readable)

The state can also be read in windows running as administrator. This is because the tray that starts automatically at sign-in (`kasane-tray.exe`) runs with UI access rights (we confirmed this with Notepad running as administrator in a Windows 11 VM). With the edition of the tray without UI access (`kasane-tray-nua.exe`), it may not be readable.

> If you choose a method that includes TSF, the DLL is loaded even if you do not use IME linking. Opening Explorer may feel slow. We have not identified the cause. This DLL is not loaded into 32-bit apps and native ARM64 apps. If you do not need it, leave the default "IMM32 only".

### How to switch the IME

You choose how helper actions turn the IME on or off with **How to switch the IME**. It applies to the `ime` step and to "Turn the IME off while typing" in text input. It has nothing to do with IME linking (enabling and disabling layers).

| Choice | Contents |
|---|---|
| Automatic (default) | Normally switches with IMM32. When the tray runs as administrator, it sends Alt+Hankaku/Zenkaku through the driver |
| IME on / IME off keys | Sends separate keys for on and off. Can be used on a Japanese keyboard layout |
| Henkan / Muhenkan | Henkan turns it on, and Muhenkan turns it off. In Microsoft IME's "Customize keys and touch", assign IME-On to Henkan and IME-Off to Muhenkan before choosing this. If you do not assign them, Henkan works as reconversion |
| Toggle with Alt+Hankaku/Zenkaku | Sends a key that toggles each time. It is not sent to apps whose current state cannot be read |
| IMM32 | Sends no keys |

The detailed behavior is in [Helper Actions and Per-App Layers](11-helper-apprules.html#helper-ime).

## NICOLA (thumb-shift)

This is the setting for using thumb-shift. It is the part you reach by scrolling down in the IME link tab.

![NICOLA](../img/ja/nicola-section.png)

1. With **Target**, choose **This keyboard** or **All keyboards (shared)**.
2. In the **NICOLA** box, choose **Use this setting**, and the layout from the NICOLA standard is entered. To go back to the standard layout later, press **Use standard layout**. If it stays at **Not managed by the GUI** (the driver's setting is used as it is), a setting entered with the command-line version (`--set-nicola`) does not change when you apply.
3. If needed, change the layout with **Edit table...**. You can also load from and save to a file with **Load nicola.json...** and **Export to nicola.json...**.
4. Press **Apply to keyboard (ON)**.

<a id="nicola-table"></a>
### Editing the table

With **Edit table...**, the "NICOLA table" window opens. In the "overall" part at the top and the list of character keys below, you can change the following.

![The NICOLA table](../img/ja/nicola-editor.png)

- **Output format**: the IME's input method (a romaji-input IME, or JIS kana), and the **Layer**
- **Enable detection**: when OFF, the settings stay on the keyboard but thumb-shift is not performed
- **Left thumb**, **Right thumb**, and the **Solo press** key of each (for example, `KC_MHEN`, `KC_HENK`). If you leave a solo press box empty, pressing the thumb key alone sends nothing.
- **When enabled**, **When disabled**: the key pressed once when detection becomes enabled or disabled (such as IME on and off; empty means none)
- **Detection time (ms)**, **Overlap ratio**, **Min overlap (ms)**
- **Continuous shift**: always shifts the character keys pressed while a thumb key is held
- The list below: the kana that comes out when each character key is pressed alone, with the left thumb, or with the right thumb (one character each; empty sends nothing). Voiced sounds (such as が), small kana (such as ぁ), ー, and 。、・，．゛゜ can also be used. **Left hand** and **Right hand** are which of `"left"` and `"right"` in `nicola.json` the key is written in. With **Add character key** and **Delete selected key**, you can add and remove rows. **Duplicate** adds a row just below the selected one with its kana copied (the same character key cannot be written in two rows, so the character key is left empty). The character key box can also be filled with **Press to enter** and **Pick...**.

You can also load and save `nicola.json` (the same format as the command-line version).

<a id="nicola-ime-link"></a>
### IME link (NICOLA)

When you check **IME link: use NICOLA only in Japanese input mode**, kasane's tray enables the NICOLA layer automatically only while the IME is in Japanese input mode (Hiragana, Katakana). In half-width alphanumeric mode, it does not enable it even if the IME is ON. Romaji input and kana input are not distinguished, so it works with either IME. No key is needed to switch. It is off by default.

- To stop it, clear the check.
- When the target is **This keyboard**, it is a setting for that keyboard only. When a keyboard has no setting of its own, it follows the **All keyboards (shared)** setting. Press **Use shared setting** to clear the per-keyboard setting.
- When you do not use it, put a key that switches to the NICOLA layer (layer 1 by default), such as `TG(1)`, in the keymap.
- Once you have checked it, the choice stays. It does not change even if you reinstall.
- If you turn IME link on and turn the IME ON on a keyboard with NICOLA set up, it switches to NICOLA after about 0.1 seconds, and the tooltip of the tray icon becomes "NICOLA on". When you turn the IME OFF (half-width alphanumeric), it goes back to ordinary keys.

The solo presses of the thumb keys (`thumbLeftTap`, `thumbRightTap`; Enter, Space, and so on) are added automatically as key assignments of the NICOLA layer when you apply. While you hold Ctrl or Shift, NICOLA detection is not performed and these assignments are used. NICOLA works even when there are no key assignments at all.

### Example: making Muhenkan and Henkan into Enter and Space

This is an example that uses Muhenkan and Henkan as the thumb keys of thumb-shift, and switches to match the IME.

| Operation | Action |
|---|---|
| Muhenkan is the left thumb, and Henkan is the right thumb | Pressed together with a character key, the character from the standard |
| When the IME is OFF, press Muhenkan alone | The IME turns ON (the Windows setting in step 2) |
| When the IME is ON, press Muhenkan alone | Enter |
| When the IME is ON, press Henkan alone | Space |
| Muhenkan or Henkan while holding Ctrl or Shift | Ctrl+Enter, Shift+Space, and so on |

1. In the NICOLA part, choose **Use this setting**, and with **Load nicola.json...**, choose `samples\nicola-enter-space.json` in the installation folder. The solo presses of the thumb keys become Enter and Space. For JIS kana input, match the IME's input method with **Edit table...**. Check that **IME link** is checked, and press **OK**.
2. In the Windows settings, set the Muhenkan key to "IME-On" (in Microsoft IME's "Customize keys and touch"; the name on the screen differs slightly by Windows version). When the IME is OFF, NICOLA does not work, so Muhenkan reaches the IME and the IME turns ON. If the Henkan key is also set to "IME-On", put it back to "Convert" so that Henkan keeps working as Henkan.
3. Press **Apply to keyboard (ON)**.
4. Check that kasane's tray is running.

The solo press of a thumb key is confirmed when the detection time for simultaneous presses (100 ms by default) passes, or when you release the key.
