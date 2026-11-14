# 14. Import and Export

With **Import / Export...** at the bottom of the editor window, you can write settings out to a file (`*.kasane.json`) and read them back in. Use it for backups, for moving to another keyboard or another PC, and for resetting settings.

![Import / Export](../img/ja/impexp-export.png)

The window has three tabs: **Export**, **Import**, and **Reset**.

<a id="export"></a>
## Export

This writes settings to a file. You choose the scope.

| Scope | Contents |
|---|---|
| Everything | The settings of all keyboards and of the tray |
| This keyboard | The settings of the one keyboard you selected |
| One layer | One layer. The macros, tap dances, and helper actions that the layer uses go in with it |

You can also open it by right-clicking the layer list and choosing **Export this layer...**.

The license key is not included in the file you export.

Keys are written as scan codes, so the file can be imported into a keyboard with a different layout. Measured values are included only for backups (when restoring to the same device).

Save the file with **Export...**. With the check boxes **Include helper settings** (actions, triggers, and per-app layers) and **Measured scan codes** (for backing up to the same keyboard), you choose what to include.

When the scope is **Everything**, you can also choose **Include recovery settings**. This adds the following settings, which are not in an ordinary file. They are for restoring to the same PC.

- Tray settings: the display (how the layer number is overlaid on the icon), colors, language, how the IME state is read, the setting to remap only while the tray is running, and how desktop icons are shown
- The emergency-release key combination
- The i8042prt settings
- The sharing scope of each keyboard
- Layouts you added yourself (those in the `Layouts` folder)

When you include recovery settings, the default in the save window is `kasane-recovery.kasane.json` in the settings folder. If you leave it in the settings folder with that name, the installer finds it and imports it when you reinstall ([1. Installation](01-install.html)).

Exports of **One layer** and **This keyboard** do not include recovery settings. This is so that settings from your own PC do not get mixed into a file you give to someone else.

**Open the output location in Explorer** can always be used. The current output location is shown to the right of the button. After you have exported, it opens with that file selected (the folder, for JSON for KeymapTool). Before you export, it opens the settings folder.

### JSON for KeymapTool

When you choose **This keyboard** as the scope, you can choose **JSON for KeymapTool** as the format. It writes `keymap.json` and `profile.json` to the folder you choose. These are input files for the command-line `KeymapTool --apply-simple`. Use this format only when you want to manage settings from the command line.

Macros, combos, tap dances, and key overrides are not included (set them separately with `--set-macro` and the like of `KeymapTool`). For backups and for handing settings to another PC, use the default `*.kasane.json`.

<a id="import"></a>
## Import

This reads in a file you exported.

![Import](../img/ja/impexp-import.png)

1. In the **Import** tab, choose a file with **Choose File...**.
2. Choose the **Items to import** (layers, macros, combos, and so on). The items and the import mode cannot be chosen until the file has been loaded, because the items you can choose depend on the contents of the file. Measured scan codes can be imported only when the layout is the same. When the file contains recovery settings, you can also choose **Recovery settings**. The emergency release, i8042prt, and the sharing scope are imported only when it is opened as administrator. The sharing scope is imported only for connected keyboards with the same UniqId. Layouts are not overwritten if a file with the same name exists.
3. Match the keyboards in the file to the connected keyboards. They are matched automatically. If they do not match, choose on the screen ([15. Backup and Migration](15-backup-migrate.html)).
4. Choose the import mode, **Replace** or **Add**.
   - Replace: replaces the chosen items with the contents of the file.
   - Add: keeps the current settings and adds the contents of the file. Macros and the like with overlapping numbers are renumbered.
5. Press **Preview** to check the result of importing without changing anything. If there is no problem, press **Import**.

What you import only goes into the state being edited. Press **Apply to keyboard (ON)** afterward. Saving is automatic (only helper settings are saved immediately).

Keys that are not in the current layout are not imported and are shown on the screen.

Layers are imported as follows.

- The layers in the file are imported by number (from layer 0 in order). Layer 0 of the file goes into the current layer 0, and layer 1 into the current layer 1.
- Replace: rebuilds the current layer from the contents of the layer in the file. Add: adds the keys of the file on top of the current layer. Missing layers are added.
- If you clear the **Layers** check, no layer of the file is imported. You cannot choose the layers in the file and import them one by one.
- For a file in which only one layer was exported, you can choose which layer to import into.

> Before importing, export your current settings and keep them. Except for importing everything, no automatic backup is made.

Before importing everything, the current settings are backed up automatically to `backup` in the settings folder.

### Importing the Emacs example

Press **Emacs Example...** under **Choose File...** in the **Import** tab, and the Emacs-style key operation example bundled with the installation (`samples\emacs\emacs.kasane.json`) is loaded (the same folder also has <a href="../../samples/emacs/EMACS-KEYBINDINGS.html" target="_blank" rel="noopener">EMACS-KEYBINDINGS.html</a>, which explains how to use it; click it to open it in a new window. It is written in Japanese). Choose the three items "Layers", "Macros", and "Key overrides", set the mode to **Replace**, check the contents, and press **Import**. Then press **Apply to keyboard (ON)**.

<a id="nodoka"></a>
## Importing nodoka settings

You can convert a nodoka (nodoka4) settings file (`.nodoka`) into kasane settings and import it. nodoka and kasane differ in how key remapping works and is expressed, and kasane lacks some features. The file is converted and imported, but some things cannot be read correctly and some cannot be imported, so please use it with care.

