using Kasane.Model;

namespace Kasane.Tray;

/// <summary>
/// 前面のアプリの IME を ON/OFF にする（Sequence の Ime ステップ・Text の ImeOffDuring。<see cref="Win32ActionEnvironment.SetIme"/> から呼ぶ）。
/// 今の状態を IMM32（<see cref="Win32ImeSource"/>）で読み、目標と同じなら何もしない。違うときだけ、選んだ方式で変える。
///  - Imm: IME ウィンドウに IMC_SETOPENSTATUS を送る。
///  - Key: VK_IME_ON / VK_IME_OFF を送る（冪等。状態を読めなくても送る）。
///  - AltKanji: Alt+半角/全角（VK_KANJI）を送る（切り替え動作なので、状態を読めないときは送らない）。
///  - Tsf: TSF のグローバルコンパートメントに書く（実験的。<see cref="TsfImeWriter"/>）。
///  - Auto: Imm、だめなら Key。Auto 以外は固定で、失敗しても別の方式へは切り替えない。
/// 変えたあとは IMM32 で読み直して、反映を確かめる（Key・Tsf は、読み直しでも状態が取れなければ、送ったことで成功とする）。
/// 1つのスレッド（ActionRunner のスレッド）から順に呼ぶこと。
/// </summary>
internal sealed class ImeController
{
    private const ushort VkKanji = 0x19, VkImeOn = 0x16, VkImeOff = 0x1A, VkMenu = 0x12;
    private const ushort ScanKanji = 0x29, ScanAlt = 0x38;
    private const byte ModLeftAlt = 0x04; // KBDLAYER_MOD_LALT
    private const int SettleTries = 8, SettleStepMs = 25;

    private readonly Win32ImeSource _imm;
    /// <summary>仮想キーを送る（vk、スキャンコード、離すとき true）。</summary>
    private readonly Action<ushort, ushort, bool> _sendVk;

    // OffRemember で覚えた、元の状態と、そのときの前面のウィンドウ（別のウィンドウに戻すと、無関係なアプリの IME を変えてしまう）。
    private bool? _saved;
    private IntPtr _savedWindow;

    /// <summary>修飾キー（KBDLAYER_MOD_*）つきでスキャンコードのキーを1回送る。ドライバ経由（Debug 構成のドライバだけ）。送れたら true。</summary>
    private readonly Func<ushort, byte, bool>? _sendScanThroughDriver;

    public ImeController(Win32ImeSource imm, Action<ushort, ushort, bool> sendVk, Func<ushort, byte, bool>? sendScanThroughDriver = null)
    {
        _imm = imm;
        _sendVk = sendVk;
        _sendScanThroughDriver = sendScanThroughDriver;
    }

    public bool Set(ImeTarget target, ImeMethod method)
    {
        try { return SetCore(target, method); }
        catch (Exception ex)
        {
            TrayLog.Write("IME: 切り替え中に例外: " + ex.Message);
            return false;
        }
    }

    private bool SetCore(ImeTarget target, ImeMethod method)
    {
        if (target == ImeTarget.Restore)
        {
            if (_saved is not bool want) return true;
            _saved = null;
            if (_imm.ForegroundWindow() != _savedWindow)
            {
                TrayLog.Write(Loc.T("IME: 前面のウィンドウが変わったため、元の状態へは戻しません", "IME: The foreground window changed, so the IME is not restored"));
                return true;
            }
            target = want ? ImeTarget.On : ImeTarget.Off;
        }
        else if (target == ImeTarget.OffRemember)
        {
            var state = _imm.Read();
            if (state.Known) { _saved = state.Open; _savedWindow = _imm.ForegroundWindow(); }
            else _saved = null; // 元の状態が分からないので、戻さない
            target = ImeTarget.Off;
        }

        bool open;
        if (target == ImeTarget.Toggle)
        {
            var state = _imm.Read();
            if (!state.Known)
            {
                TrayLog.Write(Loc.T("IME: 今の状態を読めないため、切り替え(Toggle)できません", "IME: Cannot toggle because the current state can't be read"));
                return false;
            }
            open = !state.Open;
        }
        else open = target == ImeTarget.On;

        return method switch
        {
            ImeMethod.Imm => ByImm(open),
            ImeMethod.Key => ByKey(open),
            ImeMethod.AltKanji => ByAltKanji(open),
            ImeMethod.Tsf => ByTsf(open),
            _ => ByImm(open) || FallBackToKey(open),
        };
    }

