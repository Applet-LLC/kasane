using System.Windows;
using Kasane.Model;

namespace Kasane.Views;

// MainWindow.xaml に直書きしている日本語を、表示言語に合わせて差し替える（XAML の文言は日本語のまま残し、英語のときだけ置き換わる）。
public partial class MainWindow
{
    private void Window_LocalizeInit(object? sender, EventArgs e)
    {
        BtnRestartAsAdmin.Content = Loc.T("管理者として再起動", "Restart as administrator");
        TxtReadOnlyMessage.Text = Loc.T(
            "閲覧専用モード: 管理者として実行していないため、キーボードへの適用・キャリブレーション・キーボードの特定はできません(編集・保存・エクスポート・OFFはできます)。",
            "Read-only mode: because kasane is not running as administrator, you cannot apply to the keyboard, calibrate, or identify keyboards (editing, saving, exporting, and OFF still work).");

        TxtKeyboardLabel.Text = Loc.T("キーボード:", "Keyboard:");
        CmbDevice.ToolTip = Loc.T(
            "UniqId、つけた名前、Windows 上の名前(FriendlyName・ShellFriendlyName)、ハードウェアID の順に表示します",
            "Shows the UniqId, the name you gave it, the Windows name (FriendlyName / ShellFriendlyName), and the hardware ID, in that order");
        BtnRenameKeyboard.Content = Loc.T("名前を付ける...", "Rename...");
        BtnRenameKeyboard.ToolTip = Loc.T(
            "このキーボードに、わかりやすい名前を付けます(設定画面の表示だけに使います)",
            "Give this keyboard a friendly name (used only for display in the settings screen)");
        BtnIdentify.Content = Loc.T("押して特定...", "Press to identify...");
        BtnIdentify.ToolTip = Loc.T("キーを1つ押して、どのキーボードかを選びます", "Press a key to choose which keyboard it is");
        BtnRefreshDevices.Content = Loc.T("一覧を更新", "Refresh list");
        TxtLayoutLabel.Text = Loc.T("レイアウト:", "Layout:");
        BtnAddKle.Content = Loc.T("KLEから追加...", "Add from KLE...");
        BtnAddKle.ToolTip = Loc.T(
            "keyboard-layout-editor.com で作った配列(JSON)を取り込み、レイアウトの一覧に追加します",
            "Imports a layout (JSON) made with keyboard-layout-editor.com and adds it to the layout list");

        TxtLayerLabel.Text = Loc.T("レイヤー:", "Layer:");
        BtnAddLayer.Content = Loc.T("＋ 追加", "+ Add");
        BtnRemoveLayer.Content = Loc.T("− 最後を削除", "\u2212 Remove last");

        BtnCalibrateAll.Content = Loc.T("キャリブレーション...", "Calibrate...");
        BtnCalibrateAll.ToolTip = Loc.T(
            "キーの入力確認、個別キー・全キーのキャリブレーション(最初は入力確認モード)",
            "Check key input, or calibrate individual keys or all keys (starts in input check mode)");
        BtnHelper.Content = Loc.T("ヘルパー...", "Helper...");
        BtnHelper.ToolTip = Loc.T(
            "設定の「ヘルパー動作」タブを開く(キーでプログラム起動・文字列入力・アプリ別レイヤーなど)",
            "Opens the \"Helper actions\" tab in Settings (launch programs, type text, per-app layers, and more from a key)");
        BtnMacros.Content = Loc.T("マクロ...", "Macros...");
        BtnCombos.Content = Loc.T("コンボ...", "Combos...");
        BtnTapDances.Content = Loc.T("タップダンス...", "Tap dances...");
        BtnKeyOverrides.Content = Loc.T("キー置換...", "Key overrides...");
        BtnSettings.Content = Loc.T("設定...", "Settings...");
        BtnSettings.ToolTip = Loc.T(
            "機能のON/OFF・タイミング・NICOLA・緊急解除・共有範囲",
            "Feature ON/OFF, timing, NICOLA, emergency release, and sharing scope");
        BtnImportExport.Content = Loc.T("インポート／エクスポート...", "Import / Export...");
        BtnImportExport.ToolTip = Loc.T(
            "設定をファイル(*.kasane.json)に書き出す・読み込む(全体一括・このキーボード・レイヤー1枚)。「クリア」タブで、選んだ項目を初期状態に戻せます",
            "Export settings to a file (*.kasane.json) or import them (everything at once, this keyboard, or a single layer). The \"Clear\" tab resets the items you choose to their initial state");
        BtnApply.Content = Loc.T("キーボードに適用(ON)", "Apply to keyboard (ON)");
        BtnOff.Content = Loc.T("OFF(素通しに戻す)", "OFF (pass keys through)");

        TxtLegend.Text = Loc.T(
            "クリック: 割り当てを変更 ／ 右クリック: 実測値を消す・割り当てを戻す ／ 青: 既定から変更 ／ 緑: レイヤー・Mod-Tap等 ／ 右下の印: M=マクロ・D=タップダンス・C=コンボ・R=キー置換(ボタンの( )は定義の数)／ 橙枠: 実測値が既定と異なる ／ 赤枠: スキャンコード不明(要実測) ／ ▽: 下のレイヤーと同じ(透過)",
            "Click: change assignment / Right-click: clear measured value or restore assignment / Blue: changed from default / Green: layer, Mod-Tap, etc. / Marks at bottom right: M=macro, D=tap dance, C=combo, R=key override (the ( ) on buttons is the number of definitions) / Orange border: measured value differs from default / Red border: scan code unknown (needs measuring) / \u25bd: same as the layer below (transparent)");
    }
}
