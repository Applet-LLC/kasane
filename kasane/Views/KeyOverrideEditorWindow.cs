using Kasane.Interop;
using Kasane.Model;

namespace Kasane.Views;

/// <summary>キー置換（Key Override。修飾キー＋キーを、別のキーに置き換える）の編集。</summary>
public sealed class KeyOverrideEditorWindow : IdListEditorWindow
{
    private readonly KeyOverrideDef?[] _defs = new KeyOverrideDef?[KbdLayerClient.MaxKeyOverrideCount];

    public List<KeyOverrideDef> Result { get; private set; } = new();

    public KeyOverrideEditorWindow(IEnumerable<KeyOverrideDef> keyOverrides)
        : base("キー置換", KbdLayerClient.MaxKeyOverrideCount,
            "キー置換は、指定した修飾キーを押しながら指定したキーを押したときに、代わりに別のキーを送ります。\n" +
            "例: 修飾キー MOD_LSFT、キー KC_BSPACE、置き換え先 KC_DELETE → Shift+BS で Delete。" +
            "置き換えている間、条件の修飾キーは一時的に離されます（置き換え先に LSFT(KC_X) のように書けば、その修飾キーは掛かります）。\n" +
            "修飾キーは MOD_LCTL|MOD_LSFT のように複数書けて、全部押されているときに置き換えます（左右は区別しません）。" +
            "キーは、レイヤーで切り替えた後に出るキーです。3つとも空にすると未使用になります。")
    {
        foreach (var k in keyOverrides)
            if (k.Id >= 0 && k.Id < _defs.Length)
            {
                _defs[k.Id] = new KeyOverrideDef { Id = k.Id, Mods = k.Mods, Key = k.Key, Replacement = k.Replacement };
                Names[k.Id] = k.Name;
            }
    }

    protected override string Summary(int id) =>
        _defs[id] is { } k ? $"{k.Mods} + {k.Key} → {k.Replacement}" : "";

    protected override void ShowDetail(int id)
    {
        var d = _defs[id];
        var mods = KeycodeBox(d?.Mods ?? "", "ModsBox");
        var key = KeycodeBox(d?.Key ?? "", "KeyBox");
        var replacement = KeycodeBox(d?.Replacement ?? "", "ReplacementBox");

        void Changed()
        {
            var boxes = new[] { mods, key, replacement };
            if (boxes.All(b => b.Text.Trim().Length == 0)) _defs[id] = null;
            else _defs[id] = new KeyOverrideDef
            {
                Id = id,
                Mods = mods.Text.Trim(),
                Key = key.Text.Trim(),
                Replacement = replacement.Text.Trim(),
            };
            RefreshItem(id);
            var errors = new List<string>();
            if (_defs[id] is { } k) ProjectService.KeyOverrideToEntry(k, errors);
            ShowValidation(errors.Count == 0 ? "OK" : string.Join("\n", errors), errors.Count > 0);
        }
        foreach (var b in new[] { mods, key, replacement }) b.TextChanged += (_, _) => Changed();

        Detail.Children.Add(Row("修飾キー", mods));
        Detail.Children.Add(Row("キー", KeycodeField.Wrap(key, allowMods: false)));
        Detail.Children.Add(Row("置き換え先", KeycodeField.Wrap(replacement)));
    }

    protected override bool ValidateAll(out string error)
    {
        var errors = new List<string>();
        foreach (var k in _defs.Where(k => k != null)) ProjectService.KeyOverrideToEntry(k!, errors);
        error = string.Join("\n", errors);
        if (errors.Count > 0) return false;
        Result = _defs.Where(k => k != null).Select(k => { k!.Name = Names[k.Id]; return k; }).ToList();
        return true;
    }
}
