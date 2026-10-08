using Kasane.Interop;
using Kasane.Model;

namespace Kasane.Tray;

/// <summary>「前面のアプリの規則でリマップをOFFにしたキーボード」の記録（トレイが落ちても、次の起動で戻せるように残す）。</summary>
public interface IMapOffStore
{
    IReadOnlyList<uint> Load();
    void Save(IReadOnlyList<uint> uniqIds);
}

/// <summary>1台のキーボードについて、規則で決まったこと。</summary>
public sealed record AppRuleDecision(IReadOnlyList<int> Layers, bool MapOff, string? RuleName)
{
    public static readonly AppRuleDecision None = new(Array.Empty<int>(), false, null);

    public bool SameEffect(AppRuleDecision other) =>
        MapOff == other.MapOff && Layers.OrderBy(l => l).SequenceEqual(other.Layers.OrderBy(l => l));
}

/// <summary>
/// 前面のアプリの規則（アプリ別レイヤー、2026-10-08、P2）。前面のウィンドウとヘルパー設定（<see cref="HelperConfigSet"/>）から、
/// キーボードごとに「立てるアプリ管理レイヤー」と「リマップをOFFにするか」を決め、レイヤーは <see cref="AppLayerArbiter"/> の
/// "rule" の入力源へ渡し、OFF は SET_LAYERMAP_ENABLED で行う。
///  - 規則は、キーボードごとの分 → 共通の分の順に見て、最初に一致したものを使う。どれにも一致しなければ、Default の規則
///    （無ければ何もしない）。
///  - 前面が変わってすぐは決めない（<see cref="DebounceMs"/> の間、同じ結果が続いたら決める。Alt+Tab 中のちらつきを抑える）。
///  - 一時停止中・セッションが非アクティブ（ロック・別ユーザー）のときは、レイヤーを下ろし、OFF にしたキーボードを戻す
///    （一時停止中は、OFF/ON に触らない。一時停止の仕組みが、ON になったキーボードを「ユーザーが再開した」と見るため）。
///  - OFF は「規則がそう決めた瞬間」にだけ行う。OFF にしたキーボードをユーザーが（エディタの適用などで）ON にしたら、
///    そのキーボードは、規則の結果が変わるまで触らない。
///  - OFF にしたキーボードは <see cref="IMapOffStore"/> に残す。ON/OFF はドライバが永続化するので、トレイが落ちたまま
///    再起動しても OFF のまま残らないよう、次に起動したときに戻す。
/// 時刻は nowMs で渡す（テストで進められるように）。呼び出しは 1 つのスレッドからだけ行うこと。
/// </summary>
public sealed class AppRuleController
{
    public const string SourceName = "rule";
    public const long DebounceMs = 150;
    public const long DeviceRefreshIntervalMs = 1000;
    public const long VerifyIntervalMs = 1000;

    private readonly AppLayerArbiter _arbiter;
    private readonly IDriverAccess _devices;
    private readonly IMapOffStore _mapOffStore;
    private readonly Func<long> _nowMs;

    private List<DeviceInfo> _deviceList = new();
    private long _lastDeviceRefresh = long.MinValue;
    private long _lastVerify = long.MinValue;

    // 決めた結果（キーボードごと）と、決めかけの結果（デバウンス中）。
    private Dictionary<uint, AppRuleDecision> _applied = new();
    private Dictionary<uint, AppRuleDecision>? _pending;
    private long _pendingSince;

    // 規則で OFF にしたキーボード。
    private readonly HashSet<uint> _mapOff;
    private List<uint>? _savedMapOff;
    private bool _recovered;

    public AppRuleController(AppLayerArbiter arbiter, IDriverAccess devices, IMapOffStore mapOffStore, Func<long>? nowMs = null)
    {
        _arbiter = arbiter;
        _devices = devices;
        _mapOffStore = mapOffStore;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        _mapOff = new HashSet<uint>(mapOffStore.Load());
    }

    /// <summary>今の決定（テストと診断用）。</summary>
    public IReadOnlyDictionary<uint, AppRuleDecision> Applied => _applied;

    /// <summary>規則で OFF にしているキーボード（テストと診断用）。</summary>
    public IReadOnlyCollection<uint> MapOffDevices => _mapOff;

    /// <summary>デバウンス中なら、決めるまでの残り時間（ms）。そうでなければ null（呼び出し側は、待つ時間をこれで縮める）。</summary>
    public long? PendingDueInMs => _pending == null ? null : Math.Max(0, DebounceMs - (_nowMs() - _pendingSince));

