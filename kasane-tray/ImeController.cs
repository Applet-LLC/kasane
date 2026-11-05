using Kasane.Model;

namespace Kasane.Tray;

/// <summary>
/// 前面のアプリの IME を ON/OFF にする（Sequence の Ime ステップ・Text の ImeOffDuring。<see cref="Win32ActionEnvironment.SetIme"/> から呼ぶ）。
/// 今の状態を読み（設定の「IME の状態の取得方法」に従う。<see cref="ImeSourceSelector"/>）、目標と同じなら何もしない。
/// 違うときだけ、<see cref="ImeRoutePlanner"/> が決めた経路を順に試す（2026-11-05。設定の「IME の切り替え方」に従う）。
///  - Imm: IME ウィンドウに IMC_SETOPENSTATUS を送る。
///  - VkKey: VK_IME_ON / VK_IME_OFF を SendInput で送る（冪等。状態を読めなくても送る）。
///  - ImeOnOffKey / HenkanKey: IME オン / オフ キー、変換 / 無変換（どちらも ON/OFF を直接指定するキー）。管理者として動くトレイで
///    ドライバ経由で送れればドライバ経由（管理者のウィンドウにも届く）、送れなければ SendInput。
///  - DriverAltKanji: Alt+半角/全角をドライバ経由で送る（管理者として動くトレイの Auto。送れなければ次の経路へ）。
///  - AltKanji: Alt+半角/全角。ドライバ経由で送れなければ SendInput（altkanji を書いた行）。
///  - Tsf: TSF のグローバルコンパートメントに書く（実験的。<see cref="TsfImeWriter"/>）。
/// Alt+半角/全角は切り替えのキーなので、状態を読めないときは送らない。送ったのに反映されないときも、送り直さない
/// （遅れて反映されると、二重に切り替わる）。
/// 変えたあとは読み直して、反映を確かめる（VkKey・Tsf は、読み直しでも状態が取れなければ、送ったことで成功とする）。
/// 変えたときは、使った経路をログに 1 行残す。
/// 1つのスレッド（ActionRunner のスレッド）から順に呼ぶこと。
/// </summary>
internal sealed class ImeController
{
    private const ushort VkKanji = 0x19, VkImeOn = 0x16, VkImeOff = 0x1A, VkMenu = 0x12, VkConvert = 0x1C, VkNonConvert = 0x1D;
    private const ushort ScanKanji = 0x29, ScanAlt = 0x38, ScanHenkan = 0x79, ScanMuhenkan = 0x7B;
    // IME オン / IME オフ キー（日本語キーボードの配列で VK_IME_ON / VK_IME_OFF になる。USB の LANG1 / LANG2）。
    private const ushort ScanImeOn = 0xE0F2, ScanImeOff = 0xE0F1;
    private const byte ModLeftAlt = 0x04; // KBDLAYER_MOD_LALT
    private const int SettleTries = 8, SettleStepMs = 25;

    /// <summary>前面のウィンドウと IME ウィンドウを探し、IMM32 で書く。</summary>
    private readonly Win32ImeSource _imm;
    /// <summary>今の状態を読む（設定の取得方法に従う）。</summary>
    private readonly IImeSource _state;
    /// <summary>仮想キーを送る（vk、スキャンコード、離すとき true）。</summary>
    private readonly Action<ushort, ushort, bool> _sendVk;
    /// <summary>修飾キー（KBDLAYER_MOD_*）つきでスキャンコードのキーを1回送る。ドライバ経由（1.01 以降のドライバ）。送れたら true。</summary>
    private readonly Func<ushort, byte, bool>? _sendScanThroughDriver;
    /// <summary>ドライバ経由で送れそうか（試さずに答える）。</summary>
    private readonly Func<bool> _driverUsable;
    private readonly bool _trayElevated;
    private readonly Func<ImeSwitchSetting> _imeSwitch;
    /// <summary>ドライバ経由でキーを送る直前に呼ぶ（キーフックが、そのキーをトリガーとして拾わないように）。</summary>
    private readonly Action<ushort> _markSelfSent;

    // 変換 / 無変換を送っても IME が変わらなかった（Microsoft IME に割り当てが無い）。設定が変わったら、試し直す。
    private bool _henkanUnassigned;
    private int _lastGeneration;
    // 直前の Run が、経路を試さずに飛ばした。
    private bool _skipped;

