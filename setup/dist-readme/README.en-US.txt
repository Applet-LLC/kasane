kasane Subscription - Installation Guide 1.01 2026-11-10 Applet LLC
==============================================================

This package installs a key remapping tool named kasane. The installer
installs "kbdlayer.sys", a Windows kernel-mode keyboard class filter driver,
and the settings tool. The driver itself emulates what keyboard layers can do
(layer switching, Mod-Tap, Layer-Tap, and so on). Remapping works with the
device driver alone, but IME state detection is handled by a resident app in
the taskbar notification area (the tray), which lets layers change while the
IME is on.

kasane's device identification is forked from kbdaddid.sys and keeps its
per-device identification mechanism as it is. A layer keymap can be applied to
one specific keyboard even when several keyboards are connected at once.

This edition's functionality is provided according to your Gumroad
subscription.


Requirements
------------
- Windows 11 25H2 (x64 or ARM64)
- Administrator privileges for installation
- An internet connection. The license management service connects to the license server
  (operated by Applet LLC) about once every 24 hours to re-check whether your
  subscription is active. Your license key and a computer identifier are sent;
  the server neither stores nor logs them.
  It uses the last successful check, so the feature keeps working for up to 7
  days even without a network connection.
  It connects to https://kasane-license.applet-llc.workers.dev (HTTPS, port 443).
  If a corporate network or security software blocks it, activation fails.


Installation
------------

This edition and the Pro edition cannot be installed on the same PC at the same
time.

1. Run "Install-kasane.exe".

2. Follow the instructions of the setup wizard. The keyboard driver, the
   license management service (kasaneLicenseSvc), the license management tool,
   and the apps are installed.
   The installation folder is fixed to C:\Program Files\applet\kasane.

3. Settings folder (optional)
   You can specify where settings files (per-keyboard settings, tray settings,
   logs) are stored with the environment variable KASANE.
   If left empty, they are stored in each user's %LOCALAPPDATA%\applet\kasane.
   The specified folder is created if it does not exist.

4. Import a settings file (optional; off by default)
   A settings file (*.kasane.json) exported from kasane is applied to the
   driver during installation.

5. A system restart is required to complete the installation.

6. After the restart, "kasane License Manager" starts automatically. Enter the
   license key you purchased on Gumroad to activate it.

7. Restart once more, or reconnect your keyboard, and the feature becomes
   usable.


Tray icon
---------
After you sign in, the kasane icon appears in the notification area of the
taskbar (inside the "^" flyout when hidden).
- Left click: opens the kasane settings dialog.
- Right click: a menu with 設定 (Settings), 一時停止 (Pause), レイヤー (Layer),
  ライセンス (License), バージョン情報 (About), and 終了 (Exit).
  "Pause" temporarily stops the remapping on all keyboards. Press it again to
  resume.
- If you use NICOLA (thumb-shift input), NICOLA is switched on automatically
  when the IME is enabled. You can turn this on and off at the bottom of
  kasane's 設定 (Settings) > IME 連動 (IME link).
- Helper actions: a key can launch a program, type text, operate the mouse,
  switch layers, and more. Create them from "ヘルパー動作" (Helper actions) in
  kasane's key-assignment screen (the tray must be running). Actions triggered
  by keys such as F13-F24 are under 設定 (Settings) > ヘルパー動作 (Helper
  actions).
- Per-app layers: switch layers, or stop the remapping, depending on the
  foreground app. This is under 設定 (Settings) > アプリ別レイヤー (Per-app
  layers).
- Import / export: in kasane, "インポート／エクスポート..." (Import / Export...)
  saves your settings to a file (*.kasane.json) and loads them back. You can
  move settings to another keyboard or another PC. A nodoka settings file can
  also be converted and imported.

If your subscription license becomes inactive, the layer remapping feature
stops working. Basic keyboard input keeps working as it is.


Help
----
The kasane help opens from the "ヘルプ" (Help) button (F1 key) in the editor and
from the tray menu. It is in the manual folder of the installation folder.
The help is not yet available in English. Please use your browser's translation
feature. The screenshots in the help will remain in Japanese.


