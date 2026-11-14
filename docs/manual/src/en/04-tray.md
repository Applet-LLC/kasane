# 4. Tray

The tray is a small app that stays resident after you sign in. The kasane icon appears in the taskbar's notification area (at the bottom right of the screen; inside "^" when it is hidden).

The tray does the following.

- It is the entry point for opening the editor
- It temporarily stops remapping on all keyboards
- It switches layers by hand
- It switches layers and NICOLA automatically to match the IME ([10. IME Linking and NICOLA](10-ime-nicola.html))
- It runs helper actions and per-app layers ([11. Helper Actions and Per-App Layers](11-helper-apprules.html))

The remapping itself is done by the driver. Even when the tray is stopped, your settings keep working (with the default settings, key remapping is enabled only while the tray is running).

## Icon states

| State | Icon |
|---|---|
| Running | The normal color |
| Paused | Gray, with a mark |
| Cannot connect to the driver | Gray |

![Icon states (running, paused)](../img/ja/tray-icons.png)

At the lower right of the icon, the number of the current layer is shown (1 to 9, and A to F for 10 and above). When only the base layer 0 is active, nothing is shown. If the number gets in the way, you can turn it off in "General" of the settings dialog ([13. Settings Dialog](13-settings.html)).

When you hold the mouse over the icon, the state is shown: "kasane 1.02 - Running (N)", "kasane 1.02 - Paused", or "kasane 1.02 - No keyboards are being remapped". If the tray cannot connect to the driver, that is shown. When NICOLA is set up, "NICOLA on" or "NICOLA standby (IME link)" is also shown.

## Left click

This opens kasane (the editor). If it is already open, it comes to the front.

When starting takes time, such as the first time, a small notice saying "Starting kasane editor…" is shown until the window appears. It disappears automatically when the window appears (after 30 seconds at the longest).

![Starting](../img/ja/tray-starting.png)

## The right-click menu

![The right-click menu](../img/ja/tray-menu.png)

| Item | Contents |
|---|---|
| Settings/Editor | Opens kasane (the editor) |
| Pause | Stops remapping on all keyboards. Press it again to resume. While it is checked, remapping is paused |
| Layers | Turns layers 1 to 15 on and off by hand |
| License | Opens License Manager (Subscription edition only; it does not appear in the Pro edition) |
| Fix data folder access… | Shown only when the settings folder cannot be written to. When you choose it, a single authentication prompt appears, and then the folder can be written to ([When you use administrator protection](#admin-protection)) |
| Help | Opens the help. The browser is the Windows default browser |
| About | The version, the build date and time, and the website |
| Exit | Exits the tray |

### Pause

![The menu and icon while paused](../img/ja/tray-menu-paused.png)

When you press it, remapping on all keyboards stops and the icon turns gray. When you press it again, the keyboards that were ON before you paused go back to ON. It cannot be pressed when no keyboard is being remapped.

If you restart the PC while paused, it stays paused. Start the tray again (it starts automatically at sign-in) and press **Pause** to resume. Pressing **Apply to keyboard (ON)** in the editor also resumes.

### Layers

From the submenu, you turn layers 1 to 15 on and off by hand. This is handy when you want to try a layer before you assign keys to it. The checked layers are the ones that are active now.

![The Layers submenu](../img/ja/tray-layers.png)

- When there are several keyboards, the list is split by keyboard.
- Layers turned on by `TG` on the keyboard, by IME linking, or by per-app layers stay checked but are grayed out, and cannot be turned off from here.
- A layer you turned on here is turned off by choosing it again.
- It cannot be pressed while paused, or when no keyboard is being remapped.
- There is no item for layer 0. The base layer is always active, so it is not something you turn on or off. When `DF(n)` has replaced the base with layer n, the item for layer n shows a grayed-out check.

### Exit

This exits the tray. When **Remap only while the tray is running** in "General" of the settings dialog (ON by default) is ON, remapping also goes OFF. If you exit while paused, it stays paused.

After exiting, you can start it again by clicking **kasane Tray** in the `applet` folder of the Start menu. If you click it while the tray is already running, a second one does not start.

## When you are connected with Remote Desktop

In a session connected with Remote Desktop (RDP), the tray works the same as at the physical console. While you are connected and the screen is not locked, the session counts as "in use".

- A session that you disconnected from RDP, and a locked session, are not in use. When "Remap only while the tray is running" in the settings is ON, keyboard remapping goes OFF in the meantime.
- When you reconnect and unlock, the keyboards that were turned OFF come back ON automatically.
- Right after you connect with RDP, Windows may lock the session. Until you unlock it, it is not counted as in use.

Not every key operation is necessarily sent through. We recommend trying it for real. Also, with remote connection apps other than RDP, we cannot guarantee that key remapping works.

## The difference between Pause and OFF

**OFF** in the editor and **Pause** in the tray both stop remapping, but what they apply to and how you undo them differ.

| | OFF | Pause |
|---|---|---|
| Where you do it | The editor | The tray |
| What it applies to | The keyboard you selected | All keyboards that are being remapped |
| How to undo it | **Apply to keyboard (ON)** | Press **Pause** in the tray again. **Apply to keyboard (ON)** also undoes it |
| Settings | Stay in the driver | Stay in the driver |

With both, your settings are not lost.

## Remapping linked to the tray

When **Remap only while the tray is running** in "General" of the settings dialog (ON by default) is ON, remapping goes OFF in these situations.

- The tray exits
- Sign-out or shutdown
- The screen is locked (including switching users)

When the tray starts, and when the screen is unlocked, only the keyboards that this mechanism turned OFF go back to ON. Keyboards you stopped with emergency release or the **OFF** button do not come back.

When the tray ends abnormally (a crash, a forced exit, a power loss, and so on), or when the installer stops the tray, remapping cannot be turned OFF. If you want to keep using remapping while it is OFF, turn this setting OFF.

## Status notifications

The tray tells you about failures with a balloon (notification).

## Autostart and the two executables

The tray starts automatically at sign-in for all users. There are two executables.

| File | Contents |
|---|---|
| `kasane-tray.exe` | The normal edition. Used for autostart at sign-in. It can send key input even to windows that run with administrator rights |
| `kasane-tray-nua.exe` | The edition without UI access. It is for starting by hand |

The editor opens with normal rights. When administrator rights are needed, for example to apply to the keyboard, use **Restart as administrator** in the editor ([5. Editor Basics](05-editor-basics.html)).

<a id="admin-protection"></a>
### When you use administrator protection

When Windows 11 administrator protection is enabled, the tray runs under an account whose name starts with `ADMIN_` (for example, `ADMIN_applet`). The installer checks the administrator protection setting at installation time and switches how the tray starts at sign-in, as follows.

| Administrator protection | How autostart works |
|---|---|
| Disabled | A startup entry at sign-in (the Run key) |
| Enabled | The Task Scheduler task `kasane-tray-<user name>`. The Run key is disabled |

With the Run key, a UAC confirmation dialog appears at every sign-in. With the task, it starts without a prompt.

- If the administrator protection setting changes and administrator protection becomes disabled, and you want to stop starting from Task Scheduler, choose repair in an overwrite installation, or uninstall and then install again. Autostart goes back to the Run key. The same applies when you enable administrator protection.
- The task is created only for the user who was signed in at installation. When another user will use kasane, run the installer again while signed in as that user.
- When the settings folder cannot be written to, the tray shows a notification. **Fix data folder access…** appears in the tray menu, so choose it. A single authentication prompt appears, and then the tray and the editor can write to the settings folder.
