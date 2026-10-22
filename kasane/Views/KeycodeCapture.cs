using System.Windows.Interop;
using Kasane.Keycodes;

namespace Kasane.Views;

/// <summary>
/// 「キーを押して入力」: 次に押された物理キーをキーコード名（KC_A など。表に名前の無いキーは SC_XXXX）にして返す。
/// ウィンドウ メッセージ（WM_KEYDOWN / WM_SYSKEYDOWN）の lParam からスキャンコードを取るので、左右の修飾キーやテンキーの Enter、
/// 日本語キー（無変換・変換・カタカナひらがな等）も区別できる。取れたキーはどこにも届かない（入力欄に文字が入らない）。
/// 取れないキー: Alt+Tab・Ctrl+Alt+Del などの OS が先に処理するもの、Win キー（離すとスタートが開く）。そのときはキー選択画面を使う。
/// </summary>
public sealed class KeycodeCapture
{
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_PAUSE = 0x13;

    private readonly Action<string> _onCaptured;
    private readonly Action _onEnded;
    private bool _active;

    private static int _activeCount;
    private static long _lastEscapeEndTicks = long.MinValue;

    /// <summary>
    /// 取り込み中か、Esc で取り込みを終えた直後（約 0.5 秒）か。マクロの画面などが、Esc で閉じる処理を、取り込みの Esc と区別するのに使う
    /// （取り込みの Esc で、画面まで閉じてしまわないように）。
    /// </summary>
    public static bool EscapeBelongsToCapture =>
        _activeCount > 0 || Environment.TickCount64 - _lastEscapeEndTicks < 500;

    /// <param name="onCaptured">キーが取れたとき（キーコード名）。</param>
    /// <param name="onEnded">取り込みが終わったとき（取れた・Esc・取消のどれでも）。ボタンの表示を戻すのに使う。</param>
    public KeycodeCapture(Action<string> onCaptured, Action onEnded)
    {
        _onCaptured = onCaptured;
        _onEnded = onEnded;
    }

    /// <summary>true なら、キーを取ったあとも取り込みを続ける（マクロのように、続けて何個も入れる欄用。Esc か、ボタンをもう一度押すと終わる）。</summary>
    public bool Continuous { get; init; }

    public bool IsActive => _active;

    public void Start()
    {
        if (_active) return;
        _active = true;
        _activeCount++;
        ComponentDispatcher.ThreadPreprocessMessage += OnMessage;
    }

    /// <summary>取り込みを止める（キャンセル）。</summary>
    public void Cancel()
    {
        if (!_active) return;
        End();
    }

    private void End()
    {
        if (_active) _activeCount--;
        _active = false;
        ComponentDispatcher.ThreadPreprocessMessage -= OnMessage;
        _onEnded();
    }

    // ウィンドウに届く前（TranslateMessage の前）に横取りするので、handled にすれば WM_CHAR も出ず、入力欄に文字は入らない。
    private void OnMessage(ref MSG msg, ref bool handled)
    {
        if (!_active || (msg.message != WM_KEYDOWN && msg.message != WM_SYSKEYDOWN)) return;

        int vk = (int)msg.wParam;
        long lParam = (long)msg.lParam;
        handled = true;

        if ((lParam & (1L << 30)) != 0) return; // 押しっぱなしの繰り返し
        if (vk == VK_ESCAPE) { _lastEscapeEndTicks = Environment.TickCount64; End(); return; }  // Esc は取消（Esc を入れたいときはキー選択画面から）

        string? token = vk == VK_PAUSE ? "KC_PAUS" : TokenFromKeyMessage(lParam);
        if (token == null) return; // NumLock OFF のときの偽の Shift など。次のキーを待つ

        if (!Continuous) End();
        _onCaptured(token);
    }

    /// <summary>
    /// WM_KEYDOWN の lParam（bit16-23 = スキャンコード、bit24 = 拡張キー）から、キーコード名を作る。
    /// 拡張キーは 0xE000 を足した Enc16（KeycodeTable の形式）。NumLock OFF のときに付く偽の Shift（0xE02A）は null。
    /// </summary>
    public static string? TokenFromKeyMessage(long lParam)
    {
        int scan = (int)((lParam >> 16) & 0xFF);
        bool extended = (lParam & (1L << 24)) != 0;
        if (scan == 0) return null;
        if (extended && scan == 0x2A) return null;
        ushort enc = (ushort)((extended ? 0xE000 : 0) | scan);
        return KeycodeTable.KeyToken(enc);
    }
}
