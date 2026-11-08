using System.Windows;
using Kasane.Model;

namespace Kasane.Views;

// MainWindow.xaml に直書きしている日本語を、表示言語に合わせて差し替える（XAML の文言は日本語のまま残し、英語のときだけ置き換わる）。
public partial class MainWindow
{
    private void Window_LocalizeInit(object? sender, EventArgs e)
    {
        BtnRestartAsAdmin.Content = Loc.T("管理者として再起動", "Restart as administrator");
        TxtReadOnlyMessage.Text = Loc.T("閲覧専用モードです。キーボードに適用するには、管理者として再起動してください",
                                        "Read-only mode. To apply to the keyboard, restart as administrator.");
        ReadOnlyBanner.ToolTip = Loc.T("管理者として実行していないので、キーボードへの適用・キャリブレーション・キーボードの特定はできません。編集・保存・エクスポート・OFF はできます",
                                       "Not running as administrator, so you cannot apply to the keyboard, calibrate, or identify keyboards. Editing, saving, exporting, and OFF still work.");

        TxtKeyboardLabel.Text = Loc.T("キーボード:", "Keyboard:");
        CmbDevice.ToolTip = Loc.T("UniqId・付けた名前・Windows 上の名前・ハードウェア ID の順です", "UniqId, your name, Windows name, then hardware ID");
        BtnRenameKeyboard.Content = Loc.T("名前を付ける...", "Rename...");
        BtnRenameKeyboard.ToolTip = Loc.T("このキーボードの表示名を付けます", "Gives this keyboard a display name");
        BtnIdentify.Content = Loc.T("押して特定...", "Press to identify...");
        BtnIdentify.ToolTip = Loc.T("キーを1つ押して、どのキーボードかを選びます", "Press a key to choose which keyboard it is");
        BtnRefreshDevices.Content = Loc.T("一覧を更新", "Refresh list");
        BtnHelp.Content = Loc.T("ヘルプ", "Help");
        BtnHelp.ToolTip = Loc.T("マニュアルを開きます(F1)", "Opens the manual (F1)");
        BtnLegend.Content = Loc.T("凡例", "Legend");
        BtnLegend.ToolTip = Loc.T("キーボード図の色と印の意味", "What the colors and marks in the keyboard view mean");
        BtnRefreshDevices.ToolTip = Loc.T("キーボードの一覧を読み直します", "Reloads the keyboard list");
        TxtLayoutLabel.Text = Loc.T("レイアウト:", "Layout:");
        BtnAddKle.Content = Loc.T("KLEから追加...", "Add from KLE...");
        BtnAddKle.ToolTip = Loc.T("keyboard-layout-editor.com の配列(JSON)を取り込みます", "Imports a layout (JSON) from keyboard-layout-editor.com");

        TxtLayerLabel.Text = Loc.T("レイヤー:", "Layer:");
        BtnAddLayer.Content = Loc.T("＋ 追加", "+ Add");
        BtnRemoveLayer.Content = Loc.T("− 最後を削除", "\u2212 Remove last");

        BtnCalibrateAll.Content = Loc.T("キャリブレーション...", "Calibrate...");
        BtnCalibrateAll.ToolTip = Loc.T("キーの入力確認とキャリブレーション", "Check key input and calibrate");
        BtnHelper.Content = Loc.T("ヘルパー...", "Helper...");
        BtnHelper.ToolTip = Loc.T("キーでプログラム起動・文字列入力・アプリ別レイヤー", "Launch programs, type text, and per-app layers from keys");
        BtnMacros.Content = Loc.T("マクロ...", "Macros...");
        BtnCombos.Content = Loc.T("コンボ...", "Combos...");
        BtnTapDances.Content = Loc.T("タップダンス...", "Tap dances...");
        BtnKeyOverrides.Content = Loc.T("キー置換...", "Key overrides...");
        BtnSettings.Content = Loc.T("設定...", "Settings...");
        BtnSettings.ToolTip = Loc.T(
            "機能のON/OFF・タイミング・NICOLA・緊急解除・共有範囲",
            "Feature ON/OFF, timing, NICOLA, emergency release, and sharing scope");
        BtnRemapText.Content = Loc.T("割り当ての一覧...", "Assignment list...");
        BtnRemapText.ToolTip = Loc.T("割り当てを 1 行ずつ文字で表示します", "Shows the assignments as text, one per line");
        BtnImportExport.Content = Loc.T("インポート／エクスポート...", "Import / Export...");
        BtnImportExport.ToolTip = Loc.T(
            "設定をファイル(*.kasane.json)に書き出す・読み込む。選んだ項目を初期化する",
            "Export settings to a file (*.kasane.json), import them, or reset the items you choose");
        BtnApply.Content = Loc.T("キーボードに適用(ON)", "Apply to keyboard (ON)");
        BtnOff.Content = "OFF";
        BtnOff.ToolTip = Loc.T("リマップをやめて、キーをそのまま送ります", "Stops remapping and sends keys as they are");
    }
}
