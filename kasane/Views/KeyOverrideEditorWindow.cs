using System.Windows;
using System.Windows.Controls;
using Kasane.Interop;
using Kasane.Model;

namespace Kasane.Views;

/// <summary>キー置換（Key Override。修飾キー＋キーを、別のキーやマクロに置き換える）の編集。</summary>
public sealed class KeyOverrideEditorWindow : IdListEditorWindow
{
    private readonly KeyOverrideDef?[] _defs = new KeyOverrideDef?[KbdLayerClient.MaxKeyOverrideCount];

    public List<KeyOverrideDef> Result { get; private set; } = new();

    public KeyOverrideEditorWindow(IEnumerable<KeyOverrideDef> keyOverrides, IReadOnlyDictionary<int, string>? names = null)
        : base(Loc.T("キー置換", "Key Override"), KbdLayerClient.MaxKeyOverrideCount,
            Loc.T(
            "キー置換は、指定した修飾キーを押しながら指定したキーを押したときに、代わりに別のキーを送ります。\n" +
            "例: 修飾キー MOD_LSFT、キー KC_BSPACE、置き換え先 KC_DELETE → Shift+BS で Delete。" +
            "置き換えている間、条件の修飾キーは一時的に離されます(置き換え先に LSFT(KC_X) のように書けば、その修飾キーは掛かります)。\n" +
            "置き換え先に MACRO(n) と書くと、キーを押したときにマクロnを1回実行します。実行の間だけ条件の修飾キーを離し、" +
            "終わったときに、まだ押していれば戻します(マクロの出力に Ctrl などが掛かりません)。マクロの機能が ON のときだけ動きます。\n" +
            "置き換え先には、レイヤー操作(OSL(n)・TG(n)・TO(n)・DF(n))、ヘルパー動作(HLP(n))、マークモード(MARK(on)・MARK(off)・MARK(toggle))も書けます。キーは出さず、キーの割り当てに同じ動作を置いたときと同じ処理をします" +
            "(条件の修飾キーは離しません)。例: Ctrl+X を OSL(1) にすると、次の1キーだけレイヤー1の割り当てになります。MARK(on) は Emacs のマークモードで、入っている間は移動のキー(矢印・Home・End・PageUp・PageDown)に Shift が付き、ほかのキー(修飾キーを除く)を押すと終わります。キー置換は128件まで設定できます(古いドライバは32件まで)。\n" +
            "修飾キーは MOD_LCTL|MOD_LSFT のように複数書けて、全部押されているときに置き換えます(左右は区別しません)。" +
            "キーは、レイヤーで切り替えた後に出るキーです。3つとも空にすると未使用になります。\n" +
            "「無効にする」にチェックを付けると、書いた内容を残したまま、キーボードには適用されません。",
            "A key override sends a different key instead when you press the specified key while holding the specified modifier keys.\n" +
            "Example: modifier MOD_LSFT, key KC_BSPACE, replacement KC_DELETE -> Shift+BS sends Delete. " +
            "While the replacement is sent, the modifier keys in the condition are temporarily released (write the replacement like LSFT(KC_X) to apply a modifier).\n" +
            "If you write MACRO(n) as the replacement, pressing the key runs macro n once. The condition's modifier keys are released only while it runs, " +
            "and restored afterwards if they are still held (so Ctrl and the like are not applied to the macro's output). This works only while the macro feature is ON.\n" +
            "The replacement can also be a layer action (OSL(n), TG(n), TO(n), DF(n)), a helper action (HLP(n)) or mark mode (MARK(on), MARK(off), MARK(toggle)). No key is sent; it does the same as putting that action on a key in the keymap " +
            "(the condition's modifier keys are not released). Example: Ctrl+X -> OSL(1) makes only the next key use layer 1. MARK(on) is Emacs mark mode: while it is on, the movement keys (arrows, Home, End, PageUp, PageDown) get Shift, and any other key (except modifiers) ends it. Up to 128 key overrides can be set (older drivers: 32).\n" +
            "You can write several modifiers, like MOD_LCTL|MOD_LSFT; the override applies when all of them are held (left and right are not distinguished). " +
            "The key is the key produced after layer switching. If all three fields are empty, the override is unused.\n" +
            "If you check \"Disable\", the content is kept but not applied to the keyboard."))
    {
        foreach (var k in keyOverrides)
            if (k.Id >= 0 && k.Id < _defs.Length)
            {
                _defs[k.Id] = new KeyOverrideDef { Id = k.Id, Mods = k.Mods, Key = k.Key, Replacement = k.Replacement, RepeatMacro = k.RepeatMacro };
                LoadDisabled(k.Id, k.Disabled);
            }
        LoadNames(names);
    }

