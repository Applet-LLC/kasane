# 12. Setup Examples

These are common setups, with steps. How to use the key assignment window and each editor is explained in [8. Assigning Keys](08-key-assign.html) and [9. Macros, Combos, Tap Dances and Key Overrides](09-advanced.html).

| What you want to do | Example |
|---|---|
| Swap Caps Lock and left Ctrl | [Example 1](#recipe1) |
| Make Caps Lock Esc on a short press and Ctrl on a long press | [Example 2](#recipe2) |
| Use Space as Shift while holding it | [Example 3](#recipe3) |
| Type arrow keys with H, J, K, and L while holding Space | [Example 4](#recipe4) |
| Disable Caps Lock | [Example 5](#recipe5) |
| Turn the IME on and off with Caps Lock, and make Shift+Caps Lock work as Caps Lock | [Example 6](#recipe6) |
| Make right Alt a simultaneous press of left Ctrl and left Alt (AltGr) | [Example 7](#recipe7) |
| Make Shift+2 produce `"` | [Example 8](#recipe8) |
| Make Shift+BS into Delete | [Example 9](#recipe9) |
| Send a sequence of keys (a macro) with Ctrl+1 | [Example 10](#recipe10) |
| Make the numpad switch among three layouts each time you press a key | [Example 11](#recipe11) |

## Steps for assigning a key

1. In the editor, choose the keyboard and the layout to configure ([5. Editor Basics](05-editor-basics.html)).
2. Click a key in the keyboard diagram to open the "Key assignment" window.
3. Choose from the list on the left to assign, or type the key code written in the example, as it is, into the **Key code** box at the bottom of the window, and press **OK**.
4. When you are done, press **Apply to keyboard (ON)** (you must have started as administrator).

You can write the key codes in this chapter in the **Key code** box. You can check a key's name by pressing the key in the keyboard diagram.

To set keys on another layer, choose the layer number in the layer list at the top of the window, then click the key. To add layers, press **+ Add**.

<a id="recipe1"></a>
## Example 1: Swap Caps Lock and left Ctrl

| Key | Key code to assign |
|---|---|
| Caps Lock (the Eisu key on JIS) | `KC_LCTRL` |
| Left Ctrl | `KC_CAPSLOCK` |

![The swapped keys](../img/ja/editor-swapped.png)

The detailed steps are in "Swapping keys" in [5. Editor Basics](05-editor-basics.html).

<a id="recipe2"></a>
## Example 2: Make Caps Lock Esc on a short press and Ctrl on a long press

This uses Mod-Tap.

| Key | Key code to assign |
|---|---|
| Caps Lock | `MT(MOD_LCTL,KC_ESC)` |

In the "Key assignment" window, choose **Mod-Tap (MT)** in the list on the left, specify left Ctrl as the modifier and `KC_ESC` as the tap key, and press **Insert into key code**.

- If you press and release it briefly, it is Esc.
- If you hold it and press another key, Ctrl is held. For example, if you hold Caps Lock and press `C`, it is Ctrl+C.
- If it is unintentionally treated as a hold when you type fast, change the tap-hold mode in the "Timing" tab of the settings dialog ([13. Settings Dialog](13-settings.html)).

<a id="recipe3"></a>
## Example 3: Use Space as Shift while holding it

| Key | Key code to assign |
|---|---|
| Space | `MT(MOD_LSFT,KC_SPC)` |

A short press is Space. If you hold it and press another key, Shift is held.

<a id="recipe4"></a>
## Example 4: Type arrow keys with H, J, K, and L while holding Space

Using Layer-Tap, you switch to another layout (layer 1) only while Space is held.

1. Make layer 1 with **+ Add**.
2. Assign `LT(1,KC_SPC)` to Space on layer 0. You can also specify 1 as the layer and `KC_SPC` as the tap key with **Layer-Tap (LT)** in the "Key assignment" window.
3. Choose layer 1 and assign these keys.

| Key | Key code to assign |
|---|---|
| H | `KC_LEFT` |
| J | `KC_DOWN` |
| K | `KC_UP` |
| L | `KC_RIGHT` |

Leave the other keys on layer 1 as ▽ (transparent). While Space is held, the other keys work as usual.

<a id="recipe5"></a>
## Example 5: Disable Caps Lock

| Key | Key code to assign |
|---|---|
| Caps Lock | `KC_NO` |

Choosing **KC_NO** (✕) in the **Basic** category does the same.

<a id="recipe6"></a>
## Example 6: Turn the IME on and off with Caps Lock

You make Caps Lock a key that toggles the Japanese input method (IME) on and off. Alt+Hankaku/Zenkaku is a combination that toggles Microsoft IME on and off on both JIS and US keyboards.

| Key | Key code to assign |
|---|---|
| Caps Lock | `LALT(KC_ZKHK)` |

`KC_ZKHK` is the Hankaku/Zenkaku key. Choosing **Hankaku/Zenkaku** in the **NLS** category and checking the **Left Alt** modifier gives the same assignment.

> When you want one key to turn the IME on and another to turn it off, assign **IME on** (`KC_LNG1`) and **IME off** (`KC_LNG2`) from the **NLS** category. You do not need to change any IME setting. Pressing IME on when it is already on leaves it on ([8. Assigning Keys](08-key-assign.html)).
>
> If you have assigned IME-On to Henkan and IME-Off to Muhenkan in Microsoft IME's "Customize keys and touch", you can use Henkan (`KC_HENK`) and Muhenkan (`KC_MHEN`) in the same way. If you press Henkan without this assignment, the IME treats it as reconversion.

### Making Shift+Caps Lock the original Caps Lock

If you assign `LALT(KC_ZKHK)` to the Caps Lock key, the same assignment works even while you hold Shift. If you want to use Shift+Caps Lock as the original Caps Lock, add a key override.

1. Open **Key overrides...** at the bottom of the editor window, enter the following, and press **OK**.

| Item | Input |
|---|---|
| Modifiers | `MOD_LSFT` |
| Key | `KC_ZKHK` |
| Replace with | `LSFT(KC_CAPSLOCK)` |

2. In the "Features" tab of **Settings...**, check that key overrides are ON.
3. Press **Apply to keyboard (ON)**.

- For **Key**, write the output key that the key override targets. It is the key part (`KC_ZKHK`) of the `LALT(KC_ZKHK)` assigned to Caps Lock.
- For **Replace with**, use `LSFT(KC_CAPSLOCK)` on Windows with a JIS layout. On a JIS layout, the Caps Lock key without Shift works as the "Eisu" key, and Caps Lock toggles only while Shift is held. On Windows with a US layout, `KC_CAPSLOCK` is enough.

It then works as follows.

- When you press Caps Lock alone, the IME is toggled on and off.
- When you press Caps Lock while holding Shift, Caps Lock is toggled.

> A key override decides its target by the output key. So even when you press the real Hankaku/Zenkaku key together with Shift, it is replaced with Caps Lock.

<a id="recipe7"></a>
## Example 7: Make right Alt a simultaneous press of left Ctrl and left Alt (AltGr)

| Key | Key code to assign |
|---|---|
| Right Alt | `LCTL(KC_LALT)` |

When you press right Alt, left Ctrl and left Alt are held at the same time. On Windows with a keyboard layout that has AltGr (German and others), the characters typed with AltGr come out. With layouts without AltGr (US, Japanese, Korean), it works as the Ctrl+Alt shortcut.

Conversely, if right Alt unintentionally becomes AltGr (a simultaneous press of left Ctrl and left Alt) and causes trouble, see the troubleshooting in [16. Troubleshooting and Known Limitations](16-reference.html).

<a id="recipe8"></a>
## Example 8: Make Shift+2 produce `"`

This is an example of using a US keyboard on Windows with a US layout, and changing Shift+2 from `@` to `"`. It uses a key override.

1. Set the keyboard layout to `US104`.
2. Open **Key overrides...** at the bottom of the editor window.
3. Enter the following and press **OK**.

| Item | Input |
|---|---|
| Modifiers | `MOD_LSFT` |
| Key | `KC_2` |
| Replace with | `LSFT(KC_QUOTE)` |

4. In the "Features" tab of **Settings...**, check that key overrides are ON.
5. Press **Apply to keyboard (ON)**.

For the replacement, write the apostrophe key with Shift (`LSFT(KC_QUOTE)`). While the replacement is in effect, the condition's Shift is temporarily released, so if you do not write Shift in the replacement, `'` comes out.

> Conversely, if you use a US keyboard on Windows with a JIS layout, Shift+2 produces `"`. If you want `@`, specify `KC_LBRC` as the replacement ("Key overrides" in [9. Macros, Combos, Tap Dances and Key Overrides](09-advanced.html)).

<a id="recipe9"></a>
## Example 9: Make Shift+BS into Delete

| Item | Input |
|---|---|
| Modifiers | `MOD_LSFT` |
| Key | `KC_BSPACE` |
| Replace with | `KC_DELETE` |

The steps are the same as in Example 8.

<a id="recipe10"></a>
## Example 10: Send a sequence of keys (a macro) with Ctrl+1

As an example, you send a macro that selects all and copies (Ctrl+A, Ctrl+C) with Ctrl+1. It uses a macro and a key override.

1. Open **Macros...** at the bottom of the editor window.
2. In the contents of macro 0, enter the following (keys separated by spaces).

```
LCTL(KC_A) LCTL(KC_C)
```

3. Press **OK**.
4. Open **Key overrides...**, enter the following, and press **OK**.

| Item | Input |
|---|---|
| Modifiers | `MOD_LCTL` |
| Key | `KC_1` |
| Replace with | `MACRO(0)` |

5. In the "Features" tab of **Settings...**, check that macros and key overrides are ON.
6. Press **Apply to keyboard (ON)**.

When you press Ctrl+1, macro 0 runs once. Ctrl+1 itself does not reach the app. The Ctrl you are holding is released only during the run, so write keys that use Ctrl inside the macro like `LCTL(KC_A)`.

When you want to send a sequence that includes strings or waits (delays), use a helper action trigger ([11. Helper Actions and Per-App Layers](11-helper-apprules.html)).

<a id="recipe11"></a>
## Example 11: Make the numpad switch among three layouts each time you press a key

With a keyboard that is only a numpad, you switch layer 0 → 1 → 2 → 0 with the 0 key.

- Layer 1: some keys become shortcuts and modifiers.
- Layer 2: the keys 1 to 9 type fixed strings.

### Preparation

1. Choose the numpad keyboard and set the layout to `10KEY`.
2. Press **+ Add** twice to make layers 1 and 2.

### Switching layers

`TO(n)` enables only layer n. If you assign `TO(n)` that goes to the next layer to the 0 key on each of the three layers, each press moves 0 → 1 → 2 → 0.

| Layer | 0 key |
|---|---|
| 0 | `TO(1)` |
| 1 | `TO(2)` |
| 2 | `TO(0)` |

In the "Key assignment" window, choose `TO` for **Type** under **Layer actions**, specify the number for **Layer**, and press **Insert into key code**.

### Layer 1 assignments

The 0 key on layer 1 is used for switching, so left Shift is assigned to the `+` key.

| Key | What it sends | Key code (Windows with a US layout) | Key code (Windows with a JIS layout) |
|---|---|---|---|
| 4 | Ctrl+`]` | `LCTL(KC_RBRACKET)` | `LCTL(KC_BSLASH)` |
| 1 | Ctrl+`[` | `LCTL(KC_LBRACKET)` | `LCTL(KC_RBRACKET)` |
| 5 | Ctrl+Shift+P | `LCTL(LSFT(KC_P))` | Same |
| 2 | Ctrl+Shift+O | `LCTL(LSFT(KC_O))` | Same |
| `+` | Left Shift | `KC_LSHIFT` | Same |
| `.` | Left Ctrl | `KC_LCTRL` | Same |
| Enter | Left Alt | `KC_LALT` | Same |
| 0 | Go to layer 2 | `TO(2)` | Same |

The key codes for `[` and `]` differ by layout because a key code represents the position of the key (the scan code). When the Windows layout is JIS, the `]` key is `KC_BSLASH` and the `[` key is `KC_RBRACKET`.

### Layer 2 assignments

Assign the helper action "Type text" to the keys 1 to 9. The 0 key goes back to layer 0.

| Key | Assignment |
|---|---|
| 1 to 9 | A helper action (Type text). Example: 1 is "Thank you for your continued support.", 2 is "Best regards." |
| 0 | `TO(0)` |

1. Choose layer 2 and click the 1 key.
2. In the list on the left, choose **Helper actions** and press **New action...**.
3. Set the type to **Type text (Text)**, write the string to type, and press **OK**. A code such as `HLP(1)` goes into the key.
4. Do the same for the keys 2 to 9.

Helper actions work only while the tray is running ([11. Helper Actions and Per-App Layers](11-helper-apprules.html)).

### Apply and check

Press **Apply to keyboard (ON)**. Open Notepad and check as follows.

1. Press the 0 key. It becomes layer 1 (the layer number 1 appears on the tray icon).
2. Press 4 and check that Ctrl+`]` works. If you press another key while holding `+`, Shift is held.
3. Press the 0 key. It becomes layer 2. The keys 1 to 9 type the strings you set.
4. Press the 0 key. It goes back to layer 0.

You can also switch layers from **Layers** in the tray menu ([4. Tray](04-tray.html)).
