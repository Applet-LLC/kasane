# 8. Assigning Keys

When you click a key in the editor's keyboard diagram, the "Key assignment" window opens. Here you choose the action to assign to that key.

![Key assignment](../img/ja/assign-main.png)

The window is made of these parts.

| Part | Contents |
|---|---|
| The list on the left | The categories of actions |
| The area on the right | The keys of the selected category, or its setting boxes. The heading at the top shows the key being assigned and the layer (for example, `#1 Esc (KC_ESC) / Layer 0`) |
| Modifier check boxes | Left and right Ctrl, Shift, Alt, and Win. Check them before choosing a key to make a shortcut |
| The key code box | The name of the selected action (in Consolas). You can rewrite it directly |
| **Press to enter** | Enters the key code of the next key you press (Esc cancels) |
| The message box | Shows whether the input is valid (for example, `OK: Esc`) |
| **OK**, **Cancel** | Assigns and closes, or closes without assigning |

If you click another key, the window that is already open comes to the front. The key assignment window can be used while editing windows such as macros stay open.

If you click a key that has a layer action, Mod-Tap, Layer-Tap, macro, tap dance, or helper action assigned, the window opens with that category selected. The boxes hold the current setting (for example, for `LT(3,KC_ENTER)`, layer 3 and the tap key `KC_ENTER`). To fix only part of it, you do not need to choose everything again from the start.

<a id="categories"></a>
## Categories

