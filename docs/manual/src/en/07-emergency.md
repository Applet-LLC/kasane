# 7. Emergency Release

This is an emergency measure for when a wrong setting leaves you unable to type on the keyboard. If you hold down a predetermined combination of keys at the same time for 3 seconds, every connected keyboard goes OFF.

The default combination is left and right Ctrl and left and right Shift (four keys).

After releasing, fix the settings in the editor and press **Apply to keyboard (ON)**, and remapping comes back.

<a id="emergency-tab"></a>
## Changing the combination

Open **Settings...** in the editor and choose the **Emergency release** tab. When you did not start as administrator, you are in read-only mode and the boxes and buttons cannot be pressed.

![The Emergency release tab (opened as administrator)](../img/ja/emergency-tab-admin.png)

- The combination is made of 2 to 6 keys, entered in the boxes **Key 1** to **Key 6**. All the keys of the combination must be on the same keyboard.
- Press **Press to enter** next to a box, and the next key you press goes into it. With **Pick...**, you can also choose from the on-screen keyboard or from a list. You do not need to write key names by hand.
- **Clear** empties all the boxes.
- **Reset to default** enters the default combination (left and right Ctrl, and left and right Shift).
- The combination you entered takes effect when you move to another tab or press **OK**. After it takes effect, closing the settings dialog with **Cancel** does not undo it. If all the boxes are empty, nothing is changed.
- If there is only one key, a duplicated key, or anything other than a plain key, an error appears and you cannot move to another tab.

There is one combination for the whole driver.

<a id="emergency-test"></a>
## Testing simultaneous press

Press **Test simultaneous press...** to open the "Test the emergency release combination" window. You can check whether your keyboard can detect the keys of the combination at the same time.

![Testing simultaneous press (opened as administrator)](../img/ja/emergency-check-admin.png)

The keys of the combination are lined up on the screen. When you press all of them, the keys you are pressing turn green. When all of them are green, it succeeded. End with **Close**. While this window is open, the keys you press do not reach other apps. After 3 seconds the release would actually take effect, so let go as soon as they turn green.

### As administrator

Keys are received directly from the driver, so you can check accurately.

### When not administrator

It checks with a low-level keyboard hook, so depending on the selected keyboard layout, some keys may not be read correctly. The tab and the test window say "Restart as administrator".

![The display when not administrator](../img/ja/emergency-check.png)

When you are not an administrator, the boxes and buttons of the tab cannot be pressed either.

![The Emergency release tab when not administrator](../img/ja/emergency-tab.png)

## Keyboards that cannot press four keys at once

Some keyboards cannot press four keys at the same time. This is the limit of simultaneous presses of each keyboard, and not a problem with kasane. Some keyboards can press all four.

If you use a keyboard that cannot press four keys, change to a combination with the number of keys it can press (for example, three keys: left Ctrl, left Shift, and right Ctrl).

## When the release actually works

Every connected keyboard goes OFF. Open the editor and check that the status mark in the status bar is OFF. The settings remain in the driver.