    private bool FallBackToKey(bool open)
    {
        TrayLog.Write(Loc.T("IME: IMM32 で変えられなかったため、IME キーで試します(Auto)", "IME: IMM32 did not work, trying the IME key (Auto)"));
        return ByKey(open);
    }

    private bool? ReadOpen()
    {
        var state = _imm.Read();
        return state.Known ? state.Open : null;
    }

    private bool ByImm(bool open)
    {
        if (ReadOpen() == open) return true;
        if (!_imm.TryGetImeWindow(out IntPtr imeWindow, out string reason))
        {
            TrayLog.Write("IME(IMM32): " + reason);
            return false;
        }
        if (!Win32ImeSource.SetOpenStatus(imeWindow, open))
        {
            TrayLog.Write(Loc.T("IME(IMM32): IME のウィンドウへ届きませんでした(管理者として動いている・応答しない等)", "IME (IMM32): the message did not reach the IME window (running as administrator, not responding, etc.)"));
            return false;
        }
        return Settle(open, unverifiableOk: false, "IMM32");
    }

    private bool ByKey(bool open)
    {
        if (ReadOpen() == open) return true;
        Tap(open ? VkImeOn : VkImeOff, 0);
        return Settle(open, unverifiableOk: true, "Key");
    }

    private bool ByAltKanji(bool open)
    {
        var current = ReadOpen();
        if (current == null)
        {
            TrayLog.Write(Loc.T("IME(AltKanji): 今の状態を読めないため、切り替え動作のキーは送りません", "IME (AltKanji): the current state can't be read, so the toggle key is not sent"));
            return false;
        }
        if (current == open) return true;
        // ドライバ経由で送れるなら、そちらを使う(SendInput が届かない管理者のウィンドウにも届くかを試すため)。
        // 送れなければ(Release のドライバ・昇格していない等)、SendInput へ戻す。
        if (_sendScanThroughDriver == null || !_sendScanThroughDriver(ScanKanji, ModLeftAlt))
        {
            _sendVk(VkMenu, ScanAlt, false);
            Tap(VkKanji, ScanKanji);
            _sendVk(VkMenu, ScanAlt, true);
        }
        return Settle(open, unverifiableOk: false, "AltKanji");
    }

    private bool ByTsf(bool open)
    {
        if (ReadOpen() == open) return true;
        if (!TsfImeWriter.TrySetOpen(open, out string error))
        {
            TrayLog.Write("IME(TSF): " + error);
            return false;
        }
        return Settle(open, unverifiableOk: true, "TSF");
    }

    private void Tap(ushort vk, ushort scan)
    {
        _sendVk(vk, scan, false);
        _sendVk(vk, scan, true);
    }

    /// <summary>
    /// 反映を待つ（IMM32 で読み直す）。目標になれば true。ずっと状態が取れなければ、<paramref name="unverifiableOk"/> のときだけ成功とする。
    /// 取れたのに目標と違うまま（反映されない）なら false。
    /// </summary>
    private bool Settle(bool open, bool unverifiableOk, string via)
    {
        bool sawState = false;
        for (int i = 0; i < SettleTries; i++)
        {
            Thread.Sleep(SettleStepMs);
            var current = ReadOpen();
            if (current == open) return true;
            if (current != null) sawState = true;
        }
        if (!sawState && unverifiableOk)
        {
            TrayLog.Write(Loc.T($"IME({via}): 反映を確かめられませんでしたが、送りました", $"IME ({via}): could not confirm the result, but it was sent"));
            return true;
        }
        TrayLog.Write(Loc.T($"IME({via}): 送りましたが、IME の状態が変わりませんでした", $"IME ({via}): sent, but the IME state did not change"));
        return false;
    }
}
