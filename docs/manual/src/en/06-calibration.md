# 6. Calibration

Calibration is the task of pressing keys to confirm which real key each key in the keyboard diagram corresponds to.

On a standard JIS or US keyboard, which key in the diagram is which real key is decided automatically from the layout. Calibration is therefore usually not needed. Do it in cases like these.

- The keyboard diagram has a key with a red border (a key that cannot be identified), and you want to change that key
- You set something up, but a different key is swapped

If you need to specify the Windows keyboard layout, please also consider the keyboard layout setting tool "Mana". Even when the legends on the keycaps do not match the keyboard layout, you can still remap keys. However, whether Shift + 2 produces `@` or `"`, for example, is decided by the Windows keyboard layout setting. This matters when you make macros, so take care.

- Keyboard layout setting tool "Mana": [Applet LLC page](https://appletllc.com/2026/10/07/%E3%82%AD%E3%83%BC%E3%83%9C%E3%83%BC%E3%83%89%E3%83%AC%E3%82%A4%E3%82%A2%E3%82%A6%E3%83%88%E8%A8%AD%E5%AE%9A%E3%83%84%E3%83%BC%E3%83%AB-%E3%81%BE%E3%81%AA-1-00/) (in Japanese), [GitHub](https://github.com/Applet-LLC/mana)

If the physical layout of your keyboard is far from the provided JP109, US104, and 10KEY, and the positions of the physical keys differ from the scan codes obtained, see [Adding a layout from KLE](05-editor-basics.html#kle-import) and make a layout that matches your keyboard.

## Opening it

Press **Calibrate...** at the bottom of the editor window.

![The calibration window](../img/ja/calib-main.png)

With **Mode** at the top of the window, choose one of three. The first time, it is "Input check".

| Mode | Administrator rights | Contents |
|---|---|---|
| Input check | Not needed (more accurate as administrator) | Press keys and check whether the matching key in the keyboard diagram lights up |
| Single key | Required | Choose a key you want to measure, then press it for real to measure it |
| All keys | Required | Press the keys shown, one after another, to measure them |

While this window is in front, the keys you press are not sent to Windows (the window shows "While this window is in front, keys do not reach Windows"). Pressing Enter or the Win key does nothing. Use the mouse. If you click another window, it pauses and the keys go back to normal.

<a id="calibration-check"></a>
## Input check

When you press a key on the keyboard, the matching key in the keyboard diagram turns orange, and goes back when you release it. A key you have pressed once gets a ✓.

Press all the keys once. If there are keys that do not get a ✓, or keys where a different key lights up from the one you pressed, calibration is needed. **Reset checked keys** clears the ✓ marks.

### As administrator

Keys are received directly from the driver. You see the physical key before remapping replaces it. The presses and releases of right Shift and of the Katakana/Hiragana, Eisu, and Hankaku/Zenkaku keys also arrive reliably. It looks only at input from the selected keyboard.

![Input check as administrator](../img/ja/calib-main-admin.png)

### When not administrator

The window shows "Restart as administrator to check accurately". In this case, it checks with Windows key input (a low-level keyboard hook), so there are these limits.

- What you can check is the key after remapping has replaced it. If a key is replaced, press **OFF** first.
- It cannot tell which keyboard the input came from.
- Right Shift appears as an extended key (with E0), so it cannot be handled.
- For some keys, the release does not arrive and the key looks as if it is still held down.

![Input check when not administrator](../img/ja/calib-main.png)

<a id="calibration-single"></a>
## Single-key calibration

1. In the keyboard diagram, click the key you want to measure. It turns yellow.
2. Press the matching key on the keyboard.
3. You can then measure other keys in the same way.
4. When you are done, press **Save measurements and close**.

![Single-key calibration](../img/ja/calib-single.png)

<a id="calibration-all"></a>
## All-keys calibration

Press the keys shown in yellow, one after another.

![All-keys calibration](../img/ja/calib-all.png)

| Button | Contents |
|---|---|
| **Back** | Goes back to the previous key |
| **Skip this key** | Skips a key that the keyboard does not have |
| **Save measurements and close** | Saves the measured values and closes |
| **Close** | Closes without saving measurements. Use it when you measured nothing (when you only did an input check) |

## Clearing measured values

To clear a measured value and go back to the default scan code, right-click the key in the keyboard diagram and choose **Clear measured value**.

## Applying the result

A key whose measured value differs from the standard gets an orange border in the keyboard diagram. The result is applied when you press **Apply to keyboard (ON)**.

> Measured values are not carried over to another device. When you buy a new keyboard, do it again ([15. Backup and Migration](15-backup-migrate.html)).

<a id="special-keys"></a>
## The Pause, NumLock, Copilot, Office, and AltGr keys

- Pause / Break: the physical key arrives as two signals (`E1 1D` and `45`). The driver handles them together as one key. You can replace Pause with another key, and you can make some key into Pause.
- NumLock: the scan code is `0x45`, the same value as the second signal of Pause. The driver tells them apart, so you do not usually need to think about it.
- Copilot key, Office key: on many keyboards, they arrive not as one key but as several keys pressed together (Copilot is Win + Shift + F23, and Office is Ctrl + Shift + Alt + Win). So you cannot change them with a single assignment. If you change the assignment of each key in the combination (left Win, left Shift, F23, and so on), the whole combination changes. Use **Input check** to see which keys arrive on your keyboard.
- AltGr: in Windows with a layout that has AltGr (German and others), right Alt works as AltGr. kasane cannot change it. See [16. Troubleshooting and Known Limitations](16-reference.html).

## Known limitations

A keyboard that sends right Shift as `E0 36` may not be handled by calibration. We have not been able to confirm this on real hardware.