    // OffRemember で覚えた、元の状態と、そのときの前面のウィンドウ（別のウィンドウに戻すと、無関係なアプリの IME を変えてしまう）。
    private bool? _saved;
    private IntPtr _savedWindow;

    public ImeController(Win32ImeSource imm, IImeSource state, Action<ushort, ushort, bool> sendVk,
        Func<ushort, byte, bool>? sendScanThroughDriver, Func<bool> driverUsable, bool trayElevated, Func<ImeSwitchSetting> imeSwitch, Action<ushort> markSelfSent)
    {
        _imm = imm;
        _state = state;
        _sendVk = sendVk;
        _sendScanThroughDriver = sendScanThroughDriver;
        _driverUsable = driverUsable;
        _trayElevated = trayElevated;
        _imeSwitch = imeSwitch;
        _markSelfSent = markSelfSent;
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
            var state = _state.Read();
            if (state.Known) { _saved = state.Open; _savedWindow = _imm.ForegroundWindow(); }
            else _saved = null; // 元の状態が分からないので、戻さない
            target = ImeTarget.Off;
        }

        bool? current = ReadOpen();
        bool open;
        if (target == ImeTarget.Toggle)
        {
            if (current is not bool now)
            {
                TrayLog.Write(Loc.T("IME: 今の状態を読めないため、切り替え(Toggle)できません", "IME: Cannot toggle because the current state can't be read"));
                return false;
            }
            open = !now;
        }
        else open = target == ImeTarget.On;
        if (current == open) return true;

