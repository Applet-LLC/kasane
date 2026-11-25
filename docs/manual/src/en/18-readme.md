# 18. README and Background

The kasane README is directly under the installation folder. It explains how to use the installer, the system requirements, and the installation steps. When you click a link, it opens in the Windows default app (such as Notepad).

| File | Contents |
|---|---|
| [README.en-US.txt](../../README.en-US.txt) | The README in English |
| [README.ja-JP.txt](../../README.ja-JP.txt) | The README in Japanese |

The installation steps are also in [1. Installation](01-install.html) of this help, with screenshots.

<a id="history"></a>
## Version history

### 2026-11-25 1.03

New features

- Key overrides now have "Active layers". An override works only while a layer you chose is on (supported by the driver, the editor, `layers=` in KeymapTool, and exchange files)
- Nodoka import: assignments with a modifier held, inside window and `&Prefix` keymaps, are now converted to key overrides with active layers
- Emacs-style setup example: added a version for switching Excel sheets (emacs-excel.kasane.json). The Ctrl+L and Ctrl+; key overrides are limited to a layer that is on only while Excel is in front

Bug fixes

- When an exchange file was imported with "Append", the MACRO and HLP numbers in the replacement of a key override were not renumbered

### 2026-11-17 1.02

New features

- Installer: added a "Browse..." button to the settings folder page (when the box already holds a path, or the KASANE environment variable is already set, the dialog opens with that folder selected)
- Added the English version of the help (it opens when the display language is English)
- Added plain-text versions of the license agreement (License.ja-JP.txt, License.en-US.txt)
- Emacs-style setup example: added C-q (send the next key as it is)
- Bundled the layout of the ThinkPad Japanese keyboard (THINKPAD_JP)

Improvements

- Tray menu: "Settings" is now "Settings/Editor"
- Editor: the buttons at the bottom are now in the order "Assignment list..." then "Settings..."
- The KC_RO key (Ro) is now shown as "_" instead of the kana "ろ" on key caps in the Japanese display
- The installer (Install-kasane.exe) can no longer be started twice at once
- The source license was inconsistent, so the source headers are now unified to Apache-2.0
- License Manager, the license service, the installer, and the other programs now carry version 1.02

Bug fixes

- After a key override with Alt or Win as its condition, a lone Alt press could reach the app and make the menu bar react in Notepad and similar apps (Issue #2). The driver now inserts a Ctrl tap right after it puts the released Alt or Win back

### 2026-11-10 1.01

New features

- Support for Emacs-style operation: importing a settings file for it, mark mode (MARK), and KillLine (C-k)
- The number of key overrides raised from 32 to 128
- Helper actions: a choice of how to turn the IME on and off, and unique names for triggers
- A button that lists the assigned settings, and `--dump-text` added to the CLI

Improvements

- When you assign a key, that key's information is now filled in beforehand
- Delete, duplicate, and reorder added to macros, combos, tap dances, and key overrides
- Wording removed from dialogs, and a help button and status indicators added
- The license management server moved to another server. What is sent, and that it is not stored, are now stated clearly

Bug fixes

- Fixed a bug in macros where a modifier key that was physically held was released after the macro ran

### 2026-11-01 1.00

First release.
