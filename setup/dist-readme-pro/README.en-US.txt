kasane Pro - Installation Guide 1.02 2026-11-16 Applet LLC
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

This edition is the one-time purchase edition. The license key you
purchased on Gumroad is checked during installation.


Requirements
------------
- Windows 11 25H2 (x64 or ARM64)
- Administrator privileges for installation
- An internet connection. During installation, the license key is checked with
  Gumroad. This check happens only once; no connection is needed after
  installation.
  It connects to https://api.gumroad.com/v2/licenses/verify (HTTPS, port 443).
  If a corporate network or security software blocks it, installation fails.


Installation
------------

If you are switching from the Subscription edition, first uninstall the
Subscription edition and restart. The two editions cannot be installed on the
same PC.

1. Run "Install-kasane-pro.exe". Paste the license key you purchased on
   Gumroad into the license key box, and press "Install".

2. Follow the instructions of the setup wizard. The keyboard driver and the
   apps are installed.
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

6. After the restart, kasane is ready to use (the settings also take effect when
   you reconnect your keyboard).


Tray icon
---------
After you sign in, the kasane icon appears in the notification area of the
taskbar (inside the "^" flyout when hidden).
- Left click: opens the kasane settings dialog.
- Right click: a menu with 設定 (Settings), 一時停止 (Pause), レイヤー (Layer),
  バージョン情報 (About), and 終了 (Exit).
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

While the driver is not loaded (for example, before the restart), remapping
does not take effect. Basic keyboard input keeps working as it is.


Help
----
The kasane help opens from the "ヘルプ" (Help) button (F1 key) in the editor and
from the tray menu. It is in the manual folder of the installation folder.
The help is not yet available in English. Please use your browser's translation
feature. The screenshots in the help will remain in Japanese.


Files in this package
---------------------
- Install-kasane-pro.exe
  The installer launcher.

- KasaneProSetup-en-US.msi
- KasaneProSetup-ja-JP.msi
  The launcher automatically selects and runs the right one.

- License.en-US.txt
- License.ja-JP.txt
- License.en-US.rtf
- License.ja-JP.rtf
  The license agreement that applies to the Pro license key you purchased.
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
2. Remove "kasane Pro" from "Apps & features" in Windows Settings
   (administrator rights are required).
3. Restart your PC. The driver is in use by your keyboard, so its removal
   completes at the restart. Until then the driver may keep running (if you
   pressed OFF in step 1, no key assignments take effect).

What is removed:
- The program (C:\Program Files\applet\kasane) and its Start menu entries
- The driver (kbdlayer) and the settings stored in the driver (keymaps,
  macros, combos, timing, NICOLA, the emergency release key combination, and
  so on)

What is kept:
  So that a reinstall picks them up again, the following are kept.
- The files in the %LOCALAPPDATA%\applet\kasane folder, or in the folder
  specified by the KASANE environment variable.

To remove everything, delete this folder by hand after uninstalling and
restarting.

Installing a newer version over an existing one (without uninstalling) keeps
the driver settings, and you do not need to enter the license key again. You do
not need to uninstall first. If you uninstall and then reinstall, enter the
license key again.


Support / Purchase
------------------
You can check your purchased license key and re-download the zip file from
your Gumroad library at any time.


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
2026-11-16 1.02
New features
  - Installer: added a "Browse..." button to the settings folder page (when the
    box already holds a path, or the KASANE environment variable is already
    set, the dialog opens with that folder selected)
  - Added the English version of the help (it opens when the display language
    is English)
  - Added plain-text versions of the license agreement (License.ja-JP.txt,
    License.en-US.txt)
  - Emacs-style setup example: added C-q (send the next key as it is)

Improvements
  - Tray menu: "Settings" is now "Settings/Editor"
  - Editor: the buttons at the bottom are now in the order "Assignment list..."
    then "Settings..."
  - The installer (Install-kasane.exe) can no longer be started twice at once
  - The source license was inconsistent, so the source headers are now unified
    to Apache-2.0
  - License Manager, the license service, the installer, and the other
    programs now carry version 1.02

Bug fixes
  - After a key override with Alt or Win as its condition, a lone Alt press
    could reach the app and make the menu bar react in Notepad and similar
    apps (Issue #2). The driver now inserts a Ctrl tap right after it puts the
    released Alt or Win back

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
