kasane Subscription - Installation Guide 1.00 2026-08-19 Applet LLC
==============================================================

This package installs "kbdlayer.sys", a Windows kernel-mode keyboard class
filter driver that emulates QMK/VIA/VIAL-style keyboard layers (layer
switching, Mod-Tap, Layer-Tap, and related actions) directly at the driver
level, so the remapped layout works system-wide -- in games, at the login
screen, and in any application -- without a user-mode background process
standing between your keyboard and Windows.

kasane is forked from the same author's kbdaddid device-identification
driver and reuses its per-device identification core, so a layer keymap can
be applied to one specific physical keyboard even when you have several
connected at once.

This edition's functionality is provided according to your Gumroad
subscription.


Requirements
------------
- Windows 11 24H2
- Administrator privileges for installation
- An internet connection (the license service re-checks your
  subscription with Gumroad about once every 24 hours. Using the last
  successful check, the feature keeps working offline for up to 7
  days without a network connection)


Installation
------------
1. Run "Install-kasane.exe".

2. Follow the setup wizard. This installs the keyboard driver, the license
   management service (kasaneLicenseSvc), and the license management
   tool.

   The installation folder is fixed to C:\Program Files\applet\kasane
   and cannot be changed (the tray app needs UI access (uiAccess), which
   requires a signed executable in a location only administrators can
   write to, such as Program Files). The folder where settings are stored
   can be changed in step 4.

3. You can choose how the device is identified: Device Instance ID
   mode or Hardware ID mode.

   The default is Device Instance ID mode. In this mode identification
   differs by connection point, so keyboards of the same model can be
   told apart.

   In Hardware ID mode, keyboards of the same model cannot be told
   apart and are treated as identical. In this mode, a device is still
   considered the same one even if you connect it differently.

4. Settings folder (optional). You can set where settings files (per-keyboard
   settings, tray settings, logs) are stored with the machine-wide
   environment variable KASANE. If left empty, each user's
   %LOCALAPPDATA%\applet\kasane is used as before. With Windows 11
   Administrator protection, an elevated settings window runs as a
   different user and the location would be split; setting it here keeps
   it the same. The folder is created if it does not exist.

5. Import a settings file (optional, off by default). Applies a settings
   file (*.kasane.json) exported from kasane to the driver during
   installation. This is for environments where you cannot "Apply to
   keyboard" after installation, such as with Administrator protection.
   Models that already have settings in the driver are replaced (the
   previous settings are kept as one backup and can be restored from the
   Settings dialog).

6. A system reboot is required to complete installation.

7. After reboot, "kasane License Manager" launches automatically
   from the Start menu. Enter the license key you purchased on Gumroad
   there to activate it.

8. The feature becomes usable after you restart again, or after
   reconnecting your keyboard (the driver picks up the current license
   status when a device is (re)attached).

Tray icon
---------
After you sign in, the kasane icon appears in the notification area of the
taskbar (inside the "^" flyout when hidden).
- Left click: opens kasane (the keymap editor).
- Right click: the menu 設定 (Settings) / 一時停止 (Pause) / ライセンス
  (License) / 終了 (Exit). "Pause" temporarily stops the remapping on all
  keyboards (press it again to resume).
- If you use NICOLA (thumb-shift input), it switches on automatically only
  while the IME is in kana mode (IME link; turn it on/off in kasane under
  Settings > NICOLA).
- Helper actions: a key can launch a program, type text, drive the mouse,
  switch layers, and more. Create them in kasane's key-assignment screen under
  "ヘルパー動作" (Helper actions); the tray must be running. Actions triggered by
  keys such as F13-F24 are under Settings > ヘルパー動作.
- Per-app layers: switch layers, or pause the remapping (for games), depending
  on the foreground app. Under Settings > アプリ別レイヤー.
- Import / export: in kasane, "インポート／エクスポート..." saves your settings
  to a file (*.kasane.json) and loads them back, e.g. for another keyboard or
  another PC. It can also convert and import a nodoka settings file.

Important: if your subscription license becomes inactive, layer
remapping is disabled; basic keyboard input is never affected.


Files in this package
----------------------
- Install-kasane.exe
  Installer launcher

- KasaneSetup-en-US.msi
- KasaneSetup-ja-JP.msi
  The launcher automatically selects and runs the right one.

- License.en-US.rtf
- License.ja-JP.rtf
  The license agreement covering your subscription.

- README.en-US.txt
- README.ja-JP.txt
  This README.


Uninstallation
--------------
1. Open kasane first and press the "OFF（素通しに戻す）" button so the keyboard
   returns to its normal behavior (kasane is displayed in Japanese only).
2. Use "Apps & features" in Windows Settings to remove "kasane Subscription"
   (administrator rights are required).
3. Restart your PC. The driver is in use by your keyboard, so its removal
   completes at the restart. Until then the driver may keep running (if you
   pressed OFF in step 1, no key remapping is applied).

What is removed:
- The program (C:\Program Files\applet\kasane) and its Start menu entries
- The license service (kasaneLicenseSvc)
- The driver (kbdlayer) and the settings stored in it (keymaps, macros,
  combos, timing, NICOLA, the emergency release keys, ...)

What is kept (so that a reinstall picks it up again):
- %ProgramData%\kbdlayer-license ... your license key and logs. After a
  reinstall the license is re-verified automatically with this key, so you
  do not need to activate again (an internet connection is required).
- %LOCALAPPDATA%\applet\kasane ... keymaps saved in kasane. After a reinstall,
  open kasane and press "キーボードに適用（ON）" (apply) to restore them.
  (If you used a version named LayerDriver, %LOCALAPPDATA%\LayerDriver is
  kept as well.)
To remove everything, delete these folders by hand after uninstalling and
restarting.

Installing a newer version over an existing one keeps the driver settings
and the license; you do not need to uninstall first.


Support / Purchase
-------------------
Your Gumroad library lets you check your purchased license key, manage
your subscription, or re-download the zip file at any time.


License
-------
kbdlayer is proprietary (closed-source) software, but we plan to publish
the source code in the future for the sake of transparency.

The device driver files provided in this zip are licensed for use on
a single PC only. Transferring or reselling the device driver files
or license key is not permitted. See License.en-US.rtf /
License.ja-JP.rtf for the full agreement.


Vendor / Developer
-------------------
Applet LLC
https://appletllc.com/ appletllc@gmail.com
