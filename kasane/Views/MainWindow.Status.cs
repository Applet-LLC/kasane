using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Kasane.Model;

namespace Kasane.Views;

// 状態の表示（2026-11-09、UI の見直し）。画面下のステータス欄の左に、状態の印を並べ、メッセージには種類（情報・注意・エラー）の印と色を付ける。
// 前は、ドライバの状態・未適用・閲覧専用がタイトルバーの文字だけで、メッセージも種類の区別がなかった。
// キー図の上に行を足すと画面が縦に溢れてキーボードが欠けるので、印は、もともとあるステータス欄に載せる。
public partial class MainWindow
{
    private StatusKind _statusKind = StatusKind.Info;
    private readonly DispatcherTimer _statusFade = new() { Interval = TimeSpan.FromSeconds(8) };
    private bool _statusFadeHooked;

    /// <summary>ステータス欄にメッセージを出す。情報は数秒で薄くなる。注意とエラーは、次のメッセージまで、そのまま。</summary>
    private void SetStatus(string text, StatusKind kind = StatusKind.Info)
    {
        _statusKind = kind;
        TxtStatus.Text = text;
        TxtStatus.Opacity = 1;
        TxtStatusIcon.Text = text.Length == 0 ? "" : StatusStyle.Icon(kind);
        TxtStatusIcon.Foreground = StatusStyle.Brush(kind);
        if (!_statusFadeHooked)
        {
            _statusFadeHooked = true;
            _statusFade.Tick += (_, _) => { _statusFade.Stop(); if (_statusKind == StatusKind.Info) TxtStatus.Opacity = 0.55; };
        }
        _statusFade.Stop();
        if (kind == StatusKind.Info && text.Length > 0) _statusFade.Start();
    }

    /// <summary>タイトルバーは、名前・バージョン・未保存の印（*）だけ。状態は、ステータス欄の印に出す。</summary>
    private void UpdateTitle()
    {
        Title = AppInfo.NameWithVersion + (_dirty ? " *" : "");
        UpdateIndicators();
    }

    /// <summary>状態の印を作り直す: ドライバ（ON / OFF / 設定なし / 未接続）、未適用、閲覧専用、編集中。</summary>
    private void UpdateIndicators()
    {
        var p = AppTheme.Palette;
        var chips = StatusChips.Children;
        chips.Clear();

        // ドライバ
        string driverDetail = DriverStateText();
        if (_client == null)
            chips.Add(StatusStyle.Chip("ChipDriver", Loc.T("未接続", "Not connected"), p.ErrorText, driverDetail));
        else if (SelectedDevice is not { UniqId: not 0 } d)
            chips.Add(StatusStyle.Chip("ChipDriver", Loc.T("このキーボードは未接続", "Keyboard not connected"), p.SubText, driverDetail));
        else if (!d.HasLayerMap)
            chips.Add(StatusStyle.Chip("ChipDriver", Loc.T("設定なし", "No settings"), p.SubText, driverDetail));
        else if (d.Enabled)
            chips.Add(StatusStyle.Chip("ChipDriver", "ON", p.OkText, driverDetail));
        else
            chips.Add(StatusStyle.Chip("ChipDriver", "OFF", p.SubText, driverDetail));

        // 未適用（画面の変更を、まだキーボードに適用していない）
        if (_client != null && SelectedDevice is { UniqId: not 0 } dev && _unapplied.Contains(dev.UniqId))
            chips.Add(StatusStyle.Chip("ChipUnapplied", Loc.T("未適用", "Not applied"), p.WarnText,
                Loc.T("画面の変更を、まだキーボードに適用していません", "The changes on screen have not been applied to the keyboard yet")));

        // 閲覧専用
        if (IsReadOnly)
            chips.Add(StatusStyle.Chip("ChipReadOnly", Loc.T("閲覧専用", "Read-only"), p.WarnText,
                Loc.T("管理者として実行していないので、キーボードへの適用・キャリブレーション・キーボードの特定はできません", "Not running as administrator, so you cannot apply, calibrate, or identify keyboards")));

        // 編集中（編集画面を開いている間は、キーボードやレイアウトの切り替えなどができない）
        if (_editors.Count > 0 || _keyAssign != null)
            chips.Add(StatusStyle.Chip("ChipEditing", Loc.T("編集中", "Editing"), p.InfoText,
                Loc.T("編集画面(マクロ・コンボ・タップダンス・キー置換・キーの割り当て)を開いている間は、キーボードやレイアウトの切り替え、インポート、レイヤーの追加・削除はできません",
                      "While an editor (macros, combos, tap dances, key overrides, key assignment) is open, you cannot switch keyboards or layouts, import, or add or delete layers")));

        // 色のモードを変えたとき、今のメッセージの印も塗り直す
        TxtStatusIcon.Foreground = StatusStyle.Brush(_statusKind);
    }
}
