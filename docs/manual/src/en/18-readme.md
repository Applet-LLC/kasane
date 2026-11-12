# 18. README and Background

The kasane README is directly under the installation folder. It explains how to use the installer, the system requirements, and the installation steps. When you click a link, it opens in the Windows default app (such as Notepad).

| File | Contents |
|---|---|
| [README.en-US.txt](../../README.en-US.txt) | The README in English |
| [README.ja-JP.txt](../../README.ja-JP.txt) | The README in Japanese |

The installation steps are also in [1. Installation](01-install.html) of this help, with screenshots.

<a id="history"></a>
## Version history

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
