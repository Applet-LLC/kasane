# 15. Backup and Migration

This explains where kasane's settings are stored, and how to back them up and move them to another PC or keyboard.

## Settings are kept in three places

| Place | What it holds | What writes to it |
|---|---|---|
| The driver's registry | The settings that actually affect key input | When you press **Apply to keyboard (ON)** (administrator rights required) |
| The settings folder | The source data of the settings you edit in the editor, and the tray settings | The editor and the tray |
| `%ProgramData%\kbdlayer-license` | The license token and signing key | The license service |

What actually swaps the keys is the setting in the driver's registry. `project.json` in the settings folder is the source data for editing in the editor, and is written to the driver each time you apply. The editor has no Save button. It saves automatically when you close it, when you switch to another keyboard, and when you apply.

The license is not something to back up or take with you. You can check the license key in your Gumroad library. With the Subscription edition, after reinstalling, the service checks again with the saved key. With the Pro edition, you enter the key again when you reinstall.

## The settings folder

If the environment variable `KASANE` has an absolute path, it is that folder. If not, it is `%LOCALAPPDATA%\applet\kasane`. You can check the current location in the "Data folder" tab of the settings dialog ([13. Settings Dialog](13-settings.html)).

| File | Contents | Back it up? |
|---|---|---|
| `<UniqId>\project.json` | The settings of one keyboard (layers, macros, combos, tap dances, key overrides, timing, NICOLA, the layout name, measured scan codes, the name you gave it) | Yes |
| `<UniqId>\helper.json` | That keyboard's helper actions, triggers, and per-app layers | Yes |
| `tray.json` | The tray settings (IME link, color mode, display language, and so on) and the helper settings shared by all keyboards | Yes |
| `Layouts\kle-<name>.json` | Layouts saved with "Add from KLE" | Yes. Without it, settings that use that layout cannot be opened |
| `backup\` | Backups made automatically before an import or a reset | Good to keep |
| `tray-state.json` and other `tray-*.json`, `tray.log` | The tray's state records and log | Not needed |

`<UniqId>` is an 8-digit hexadecimal number for each keyboard. Even for the same keyboard, it changes if you plug it into a different USB port. If you put the port back, the original settings can be used as they are.

## Making a backup

In **Export** of [14. Import and Export](14-import-export.html), choose **Everything** as the scope. The settings of all keyboards and the tray settings go into a single `*.kasane.json`.

If you choose **Include recovery settings** in the export, these settings for restoring to the same PC are also included: the tray settings (colors, language, how desktop icons are shown, and so on), the emergency-release key combination, the i8042prt settings, the sharing scope of each keyboard, and layouts you added yourself.

The license is not included in the export. Recovery settings are included only when you choose them.

If you only want to restore to the same keyboard on the same PC, you can also copy the `<UniqId>` folder, `tray.json`, and `Layouts\` in the settings folder.

## Moving to another PC or another keyboard

1. On the original PC, export **Everything**.
2. Install kasane on the new PC and connect the keyboard.
3. In the editor, import the file with **Import / Export...**. The keyboards in the file are matched to the connected keyboards automatically. Those with the same `UniqId` are assigned first, then those with the same `HardwareId`. If they do not match, you can choose on the screen.
4. When moving to a keyboard with a different layout (JIS and US, one with a numpad, and so on), assignments you did not intend may result. Check the import report.
5. Measured values (the results of calibration) are not carried over to another device. Do the calibration again if needed.

You can also import at installation time ([1. Installation](01-install.html)).

## Quick reference by situation

| Situation | What to do |
|---|---|
| Buying a new PC | Export everything on the old PC and import on the new PC. If you have layouts you added yourself, copy `Layouts\` too |
| Buying a new keyboard | Import the old keyboard's settings into the new keyboard. If the layout differs, check the report and do calibration again |
| You changed the USB port | The editor settings and tray settings are not carried over. Put it back in the original port, or move them with export and import. The driver settings carry over if the sharing scope is "Share across the same model" |
| Reinstalling | With an overwrite, the settings remain. Uninstalling deletes the driver settings. Export everything first |
| Resetting settings | Use the "Reset" tab of Import / Export |
| Using administrator protection | The elevated editor runs as a different user, so the save location differs. Pin the settings folder with the environment variable `KASANE` |

## When you use administrator protection

When Windows 11 administrator protection is enabled, the elevated editor and the tray run as a different account whose name starts with `ADMIN_` (for example, `ADMIN_applet`). The `%LOCALAPPDATA%` of this account is separate from that of the account you normally use.

If you specify the settings folder in the installer (the environment variable `KASANE` is set), the elevated editor and the tray see the same settings as your normal account. When you use administrator protection, specify the settings folder.

If the `ADMIN_` account cannot write to the folder you specified, **Fix data folder access…** appears in the tray menu. When you choose it, a single authentication prompt appears, and then it can be written to. The same guidance also appears in the notification when the tray starts.

If you try to close the editor while it still cannot write, it shows that saving failed, along with the save location and the user it runs as. If you choose **Yes**, your current changes are discarded and it closes. If you choose **No**, you return to the editor.

The installer switches how the tray starts automatically depending on whether administrator protection is on ([4. Tray](04-tray.html)).

> If you want to restore the "i8042prt" and "Desktop" settings of the settings dialog after a reinstall, before you uninstall, export everything with **Include recovery settings** chosen. If you leave the save location at its default (`kasane-recovery.kasane.json` in the settings folder), the installer imports it automatically when you specify the settings folder at the reinstall. Importing a file for which you did not choose it does not restore these.