| Category | Contents |
|---|---|
| Basic | Transparent (`KC_TRNS`, ▽), do nothing (`KC_NO`) |
| Letters, digits | A to Z, 0 to 9 |
| Symbols / Editing | Esc, Tab, Enter, BS, Space, Caps, symbol keys |
| Function | F1 to F24 |
| Modifiers | Left and right Ctrl, Shift, Alt, Win, and the Application key |
| Navigation | PrintScreen, Pause, Insert, Home, End, PgUp, PgDn, arrows, Delete |
| Numpad | NumLock, numpad digits and symbols |
| NLS | Hankaku/Zenkaku, Ro, Kana, ¥, Henkan, Muhenkan (keys on a Japanese keyboard), IME on, IME off |
| Media / System | Volume, play, calculator, browser keys, mail, sleep, power |
| Layer actions | `MO`, `TG`, `DF`, `OSL`, `TO`, `TT`, `LM`, `OSM` |
| Mod-Tap (MT) | A key on a short press, a modifier on a long press |
| Layer-Tap (LT) | A key on a short press, a layer on a long press |
| Macro / Tap dance | `MACRO(n)`, `TAPDANCE(n)` |
| Helper actions | `HLP(id)` |
| Keyboard view | Choose from a picture of the keyboard |
| Combo | Opens a combo that uses this key, or makes a combo with this key (this key's assignment does not change) |
| Key override | Opens a key override that uses this key as its condition, or makes one with this key (this key's assignment does not change) |

Keys that your keyboard has no physical key for, such as F13 to F24, NLS keys, and browser keys, can still be assigned as outputs. For example, you can assign "Henkan" to a key on a US layout.

The NLS **IME on** and **IME off** (`KC_LNG1`, `KC_LNG2`) put the IME in the ON or the OFF state, unlike Hankaku/Zenkaku, which toggles each time you press it. They are used like the Mac "Kana" and "Eisu" keys. They can be used on a Japanese keyboard layout. You can also write them with the QMK names (`KC_LANG1`, `KC_LANG2`, `KC_HAEN`, `KC_HANJ`).

For a key that is not in the list, you can write `SC_XXXX` directly in the key code box. `XXXX` is a 4-digit hexadecimal scan code (the upper 2 digits are one of `00`, `E0`, and `E1`; the lower 2 digits are not `00`). For example, the browser "Back" key is `SC_E06A`.

### Power keys

When you assign the sleep, power, or wake-up key, a confirmation appears, because pressing it may put the PC to sleep or shut it down. A ⚠ is also shown on the keycap.

![Power key confirmation](../img/ja/assign-power.png)

## Keys with modifiers

This is how to assign a shortcut such as Ctrl+C. Check the modifier check boxes at the bottom of the window first, then choose the key. For example, check left Ctrl and choose `C`, and you get `LCTL(KC_C)` (Ctrl+C). In the keyboard diagram, it is shown like `^C`.

You can also write it directly in the key code box, like `LCTL(LSFT(KC_T))`.

Even if you hold the key and press the next key, the added modifier is not applied to the next key. For example, the "a" right after the key that types "!" stays "a".

<a id="capture"></a>
## Press to enter

After you press **Press to enter**, press the key you want to enter, and that key's key code goes in. Esc cancels.

Left and right modifier keys, the numpad Enter, and Japanese keys (Muhenkan, Henkan, and so on) are entered distinguished from each other. Keys that Windows handles first, such as the Win key and Alt+Tab, cannot be captured. In that case, choose from **Keyboard view** or from the list.

<a id="keyboard-view-pick"></a>
## Choosing from the keyboard diagram

If you choose **Keyboard view** at the end of the list on the left, you can choose from a picture of the keyboard. If you check a modifier, it takes the form of `LCTL(KC_C)`.

![Keyboard view](../img/ja/assign-softkeyboard.png)

<a id="layer-actions"></a>
## Layer actions

When you choose **Layer actions**, boxes for **Type** and **Layer** appear. After choosing, press **Insert into key code**, and it goes into the key code box in a form like `MO(1)`.

![Layer actions](../img/ja/assign-layer.png)

| Action | Contents |
|---|---|
| `MO(n)` | Enables layer n only while the key is held |
| `TG(n)` | Each press toggles layer n on and off |
| `DF(n)` | Switches the base layer to n |
| `OSL(n)` | Uses layer n for just the next key you press |
| `TO(n)` | Enables only layer n (other layers are disabled) |
| `TT(n)` | Layer n while held. If you tap several times in a row, it stays enabled |
| `LM(n,mod)` | While held, enables layer n and also holds down a modifier |
| `OSM(mod)` | Applies a modifier to just the next key you press |

- Choose the modifier for `LM` and `OSM` with the check boxes at the bottom of the window.
- The number of taps needed to lock `TT(n)` is 5 by default. You can change it in the "Timing" tab of the settings dialog ([13. Settings Dialog](13-settings.html)).
- With `OSM`, if you press and release the key briefly, the modifier is applied to just the next key you press. For example, `a` after a Shift `OSM` gives "A". If you tap again while waiting, it is cancelled. If you use it while holding it down, it is an ordinary modifier.
- If you hold `LM(1,MOD_LCTL)` and press `C`, which is still ▽ on layer 1, you get Ctrl+C.

### Adding a layer

Add layers with **+ Add** in the editor. On layer 1 and above, all keys are ▽ (transparent) at first. ▽ means to use the same action as the layer below.

<a id="mod-tap"></a>
## Mod-Tap and Layer-Tap

These change the action between a short press and a long press. Set them with **Mod-Tap (MT)** and **Layer-Tap (LT)** in the list on the left.

| Action | Short press | Long press |
|---|---|---|
| Mod-Tap (`MT(mod,kc)`) | `kc` | A modifier (`mod`) |
| Layer-Tap (`LT(n,kc)`) | `kc` | Enables layer n |

![Mod-Tap](../img/ja/assign-modtap.png)

The **Tap key** box already holds the current key code of the key you clicked. For the A key, that is `KC_A`, so you only choose a modifier or a layer, and you get a key that gives "A on a short press". When the current assignment is not a plain key (such as ▽ or `LCTL(KC_C)`), the key code of the key's legend is entered.

- If you press another key while holding, it is treated as a hold. That key is entered with the assignment of the layer you switched to. For example, if you press `J` while holding `LT(1,KC_SPC)`, you get the key at the `J` position on layer 1.
- If you press again and keep holding right after a short press, the key of the short press repeats. You can hit the space bar repeatedly, or hold it.
- You can change the detection time and mode in the "Timing" tab of the settings dialog. If Mod-Tap is unintentionally treated as a hold when you type fast, try changing the mode.

<a id="macro-tapdance-helper"></a>
## Macros, tap dances, and helper actions

- **Macro / Tap dance**: assigns `MACRO(n)` and `TAPDANCE(n)`. Choose the type and enter a number. Press **Macro...** next to the number (**Tap dance...** when the type is `TAPDANCE`) to choose from a list. For how to make the contents, see [9. Macros, Combos, Tap Dances and Key Overrides](09-advanced.html).
  - Press **Edit...** to open the selected number in the macro (or tap dance) window.
  - Press **New...** to open an unused number. For a tap dance, it is made with this key's current key (for example, `KC_A`) put in **Single tap**. After you decide the contents, use **Insert TAPDANCE(n) into the key assignment field** in that window to assign it to this key.
- **Helper actions**: when you press the key, the tray runs something such as launching a program. Assign one with **Assign selected action**, or make one with **New action...**. You can fix the contents of an action selected in the list with **Edit...** (what you fix is saved on the spot). For details, see [11. Helper Actions and Per-App Layers](11-helper-apprules.html).
  - With **Trigger** at the bottom of the same window, you can choose a trigger (a key the tray watches; see [11. Helper Actions and Per-App Layers](11-helper-apprules.html)) and assign its key (in the form `LCTL(KC_F13)` if it has a modifier) to this key. Press **Assign selected trigger's key**. You can also make a trigger with **New trigger...** and assign it right away. A key assigned this way gets a **T** mark in the keyboard diagram (**H** for a `HLP(id)` key).

<a id="combo-keyoverride"></a>
## Combos and key overrides

With **Combo** and **Key override** at the end of the list on the left, you open or make the combos and key overrides that involve this key, each in its own window. This key's assignment does not change, so **OK** cannot be pressed while you have these two selected. To change the assignment, choose another category.

| Category | When one already exists | When you make a new one |
|---|---|---|
| Combo | A list of the combos that use this key appears. Choose one and press **Open...** to open that number in the combo window | **Create a combo with this key...** makes a combo in an unused number, with this key as **Key 1**. Choose key 2 and the key to send, and press **OK** in the combo window to confirm |
| Key override | A list of the key overrides that use this key as their condition appears. Choose one and press **Open...** to open that number in the key override window | **Create a key override with this key...** makes a key override in an unused number, with this key as **Key**. Write the modifiers and the replacement, and press **OK** in the key override window to confirm |

- One key can be used in only one combo. When this key is already used in an enabled combo, **Create a combo with this key...** cannot be pressed.
- The condition of a key override is the key that this key sends on this layer. A ▽ key is decided by the assignment of the layer below. For a key with modifiers such as `LALT(KC_ZKHK)`, the condition is `KC_ZKHK`. For an assignment that sends no key, such as `MO(1)`, you cannot make a key override.
- You can make several key overrides with different modifiers for the same key.
- Disabled combos and key overrides also appear in the list, with " (disabled)" added. You can open one and enable it again.
- A combo or key override you made does not go into the settings until you press **OK** or **Apply** in its own window. If you cancel, nothing changes.

You can do the same by right-clicking a key in the keyboard diagram. The menu shows items like "Open combo 3..." if there is something involving this key, and **Create a combo with this key...** and the like if there is not. **Create a tap dance with this key...** opens this key's assignment window together with the tap dance window.

## Keyword list

This is a list of the short names you write in assignment boxes, macros, combos, and so on. Most are the same names as in QMK Firmware. `n` is a layer number and `kc` is a key code.

| Keyword | Meaning |
|---|---|
| `MO(n)`, `TG(n)`, `DF(n)`, `OSL(n)`, `TO(n)`, `TT(n)` | Layer actions (the table above) |
| `LM(n,mod)` | While held, enables layer n and also holds down a modifier |
| `OSM(mod)` | Applies a modifier to just the next key you press |
| `MT(mod,kc)` | `kc` on a short press, a modifier on a long press |
| `LT(n,kc)` | `kc` on a short press, layer n on a long press |
| `MACRO(n)` | Runs macro n. It can also be used as the output of key overrides, combos, and tap dances |
| `TAPDANCE(n)` | Runs tap dance n, which changes the action by the number of taps or a long press |
| `HLP(id)` | The key itself sends nothing, and the tray runs an action (specific to kasane) |
| `LCTL(kc)`, `LSFT(kc)`, `LALT(kc)`, `LGUI(kc)` | Presses `kc` while holding the left modifier. The right ones work the same with `RCTL` and so on |
| `MOD_LCTL`, `MOD_LSFT`, `MOD_LALT`, `MOD_LGUI` | Names of modifiers (used in `LM`, `MT`, `OSM`, and key override conditions). The right ones are `MOD_RCTL` and so on |
| `KC_xxx` | An ordinary key (`KC_A`, `KC_F1`, `KC_ENTER`, and so on) |
| `KC_TRNS` | Transparent (▽). Uses the assignment of the layer below |
| `KC_NO` | A key that does nothing |
| `SC_xxxx` | Specifies a scan code directly in hexadecimal (for keys without a name) |

## Example: assigning F13 or the browser "Back" key

1. Click the key you want to assign.
2. Choose **Function** in the list on the left, and press `F13`. For the browser "Back" key, choose it from **Media / System**.
3. Press **OK**, and then press **Apply to keyboard (ON)**.

F13 to F24 are handy to use with other tools, and as triggers for helper actions. However, F24 may be used internally by kasane ([11. Helper Actions and Per-App Layers](11-helper-apprules.html)).
