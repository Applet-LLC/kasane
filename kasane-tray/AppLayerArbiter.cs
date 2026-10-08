namespace Kasane.Tray;

/// <summary>
/// アプリ管理レイヤーを実際に立てる・下ろす操作（テストでは偽物に差し替える）。失敗は例外で知らせる。
/// 実装（<see cref="KbdLayerAppLayerDriver"/>）は、立てている間ハンドルを開いたまま持ち、失敗したらハンドルを閉じる
/// （ドライバは、所有者のハンドルが閉じると、そのハンドルで立てたレイヤーをすべて下ろす）。
/// </summary>
public interface ILayerStateDriver
{
    /// <summary>そのキーボードのアプリ管理レイヤーの、layer のビットが立っているか。</summary>
    bool IsOn(uint uniqId, int layer);
    /// <summary>layer のビットを立てる／下ろす（NICOLA の OnEnter/OnExit キーは送らない）。</summary>
    void Set(uint uniqId, int layer, bool on);
    /// <summary>ハンドルを閉じる。アプリ管理レイヤーは、ハンドルを閉じると 0 に戻る。</summary>
    void Release();
}

/// <summary>
/// アプリ管理レイヤーの合成器（2026-10-08、P2）。アプリ管理レイヤー（AppLayerMask）の所有者は、キーボードごとに
/// 1つのハンドルだけで、別のハンドルが立てると所有を奪い、そのハンドルを閉じると全部が 0 に戻る。そのため、
/// IME 連動（"ime"）・前面のアプリの規則（"rule"）など、トレイの中の入力源は、すべてここを通して 1 本のハンドルで立てる。
///  - 入力源ごとに「立てたい (UniqId, Layer)」の集合を持ち、ドライバのビットは、どれかの入力源が立てたいかどうか（OR）にする。
///    片方の入力源が下ろしても、もう片方が立てたいビットは残る。
///  - 立てたビットを <c>_applied</c> に覚え、差分だけを操作する。すべての入力源が空になったら、ハンドルを閉じる
///    （常駐中にハンドルを持ち続けて、ドライバの更新を妨げないという、IME 連動の方針を保つ）。
///  - ドライバの操作が失敗したら、実装がハンドルを閉じる（＝立てたビットは全部消える）ので、<c>_applied</c> も空にする。
///    次の <see cref="Reconcile"/> か <see cref="Verify"/> で立て直す。
/// 呼び出しは 1 つのスレッド（IME 連動のスレッド）からだけ行うこと（ロックは持たない）。
/// </summary>
public sealed class AppLayerArbiter
{
    private readonly ILayerStateDriver _driver;
    private readonly Dictionary<string, HashSet<(uint UniqId, int Layer)>> _sources = new();
    private readonly HashSet<(uint UniqId, int Layer)> _applied = new();
    private bool _holding;

    public AppLayerArbiter(ILayerStateDriver driver)
    {
        _driver = driver;
    }

    /// <summary>今どれかの入力源が立てたいビット。</summary>
    public IReadOnlyCollection<(uint UniqId, int Layer)> Desired => DesiredSet();

    /// <summary>ハンドルを持っているか（テストと診断用）。</summary>
    public bool Holding => _holding;

    /// <summary>実際のドライバの状態（再接続でビットが消えていないかの確認に使う）。</summary>
    public bool IsOn(uint uniqId, int layer) => Guard(() => _driver.IsOn(uniqId, layer));

    /// <summary>入力源 source の 1 ビットの希望を変えて、合わせる。立てる希望なら、実際に立っているかも確かめる。</summary>
    public void SetBit(string source, uint uniqId, int layer, bool on)
    {
        var set = SourceSet(source);
        if (on) set.Add((uniqId, layer));
        else set.Remove((uniqId, layer));
        Reconcile();
        // 呼び出し元（IME 連動）は「実際の状態が違う」ときに呼ぶ。記録の上では立ててあっても、キーボードの
        // 再接続などで実際には消えていることがあるので、立てる希望なら確かめて立て直す。
        if (on && !IsOn(uniqId, layer)) Guard(() => { _driver.Set(uniqId, layer, true); return true; });
    }