        bool driverUsable = _sendScanThroughDriver != null && _driverUsable();
        var setting = _imeSwitch();
        // 設定が変わった（トレイが tray.json を読み直して、別の値を見た）ら、変換 / 無変換の割り当ての判定を試し直す。
        // 値ではなく変わった回数で比べる（「自動」にして IME を操作しないまま「変換 / 無変換」に戻しても、気づけるように）。
        if (setting.Generation != _lastGeneration) { _lastGeneration = setting.Generation; _henkanUnassigned = false; }
        var routes = ImeRoutePlanner.Plan(method, setting.Value, _trayElevated, driverUsable, current != null);
        bool previousSkipped = false;
        for (int i = 0; i < routes.Count; i++)
        {
            // 前の経路を試さずに飛ばした（割り当てが無いとみなした変換 / 無変換）ときは、「変えられなかった」とは書かない。
            if (i > 0 && !previousSkipped)
                TrayLog.Write(Loc.T($"IME: {RouteName(routes[i - 1])}で変えられなかったため、{RouteName(routes[i])}で試します",
                                    $"IME: {RouteName(routes[i - 1])} did not work, trying {RouteName(routes[i])}"));
            _skipped = false;
            if (Run(routes[i], open, out string via))
            {
                TrayLog.Write(Loc.T($"IME: {via}で {(open ? "ON" : "OFF")} にしました", $"IME: turned {(open ? "on" : "off")} via {via}"));
                return true;
            }
            previousSkipped = _skipped;
        }
        return false;
    }

    private static string RouteName(ImeRoute route) => route switch
    {
        ImeRoute.Imm => "IMM32",
        ImeRoute.VkKey => Loc.T("IME キー", "the IME key"),
        ImeRoute.DriverAltKanji => Loc.T("ドライバ経由の Alt+半角/全角", "Alt+Hankaku/Zenkaku through the driver"),
        ImeRoute.AltKanji => "Alt+半角/全角",
        ImeRoute.ImeOnOffKey => Loc.T("IME オン/オフ キー", "the IME on/off key"),
        ImeRoute.HenkanKey => Loc.T("変換/無変換キー", "the Henkan/Muhenkan key"),
        _ => "TSF",
    };

    private bool Run(ImeRoute route, bool open, out string via)
    {
        via = RouteName(route);
        switch (route)
        {
            case ImeRoute.Imm: return ByImm(open);
            case ImeRoute.VkKey: return ByKey(open);
            case ImeRoute.DriverAltKanji: return ByAltKanji(open, sendInputFallback: false, out via);
            case ImeRoute.AltKanji: return ByAltKanji(open, sendInputFallback: true, out via);
            case ImeRoute.ImeOnOffKey: return ByAbsoluteKey(open, henkan: false, out via);
            case ImeRoute.HenkanKey: return ByAbsoluteKey(open, henkan: true, out via);
            default: return ByTsf(open);
        }
    }

    private bool? ReadOpen()
    {
        var state = _state.Read();
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

    private bool ByAltKanji(bool open, bool sendInputFallback, out string via)
    {
        via = RouteName(ImeRoute.DriverAltKanji);
        var current = ReadOpen();
        if (current == null)
        {
            TrayLog.Write(Loc.T("IME(AltKanji): 今の状態を読めないため、切り替え動作のキーは送りません", "IME (AltKanji): the current state can't be read, so the toggle key is not sent"));
            return false;
        }
        if (current == open) return true;
        if (!SendThroughDriver(ScanKanji, ModLeftAlt))
        {
            // ドライバ経由で送れなかった（1.00 のドライバ・昇格していない・ライセンスが無効など。理由は DriverKey: の行）。
            if (!sendInputFallback) return false;
            via = Loc.T("SendInput の Alt+半角/全角", "Alt+Hankaku/Zenkaku via SendInput");
            _sendVk(VkMenu, ScanAlt, false);
            Tap(VkKanji, ScanKanji);
            _sendVk(VkMenu, ScanAlt, true);
        }
        return Settle(open, unverifiableOk: false, "AltKanji");
    }

    /// <summary>
    /// ON/OFF を直接指定するキー（IME オン / オフ キー、変換 / 無変換）を送る。状態を読めなくても送る（何度送っても同じ結果になる）。
    /// 管理者として動くトレイでは、ドライバ経由を先に試す。送れなければ SendInput。
    /// </summary>
    private bool ByAbsoluteKey(bool open, bool henkan, out string via)
    {
        string name = RouteName(henkan ? ImeRoute.HenkanKey : ImeRoute.ImeOnOffKey);
        via = name;
        if (henkan && _henkanUnassigned) { _skipped = true; return false; } // 割り当てが無いとみなしている（次の IMM32 で変える）
        if (ReadOpen() == open) return true;
        ushort scan = henkan ? (open ? ScanHenkan : ScanMuhenkan) : (open ? ScanImeOn : ScanImeOff);
        if (_trayElevated && _driverUsable() && SendThroughDriver(scan, 0))
        {
            via = Loc.T("ドライバ経由の" + name, name + " through the driver");
        }
        else if (henkan) Tap(open ? VkConvert : VkNonConvert, scan);
        else Tap(open ? VkImeOn : VkImeOff, 0); // スキャンコードを持たない VK として送る（SendInput では、この形で効くことを確かめている）
        bool ok = Settle(open, unverifiableOk: true, henkan ? "Henkan" : "ImeKey");
        if (henkan && !ok)
        {
            // 状態を読めたのに変わらなかった（Settle が false なのは、読めて目標と違うときだけ）。Microsoft IME に割り当てが無いとみなし、
            // 設定の「IME の切り替え方」が変わるか、トレイを起動し直すまで、変換 / 無変換を送らない。割り当てが無いと、変換は再変換として働き、
            // 入力済みの文字を変換中の文字に取り込んでしまう（2026-11-05 の試験。Text の「元に戻す」で起きた）。
            _henkanUnassigned = true;
            TrayLog.Write(Loc.T("IME: 変換/無変換で IME が変わらなかったため、Microsoft IME に割り当てが無いとみなし、以後は変換/無変換を送りません(IMM32 で変えます)。割り当てたあとは、設定の「IME の切り替え方」を選び直すか(ほかのものにして OK したあと、戻して OK)、トレイを起動し直してください",
                                "IME: Henkan/Muhenkan did not change the IME, so they are treated as unassigned in Microsoft IME and are no longer sent (IMM32 is used). After assigning them, choose \"How to switch the IME\" again (change it, click OK, then change it back and click OK) or restart the tray"));
        }
        return ok;
    }

    private bool SendThroughDriver(ushort scan, byte mods)
    {
        if (_sendScanThroughDriver == null) return false;
        _markSelfSent(scan);
        return _sendScanThroughDriver(scan, mods);
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
    /// 反映を待つ（読み直す）。目標になれば true。ずっと状態が取れなければ、<paramref name="unverifiableOk"/> のときだけ成功とする。
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
