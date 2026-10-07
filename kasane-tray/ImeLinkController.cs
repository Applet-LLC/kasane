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

/// <summary>IME連動の対象: NICOLAが設定されて有効なキーボードと、そのNICOLAのレイヤー。</summary>
public sealed record NicolaTarget(uint UniqId, int Layer);

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

public sealed record ImeLinkStatus(bool Linked, bool NicolaOn, int TargetCount)
{
    public static readonly ImeLinkStatus Off = new(false, false, 0);
}

/// <summary>
/// IME連動の中身。IMEがかなモードのときだけ、NICOLAを設定したキーボードのNICOLAのレイヤーを、アプリ管理レイヤーとして
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
    public const long TargetRefreshIntervalMs = 3000;

    private readonly IImeLayerDriver _driver;
    private readonly Func<long> _nowMs;

    private bool _imeOn;
    private bool _holding;
    private long _lastVerify = long.MinValue;
    private long _lastTargetRefresh = long.MinValue;
    private IReadOnlyList<NicolaTarget> _targets = Array.Empty<NicolaTarget>();
    private bool _lastApplied;

    public ImeLinkController(IImeLayerDriver driver, Func<long>? nowMs = null)
    {
        _driver = driver;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    /// <summary>IMEの状態から、NICOLAを立てるか。取れないときは直前の判断を保つ。</summary>
    public static bool Decide(ImeState state, bool previous) => state.Known ? state.IsKanaInput : previous;

    public ImeLinkStatus Tick(ImeState ime, bool enabled, bool sessionActive)
    {
        if (!enabled || !sessionActive)
        {
            Reset();
            return ImeLinkStatus.Off;
        }

        _imeOn = Decide(ime, _imeOn);
        long now = _nowMs();

        if (now - _lastTargetRefresh >= TargetRefreshIntervalMs || _lastTargetRefresh == long.MinValue)
        {
            _lastTargetRefresh = now;
            try { _targets = _driver.FindTargets(); }
            catch (Exception ex) when (IsDriverFailure(ex)) { _targets = Array.Empty<NicolaTarget>(); }
        }

        if (_targets.Count == 0)
        {
            // 対象が無いので、ハンドルを持ち続けない（ドライバの更新・アンインストールを妨げないため）。
            if (_holding) { SafeRelease(); }
            _holding = false;
            _lastVerify = long.MinValue;
            return new ImeLinkStatus(true, false, 0);
        }

        if (_imeOn != _lastApplied || now - _lastVerify >= VerifyIntervalMs || _lastVerify == long.MinValue)
        {
            _lastVerify = now;
            _lastApplied = _imeOn;
            foreach (var t in _targets)
            {
                try
                {
                    if (_driver.IsAppLayerOn(t.UniqId, t.Layer) != _imeOn) _driver.SetAppLayer(t.UniqId, t.Layer, _imeOn);
                    _holding = true;
                }
                catch (Exception ex) when (IsDriverFailure(ex))
                {
                    _lastApplied = !_imeOn; // 次の周期でやり直す
                    _lastTargetRefresh = long.MinValue; // 対象の一覧も読み直す
                }
            }
        }
        return new ImeLinkStatus(true, _imeOn, _targets.Count);
    }

    /// <summary>連動をやめる（トレイの終了時を含む）。立てていたレイヤーはドライバが戻す。</summary>
    public void Shutdown() => Reset();

    private void Reset()
    {
        if (_holding) SafeRelease();
        _holding = false;
        _imeOn = false;
        _lastApplied = false;
        _lastVerify = long.MinValue;
        _lastTargetRefresh = long.MinValue;
        _targets = Array.Empty<NicolaTarget>();
    }

    private void SafeRelease()
    {
        try { _driver.Release(); }
        catch (Exception ex) when (IsDriverFailure(ex)) { }
    }

    private static bool IsDriverFailure(Exception ex) =>
        ex is Interop.KbdLayerException or InvalidOperationException or IOException;
}
