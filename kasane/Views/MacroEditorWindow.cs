using System.Windows.Controls;
using Kasane.Interop;
using Kasane.Model;

namespace Kasane.Views;

/// <summary>マクロ（MACRO(n)キーで順にタップするキー列）の編集。</summary>
public sealed class MacroEditorWindow : IdListEditorWindow
{
    private readonly string[] _texts = new string[KbdLayerClient.MaxMacroCount];

    public List<MacroDef> Result { get; private set; } = new();

    public MacroEditorWindow(IEnumerable<MacroDef> macros, IReadOnlyDictionary<int, string>? names = null)
        : base("マクロ", KbdLayerClient.MaxMacroCount,
            "マクロは、キーマップで MACRO(n) を割り当てたキーを押したときに、並べたキーを順にタップ(押して離す)します。\n" +
            "キーコードをスペース区切りで並べてください(例: KC_H KC_I)。Ctrl+C のような同時押しは LCTL(KC_C) と書きます" +
            "(C(KC_C) でも可。Ctrl+Shift+T は LCTL(LSFT(KC_T)))。\n" +
            $"1つのマクロは{KbdLayerClient.MaxMacroSteps}ステップまでです(キー1つで2、修飾キー1つにつき2を使います)。")
    {
        for (int i = 0; i < _texts.Length; i++) _texts[i] = "";
        foreach (var m in macros)
            if (m.Id >= 0 && m.Id < _texts.Length) _texts[m.Id] = string.Join(" ", m.Keys);
        LoadNames(names);
    }

    protected override string Summary(int id) => _texts[id];

    /// <summary>編集中の内容から、名前か中身があるマクロの一覧を作る（他の画面の「マクロ...」の選択肢になる）。</summary>
    public IReadOnlyList<MacroInfo> Snapshot()
    {
        var list = new List<MacroInfo>();
        for (int i = 0; i < _texts.Length; i++)
            if (_texts[i].Length > 0 || !string.IsNullOrWhiteSpace(Names[i]))
                list.Add(new MacroInfo(i, Names[i], _texts[i]));
        return list;
    }

    // 一覧の行のダブルクリック: 他の画面（コンボ・タップダンス・キー置換・キーの割り当て）で最後に選んだ入力欄へ MACRO(n) を入れる。
    protected override void OnItemActivated(int id) => InsertIntoOtherWindow(id);

    private void InsertIntoOtherWindow(int id)
    {
        if (_texts[id].Length == 0)
        {
            ShowValidation("中身が空のマクロは入れられません。", isError: true);
            return;
        }
        bool ok = MacroInsert.TryInsert($"MACRO({id})", out var reason);
        ShowValidation(ok ? $"MACRO({id}) を入れました。" : reason, isError: !ok);
    }

    protected override void ShowDetail(int id)
    {
        var box = KeycodeBox(_texts[id], "MacroBox");
        box.Width = 380;
        box.TextChanged += (_, _) =>
        {
            _texts[id] = box.Text.Trim();
            RefreshItem(id);
            var errors = new List<string>();
            ProjectService.MacroSteps(ToDef(id), errors);
            ShowValidation(errors.Count == 0 ? "OK" : string.Join("\n", errors), errors.Count > 0);
        };
        Detail.Children.Add(Row($"マクロ {id}", KeycodeField.Wrap(box, append: true)));

        var insert = new System.Windows.Controls.Button
        {
            Name = "InsertMacroButton",
            Content = $"他の画面の入力欄に MACRO({id}) を入れる",
            Margin = new System.Windows.Thickness(110, 8, 0, 0),
            Padding = new System.Windows.Thickness(10, 3, 10, 3),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            ToolTip = "コンボ・タップダンス・キー置換・キーの割り当ての画面で、最後に選んだ入力欄へ入れます(一覧の行をダブルクリックでも同じ)。",
        };
        insert.Click += (_, _) => InsertIntoOtherWindow(id);
        Detail.Children.Add(insert);
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
