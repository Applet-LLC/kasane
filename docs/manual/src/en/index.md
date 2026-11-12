<div class="banner"><img src="../img/ja/editor-main-admin.png" alt="The kasane editor window"></div>

<div class="title-row">
<h1 id="kasane">kasane Help</h1>
<img src="../assets/about.png" alt="kasane">
</div>

kasane is a tool that changes how the keys on a Windows keyboard behave. For each keyboard, you can swap keys, switch layers, change what a key does depending on whether you hold or tap it (Mod-Tap and Layer-Tap), record macros, use thumb-shift (NICOLA), and more.

This help walks through every feature, from installation to uninstallation, with pictures of the screens. The screenshots are taken with the Japanese display language for now.

## System requirements

- Windows 11 25H2 (x64 or ARM64)
- Administrator rights for installation
- An internet connection. With the Subscription edition, the license management service connects to the license server (run by Applet LLC) about every 24 hours to check that the subscription is active. It uses the result of the latest check, so you can keep using kasane offline for up to 7 days. With the Pro edition, a connection is needed only once, when the license key is checked during installation.

## New to kasane

1. [Install](01-install.html) kasane and restart.
2. [Activate the license](03-license.html) (with the Pro edition this is already done during installation).
3. In the [editor](05-editor-basics.html), choose a keyboard, swap some keys, and apply the changes to the keyboard.
4. In case something goes wrong, it helps to read about [emergency release](07-emergency.html) and [uninstallation](02-uninstall.html) first.

## Names and files

| Part | Name | File and location |
|---|---|---|
| Product | kasane | Installed in `C:\Program Files\applet\kasane` |
| Editor | kasane | `kasane.exe` (`keymap-gui` folder) |
| Tray (resident) | kasane-tray | `kasane-tray.exe` (`tray` folder; starts automatically at sign-in). The edition without UI access is `kasane-tray-nua.exe` (start it manually) |
| Device driver | kbdlayer | `kbdlayer.sys` (the internal and service name is `kbdlayer`; `kbdlayer` folder) |
| License manager (License Manager, Subscription edition only) | kasane-license | `kasane-license.exe` (`license-gui` folder) |
| License service (Subscription edition only) | kasaneLicenseSvc (display name: kasane License Service) | `kasane-license-svc.exe` (`license-svc` folder). License records are in `%ProgramData%\kbdlayer-license` |
| Driver management tool | DriverManager | `DriverManager.exe` (directly under the installation folder) |
| Command-line configuration tool | KeymapTool | `KeymapTool.exe` (directly under the installation folder). See [17. KeymapTool and Samples](17-keymaptool.html) |
| Installer | Install-kasane | `Install-kasane.exe`, `KasaneSetup-ja-JP.msi`, `KasaneSetup-en-US.msi` |
| Help | | `manual\en\index.html` (directly under the installation folder; opened from **Help** in the editor and **Help** in the tray menu) |
| Settings folder | | `%LOCALAPPDATA%\applet\kasane` (can be changed with the `KASANE` environment variable) |

## Find what you want to do

