using Kasane.Interop;
using Kasane.Keycodes;
using Kasane.Layout;

namespace Kasane.Model;

/// <summary>
/// メイン画面のキー図に出す、キー置換・コンボ・マクロ・タップダンスの印（2026-10-21）。
/// 割り当てが普通のキーに見えても、別の機能が関わるキーが分かるようにする。印は右下に1文字ずつ:
/// M = マクロ（キーの割り当てが MACRO(n)）、D = タップダンス（TAPDANCE(n)）、C = コンボ（同時押しの対象のキー）、
/// R = キー置換（Replace。修飾キーと押したときに、別のキーに置き換わるキー）。
/// コンボは物理のキー（スロット）で決まるので、すべてのレイヤーに出す。キー置換は「レイヤーを通したあとの出力キー」が条件なので、
/// そのキーを出すレイヤーに出す。透過（KC_TRNS）のキーは、下のレイヤーの割り当てを引き継ぐものとして数える。
/// </summary>
public static class KeyMarks
{
    /// <param name="Marks">右下に出す文字（「MCR」のように並べる。無ければ空）。</param>
    /// <param name="ToolTip">ツールチップに足す説明（印ごとに1行。無ければ空）。</param>
    public sealed record Info(string Marks, string ToolTip)
    {
        public static readonly Info None = new("", "");
    }

    /// <summary>レイヤー <paramref name="layer"/> の、そのキーの印。</summary>
    public static Info For(KeymapProject project, KeyboardLayout layout, int layer, KeySlot slot)
    {
        string token = EffectiveToken(project, layer, slot);
        var marks = new System.Text.StringBuilder();
        var tip = new List<string>();

        if (KeycodeTable.TryResolve(token, out var action))
        {
            if (action.Type == ActionType.Macro)
            {
                marks.Append('M');
                tip.Add(Describe(Loc.T("マクロ", "Macro"), action.Param1, project.MacroNames));
            }
            else if (action.Type == ActionType.TapDance)
            {
                marks.Append('D');
                tip.Add(Describe(Loc.T("タップダンス", "Tap dance"), action.Param1, project.TapDanceNames));
            }
        }

        foreach (var c in project.Combos)
        {
            if (c.Slot1 != slot.Index && c.Slot2 != slot.Index) continue;
            int otherIndex = c.Slot1 == slot.Index ? c.Slot2 : c.Slot1;
            string other = layout.Slots.FirstOrDefault(s => s.Index == otherIndex)?.Description ?? Loc.T($"キー{otherIndex}", $"Key {otherIndex}");
            string combo = Describe(Loc.T("コンボ", "Combo"), c.Id, project.ComboNames);
            tip.Add(Loc.T($"{combo}: {other} と同時に押す → {c.Output}", $"{combo}: press together with {other} → {c.Output}"));
        }
        if (project.Combos.Any(c => c.Slot1 == slot.Index || c.Slot2 == slot.Index)) marks.Append('C');

        foreach (var k in project.KeyOverrides)
        {
            if (!string.Equals(k.Key?.Trim(), token, StringComparison.OrdinalIgnoreCase)) continue;
            string ko = Describe(Loc.T("キー置換", "Key override"), k.Id, project.KeyOverrideNames);
            tip.Add(Loc.T($"{ko}: {k.Mods.Replace(" ", "")} を押しながら → {k.Replacement}", $"{ko}: while holding {k.Mods.Replace(" ", "")} → {k.Replacement}"));
        }
        if (project.KeyOverrides.Any(k => string.Equals(k.Key?.Trim(), token, StringComparison.OrdinalIgnoreCase))) marks.Append('R');

        // 並びは「M D C R」の順に固定する（キーごとに同じ順で読めるように）。
        string ordered = new string("MDCR".Where(ch => marks.ToString().Contains(ch)).ToArray());
        return ordered.Length == 0 ? Info.None : new Info(ordered, string.Join("\n", tip));
    }

    /// <summary>レイヤーを通した、そのキーの割り当て。透過なら下のレイヤーへ、レイヤー0でも透過なら既定のキーコード。</summary>
    public static string EffectiveToken(KeymapProject project, int layer, KeySlot slot)
    {
        for (int l = Math.Min(layer, project.Layers.Count - 1); l >= 0; l--)
        {
            var row = project.Layers[l];
            if (slot.Index >= row.Count) continue;
            string t = row[slot.Index];
            if (!string.IsNullOrEmpty(t) && t != "KC_TRNS") return t;
        }
        return slot.DefaultKeycode ?? "";
    }

    private static string Describe(string kind, int id, Dictionary<int, string>? names) =>
        names != null && names.TryGetValue(id, out var n) && !string.IsNullOrWhiteSpace(n)
            ? Loc.T($"{kind}{id}「{n}」", $"{kind} {id} \"{n}\"")
            : Loc.T($"{kind}{id}", $"{kind} {id}");

    /// <summary>ボタンの表示。定義が無ければ「マクロ...」、あれば「マクロ(2)...」、機能がOFFなら「マクロ(2・OFF)...」。</summary>
    public static string ButtonText(string name, int count, bool featureOn) =>
        count == 0 ? $"{name}..."
        : featureOn ? Loc.T($"{name}({count})...", $"{name} ({count})...")
        : Loc.T($"{name}({count}・OFF)...", $"{name} ({count}, OFF)...");

    /// <summary>機能のON/OFF（<see cref="KeymapProject.EnabledFeatures"/> が null なら、GUIでは管理しない＝すべて有効とみなす）。</summary>
    public static bool FeatureOn(KeymapProject project, uint feature) =>
        project.EnabledFeatures is not uint f || (f & feature) != 0;
}