![Importing nodoka settings](../img/ja/nodoka-import.png)

1. In the **Import** tab, press **Import Nodoka Settings...**.
2. Choose from the list of nodoka settings (★ marks the one in use now), or specify a `.nodoka` file with **Choose File...**. The symbol that corresponds to nodoka's `-D` (such as `USE109`) is entered when you choose from the list. You may also edit it.
3. Press **Convert**. The result is divided into "converted", "approximated", and "cannot convert", each shown with `file name:line`. You can switch with **Show**.
4. When you press **Import with these contents**, you return to the import window. As with other imports, you choose the items and the destination and import. For nodoka settings, **Add** is the default. If you choose Replace, items that nodoka does not have are also removed.

![The conversion result](../img/ja/nodoka-result.png)

Files that are `include`d are also read. The character encoding is determined in this order: BOM (UTF-16, UTF-8), UTF-8, CP932.

### How it is converted

A nodoka setting is a table of "key × modifier state × keymap", while kasane has a table of "key → action, for each layer". So it is flattened as follows.

| nodoka | kasane |
|---|---|
| `key *X = *Y` (Global) | Layer 0: X → Y |
| `key C-X = Home` | A key override (Ctrl, X → Home) |
| The right side has several keys | A macro (types them in order when pressed; up to 64 steps). Long ones, and ones with waits or strings, become helper sequences |
| `mod M0 = !Up` and `key M0-Left = …` | `MO(layer)` on Up, and an assignment for Left on that layer |
| `mod M0 = !!Space` | `LT(layer, Space)` |
| `mod shift += !!Space` (SandS) | `MT(MOD_LSFT, KC_SPC)` |
| `&Toggle(Lock0)` | `TG(layer)` |
| `&Prefix(km)` | `OSL(layer)` |
| `&EditNextModifier(M-)` | `OSM(MOD_LALT)` |
| `&ShellExecute`, `&SendText`, `&Wait`, `&MouseMove`, `&MouseWheel` | A helper action as `HLP(n)` |
| `combo A B = X` (2 keys) | A combo |
| `taphold X tap=A hold=LShift` | `MT(MOD_LSFT, A)` |
| `tapdance X tap1= tap2= tap3=` | A tap dance |
| `window W /class/` | A layer for W, and a per-app layer rule |
| `def option UnitID = …` | Made separately as a setting for that keyboard |

Layers: Global is layer 0, and the others are numbered 1, 2, … in the order they appear in the assignments. Anything beyond 16 layers is not converted and appears in the report. When you import with Add, they overlap the current layers with the same numbers. If you are using layer 1 or above now, move them away first, or check the destination.

### What cannot be converted

The following are not converted and are reported with `file name:line`. Only that line is treated as if it did not exist, and the other lines are converted.

- Conditions: `IL-` (IME on), `IC-` (converting), `NL-`, `CL-`, `SL-`, `KL-`, `MAX-`, `MIN-`, `DP-` (double press), `U-`, `D-`, `R-`, `T-` (touchpad), `K0-`
- Functions: `&Window*`, `&Clipboard*`, `&Emacs*`, `&PostMessage`, `&SendPostMessage`, `&SetForegroundWindow`, `&LoadSetting`, `&HelpMessage`, `&Repeat`, `&MouseHook`, `&SetIme*`, and so on
- Anything that uses a mouse or gamepad pseudo key (`LButton`, `GP_*`, and so on) on the left or right side
- Combos of 3 or more keys, combos other than Global, taphold, tapdance, `event`
- "A sequence of several keys" and "layer operations" while holding modifiers

### What behaves a little differently from nodoka

- The condition "only when no modifier is held" is not distinguished in kasane.
- A sequence of several keys does not behave as holding the last key down only while you hold the key.
- `mod M0 = X` without a "!" keeps the key's own action in nodoka, but in kasane it works as a layer key.
- `&Prefix` is `OSL` (just the next one key).
- A helper action while holding modifiers is a keyboard hook trigger. It does not reach the app, and the tray must be running.

<a id="reset"></a>
## Reset

This returns only the items you choose to their initial state. Items you did not choose remain.

![Reset](../img/ja/impexp-clear.png)

| What you choose | Contents |
|---|---|
| Items | Layers, macros, combos, tap dances, key overrides, timing, feature ON and OFF, NICOLA, helper settings, and measured scan codes. There are also **Select All** and **Deselect All** buttons |
| Target | **This keyboard**, **All connected keyboards**, or **Selected keyboards** (check them in the list) |

- After choosing the items, press **Reset...**.
- When running as administrator, the settings stored in the driver are also removed at the same time. In read-only mode, only the settings on the screen are reset.
- For keyboards that are "Shared by keyboards of the same model", removing the driver's settings affects every keyboard of the same model. This is shown in the confirmation.
- Before it runs, the current settings are backed up automatically to `backup` in the settings folder (`before-clear-….kasane.json`). You can restore them with Import. Settings that are only in the driver and not shown on the screen are not included in the backup.
- When you reset **Helper settings**, each keyboard's `helper.json` is removed. When the target is **All connected keyboards**, the tray's shared helper settings are removed too.

## Restoring from the backup made before an import

Backups made before an import or a reset are in `backup` in the settings folder. They are in the same format as exported files, so you can restore them by importing with **Import**.
