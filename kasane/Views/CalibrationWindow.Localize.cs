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
        ModeInputCheck.ToolTip = Loc.T("キーを押すと、画面のキーの色が変わります。キャリブレーションが必要かを確かめます(管理者権限は要りません)。",
                                       "Pressing a key changes the color of that key on screen. Use this to check whether calibration is needed (no administrator rights required).");
        ModeSingleKey.Content = Loc.T("個別キーのキャリブレーション", "Single-key calibration");
        ModeSingleKey.ToolTip = Loc.T("画面のキーをクリックして、そのキーだけを実測します(管理者権限が要ります)。",
                                      "Click a key on screen to measure only that key (requires administrator rights).");
        ModeAllKeys.Content = Loc.T("全体のキャリブレーション", "Full calibration");
        ModeAllKeys.ToolTip = Loc.T("黄色のキーを順に押して、全部のキーを実測します(管理者権限が要ります)。",
                                    "Press the yellow keys in turn to measure all keys (requires administrator rights).");
        TxtNote.Text = Loc.T("このウィンドウが前面にある間、キーボードで押したキーはWindowsに送られません。操作はマウスで行ってください。",
                             "While this window is in front, keys you press are not sent to Windows. Use the mouse to operate it.");
        BtnResetChecks.Content = Loc.T("確認済みをリセット", "Reset checked keys");
        BtnBack.Content = Loc.T("1つ戻る", "Back");
        BtnSkip.Content = Loc.T("このキーを飛ばす", "Skip this key");
        BtnFinish.Content = Loc.T("実測値を保存して閉じる", "Save measurements and close");
        BtnCancel.Content = Loc.T("閉じる(実測値は保存しない)", "Close (do not save measurements)");
    }
}
