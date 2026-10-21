using System.Windows;
using System.Windows.Controls;
using Kasane.Model;

namespace Kasane.Views;

/// <summary>
/// 「IME連動」タブの NICOLA の部分にある、NICOLA の IME 連動（IME がかなモードのときだけ NICOLA にする）の設定（2026-10-19）。
/// 対象は「このキーボード」か「全キーボード共通」。キーボードごとの設定（helper.json の <see cref="HelperConfig.NicolaImeLink"/>）が先で、
/// 設定が無ければ共通（tray.json の <see cref="TrayConfig.ImeLink"/>）に従う。共通の値は、設定画面が OK のときに保存する（呼び出し側が持つ）。
/// </summary>
internal sealed class NicolaLinkPanel : HelperTabBase
{
    private readonly Func<bool> _getGlobal;
    private readonly Action<bool> _setGlobal;
    private readonly Action _changed;
    private readonly Button _inherit = Btn("NicolaLinkInheritButton", Loc.T("共通の設定に従う", "Use shared setting"), 150);
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.Palette.SubText, Margin = new Thickness(0, 4, 0, 6) };
    private bool _loading;

    /// <summary>連動のチェック（今の範囲での値）。</summary>
    public CheckBox LinkCheck { get; }

    /// <param name="getGlobal">全キーボード共通の値（tray.json の ImeLink）を読む。</param>
    /// <param name="setGlobal">全キーボード共通の値を変える（保存は、設定画面の OK のとき）。</param>
    /// <param name="changed">値が変わったとき（NICOLA の説明の注意を見直すため）。</param>
    public NicolaLinkPanel(HelperSettingsModel model, Window owner, Func<bool> getGlobal, Action<bool> setGlobal, Action changed)
        : base(model, owner, "NicolaLink",
            Loc.T("NICOLA の IME 連動(IME がかなモードのときだけ、NICOLA のレイヤーを自動で立てる)を使うかどうかを決めます。" +
                "「このキーボード」の設定が「全キーボード共通」より先に使われ、変更は OK で保存されます。",
                "Choose whether to use IME link for NICOLA (the NICOLA layer is turned on automatically only while the IME is in kana mode). " +
                "The \"This keyboard\" setting takes priority over \"All keyboards\", and changes are saved with OK."))
    {
        _getGlobal = getGlobal;
        _setGlobal = setGlobal;
        _changed = changed;

        LinkCheck = new CheckBox
        {
            Name = "NicolaImeLinkCheck",
            Content = Loc.T("IME連動: IMEがかなモードのときだけNICOLAにする(タスクトレイの kasane が必要)", "IME link: use NICOLA only while the IME is in kana mode (requires kasane in the tray)"),
            Margin = new Thickness(0, 6, 0, 0),
        };
        LinkCheck.Click += (_, _) => Apply();
        _inherit.Click += (_, _) =>
        {
            Config.NicolaImeLink = null;
            Refresh();
            _changed();
        };
        Stack.Children.Add(LinkCheck);
        Stack.Children.Add(Buttons(_inherit));
        Stack.Children.Add(_hint);
        Init();
    }

    protected override void Refresh()
    {
        _loading = true;
        bool global = _getGlobal();
        if (Scope == HelperScope.Global)
        {
            LinkCheck.IsChecked = global;
            _inherit.IsEnabled = false;
            _hint.Text = Loc.T("全キーボード共通の設定です(キーボードごとの設定が無いキーボードは、これに従います)。", "This is the setting shared by all keyboards (keyboards without their own setting follow it).");
        }
        else
        {
            bool? own = Config.NicolaImeLink;
            LinkCheck.IsChecked = own ?? global;
            _inherit.IsEnabled = Editable && own != null;
            _hint.Text = own == null
                ? Loc.T($"このキーボードには設定がありません。全キーボード共通の設定(今は「{(global ? "連動する" : "連動しない")}」)に従います。", $"This keyboard has no setting of its own. It follows the shared setting (currently {(global ? "linked" : "not linked")}).")
                : Loc.T("このキーボードだけの設定です。", "This setting applies to this keyboard only.");
        }
        LinkCheck.IsEnabled = Editable;
        _loading = false;
    }

    private void Apply()
    {
        if (_loading || !Editable) return;
        bool on = LinkCheck.IsChecked == true;
        if (Scope == HelperScope.Global) _setGlobal(on);
        else Config.NicolaImeLink = on;
        Refresh();
        _changed();
    }
}
