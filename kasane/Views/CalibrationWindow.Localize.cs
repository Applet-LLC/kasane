using System.Windows;
using Kasane.Model;

namespace Kasane.Views;

public partial class CalibrationWindow
{
    // XAML の固定の文言を、表示言語に合わせる（InitializeComponent の最後に起きる Initialized で設定する。
    // コンストラクターが後から設定する文言（ツールチップなど）は、それがそのまま優先される）。
    private void Window_Initialized(object? sender, EventArgs e)
    {
        TxtModeLabel.Text = Loc.T("モード:", "Mode:");
        ModeInputCheck.Content = Loc.T("入力確認", "Input check");
        ModeInputCheck.ToolTip = Loc.T("キーを押して、キャリブレーションが要るかを確かめます", "Press keys to check whether calibration is needed");
        ModeSingleKey.Content = Loc.T("個別キー", "Single key");
        ModeSingleKey.ToolTip = Loc.T("クリックしたキーだけを実測します(管理者)", "Measures only the key you click (administrator)");
        ModeAllKeys.Content = Loc.T("全体", "All keys");
        ModeAllKeys.ToolTip = Loc.T("黄色のキーを順に押して、全キーを実測します(管理者)", "Press the yellow keys in turn to measure every key (administrator)");
        TxtNote.Text = Loc.T("この画面が前面の間、キーは Windows に届きません", "While this window is in front, keys do not reach Windows");
        BtnResetChecks.Content = Loc.T("確認済みをリセット", "Reset checked keys");
        BtnBack.Content = Loc.T("1つ戻る", "Back");
        BtnSkip.Content = Loc.T("このキーを飛ばす", "Skip this key");
        BtnFinish.Content = Loc.T("実測値を保存して閉じる", "Save measurements and close");
        BtnCancel.Content = Loc.T("閉じる", "Close");
        BtnCancel.ToolTip = Loc.T("実測値は保存しません", "Measurements are not saved");
    }
}
