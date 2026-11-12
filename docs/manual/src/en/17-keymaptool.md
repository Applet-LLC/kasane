# 17. KeymapTool and Samples

KeymapTool (`KeymapTool.exe`) is a tool for configuring the keyboard from the command line. You can do calibration, writing a keymap, and switching ON and OFF from the console. If you work on screen, start reading from [5. Editor Basics](05-editor-basics.html).

The installation folder holds a tutorial and sample keymaps for use with KeymapTool. This chapter is a guide to them. When you click a link, the file opens in the Windows default app.

## Tutorial

| File | Contents |
|---|---|
| [TUTORIAL-CLI.md](../../docs/TUTORIAL-CLI.md) | Steps, done with commands, from installation through calibration, writing a key swap, and checking that it works. It covers both JIS and US keyboards. It also covers a numpad alone, thumb-shift (NICOLA), and IME linking. (The tutorial is in Japanese) |

The tutorial is kept in the `docs` folder of the installation folder.

## Sample keymaps

They are in the `samples` folder of the installation folder. [samples/README.md](../../samples/README.md) explains how to use each file and what to check.

Start with these four. They are used in the steps of [TUTORIAL-CLI.md](../../docs/TUTORIAL-CLI.md).

| File | Contents |
|---|---|
| [tutorial-jis109-eisu-lctrl.json](../../samples/tutorial-jis109-eisu-lctrl.json) | Swaps the Eisu key and left Ctrl on a JIS keyboard |
| [tutorial-us104-caps-lctrl.json](../../samples/tutorial-us104-caps-lctrl.json) | Swaps CapsLock and left Ctrl on a US keyboard |
| [tutorial-swap-2key.json](../../samples/tutorial-swap-2key.json) | Swaps just two keys. Works on both JIS and US |
| [tutorial-10key-numbers.json](../../samples/tutorial-10key-numbers.json) | On a numpad-only keyboard, always sends digits regardless of NumLock |

These are samples for checking each feature.

| File | Contents |
|---|---|
| [nicola.json](../../samples/nicola.json) | The thumb-shift (NICOLA) layout. Figure 1 of the NICOLA layout standard, written out as it is |
| [nicola-enter-space.json](../../samples/nicola-enter-space.json) | The same layout as `nicola.json`, but makes Muhenkan Enter and Henkan Space when a thumb key is pressed alone |
| [test-keymap-nicola-toggle.json](../../samples/test-keymap-nicola-toggle.json) | A one-key layer that has only the key that switches thumb-shift ON and OFF |
| [test-keymap-modtap.json](../../samples/test-keymap-modtap.json) | An example of Mod-Tap and Layer-Tap, which change the action between a long press and a tap |
| [test-keymap-macro.json](../../samples/test-keymap-macro.json) | An example of a macro |
| [test-keymap-combo.json](../../samples/test-keymap-combo.json) | An example of a combo, which sends another key when two keys are pressed together |
| [test-keymap-tapdance.json](../../samples/test-keymap-tapdance.json) | An example of a tap dance, which changes the action by the number of taps |
| [test-keymap-helper.json](../../samples/test-keymap-helper.json) | An example of helper action keys that call things such as launching programs and typing strings |
| [test-keymap-simple.json](../../samples/test-keymap-simple.json) | A minimal example with only 10 keys |
| [test-keymap-us104.json](../../samples/test-keymap-us104.json) | A keymap that lists every key of a US 104 keyboard |
| [test-keymap-jis.json](../../samples/test-keymap-jis.json) | A keymap that lists every key of a JIS 109 keyboard |
| [test-keymap-10key.json](../../samples/test-keymap-10key.json) | A keymap for a numpad alone (17 keys) |

If you want to set up helper actions on screen, see [11. Helper Actions and Per-App Layers](11-helper-apprules.html).
