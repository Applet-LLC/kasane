using System.Windows;
using System.Windows.Controls;
using Kasane.Model;

namespace Kasane.Views;

/// <summary>
/// マクロ（またはタップダンス）1件の、選ぶ画面に出す情報（番号・名前・中身）。<paramref name="Kind"/> は "MACRO" か "TAPDANCE"
/// （入れるトークン MACRO(n)・TAPDANCE(n) の名前）。
/// </summary>
public sealed record MacroInfo(int Id, string? Name, string Content, string Kind = "MACRO", bool Disabled = false)
{
    // 無効にしたものは、先頭に印を付ける（選べるが、適用されない）。
    private string Tag => Disabled ? Loc.T("[無効] ", "[Disabled] ") : "";

    public override string ToString() =>
        string.IsNullOrWhiteSpace(Name)
            ? $"{Id,3}: {Tag}{(Content.Length == 0 ? Loc.T("(未使用)", "(unused)") : Content)}"
            : $"{Id,3}: {Name} — {Tag}{(Content.Length == 0 ? Loc.T("(未使用)", "(unused)") : Content)}";

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
    private sealed record Registration(Action<string> Put, bool AcceptsTapDance);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBox, Registration> Registrations = new();

    /// <summary>
    /// 登録済みの入力欄を、入れ先にする（フォーカスを受けたとき、または、その欄のある画面が前面になったときに呼ぶ。
    /// 画面を開いただけで、欄を一度もクリックしていなくても、入れ先として扱えるようにする）。
    /// </summary>
    public static void Select(TextBox box)
    {
        if (Registrations.TryGetValue(box, out var r)) { _box = box; _put = r.Put; _acceptsTapDance = r.AcceptsTapDance; }
    }

    private static TextBox? _box;
    private static Action<string>? _put;
    private static bool _acceptsTapDance;

    /// <summary>入力欄を入れ先として登録する。<paramref name="put"/> は、欄へトークンを入れる処理（追記か置き換えかは欄ごとに違う）。</summary>
    /// <param name="acceptsTapDance">true なら TAPDANCE(n) も入れられる欄（キー割り当て画面のキーコード欄）。false は MACRO(n) だけ。</param>
    public static void Register(TextBox box, Action<string> put, bool acceptsTapDance = false)
    {
        Registrations.Remove(box);
        Registrations.Add(box, new Registration(put, acceptsTapDance));
        box.GotKeyboardFocus += (_, _) => Select(box);
        box.Unloaded += (_, _) =>
        {
            if (ReferenceEquals(_box, box)) { _box = null; _put = null; }
        };
    }

    /// <summary>
    /// <paramref name="token"/>（MACRO(n) など）を、まだ入れていない入れ先があるか。入れられる入れ先の欄があり、その欄の内容に
    /// トークンが無いとき true（編集画面を閉じるときの確認と、「入れる」ボタンの強調に使う）。
    /// </summary>
    public static bool NeedsInsert(string token)
    {
        if (_box == null || _put == null || !_box.IsLoaded) return false;
        return NeedsInsert(token, _box.Text, _acceptsTapDance);
    }

    /// <summary>入れ先の欄の内容（<paramref name="targetText"/>）に、トークンがまだ入っていないか。TAPDANCE(n) は、入れられない欄では false。</summary>
    public static bool NeedsInsert(string token, string targetText, bool acceptsTapDance)
    {
        if (token.StartsWith("TAPDANCE(", StringComparison.Ordinal) && !acceptsTapDance) return false;
        return !targetText.Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>最後にフォーカスを受けた入力欄へ入れる。入れ先が無ければ false（<paramref name="reason"/> に理由）。</summary>
    public static bool TryInsert(string token, out string reason)
    {
        if (_box == null || _put == null || !_box.IsLoaded)
        {
            reason = Loc.T("入れ先の入力欄がありません。他の画面(コンボ・タップダンス・キー置換・キーの割り当て)で、入れたい欄を一度クリックしてから、もう一度試してください。",
                "There is no input field to insert into. In another screen (Combos, Tap dance, Key override, Key assignment), click the field you want to insert into once, then try again.");
            return false;
        }
        if (token.StartsWith("TAPDANCE(", StringComparison.Ordinal) && !_acceptsTapDance)
        {
            reason = Loc.T("TAPDANCE(n) を入れられるのは、キーの割り当て画面のキーコード欄だけです。そこをクリックしてから、もう一度試してください。",
                "TAPDANCE(n) can only be inserted into the keycode field of the Key assignment screen. Click it, then try again.");
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