| What you want to do | Chapter |
|---|---|
| Install kasane and choose the settings folder | [1. Installation](01-install.html) |
| Bring in earlier settings during installation | [1. Installation](01-install.html), [14. Import and Export](14-import-export.html) |
| Uninstall kasane | [2. Uninstallation](02-uninstall.html) |
| Activate the license, use the trial, or confirm a Pro license key | [3. License Activation](03-license.html) |
| Stop key remapping for a while | [4. Tray](04-tray.html) (Pause), [5. Editor Basics](05-editor-basics.html) (OFF) |
| Swap keys | [5. Editor Basics](05-editor-basics.html), [12. Setup Examples](12-recipes.html) |
| Use another keyboard (add a layout) | [5. Editor Basics](05-editor-basics.html) (Add from KLE) |
| Learn what the marks on the screen (ON, Not applied, Read-only, H, T, and so on) mean, or read the explanation for each screen | [5. Editor Basics](05-editor-basics.html) (status marks, reading the keyboard diagram, opening help) |
| A different key is swapped from the one you set | [6. Calibration](06-calibration.html) |
| A wrong setting left you unable to type on the keyboard | [7. Emergency Release](07-emergency.html) |
| Do one thing on a short press and another on a long press | [8. Assigning Keys](08-key-assign.html), [12. Setup Examples](12-recipes.html) |
| Switch to another key layout only while a key is held | [8. Assigning Keys](08-key-assign.html) (layers), [12. Setup Examples](12-recipes.html) |
| Send a fixed sequence of keys with one key | [9. Macros, Combos, Tap Dances and Key Overrides](09-advanced.html) (macros) |
| Send another key when two keys are pressed together | [9. Macros, Combos, Tap Dances and Key Overrides](09-advanced.html) (combos) |
| Turn a combination such as Ctrl+X into another action | [9. Macros, Combos, Tap Dances and Key Overrides](09-advanced.html) (key overrides), [12. Setup Examples](12-recipes.html) |
| Switch key layouts to match the Japanese input method (IME), or use thumb-shift | [10. IME Linking and NICOLA](10-ime-nicola.html) |
| Launch a program or type a string with a key | [11. Helper Actions and Per-App Layers](11-helper-apprules.html) |
| Use a different key layout for each app, or pause kasane only while a game runs | [11. Helper Actions and Per-App Layers](11-helper-apprules.html) |
| Change the colors or the display language | [13. Settings Dialog](13-settings.html) |
| Back up settings, or move them to another PC | [14. Import and Export](14-import-export.html), [15. Backup and Migration](15-backup-migrate.html) |
| Configure from the command line, or use the sample keymaps | [17. KeymapTool and Samples](17-keymaptool.html) |
| Use nodoka settings | [14. Import and Export](14-import-export.html) |
| Something does not work, or you want to see the logs | [16. Troubleshooting and Known Limitations](16-reference.html) |
| See what changed since 1.00 | [18. README and Background](18-readme.html) |
| Check the terms of use and the licenses of bundled software | [19. Terms of Use](19-terms.html), [20. Third-Party Notices](20-third-party.html) |

## Chapter guide

| Chapter | Contents |
|---|---|
| [1. Installation](01-install.html) | The installer screens, choosing the settings folder, importing a settings file |
| [2. Uninstallation](02-uninstall.html) | Steps, what is removed, what remains |
| [3. License Activation](03-license.html) | License Manager, the trial, confirming a Pro key |
| [4. Tray](04-tray.html) | The resident icon, Pause, switching layers by hand |
| [5. Editor Basics](05-editor-basics.html) | The parts of the screen, status marks, reading the keyboard diagram, swapping keys, applying and OFF, the assignment list |
| [6. Calibration](06-calibration.html) | Measuring keys, input check |
| [7. Emergency Release](07-emergency.html) | Releasing kasane when a wrong setting stops your input |
| [8. Assigning Keys](08-key-assign.html) | The assignment screen, layers, Mod-Tap, Layer-Tap |
| [9. Macros, Combos, Tap Dances and Key Overrides](09-advanced.html) | How to use each editor |
| [10. IME Linking and NICOLA](10-ime-nicola.html) | Switching layers to match the IME, thumb-shift |
| [11. Helper Actions and Per-App Layers](11-helper-apprules.html) | Launching programs, typing strings, switching by the foreground app |
| [12. Setup Examples](12-recipes.html) | Common setups, with steps |
| [13. Settings Dialog](13-settings.html) | The tabs: General, Features, Timing, and more |
| [14. Import and Export](14-import-export.html) | Exporting and importing settings, importing nodoka settings |
| [15. Backup and Migration](15-backup-migrate.html) | Where settings are kept, moving to another PC |
| [16. Troubleshooting and Known Limitations](16-reference.html) | Troubleshooting, known limitations, command line, environment variables, logs |
| [17. KeymapTool and Samples](17-keymaptool.html) | Configuring from the command line, sample keymaps, how keyboard IDs are calculated |
| [18. README and Background](18-readme.html) | The README in the installation folder, and the changes in each version |
| [19. Terms of Use](19-terms.html) | The license agreement in the installation folder |
| [20. Third-Party Notices](20-third-party.html) | Licenses of the bundled software |