    /// <summary>入力源 source の希望を、bits に置き換えて、合わせる。</summary>
    public void SetSource(string source, IEnumerable<(uint UniqId, int Layer)> bits)
    {
        var set = SourceSet(source);
        set.Clear();
        set.UnionWith(bits);
        Reconcile();
    }

    /// <summary>入力源 source の希望を全部消して、合わせる。</summary>
    public void ClearSource(string source)
    {
        if (_sources.TryGetValue(source, out var set)) set.Clear();
        Reconcile();
    }

    /// <summary>入力源 source が今立てたいビット（テストと診断用）。</summary>
    public IReadOnlyCollection<(uint UniqId, int Layer)> SourceBits(string source) =>
        _sources.TryGetValue(source, out var set) ? set : Array.Empty<(uint, int)>();

    /// <summary>
    /// ドライバのビットを、入力源の希望（OR）に合わせる。差分だけを操作する。すべて空になったらハンドルを閉じる。
    /// 失敗は例外で返す（そのときは記録を空にしてあるので、次の呼び出しでやり直す）。
    /// </summary>
    public void Reconcile()
    {
        var desired = DesiredSet();

        foreach (var bit in _applied.Where(b => !desired.Contains(b)).ToList())
        {
            if (_holding) Guard(() => { _driver.Set(bit.UniqId, bit.Layer, false); return true; });
            _applied.Remove(bit);
        }
        foreach (var bit in desired.Where(b => !_applied.Contains(b)).ToList())
        {
            Guard(() => { _driver.Set(bit.UniqId, bit.Layer, true); return true; });
            _holding = true;
            _applied.Add(bit);
        }
        if (desired.Count == 0 && _holding)
        {
            // 立てているものが無くなったので、ハンドルを手放す（閉じると、残っていても 0 に戻る）。
            _holding = false;
            _applied.Clear();
            _driver.Release();
        }
    }

    /// <summary>
    /// 立てたはずのビットが実際に立っているかを確かめ、消えていたら立て直す（キーボードの再接続でアプリ管理レイヤーが
    /// 消えた場合など）。定期的に呼ぶ。
    /// </summary>
    public void Verify()
    {
        foreach (var bit in DesiredSet())
        {
            if (!IsOn(bit.UniqId, bit.Layer))
            {
                Guard(() => { _driver.Set(bit.UniqId, bit.Layer, true); return true; });
                _holding = true;
                _applied.Add(bit);
            }
        }
    }

    /// <summary>すべての入力源を消し、ハンドルを閉じる（トレイの終了時）。失敗しても例外は出さない。</summary>
    public void Shutdown()
    {
        _sources.Clear();
        _applied.Clear();
        _holding = false;
        try { _driver.Release(); }
        catch (Exception ex) when (ex is Interop.KbdLayerException or InvalidOperationException or IOException) { }
    }

    private HashSet<(uint UniqId, int Layer)> SourceSet(string source)
    {
        if (!_sources.TryGetValue(source, out var set)) _sources[source] = set = new HashSet<(uint, int)>();
        return set;
    }

    private HashSet<(uint UniqId, int Layer)> DesiredSet()
    {
        var all = new HashSet<(uint, int)>();
        foreach (var set in _sources.Values) all.UnionWith(set);
        return all;
    }

    // ドライバの操作が失敗すると、実装はハンドルを閉じる（立てたビットは全部消える）。記録もそれに合わせてから、例外を返す。
    private T Guard<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is Interop.KbdLayerException or InvalidOperationException or IOException)
        {
            _applied.Clear();
            _holding = false;
            throw;
        }
    }
}
