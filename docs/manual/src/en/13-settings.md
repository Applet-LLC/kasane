# 13. Settings Dialog

Press **Settings...** at the bottom of the editor window to open the settings dialog, which has tabs.

![The settings dialog](../img/ja/settings-general.png)

| Tab | Contents | Chapter that explains it |
|---|---|---|
| General | Linking with the tray, showing the layer number, color mode, display language | This chapter |
| Features | Turning macros, combos, tap dances, and key overrides ON and OFF | This chapter |
| Timing | The detection times for hold, simultaneous press, and consecutive taps | This chapter |
| IME link | Switching layers to match the IME, NICOLA | [10. IME Linking and NICOLA](10-ime-nicola.html) |
| Helper actions | Launching programs and the like with a key | [11. Helper Actions and Per-App Layers](11-helper-apprules.html) |
| Per-app layers | Switching layers to match the foreground app | [11. Helper Actions and Per-App Layers](11-helper-apprules.html) |
| Emergency release | The emergency-release key combination | [7. Emergency Release](07-emergency.html) |
| Sharing scope | The unit in which the driver stores settings | This chapter |
| Data folder | The settings folder, restoring pre-import settings | This chapter |
| i8042prt | The layout of PS/2 keyboards | This chapter |
| Desktop | How desktop icons are shown | This chapter |
| About | The version and build date and time | This chapter |

Close the dialog with **OK** and **Cancel** at the bottom. The moment when a setting takes effect differs by tab.

| Tab | When it takes effect |
|---|---|
| General, Desktop | Saved with **OK**. The tray reads it again within a few seconds |
| Features, Timing, the NICOLA layout in IME link | When you press **Apply to keyboard (ON)** in the editor after **OK** |
| Helper actions, Per-app layers | Saved with **OK**. The tray reads it again within a few seconds |
| Emergency release | When you move to another tab, or press **OK** (only when started as administrator) |
| Sharing scope | When you press the button in the tab (unrelated to **OK** and **Cancel**) |
| i8042prt | When you press **Save** in the tab (unrelated to **OK** and **Cancel**) |

<a id="general"></a>
## General

![General](../img/ja/settings-general.png)

### Tray

- **Remap only while the tray is running**: when ON, remapping goes OFF when the tray exits, on sign-out, on shutdown, and when the screen is locked, and goes back ON when the tray starts and when the screen is unlocked (ON by default). For details, see [4. Tray](04-tray.html).
- **Show the layer number on the tray icon**: the number of the current layer is shown at the lower right of the icon. It is not shown when only the base layer is active. It hides most of the icon, so clear the check if you do not want it.

### Colors

With **Color mode**, you can choose **Light (default)** or **Dark**. When you save with **OK**, it applies to every window, both those already open and those you open from now on: the main window, settings, every editing window, key assignment, and so on. The title bar also becomes dark.

![Dark mode](../img/ja/settings-dark.png)

- It also applies to the tray (the right-click menu and About) and to License Manager. The tray reads it each time you open a menu or dialog. License Manager reads it each time it starts. After you change the setting, it changes from the next time you open them. Reopen a License Manager that is already open.
- It does not follow the Windows mode setting. What you choose here takes priority.
- The colors of confirmation screens that Windows shows (messages, file selection screens) do not change.

### Display language

With **Language**, you choose the display language of kasane and the tray from **Auto (default)**, **Japanese**, and **English**. Auto follows the Windows display language. The change takes effect after kasane and the tray are restarted. When you save, a confirmation recommends restarting kasane. The tray restarts automatically after a few seconds.

The help also follows this language. When you choose English, the English help opens.

![The confirmation after changing the display language](../img/ja/settings-language-restart.png)

<a id="features"></a>
## Features

You can turn macros, combos, tap dances, and key overrides OFF if you do not use them. The screen also shows the keyboard's current values ("Macro ON / Combo ON / …").

![Features](../img/ja/settings-features.png)

- Keys of a feature you turned OFF do nothing. The keys that make up a combo work as ordinary keys.
- Even if you turn a feature OFF, its contents (such as macro definitions) are not deleted.
- Layers and key replacement, Mod-Tap and Layer-Tap, and keys with modifiers cannot be turned OFF. They are always available.
- If you have never touched a check box, applying does not change the keyboard's settings. Values you changed with the command-line version (`--set-features`) remain as they are.

<a id="timing"></a>
## Timing

You change the detection times for hold, simultaneous press, and consecutive taps, and similar settings.

![Timing](../img/ja/settings-timing.png)

| Item | Contents |
|---|---|
| TapTerm | For Mod-Tap and Layer-Tap, the time after which pressing longer counts as a hold (default 200) |
| ComboTerm | For combos, the upper limit of the interval between pressing the two keys (default 50) |
| TapDanceTerm | For tap dances, the tap interval and the time for detecting a hold (default 200) |
| Tap-hold mode | How Mod-Tap and Layer-Tap are detected. Three ways: **Hold when another key is pressed** (default), **Hold when another key is pressed and released**, and **Tap if released in time**. Tap dances always use the default mode |
| Quick Tap | A feature that repeats the key if you keep holding it right after a short press. Switch it ON and OFF with **Enable**. With a time of 0, it is the same as TapTerm |
| TT count | The number of taps needed to lock `TT(n)` (0 is the default of 5) |

The unit is milliseconds, and you enter an integer from 0 to 10000. With 0, the default value is used. The current values of the keyboard are also shown at the top of the screen. If Mod-Tap is unintentionally treated as a hold when you type fast, try the second or third mode.

<a id="scope"></a>
## Sharing scope

