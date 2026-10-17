using System.Windows;
using System.Windows.Controls;

namespace Kasane.Views;

/// <summary>
/// マクロ（またはタップダンス）1件の、選ぶ画面に出す情報（番号・名前・中身）。<paramref name="Kind"/> は "MACRO" か "TAPDANCE"
/// （入れるトークン MACRO(n)・TAPDANCE(n) の名前）。
/// </summary>
public sealed record MacroInfo(int Id, string? Name, string Content, string Kind = "MACRO")
{
    public override string ToString() =>
        string.IsNullOrWhiteSpace(Name)
            ? $"{Id,3}: {(Content.Length == 0 ? "（未使用）" : Content)}"
            : $"{Id,3}: {Name} — {(Content.Length == 0 ? "（未使用）" : Content)}";

    public string Token => $"{Kind}({Id})";
}

/// <summary>
/// 画面が「いまのマクロの一覧」を得るための窓口。メイン画面が、プロジェクトのマクロ（マクロの編集画面が開いていれば、
/// その画面の編集中の内容）を返す関数を設定する。名前か中身があるものだけを返す。
/// </summary>
public static class MacroDirectory
{
    public static Func<IReadOnlyList<MacroInfo>>? Provider { get; set; }

    public static IReadOnlyList<MacroInfo> Items => Provider?.Invoke() ?? Array.Empty<MacroInfo>();

    /// <summary>タップダンスの一覧を返す関数（<see cref="Provider"/> と同じ。Kind は "TAPDANCE"）。</summary>
    public static Func<IReadOnlyList<MacroInfo>>? TapDanceProvider { get; set; }

    public static IReadOnlyList<MacroInfo> TapDances => TapDanceProvider?.Invoke() ?? Array.Empty<MacroInfo>();
}

/// <summary>
/// マクロ・タップダンスの画面で選んだものを、他の画面の入力欄へ入れるための受け口。マクロを入れられる入力欄（<see cref="KeycodeField.Wrap"/> の
/// acceptsMacro、キー割り当て画面のキーコード欄）が、フォーカスを受けたときに自分を登録する。最後にフォーカスを受けた欄が入れ先になる。
/// </summary>
public static class MacroInsert
{
    private static TextBox? _box;
    private static Action<string>? _put;
    private static bool _acceptsTapDance;

    /// <summary>入力欄を入れ先として登録する。<paramref name="put"/> は、欄へトークンを入れる処理（追記か置き換えかは欄ごとに違う）。</summary>
    /// <param name="acceptsTapDance">true なら TAPDANCE(n) も入れられる欄（キー割り当て画面のキーコード欄）。false は MACRO(n) だけ。</param>
    public static void Register(TextBox box, Action<string> put, bool acceptsTapDance = false)
    {
        box.GotKeyboardFocus += (_, _) => { _box = box; _put = put; _acceptsTapDance = acceptsTapDance; };
        box.Unloaded += (_, _) =>
        {
            if (ReferenceEquals(_box, box)) { _box = null; _put = null; }
        };
    }

    /// <summary>最後にフォーカスを受けた入力欄へ入れる。入れ先が無ければ false（<paramref name="reason"/> に理由）。</summary>
    public static bool TryInsert(string token, out string reason)
    {
        if (_box == null || _put == null || !_box.IsLoaded)
        {
            reason = "入れ先の入力欄がありません。他の画面（コンボ・タップダンス・キー置換・キーの割り当て）で、入れたい欄を一度クリックしてから、もう一度試してください。";
            return false;
        }
        if (token.StartsWith("TAPDANCE(", StringComparison.Ordinal) && !_acceptsTapDance)
        {
            reason = "TAPDANCE(n) を入れられるのは、キーの割り当て画面のキーコード欄だけです。そこをクリックしてから、もう一度試してください。";
            return false;
        }
        _put(token);
        var window = Window.GetWindow(_box);
        window?.Activate();
        _box.Focus();
        reason = "";
        return true;
    }
}
