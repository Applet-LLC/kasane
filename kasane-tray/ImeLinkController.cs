namespace Kasane.Tray;

/// <summary>IMEの変換モードのビット（imm.h の IME_CMODE_*）。</summary>
public static class ImeConversion
{
    public const uint Native = 0x0001;     // IME_CMODE_NATIVE（かな）
    public const uint Katakana = 0x0002;   // IME_CMODE_KATAKANA
    public const uint FullShape = 0x0008;  // IME_CMODE_FULLSHAPE
}

/// <summary>フォーカス中のウィンドウのIMEの状態。取れなかったときは Known=false。</summary>
public readonly record struct ImeState(bool Known, bool Open, uint ConversionMode)
{
    public static readonly ImeState Unknown = new(false, false, 0);

    /// <summary>IMEがONで、変換モードがかな（ひらがな・カタカナ）。半角英数（IMEはONでも直接入力と同じ）は含まない。</summary>
    public bool IsKanaInput => Known && Open && (ConversionMode & ImeConversion.Native) != 0;
}

/// <summary>
/// IME連動の対象: IMEの状態に合わせて立てるレイヤー。2種類ある。
///  - NICOLA（<see cref="IsNicola"/>）: NICOLAが設定されて有効なキーボードと、そのNICOLAのレイヤー。IMEがかな入力のときに立てる。
///  - レイヤーだけの連動（NICOLAではない。設定の「IME連動」タブ）: 設定したレイヤー。<see cref="KanaOnly"/> が true ならかな入力のとき、false ならIMEがONのとき（半角英数を含む）。
/// MapEnabled は、そのキーボードのレイヤーマップがONか（NICOLAはレイヤーマップがONのときだけ動く。OFF＝緊急解除・一時停止のキーボードでは、
/// レイヤーを立てても入力は変わらない）。
/// </summary>
public sealed record NicolaTarget(uint UniqId, int Layer, bool MapEnabled = true, bool IsNicola = true, bool KanaOnly = true);

/// <summary>IME連動が使うドライバの操作（テストでは偽物に差し替える）。失敗は例外で知らせる。</summary>
public interface IImeLayerDriver
{
    /// <summary>NICOLAが設定されて有効なキーボード（ドライバに接続できなければ例外）。</summary>
    IReadOnlyList<NicolaTarget> FindTargets();
    /// <summary>そのキーボードのアプリ管理レイヤーの、layerのビットが立っているか。</summary>
    bool IsAppLayerOn(uint uniqId, int layer);
    /// <summary>アプリ管理レイヤーのlayerのビットを立てる／下ろす（NICOLAのOnEnter/OnExitキーは送らない）。</summary>
    void SetAppLayer(uint uniqId, int layer, bool on);
    /// <summary>ドライバのハンドルを閉じる。アプリ管理レイヤーは、ハンドルを閉じると0に戻る。</summary>
    void Release();
}

public sealed record ImeLinkStatus(bool Linked, bool NicolaOn, int TargetCount, int NicolaTargetCount = -1)
{
    public static readonly ImeLinkStatus Off = new(false, false, 0);

    /// <summary>NICOLAの対象があるか（トレイのツールチップに、NICOLAの状態を出すかの判定。指定が無いときは、対象があればNICOLAとみなす）。</summary>
    public bool HasNicola => Linked && (NicolaTargetCount < 0 ? TargetCount > 0 : NicolaTargetCount > 0);
}

/// <summary>
/// IME連動の中身。IMEが日本語入力モードのときだけ、NICOLAを設定したキーボードのNICOLAのレイヤーを、アプリ管理レイヤーとして
/// 立てる（キーボード側のTGなどとは別に持つので、お互いを壊さない。実効のレイヤーは両方のOR）。
///  - IMEの状態が取れないとき（管理者のウィンドウ、UWPの一部など）は、直前の状態を保つ。
///  - 状態が変わったときと、1秒ごとの見直しで、ドライバの状態を合わせる。後者は、キーボードを接続し直して
///    アプリ管理レイヤーが失われた場合などの取りこぼしを直すため。
///  - 連動を止める・セッションが非アクティブ（ロック・別ユーザー）・対象が無いときは、ハンドルを閉じてレイヤーを戻す。
/// 時刻は <paramref name="nowMs"/>（ミリ秒）で渡す（テストで進められるように）。
/// </summary>
public sealed class ImeLinkController
{
    public const long VerifyIntervalMs = 1000;
    public const long TargetRefreshIntervalMs = 1000;

    private readonly IImeLayerDriver _driver;
    private readonly Func<long> _nowMs;

    private bool _imeOn;    // IMEがかな入力（NICOLAと、かな入力だけの連動）
    private bool _imeOpen;  // IMEがON（半角英数を含む）
    private bool _holding;
    private long _lastVerify = long.MinValue;
    private long _lastTargetRefresh = long.MinValue;
    private IReadOnlyList<NicolaTarget> _targets = Array.Empty<NicolaTarget>();
    private bool _lastApplied;
    private bool _lastAppliedOpen;
    // 私たちがレイヤーを操作したキーボードとレイヤー。NICOLAをクリアして対象から外れたキーボードのレイヤーを戻すために覚える。
    private readonly HashSet<(uint UniqId, int Layer)> _driven = new();

