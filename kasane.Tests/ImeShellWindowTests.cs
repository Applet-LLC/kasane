using Kasane.Tray;

namespace Kasane.Tests;

/// <summary>IMEの状態を見ないウィンドウの判定（シェル・メニュー・スタートメニューなど）。</summary>
public class ImeShellWindowTests
{
    [Theory]
    [InlineData("Shell_TrayWnd", "explorer")]
    [InlineData("TopLevelWindowForOverflowXamlIsland", "explorer")]
    [InlineData("#32768", "anything")]                           // メニュー
    [InlineData("Windows.UI.Core.CoreWindow", "SearchHost")]
    [InlineData("", "explorer")]                                 // クラス名が取れなくても、エクスプローラー本体なら除外
    [InlineData("SomeNewShellClass", "explorer")]                // 見慣れないシェルのウィンドウ
    [InlineData("SomeClass", "Explorer")]                        // 大文字小文字は区別しない
    [InlineData("Windows.UI.Core.CoreWindow", "StartMenuExperienceHost")]
    [InlineData("X", "ShellExperienceHost")]
    [InlineData("X", "LockApp")]
    [InlineData("X", "TextInputHost")]
    public void Shell_Windows_Are_Ignored(string windowClass, string process)
    {
        Assert.True(Win32ImeSource.IsShellWindow(windowClass, process));
    }

    [Theory]
    [InlineData("Notepad", "Notepad")]
    [InlineData("Chrome_WidgetWin_1", "chrome")]
    [InlineData("CabinetWClass", "explorer")]    // ファイル窓（名前の変更・検索ボックス）は、IMEを使う
    [InlineData("ExploreWClass", "explorer")]
    [InlineData("#32770", "explorer")]           // 名前を付けて保存などのダイアログ
    [InlineData("", "notepad")]                  // クラス名が取れなくても、シェルではないプロセスは対象
    public void Real_Application_Windows_Are_Read(string windowClass, string process)
    {
        Assert.False(Win32ImeSource.IsShellWindow(windowClass, process));
    }
}
