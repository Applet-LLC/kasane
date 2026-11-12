# 2. Uninstallation

These are the steps to remove kasane from your PC. The driver is in use by the keyboard, so it is removed completely only after you restart.

If you only want to install a newer version, you do not need to uninstall (see "Overwrite installation" in [1. Installation](01-install.html)). You do need to uninstall when you move from the Subscription edition to the Pro edition (see "Moving from Subscription to Pro" in [1. Installation](01-install.html)).

## Steps

### 1. Turn remapping OFF

Open kasane (the editor) and press **OFF**. Keyboard remapping stops and the keys return to their original layout.

![The state after pressing OFF](../img/ja/uninstall-off.png)

If you skip this step, the driver may keep running until you restart. If you turn remapping OFF in step 1, your assignments have no effect.

### 2. Remove it from Apps

In Windows Settings, under "Installed apps" (also called "Apps & features"), find "kasane Subscription" ("kasane Pro" for the Pro edition). Typing "kasane" in the search box narrows the list. Press **…** at the right end to see **Modify** and **Uninstall**. Choose **Uninstall**. Administrator rights are required.

![Finding kasane in Windows Settings](../img/ja/uninstall-apps.png)

![The … menu](../img/ja/uninstall-apps-menu.png)

When a confirmation screen appears, press **Yes**.

![Uninstall confirmation](../img/ja/uninstall-confirm.png)

#### The files-in-use screen

If the tray is running, a screen appears saying that the following applications need to be closed to continue. The list shows `kasane-tray`.

- **Automatically close applications and attempt to restart them after setup is complete**: the PC restarts automatically after the uninstall.
- **Do not close applications. (A reboot may be required.)**: you restart by yourself.

Uninstallation completes with either choice. The restart in step 3 is needed in both cases.

### 3. Restart

Restart your PC. Removal of the driver is completed when you restart.

## What is removed

| Item | Contents |
|---|---|
| Program | The contents of `C:\Program Files\applet\kasane`, and the Start menu items |
| License management service | `kasaneLicenseSvc` (Subscription edition only) |
| Driver | `kbdlayer`, and the settings stored in the driver (keymaps, macros, combos, timing, NICOLA, the emergency-release key combination, and so on) |
| Environment variable | `KASANE` (if you set it in the installer) |
| Autostart | The tray's autostart entry (Run key). If you use administrator protection, also the Task Scheduler task `kasane-tray-<user name>` |

The i8042prt settings are stored in the driver, not in the settings folder, so uninstalling deletes them. If you want to keep them, export everything before you uninstall, with **Include recovery settings** selected ([14. Import and Export](14-import-export.html)).

## What remains

So that they carry over when you install again, the following are kept.

| Location | Contents |
|---|---|
| `%ProgramData%\kbdlayer-license` | The license key and the logs (Subscription edition only). When you install again, the key is checked again automatically, so you do not need to activate again. It is not created for the Pro edition |
| `%LOCALAPPDATA%\applet\kasane` | The settings for each keyboard (`project.json`), the tray settings, and the logs. If you specified the environment variable `KASANE`, that folder |

## Removing everything

After you uninstall and restart, delete the two folders above by hand. If you delete `%LOCALAPPDATA%\applet\kasane`, your saved settings are lost. If you want to keep them, [export](14-import-export.html) first. If you delete `%ProgramData%\kbdlayer-license`, the saved license key is lost. The inside of `license-svc` in this folder cannot be deleted without administrator rights. The next time you install, you enter the key again in License Manager. The license key is not included in exports, so make sure you can look it up in your Gumroad email or library.

## When you install again

- The license is checked again automatically with the saved key.
- The saved keymap (`project.json`) is loaded when you open kasane. The driver-side settings have been deleted, so press **Apply to keyboard (ON)** to restore them.