    public ImeLinkController(IImeLayerDriver driver, Func<long>? nowMs = null)
    {
        _driver = driver;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    /// <summary>IMEの状態から、NICOLAを立てるか。取れないときは直前の判断を保つ。</summary>
    public static bool Decide(ImeState state, bool previous) => state.Known ? state.IsKanaInput : previous;

    /// <summary>IMEがONか（半角英数のモードも含む）。取れないときは直前の判断を保つ。</summary>
    public static bool DecideOpen(ImeState state, bool previous) => state.Known ? state.Open : previous;

    public ImeLinkStatus Tick(ImeState ime, bool enabled, bool sessionActive)
    {
        if (!enabled || !sessionActive)
        {
            Reset();
            return ImeLinkStatus.Off;
        }

        _imeOn = Decide(ime, _imeOn);
        _imeOpen = DecideOpen(ime, _imeOpen);
        long now = _nowMs();

        if (now - _lastTargetRefresh >= TargetRefreshIntervalMs || _lastTargetRefresh == long.MinValue)
        {
            _lastTargetRefresh = now;
            // 一覧を取れなかったときは、前の一覧のまま続ける（一瞬の失敗で、全キーボードのレイヤーとハンドルを手放さない）。
            try { _targets = _driver.FindTargets(); DropRemovedTargets(); }
            catch (Exception ex) when (IsDriverFailure(ex)) { }
        }

        if (_targets.Count == 0)
        {
            // 対象が無いので、ハンドルを持ち続けない（ドライバの更新・アンインストールを妨げないため）。
            if (_holding) { SafeRelease(); }
            _holding = false;
            _driven.Clear();
            _lastVerify = long.MinValue;
            return new ImeLinkStatus(true, false, 0);
        }

        if (_imeOn != _lastApplied || _imeOpen != _lastAppliedOpen || now - _lastVerify >= VerifyIntervalMs || _lastVerify == long.MinValue)
        {
            _lastVerify = now;
            _lastApplied = _imeOn;
            _lastAppliedOpen = _imeOpen;
            // 同じキーボードの同じレイヤーを複数の対象が指していたら、どれかが立てたいときに立てる（OR）。
            foreach (var group in _targets.GroupBy(t => (t.UniqId, t.Layer)))
            {
                bool want = group.Any(t => t.KanaOnly ? _imeOn : _imeOpen);
                try
                {
                    if (_driver.IsAppLayerOn(group.Key.UniqId, group.Key.Layer) != want) _driver.SetAppLayer(group.Key.UniqId, group.Key.Layer, want);
                    _holding = true;
                    _driven.Add((group.Key.UniqId, group.Key.Layer));
                }
                catch (Exception ex) when (IsDriverFailure(ex))
                {
                    _lastApplied = !_imeOn; // 次の周期でやり直す
                    _lastAppliedOpen = !_imeOpen;
                    _lastTargetRefresh = long.MinValue; // 対象の一覧も読み直す
                }
            }
        }
        // NICOLAが入力中なのは、IMEが日本語入力モードで、レイヤーマップがONのNICOLAのキーボードがあるとき。
        int nicola = _targets.Count(t => t.IsNicola);
        return new ImeLinkStatus(true, _imeOn && _targets.Any(t => t.IsNicola && t.MapEnabled), _targets.Count, nicola);
    }

    /// <summary>
    /// 対象から外れたキーボード（NICOLAをクリアした、NICOLAのレイヤーを変えた）のレイヤーを戻す。ハンドルを閉じると全部戻るが、
    /// 別のキーボードの対象が残っている間は閉じないので、1つずつ下ろす。
    /// </summary>
    private void DropRemovedTargets()
    {
        if (_driven.Count == 0) return;
        foreach (var key in _driven.ToList())
        {
            if (_targets.Any(t => t.UniqId == key.UniqId && t.Layer == key.Layer)) continue;
            if (_targets.Count > 0)
            {
                try { _driver.SetAppLayer(key.UniqId, key.Layer, false); }
                catch (Exception ex) when (IsDriverFailure(ex)) { /* 外されたキーボード。無くなっていれば、レイヤーも無い */ }
            }
            _driven.Remove(key);
        }
    }

    /// <summary>連動をやめる（トレイの終了時を含む）。立てていたレイヤーはドライバが戻す。</summary>
    public void Shutdown() => Reset();

    private void Reset()
    {
        if (_holding) SafeRelease();
        _holding = false;
        _imeOn = false;
        _imeOpen = false;
        _lastApplied = false;
        _lastAppliedOpen = false;
        _lastVerify = long.MinValue;
        _lastTargetRefresh = long.MinValue;
        _targets = Array.Empty<NicolaTarget>();
        _driven.Clear();
    }

    private void SafeRelease()
    {
        try { _driver.Release(); }
        catch (Exception ex) when (IsDriverFailure(ex)) { }
    }

    private static bool IsDriverFailure(Exception ex) =>
        ex is Interop.KbdLayerException or InvalidOperationException or IOException;
}
