using Kasane.Keycodes;

namespace Kasane.Model;

/// <summary>割り当てダイアログで、開いたときに選んでおくパネル。</summary>
public enum KeyAssignPanel { Keys, Layer, ModTap, LayerTap, Macro, Helper }

/// <summary>
/// 割り当てダイアログを開いたときの、各パネルの初期値（2026-11-05、GUI 計画 1-1）。
/// 今の割り当て（<c>MT(..)</c>・<c>LT(..)</c>・<c>MO(n)</c> など）を、該当するパネルの入力に戻して開く。
/// 以前は、開き直すとパネルが既定値（<c>KC_A</c>・<c>KC_SPACE</c>・レイヤー 1）に戻っていた。
/// 読むだけで、割り当ては変えない。
/// </summary>
public sealed record KeyAssignPrefill
{
    /// <summary>最初に選ぶパネル。普通のキーは <see cref="KeyAssignPanel.Keys"/>（分類の先頭のまま）。</summary>
    public KeyAssignPanel Panel { get; init; } = KeyAssignPanel.Keys;

    /// <summary>Mod-Tap / Layer-Tap のタップのキー。今の割り当てのタップ側か、今の基本キー、キーの刻印の順。どれも無ければ null。</summary>
    public string? TapKey { get; init; }

    /// <summary>Mod-Tap の修飾キー（MOD_LCTL など）。</summary>
    public string ModName { get; init; } = KeycodeTable.ModifierNames[0];

    /// <summary>レイヤー操作・Layer-Tap のレイヤー。</summary>
    public int Layer { get; init; } = 1;

    /// <summary>レイヤー操作の種類（MO / TG / DF / OSL / TO / TT / LM / OSM）。</summary>
    public string LayerFunc { get; init; } = "MO";

    /// <summary>マクロ / タップダンスの種類（MACRO / TAPDANCE）と番号。</summary>
    public string MacroKind { get; init; } = "MACRO";
    public int MacroId { get; init; }

    /// <summary>ヘルパー動作の Id（HLP(n) のとき）。</summary>
    public int? HelperId { get; init; }

    /// <summary>Mod-Tap のタップのキー。決まらなければ、従来の既定（KC_A）。</summary>
    public string ModTapKey => TapKey ?? "KC_A";

    /// <summary>Layer-Tap のタップのキー。決まらなければ、従来の既定（KC_SPACE）。</summary>
    public string LayerTapKey => TapKey ?? "KC_SPACE";

    /// <param name="currentToken">今の割り当て。</param>
    /// <param name="slotDefault">キーの刻印のキーコード（KLE の既定キーコード。無ければ null）。</param>
    /// <param name="maxLayer">レイヤーの数（既定の Layer の上限に使う）。</param>
    public static KeyAssignPrefill From(string currentToken, string? slotDefault, int maxLayer)
    {
        int defaultLayer = Math.Max(0, Math.Min(1, maxLayer - 1));
        string? slotKey = BaseKeyOrNull(slotDefault);
        string token = currentToken.Trim();
        if (!KeycodeTable.TryResolve(token, out var a))
            return new KeyAssignPrefill { TapKey = slotKey, Layer = defaultLayer };

        string? tap = a.Type == ActionType.Key && a.Param2 != 0 ? BaseKeyOrNull(token) : slotKey;
        var p = new KeyAssignPrefill { TapKey = tap, Layer = defaultLayer };
        return a.Type switch
        {
            ActionType.ModTap => p with
            {
                Panel = KeyAssignPanel.ModTap,
                TapKey = KeycodeTable.KeyToken(a.Param2),
                ModName = a.Param1 < KeycodeTable.ModifierNames.Length ? KeycodeTable.ModifierNames[a.Param1] : p.ModName,
            },
            ActionType.LayerTap => p with { Panel = KeyAssignPanel.LayerTap, TapKey = KeycodeTable.KeyToken(a.Param2), Layer = a.Param1 },
            ActionType.Momentary => p with { Panel = KeyAssignPanel.Layer, LayerFunc = "MO", Layer = a.Param1 },
            ActionType.Toggle => p with { Panel = KeyAssignPanel.Layer, LayerFunc = "TG", Layer = a.Param1 },
            ActionType.Default => p with { Panel = KeyAssignPanel.Layer, LayerFunc = "DF", Layer = a.Param1 },
            ActionType.OneShotLayer => p with { Panel = KeyAssignPanel.Layer, LayerFunc = "OSL", Layer = a.Param1 },
            ActionType.LayerTo => p with { Panel = KeyAssignPanel.Layer, LayerFunc = "TO", Layer = a.Param1 },
            ActionType.LayerTapToggle => p with { Panel = KeyAssignPanel.Layer, LayerFunc = "TT", Layer = a.Param1 },
            ActionType.LayerMod => p with { Panel = KeyAssignPanel.Layer, LayerFunc = "LM", Layer = a.Param1 },
            ActionType.OneShotMod => p with { Panel = KeyAssignPanel.Layer, LayerFunc = "OSM" },
            ActionType.Macro => p with { Panel = KeyAssignPanel.Macro, MacroKind = "MACRO", MacroId = a.Param1 },
            ActionType.TapDance => p with { Panel = KeyAssignPanel.Macro, MacroKind = "TAPDANCE", MacroId = a.Param1 },
            ActionType.Custom => p with { Panel = KeyAssignPanel.Helper, HelperId = a.Param2 },
            _ => p,
        };
    }

    // 基本キー（KC_* / SC_XXXX。KC_NO を除く）なら、書いたままの名前。
    private static string? BaseKeyOrNull(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        string t = token.Trim();
        return KeycodeTable.TryResolveBaseKey(t, out var sc) && sc != 0 ? t : null;
    }
}
