using System.Windows.Controls;
using KbdLayerKeymapGui.Interop;
using KbdLayerKeymapGui.Model;

namespace KbdLayerKeymapGui.Views;

/// <summary>マクロ（MACRO(n)キーで順にタップするキー列）の編集。</summary>
public sealed class MacroEditorWindow : IdListEditorWindow
{
    private readonly string[] _texts = new string[KbdLayerClient.MaxMacroCount];

    public List<MacroDef> Result { get; private set; } = new();

    public MacroEditorWindow(IEnumerable<MacroDef> macros)
        : base("マクロ", KbdLayerClient.MaxMacroCount,
            "マクロは、キーマップで MACRO(n) を割り当てたキーを押したときに、並べたキーを順にタップ（押して離す）します。\n" +
            "キーコードをスペース区切りで並べてください（例: KC_H KC_I）。修飾キーとの同時押し（Ctrl+C等）は表現できません。" +
            $"1つのマクロは{KbdLayerClient.MaxMacroSteps / 2}キーまでです。")
    {
        for (int i = 0; i < _texts.Length; i++) _texts[i] = "";
        foreach (var m in macros)
            if (m.Id >= 0 && m.Id < _texts.Length) _texts[m.Id] = string.Join(" ", m.Keys);
    }

    protected override string Summary(int id) => _texts[id];

    protected override void ShowDetail(int id)
    {
        var box = KeycodeBox(_texts[id]);
        box.Width = 480;
        box.TextChanged += (_, _) =>
        {
            _texts[id] = box.Text.Trim();
            RefreshItem(id);
            var errors = new List<string>();
            ProjectService.MacroSteps(ToDef(id), errors);
            ShowValidation(errors.Count == 0 ? "OK" : string.Join("\n", errors), errors.Count > 0);
        };
        Detail.Children.Add(Row($"マクロ {id}", box));
    }

    private MacroDef ToDef(int id) => new()
    {
        Id = id,
        Keys = _texts[id].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList(),
    };

    protected override bool ValidateAll(out string error)
    {
        var errors = new List<string>();
        var result = new List<MacroDef>();
        for (int i = 0; i < _texts.Length; i++)
        {
            if (_texts[i].Length == 0) continue;
            var def = ToDef(i);
            ProjectService.MacroSteps(def, errors);
            result.Add(def);
        }
        error = string.Join("\n", errors);
        if (errors.Count > 0) return false;
        Result = result;
        return true;
    }
}