Files in this package
---------------------
- Install-kasane.exe
  The installer launcher.

- KasaneSetup-en-US.msi
- KasaneSetup-ja-JP.msi
  The launcher automatically selects and runs the right one.

- License.en-US.txt
- License.ja-JP.txt
- License.en-US.rtf
- License.ja-JP.rtf
  The license agreement covering your subscription.
  The .txt files have the same content as the .rtf files, in plain text (UTF-8).

- README.en-US.txt
- README.ja-JP.txt
  This README.

- THIRD-PARTY-NOTICES.txt
- third-party folder
  The list of third-party software included in kasane
  (THIRD-PARTY-NOTICES.txt) and the full text of each license (the
  third-party folder).

The license agreements (License.*.txt and License.*.rtf), the README, THIRD-PARTY-NOTICES.txt,
and the third-party folder are also placed directly in the installation folder
(C:\Program Files\applet\kasane).


Uninstallation
--------------
1. First open kasane and press "OFF". The keyboard remapping is disabled.
2. Remove "kasane Subscription" from "Apps & features" in Windows Settings
   (administrator rights are required).
3. Restart your PC. The driver is in use by your keyboard, so its removal
   completes at the restart. Until then the driver may keep running (if you
   pressed OFF in step 1, no key assignments take effect).

What is removed:
- The program (C:\Program Files\applet\kasane) and its Start menu entries
- The license management service (kasaneLicenseSvc)
- The driver (kbdlayer) and the settings stored in the driver (keymaps,
  macros, combos, timing, NICOLA, the emergency release key combination, and
  so on)

What is kept:
  So that a reinstall picks them up again, the following are kept.
- %ProgramData%\kbdlayer-license ... your license key and logs. After a
  reinstall the license is re-verified automatically with this license key, so
  you do not need to activate again.
- The files in the %LOCALAPPDATA%\applet\kasane folder, or in the folder
  specified by the KASANE environment variable.

To remove everything, delete these folders by hand after uninstalling and
restarting.

Installing a newer version over an existing one (without uninstalling) keeps
the driver settings and the license. You do not need to uninstall first.


Support / Purchase
------------------
You can check your purchased license key, manage your subscription, and
re-download the zip file from your Gumroad library at any time.


License
-------
kasane is licensed for use on a single PC only.
Transferring or reselling the files or the license key is not permitted.
See License.ja-JP.txt / License.en-US.txt for details.

kasane contains parts derived from the following third-party software and data.
- nodoka4 (のどか): Eclipse Public License 2.0
- 窓使いの憂鬱 (mayu) / YAMY: BSD-3-Clause (via nodoka4)
- Microsoft .NET (runtime and libraries): MIT License
- Layout data (keyboard diagrams): Keyboard Layout Editor and others
- WiX Toolset 3.14 (installer): Microsoft Reciprocal License (MS-RL)
- 天狼 (sirius): MIT License
The full text of each license is in THIRD-PARTY-NOTICES.txt and the
third-party folder.


Release History
---------------
2026-11-10 1.01
New features
  - Import of settings files that enable Emacs-style operation; mark mode (MARK)
    and the KillLine (C-k) helper action
  - Increased the number of key overrides from 32 to 128
  - Helper actions: added a choice of how the IME is turned on/off, and
    names for triggers
  - Added a button that lists the assigned settings, and --dump-text for the CLI

Improvements
  - When you assign a key, its current setting is now filled in beforehand
  - Macros, combos, tap dances, key overrides: added delete, duplicate, and
    reorder
  - Removed explanatory text from the dialogs; added help buttons and status
    indicators
  - Moved the license management server to another server. Stated what is sent
    and that it is not stored

Bug fixes
  - Macros: modifier keys that were physically held were released after the
    macro ran

2026-11-01 1.00  Initial release.


Vendor / Developer
------------------
Applet LLC
https://appletllc.com/ appletllc@gmail.com