A keyboard is treated as a different keyboard for each place it is connected (such as the USB port). The settings you made in the editor (`project.json`) and the tray settings are not carried over when you change ports (you can move them with export and import).

The sharing scope is the unit in which the driver stores settings.

![Sharing scope (opened as administrator)](../img/ja/settings-scope-admin.png)

When you are not an administrator, you are in read-only mode and **Change** cannot be pressed.

![Sharing scope (read-only mode)](../img/ja/settings-scope.png)

| Choice | Contents |
|---|---|
| Share across the same model (default) | For the same model, the same settings are used even if you change ports |
| This keyboard only | The settings apply only to the keyboard in this connector |
| Share across all keyboards | One setting is used for all keyboards |

Choose one and press **Change** to apply it. At the bottom of the screen, the current scope and the keyboard's identifiers (InstanceKey, ModelKey) are shown. In read-only mode, you cannot change it.

The two identifiers shown on the screen are 8-digit hexadecimal numbers, made from the keyboard's information as follows.

| Identifier | What it is calculated from | Properties |
|---|---|---|
| InstanceKey | The device instance ID (including the connection location) | Different for each keyboard, and it changes when you change the USB port. It is the same value as `DeviceId` and `UniqId`, and becomes the name of the `<UniqId>` folder in the settings folder |
| ModelKey | The first of the hardware IDs | The same value for the same model, and it does not change when you change ports. A different revision number (`REV_xxxx`) is treated as a different model |

Both are made from a 16-bit calculation result, so they occasionally match those of another keyboard.

- "Share across all keyboards" gives unintended assignments if you mix keyboards with different layouts (such as JIS and a numeric keypad). If a keyboard of a different model is connected, a confirmation appears.
- When you switch, the current settings are copied to the new scope, and the existing settings in the new scope are overwritten. For example, going back to "same model" from "all" or "this keyboard" replaces the settings shared across the same model.
- The change is not applied to another keyboard of the same model until you reconnect that keyboard (or restart the PC).

<a id="data-dir"></a>
## Data folder

This shows the folder where the settings files (the settings for each keyboard, the tray settings, and the logs) are stored, and the value of the environment variable `KASANE`.

![Data folder](../img/ja/settings-datadir-admin.png)

- If an absolute path is set in `KASANE`, that folder is used. If not, `%LOCALAPPDATA%\applet\kasane` is used.
- You change the value in "Settings folder" in the installer ([1. Installation](01-install.html)). After the change, restart, or sign out and sign in, as needed.
- With **Open folder**, you can open it in Explorer.

### Restore pre-import settings

Models whose settings were replaced by importing a settings file at installation are listed at the bottom of this tab. **Restore pre-import settings** swaps the kept settings with the current settings. Press it again to undo.

It writes the driver's registry, so it can be pressed only after you use **Restart as administrator**. It takes effect after you reconnect the keyboard (or after you restart the PC).

<a id="i8042"></a>
## i8042prt

The layout of a PS/2 (ACPI) keyboard can be specified only with values in the registry `HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\i8042prt\Parameters`. For HID keyboards, if these values remain, per-keyboard specification stops working.

Use this setting when the built-in keyboard of a laptop is a PS/2 (ACPI) keyboard and you also use an external keyboard. It is for when you want to use different keyboard layouts for the built-in and the external keyboards. When enabled, the specified layout is applied at PC startup, when the PS/2 (ACPI) keyboard is recognized. Try it when the layout of the PS/2 (ACPI) keyboard does not become JIS even if you set the keyboard layout to "Connected keyboard" in the Windows "Language & region" options.

![i8042prt (opened as administrator)](../img/ja/settings-i8042-admin.png)

When you are not an administrator, the input boxes and **Save** cannot be pressed.

![i8042prt (read-only mode)](../img/ja/settings-i8042.png)

When you enable **Reset the i8042prt values on connection**, every time a keyboard is connected (including at startup), the values are rewritten as follows.

- For a model that has a profile, the values you set are written (blank values are deleted).
- For a model that has no profile, `OverrideKeyboardType`, `OverrideKeyboardSubtype`, `OverrideKeyboardIdentifier`, `LayerDriver JPN`, and `LayerDriver KOR` are deleted, which restores the OS defaults.

When disabled (the default), nothing is done. When you select **Use the settings below for this keyboard**, the values below are used as the profile for the model of the selected keyboard. Choose from **Region/layout preset**, or enter **Type**, **Subtype**, `OverrideKeyboardIdentifier`, `LayerDriver JPN`, and `LayerDriver KOR` (the layout DLL name, for example `kbd106.dll`) by hand. When other models are registered, they appear in the list. While disabled, you cannot set a profile, but what was saved remains and is used when you enable it. This is a driver-wide setting, and it is saved immediately when you press **Save**. The rewriting is done from the next time a keyboard is connected (reconnecting, or restarting).

At the top of the screen, the `DeviceId` of the selected keyboard and the `ModelKey`, which represents its model, are shown. Profiles are stored per `ModelKey`, so they are shared by keyboards of the same model. For the two identifiers, see the earlier section "Sharing scope".

<a id="desktop"></a>
## Desktop

This changes how desktop icons are shown (Icons, Details, Small icons, List, Tiles). It has nothing to do with key remapping; it carries over a feature that was in the nodoka settings.

![Desktop](../img/ja/settings-desktop.png)

If you leave it at **Don't change (default)**, nothing is done. The view you choose is applied when you save with **OK** and when the tray starts. Depending on the OS and the state of the desktop, the view you chose may not be applied.

<a id="about"></a>
## About

The product name, the version, the build date and time, the location of the running file, and the website are shown. You can also open it from the version display in the title bar of the main window, and from **About** in the tray.

![About](../img/ja/settings-about.png)
