# 16. Troubleshooting and Known Limitations

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| The keyboard list shows only "(Driver not connected: edit and export only)" | The driver is not loaded, or the license is not active. Check that you restarted after installation, and check the state of [License Manager](03-license.html) |
| You press a key in **Press to identify...** but the keyboard is not found | The driver does not recognize that keyboard. Check that you restarted after activating the license |
| "Cannot apply" is shown | Fix the reason shown and apply again. At that point, the keyboard's settings have not changed. Examples are a `MO(n)` to a layer that does not exist, and the same scan code being recorded for two keys (the latter needs calibration to be redone) |
| You set something up, but a different key is swapped | This is a keyboard that sends a signal different from the standard. In **Calibrate...**, measure that key with single-key calibration and apply again ([6. Calibration](06-calibration.html)) |
| There is a key with a red border | It is a key that cannot be identified. Measure it with single-key calibration only when you want to change it. If you do not change it, you can leave it as it is |
| "It swaps even though it is OFF" or "I set it up but it has no effect" | Look first at the status marks (ON, OFF, No settings) in the status bar at the bottom of the window ([5. Editor Basics](05-editor-basics.html)) |
| A wrong setting left you unable to type | Hold down the emergency-release combination for 3 seconds ([7. Emergency Release](07-emergency.html)) |
| Settings disappeared after you changed the USB port, or re-paired a Bluetooth keyboard | A keyboard is treated as a different keyboard for each port. Put it back in the original port, or move the settings with export and import ([15. Backup and Migration](15-backup-migrate.html)). Also see "Sharing scope" in [13. Settings Dialog](13-settings.html) |
| When you press right Alt, the output is that of a simultaneous press of left Ctrl and left Alt (AltGr) | This is how Windows keyboard layouts work when the Windows language settings use a keyboard layout other than US, Japanese, or Korean. kasane cannot solve it. Review the Windows keyboard layout settings |
| You cannot press Apply to keyboard (ON) | You are in read-only mode. Press **Restart as administrator** at the top of the window |
| You use administrator protection, and a UAC prompt appears at every sign-in | Autostart is still the Run key, not the task. The installer may not have found the signed-in user at installation. Run the installer (repair) while signed in as the user who will use kasane ([4. Tray](04-tray.html)) |
| You use administrator protection, and settings are not saved. When you try to close the editor, it says saving failed | The account whose name starts with `ADMIN_` cannot write to the settings folder. Choose **Fix data folder access…** in the tray menu. One authentication prompt appears. If it does not appear in the menu, install again with the settings folder specified ([15. Backup and Migration](15-backup-migrate.html)) |
| You use administrator protection, and the tray icon stays hidden and does not appear on the taskbar | If autostart is not a task, the setting to show the icon may not be writable. Run the installer (repair), as above |
| The tray still starts from Task Scheduler even after you disabled administrator protection | Choose repair in an overwrite installation, or uninstall and install again. Starting goes back to the Run key |
| Installation failed | See "If something goes wrong" in [1. Installation](01-install.html) |

## Known limitations

These are the limitations known now. Some individual limitations are written in each chapter.

- You cannot use or save separate keyboard settings for each user. The settings are common to all users. You can apply your own settings after you sign in and use them. In that case, it is a good idea to put the settings back before you sign out.
- Even if the Windows keyboard layout setting does not match the legends on the keycaps, you can remap keys by scan code. However, even with the Windows keyboard layout set to "Connected keyboard", Windows may not recognize an ACPI (PS/2) keyboard, or a keyboard that does not support Plug and Play, automatically. Also consider the settings in the i8042prt tab of the settings dialog. You can check the current Windows settings with the keyboard layout tool "Mana" (https://github.com/Applet-LLC/mana).
- This help has an English version, but the screenshots show the Japanese display language. Please read them together with the English text.
- When you are not an administrator, "Input check" in calibration and "Test simultaneous press" in emergency release check with Windows key input (a low-level keyboard hook). There are limits such as right Shift not being handled, and the release not arriving for some keys. When you start as administrator, keys are received directly from the driver, so you can check accurately.
- A keyboard that sends right Shift as `E0 36` may not be handled by calibration (not confirmed on real hardware).
- The feature that reads the IME state with TSF is experimental. The problem that using it makes Explorer start slowly has not been solved ([10. IME Linking and NICOLA](10-ime-nicola.html)).

## The environment variable KASANE

This specifies the settings folder. It is valid only when you enter an absolute path. When it is invalid or not set, `%LOCALAPPDATA%\applet\kasane` is used.

- You normally specify it in the installer ([1. Installation](01-install.html)).
- The value is read again each time it is used. After you change it, restart, or sign out and sign in, as needed.

## Command line

You do not use these in normal operation. They are for being called from the installer, from diagnostics, and from other tools.

| Executable | Arguments | Contents |
|---|---|---|
| `kasane.exe` | `--install-import <file>` | Writes a `*.kasane.json` to the driver's registry. Used by the installer |
| `kasane.exe` | `--restore-import-backup [model or --all]` | Restores the settings from before the import at installation |
| `kasane.exe` | `--import-pending` | Imports `kasane-import.json` in the settings folder, only for keyboards without settings. The tray calls it at startup |
| `kasane.exe` | `--prepare-data-dir <folder> <file>` | Creates the settings folder and sets its permissions. Used by the installer |
| `kasane.exe` | `--dump-text <UniqId> [<output>]` | Writes the assignment list as text from saved settings ([5. Editor Basics](05-editor-basics.html#remap-text)). If you omit the output, it writes to standard output, so receive it like `kasane.exe --dump-text 55F70000 > a.txt` |
| `kasane-tray.exe` | `--probe-ime` | Diagnostics. Waits 3 seconds and writes the IME state to `%TEMP%\kasane-ime-probe.txt` |
| `DriverManager.exe` | `install keyboard`, `uninstall keyboard`, `check-pending` | Installs and removes the driver, and checks for a pending removal. Administrator rights are required |
| `KeymapTool.exe` | `--apply-simple` and others | The command-line configuration tool |

How to configure from commands is in [17. KeymapTool and Samples](17-keymaptool.html).

## Logs and files

| Location | Contents |
|---|---|
| `tray.log` in the settings folder | A record of tray problems |
| `import.log` in the settings folder | A record of settings imports |
| `%ProgramData%\applet\kasane\import.log` | The result of the import at installation |
| `%ProgramData%\kbdlayer-license\debug.log` | The log of the license service |
| `%ProgramData%\kbdlayer-license\gui-debug.log` | The log of License Manager |
| `%TEMP%\MSI*.LOG` | The installation log |
| The Windows event log (source `kasaneLicenseSvc`) | Events of the license service |

## Internal names

| Name | What it is for |
|---|---|
| `kbdlayer`, `kbdlayer.sys` | The keyboard driver, and its service name |
| `kbdlayer-license` | The license storage location, an internal name used by license management |