    /// <summary>
    /// 前面のウィンドウ（取れなければ null = 前の決定を保つ）と設定から、決め直して反映する。
    /// </summary>
    public void Tick(ForegroundWindowInfo? foreground, HelperConfigSet config, bool sessionActive, bool paused)
    {
        long now = _nowMs();

        if (!sessionActive || paused)
        {
            Suspend(restoreMapOff: !paused);
            return;
        }

        // 規則が無く、決めたものも OFF にしたものも無ければ、何もしない（キーボードの一覧も取りに行かない）。
        if (!config.HasAnyRules && _applied.Count == 0 && _mapOff.Count == 0 && _recovered) return;

        RefreshDevices(now);
        RecoverMapOffOnce();

        if (!config.HasAnyRules)
        {
            // 規則が無い（消された）。決めてあったものがあれば戻す。
            _pending = null;
            if (_applied.Count > 0) ApplyDecisions(new Dictionary<uint, AppRuleDecision>());
            return;
        }
        if (foreground == null)
        {
            // 前面が取れない（ロック画面に移る途中など）。前の決定を保つ。
            VerifyLayers(now);
            return;
        }

        var decisions = Decide(foreground, config);
        if (SameAsApplied(decisions))
        {
            _pending = null;
            VerifyLayers(now);
            return;
        }

        if (_pending == null || !SameDecisions(_pending, decisions))
        {
            _pending = decisions;
            _pendingSince = now;
        }
        if (now - _pendingSince >= DebounceMs)
        {
            _pending = null;
            ApplyDecisions(decisions);
        }
    }

    /// <summary>トレイの終了時: レイヤーを下ろし、OFF にしたキーボードを戻す。</summary>
    public void Shutdown() => Suspend(restoreMapOff: true);

    /// <summary>規則を照らし合わせて、キーボードごとの決定を作る（レイヤーマップのあるキーボードだけ）。</summary>
    public Dictionary<uint, AppRuleDecision> Decide(ForegroundWindowInfo foreground, HelperConfigSet config)
    {
        var result = new Dictionary<uint, AppRuleDecision>();
        foreach (var d in _deviceList.Where(d => d.UniqId != 0 && d.HasLayerMap))
        {
            var decision = DecideFor(d.UniqId, foreground, config);
            if (decision.Layers.Count > 0 || decision.MapOff) result[d.UniqId] = decision;
        }
        return result;
    }

    /// <summary>1 台のキーボードについて決める（照合だけ。テスト用に公開）。</summary>
    public static AppRuleDecision DecideFor(uint uniqId, ForegroundWindowInfo foreground, HelperConfigSet config)
    {
        AppLayerRule? defaultRule = null;
        foreach (var rule in config.RulesFor(uniqId))
        {
            if (rule.Default)
            {
                defaultRule ??= rule; // キーボードごとの Default を、共通の Default より優先する
                continue;
            }
            if (rule.Match.IsMatch(foreground)) return ToDecision(rule);
        }
        return defaultRule != null ? ToDecision(defaultRule) : AppRuleDecision.None;
    }

    private static AppRuleDecision ToDecision(AppLayerRule rule) =>
        new(rule.Layers.Where(l => l >= 0 && l < KbdLayerClient.MaxLayers).Distinct().ToList(), rule.MapOff, rule.Name);

    private void RefreshDevices(long now)
    {
        if (now - _lastDeviceRefresh < DeviceRefreshIntervalMs && _lastDeviceRefresh != long.MinValue) return;
        _lastDeviceRefresh = now;
        // 一覧を取れなかったときは、前の一覧のまま続ける（一瞬の失敗で、全部を下ろさない）。
        try { _deviceList = _devices.ListDevices(); }
        catch (Exception ex) when (IsDriverFailure(ex)) { }
    }

    // 一覧を、間隔を待たずに取り直す（取れなければ前の一覧のまま）。
    private void RefreshDevicesNow()
    {
        _lastDeviceRefresh = _nowMs();
        try { _deviceList = _devices.ListDevices(); }
        catch (Exception ex) when (IsDriverFailure(ex)) { }
    }

    // 自分で ON/OFF を変えたら、次に一覧を取り直すまでの間も合うように、控えの一覧を直す。
    private void SetCachedEnabled(uint id, bool enabled)
    {
        int i = _deviceList.FindIndex(d => d.UniqId == id);
        if (i >= 0) _deviceList[i] = _deviceList[i] with { Enabled = enabled };
    }