    /// <summary>
    /// 最初の空き番号に、条件のキーを <paramref name="key"/> にしたキー置換を作って選ぶ（修飾キーと置き換え先は利用者が書く）。
    /// プロジェクトは、適用・OK まで変わらない。空き番号が無ければ -1。
    /// </summary>
    public int StartNew(string key)
    {
        int id = FreeId();
        if (id < 0) return -1;
        _defs[id] = new KeyOverrideDef { Id = id, Mods = "", Key = key, Replacement = "" };
        RefreshItem(id);
        ShowId(id);
        return id;
    }

    protected override string Summary(int id) =>
        _defs[id] is { } k ? $"{k.Mods} + {k.Key} → {k.Replacement}" + (k.RepeatMacro ? Loc.T("(繰り返す)", " (repeat)") : "") : "";

    private static bool IsMacro(string replacement) => replacement.Trim().StartsWith("MACRO(", StringComparison.OrdinalIgnoreCase);

    protected override void ShowDetail(int id)
    {
        var d = _defs[id];
        var mods = KeycodeBox(d?.Mods ?? "", "ModsBox");
        var key = KeycodeBox(d?.Key ?? "", "KeyBox");
        var replacement = KeycodeBox(d?.Replacement ?? "", "ReplacementBox");
        var repeat = new CheckBox
        {
            Name = "RepeatMacroCheck",
            Content = Loc.T("キーを押し続けたとき(オートリピート)も、マクロを繰り返す", "Repeat the macro while the key is held (auto-repeat)"),
            IsChecked = d?.RepeatMacro ?? false,
            Margin = new Thickness(0, 6, 0, 0),
        };

        void Changed()
        {
            repeat.Visibility = IsMacro(replacement.Text) ? Visibility.Visible : Visibility.Collapsed;
            var boxes = new[] { mods, key, replacement };
            if (boxes.All(b => b.Text.Trim().Length == 0)) _defs[id] = null;
            else _defs[id] = new KeyOverrideDef
            {
                Id = id,
                Mods = mods.Text.Trim(),
                Key = key.Text.Trim(),
                Replacement = replacement.Text.Trim(),
                RepeatMacro = repeat.IsChecked == true && IsMacro(replacement.Text),
            };
            RefreshItem(id);
            var errors = new List<string>();
            if (_defs[id] is { } k) ProjectService.KeyOverrideToEntry(k, errors);
            ShowValidation(errors.Count == 0 ? "OK" : string.Join("\n", errors), errors.Count > 0);
        }
        foreach (var b in new[] { mods, key, replacement }) b.TextChanged += (_, _) => Changed();
        repeat.Click += (_, _) => Changed();

        Detail.Children.Add(Row(Loc.T("修飾キー", "Modifiers"), mods));
        Detail.Children.Add(Row(Loc.T("キー", "Key"), KeycodeField.Wrap(key, allowMods: false)));
        Detail.Children.Add(Row(Loc.T("置き換え先", "Replace with"), KeycodeField.Wrap(replacement, acceptsMacro: true)));
        Detail.Children.Add(repeat);
        repeat.Visibility = IsMacro(replacement.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    protected override void OnItemEnabled(int id)
    {
        var errors = new List<string>();
        if (_defs[id] is { } k) ProjectService.KeyOverrideToEntry(k, errors);
        ShowValidation(errors.Count == 0 ? "OK" : string.Join("\n", errors), errors.Count > 0);
    }

    protected override bool ValidateAll(out string error)
    {
        var errors = new List<string>();
        // 無効のものは、検証しない。
        foreach (var k in _defs.Where(k => k != null && !Disabled[k.Id])) ProjectService.KeyOverrideToEntry(k!, errors);
        error = string.Join("\n", errors);
        if (errors.Count > 0) return false;
        Result = _defs.Where(k => k != null).Select(k => { k!.Disabled = Disabled[k.Id]; return k; }).ToList();
        return true;
    }
}