    // 起動の前に（前回のトレイが落ちて）OFF のまま残ったキーボードを、最初の 1 回だけ戻す。
    private void RecoverMapOffOnce()
    {
        if (_recovered) return;
        _recovered = true;
        if (_mapOff.Count == 0) return;
        foreach (var id in _mapOff.ToList()) RestoreMapOff(id);
        SaveMapOff();
    }

    private void ApplyDecisions(Dictionary<uint, AppRuleDecision> decisions)
    {
        var bits = decisions.SelectMany(kv => kv.Value.Layers.Select(l => (kv.Key, l))).ToList();
        try { _arbiter.SetSource(SourceName, bits); }
        catch (Exception ex) when (IsDriverFailure(ex)) { _lastVerify = long.MinValue; /* 次の Verify で立て直す */ }

        // リマップの OFF/ON は、決定が変わったキーボードだけ（変わった瞬間にだけ）操作する。
        foreach (var (id, decision) in decisions)
        {
            bool wasOff = _applied.TryGetValue(id, out var prev) && prev.MapOff;
            if (decision.MapOff && !wasOff) TurnMapOff(id);
            else if (!decision.MapOff && wasOff) RestoreMapOff(id);
        }
        foreach (var (id, prev) in _applied)
        {
            if (prev.MapOff && !decisions.ContainsKey(id)) RestoreMapOff(id);
        }
        SaveMapOff();
        _applied = decisions;
    }

    private void TurnMapOff(uint id)
    {
        // 「すでに OFF か」は、今の実際の状態で見る。1 秒ごとの一覧は、直前に自分が戻した台を古いまま（OFF）と見て、
        // 「ユーザーが止めた」と取り違える（MapOff の窓を離れてすぐ戻ると OFF にならない、P2 の所見1）。
        RefreshDevicesNow();
        var device = _deviceList.FirstOrDefault(d => d.UniqId == id);
        if (device == null || !device.HasLayerMap || !device.Enabled) return; // すでに OFF（ユーザーや一時停止が止めた）なら触らない
        try
        {
            _devices.SetEnabled(id, false);
            _mapOff.Add(id);
            SetCachedEnabled(id, false);
        }
        catch (Exception ex) when (IsDriverFailure(ex)) { }
    }

    private void RestoreMapOff(uint id)
    {
        if (!_mapOff.Contains(id)) return;
        try
        {
            _devices.SetEnabled(id, true);
            _mapOff.Remove(id);
            SetCachedEnabled(id, true);
        }
        catch (Exception ex) when (IsDriverFailure(ex))
        {
            // 外されたキーボードなど。接続されていなければ記録から消す（次に接続されたら、前回の ON/OFF で復元される。
            // 規則で OFF にしたまま外したキーボードは OFF で戻るが、それは前面のアプリがそのままの場合と同じ）。
            if (_deviceList.All(d => d.UniqId != id)) _mapOff.Remove(id);
        }
    }

    // 記録が変わったときだけ書く。
    private void SaveMapOff()
    {
        var now = _mapOff.OrderBy(id => id).ToList();
        if (_savedMapOff != null && _savedMapOff.SequenceEqual(now)) return;
        try
        {
            _mapOffStore.Save(now);
            _savedMapOff = now;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void Suspend(bool restoreMapOff)
    {
        _pending = null;
        try { _arbiter.ClearSource(SourceName); }
        catch (Exception ex) when (IsDriverFailure(ex)) { }
        if (restoreMapOff)
        {
            foreach (var id in _mapOff.ToList()) RestoreMapOff(id);
            SaveMapOff();
            _applied = new Dictionary<uint, AppRuleDecision>();
        }
        else
        {
            // 一時停止中: OFF/ON には触らない。レイヤーだけを下ろし、OFF の記録は残す（再開の後、前面が変わったら戻す）。
            _applied = _applied.Where(kv => kv.Value.MapOff)
                               .ToDictionary(kv => kv.Key, kv => kv.Value with { Layers = Array.Empty<int>() });
        }
    }

    private void VerifyLayers(long now)
    {
        if (now - _lastVerify < VerifyIntervalMs && _lastVerify != long.MinValue) return;
        _lastVerify = now;
        try { _arbiter.Verify(); }
        catch (Exception ex) when (IsDriverFailure(ex)) { }
    }

    private bool SameAsApplied(Dictionary<uint, AppRuleDecision> decisions) => SameDecisions(_applied, decisions);

    private static bool SameDecisions(IReadOnlyDictionary<uint, AppRuleDecision> a, IReadOnlyDictionary<uint, AppRuleDecision> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var other) && kv.Value.SameEffect(other));

    private static bool IsDriverFailure(Exception ex) => ex is KbdLayerException or InvalidOperationException or IOException;
}
