using Kasane.Interop;
using Kasane.Model;

namespace Kasane.Tray;

/// <summary>
/// トレイアイコンとメニュー。
///  - 左クリック: エディタ（kasane.exe）を開く（起動済みなら前面へ）。
///  - 右クリックのメニュー（上から）: 設定（＝エディタを開く）／一時停止（チェック付き。全キーボードをOFFにし、再開で戻す）／
///    レイヤー（サブメニュー。手でレイヤー 1〜15 を入れ切りする。チェックは今有効なレイヤー）／ライセンス（ライセンス管理を開く）／終了（トレイを終える。一時停止中でも、停止のまま終了する）。
///  - ツールチップに、動作中・一時停止中・ドライバに接続できない、を出す。状態はタイマーで読み直す。
///  - IME連動（<see cref="ImeLinkHost"/>）: IMEがかなモードのときだけNICOLAのレイヤーを自動で立てる。メニューは変えず、
///    ON/OFFはエディタの設定（NICOLAのタブ）で選ぶ。ツールチップにNICOLAの状態を足す。
///  - 前面のアプリの規則（アプリ別レイヤー・リマップOFF）も <see cref="ImeLinkHost"/> の中で動かす（helper.json）。
/// UI Automation（kasane.Tests/UiAutomation/T10-TrayMenu.ps1）では、メニュー項目は表示名（設定・一時停止・ライセンス・終了）で探す
/// （ToolStripの項目のNameは、UIAのAutomationIdにはならない）。
/// </summary>
public sealed class TrayContext : ApplicationContext
{
    private const int PollIntervalMs = 3000;

    private readonly PauseController _pause;
    private readonly AutoOffController _autoOff;
    private volatile bool _autoOffMode = true; // TrayConfig.RemapOnlyWhileTrayRuns の最新の値（ログオフの通知は別スレッドで来るため）
    private bool _autoOffShutdownDone;
    private readonly NotifyIcon _notifyIcon;
    private readonly TrayIconPromoter? _promoter; // タスクバーの設定で、アイコンを初回だけ「表に表示」にする
    private DesktopViewApplier? _desktopView; // デスクトップのアイコンの表示方法を合わせている最中のもの（設定が無ければ null）
    private int? _desktopViewApplied;        // 最後に反映を始めた DesktopViewMode（設定が変わったかを見るため）
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _settingsItem = new("設定") { Name = "TraySettings" };
    private readonly ToolStripMenuItem _pauseItem = new("一時停止") { Name = "TrayPause" };
    private readonly ToolStripMenuItem _layerItem = new("レイヤー") { Name = "TrayLayers" };
    private readonly ToolStripMenuItem _licenseItem = new("ライセンス") { Name = "TrayLicense" };
    private readonly ToolStripMenuItem _exitItem = new("終了") { Name = "TrayExit" };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = PollIntervalMs };

    private readonly Icon _normalIcon;
    private readonly Icon _pausedIcon;
    private readonly Icon _unavailableIcon;
    private readonly ImeLinkHost _imeLink;
    private readonly HelperHost _helper;
    private readonly Control _ui = new(); // 動作の実行スレッドから、UI スレッドへ処理を渡すため
    private readonly LayerWatchService _layerWatch;
    private readonly Dictionary<char, Icon> _badgeIcons = new(); // レイヤー番号つきのアイコン（最大16個。GDIハンドルを増やさないよう使い回す）
    private readonly HashSet<(uint UniqId, int Layer)> _menuHeld = new(); // 「レイヤー」メニューで立てたもの（他の入力源が立てたものと見分けるため）
    private volatile bool _showLayer = true;
    private LayerBadge? _layerBadge;
    private TraySnapshot _snapshot = new(TrayStatus.DriverUnavailable, 0, 0);

    public TrayContext()
    {
        _pause = new PauseController(new KbdLayerDriverAccess(), new JsonPauseStateStore());
        _autoOff = new AutoOffController(new KbdLayerDriverAccess(), new JsonAutoOffStore());
        _autoOffMode = TrayConfig.Load().RemapOnlyWhileTrayRuns;
        Microsoft.Win32.SystemEvents.SessionEnded += OnSessionEnded; // ログオフ・シャットダウン
        _imeLink = new ImeLinkHost();
        _ = _ui.Handle; // このスレッド（UI スレッド）にハンドルを作っておく
        _imeLink.StateChanged += RefreshStateSoon;
        _helper = new HelperHost(_imeLink, () =>
        {
            try { _ui.BeginInvoke(new Action(TogglePause)); }
            catch (InvalidOperationException) { /* 終了中 */ }
        });

        _layerWatch = new LayerWatchService(new KbdLayerWatchSource(), () => _showLayer && _imeLink.SessionActive, OnLayersChanged, TrayLog.Write);

        _normalIcon = TrayIcons.Load();
        _pausedIcon = TrayIcons.Gray(_normalIcon, pauseMarks: true);
        _unavailableIcon = TrayIcons.Gray(_normalIcon, pauseMarks: false);

        // 「設定」は左クリックと同じ動作なので、既定の項目として太字にする（Windowsの慣習）。
        _settingsItem.Font = new Font(_settingsItem.Font, FontStyle.Bold);
        _settingsItem.Click += (_, _) => OpenEditor();
        _pauseItem.Click += (_, _) => TogglePause();
        _licenseItem.Click += (_, _) => Notify(AppLauncher.OpenOrActivate(AppLauncher.FindLicense(), "ライセンス管理"));
        _exitItem.Click += (_, _) => ExitTray();
        _menu.Items.AddRange(new ToolStripItem[] { _settingsItem, _pauseItem, _layerItem, _licenseItem, new ToolStripSeparator(), _exitItem });
        _menu.Opening += (_, _) => { RefreshState(); RebuildLayerMenu(); }; // 開く直前に、最新の状態にする

        _notifyIcon = new NotifyIcon
        {
            Icon = _unavailableIcon,
            Text = "kasane",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) OpenEditor();
        };

        if (Environment.ProcessPath is { } exePath)
        {
            _promoter = new TrayIconPromoter(exePath);
            _promoter.Start();
        }

        SyncDesktopView();

        _timer.Tick += (_, _) => { RefreshState(); SyncDesktopView(); };
        _timer.Start();
        StartPendingImport();
        RefreshState();
    }

    // 保存先（環境変数 KASANE のフォルダ）に取り込みファイルがあれば、設定の無いものだけ取り込む（kasane.exe --import-pending。画面は出ない）。
    // 接続中のキーボードに対応づけるので、あとから接続したキーボード分は、設定画面を開いたときにも調べる。
    private static void StartPendingImport()
    {
        try
        {
            string file = Path.Combine(Kasane.Model.HelperConfig.BaseDirectory, "kasane-import.json");
            if (!File.Exists(file)) return;
            string? exe = AppLauncher.FindEditor();
            if (exe == null) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--import-pending")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            TrayLog.Write("import-pending: " + ex.Message);
        }
    }

    // メニューを開くときに、ドライバから直接読む（監視サービスは、アイコンの表示をオフにすると止まるため、頼らない。読み取り側のIOCTLで、すぐ返る）。
    private static IReadOnlyDictionary<uint, LayerState> ReadLayerStates()
    {
        var states = new Dictionary<uint, LayerState>();
        try
        {
            using var client = KbdLayerClient.TryOpen(out _);
            if (client == null) return states;
            foreach (var d in client.ListDevices().Where(d => d.UniqId != 0 && d.HasLayerMap))
                states[d.UniqId] = client.GetLayerState(d.UniqId);
        }
        catch (Exception ex) when (ex is KbdLayerException or InvalidOperationException or IOException) { /* 読めなければ、項目は出さない */ }
        return states;
    }

    // 「レイヤー」サブメニュー（B-5a）。開くたびに、ドライバから読んで作り直す。選ぶと、"action" の入力源でレイヤーを反転する（IME連動・規則と OR で合わさる）。
    private void RebuildLayerMenu()
    {
        _layerItem.DropDownItems.Clear();
        var states = ReadLayerStates();
        var keyboards = LayerMenuModel.Build(states, _menuHeld);
        _layerItem.Enabled = _snapshot.Status == TrayStatus.Running && keyboards.Count > 0;
        if (!_layerItem.Enabled || !_imeLink.SessionActive)
        {
            _menuHeld.Clear(); // 一時停止・ロックでは、メニューが立てたレイヤーは下ろされる（ActionLayerController）
            if (!_layerItem.Enabled) return;
        }
        // 実際に立っていないものは忘れる（取り消された・キーボードが外れた）。
        _menuHeld.RemoveWhere(h => !states.TryGetValue(h.UniqId, out var s) || (s.AppLayerMask >> h.Layer & 1) == 0);

        foreach (var kb in keyboards)
        {
            var target = keyboards.Count == 1 ? _layerItem : new ToolStripMenuItem(kb.Label) { Name = "TrayLayers_" + kb.UniqId.ToString("X8") };
            foreach (var item in kb.Items)
            {
                var entry = new ToolStripMenuItem($"レイヤー {item.Layer}") { Name = $"TrayLayer_{kb.UniqId:X8}_{item.Layer}", Checked = item.Checked, CheckOnClick = false, Enabled = item.Enabled };
                if (!item.Enabled) entry.ToolTipText = "キーボードのレイヤー切り替え、IME連動、アプリ別レイヤーが立てているので、ここからは切れません。";
                uint uniq = kb.UniqId;
                int layer = item.Layer;
                entry.Click += (_, _) =>
                {
                    if (!_menuHeld.Remove((uniq, layer))) _menuHeld.Add((uniq, layer));
                    _imeLink.RequestLayer(new LayerRequest(uniq, 0, new[] { layer }, LayerOp.Toggle));
                };
                target.DropDownItems.Add(entry);
            }
            if (target != _layerItem) _layerItem.DropDownItems.Add(target);
        }
    }

    private void OpenEditor()
    {
        // 起動中（ウィンドウが出るまで）に押し直されても、2つ目を起動しない。
        if (StartingNotice.IsShowing) return;
        Notify(AppLauncher.OpenOrActivate(AppLauncher.FindEditor(), "kasane",
            started => StartingNotice.ShowFor(started, "kasane の設定画面を準備しています…")));
    }

    /// <summary>失敗の理由があれば、バルーンで知らせる。</summary>
    private void Notify(string? error)
    {
        if (error != null) _notifyIcon.ShowBalloonTip(5000, "kasane", error, ToolTipIcon.Warning);
    }

    private void TogglePause()
    {
        try
        {
            ApplySnapshot(_snapshot.Status == TrayStatus.Paused ? _pause.Resume() : _pause.Pause());
        }
        catch (Exception ex)
        {
            TrayLog.Write("TogglePause: " + ex);
            Notify($"一時停止・再開に失敗しました: {ex.Message}");
        }
    }

    // tray.json の DesktopViewMode が、最後に反映したものと変わっていたら、反映し直す（起動時の1回目も同じ経路）。
    // null（変えない）に戻したときは何もしない（デスクトップは今の表示のまま）。
    private void SyncDesktopView()
    {
        int? mode = TrayConfig.Load().DesktopViewMode;
        if (mode == _desktopViewApplied) return;
        _desktopViewApplied = mode;
        _desktopView?.Dispose();
        _desktopView = DesktopViewApplier.StartIfConfigured(mode);
    }

    private void RefreshState()
    {
        try
        {
            if (_imeLink.ReloadConfig()) _helper.OnConfigChanged();
            var config = TrayConfig.Load();
            _showLayer = config.ShowLayerOnIcon;
            _autoOffMode = config.RemapOnlyWhileTrayRuns;
            // 動いている間だけ有効にするモード: セッションが非アクティブなら OFF、アクティブなら（起動直後も）記録した分を ON に戻す。
            // 一時停止の判定より前に行う（起動直後に、戻す前の「リマップ中のキーボードなし」を見せないため）。
            if (!_autoOffShutdownDone) _autoOff.Tick(_autoOffMode, _imeLink.SessionActive, _pause.IsPaused); // 終了処理の後は、戻さない
            var snapshot = _pause.Refresh();
            _layerWatch.Sync(); // キーボードの増減・設定・セッションの変化に追従する
            ApplySnapshot(snapshot);
        }
        catch (Exception ex) { TrayLog.Write("Refresh: " + ex); } // 常駐中に落ちないよう、読み直しの失敗は記録だけにする
    }

    // 待機スレッドから呼ばれる。UI スレッドで表示を更新する。
    private void OnLayersChanged(IReadOnlyDictionary<uint, LayerState> states)
    {
        var badge = LayerDisplay.Compute(states);
        try
        {
            _ui.BeginInvoke(new Action(() =>
            {
                if (Equals(badge, _layerBadge)) return;
                _layerBadge = badge;
                ApplySnapshot(_snapshot);
            }));
        }
        catch (InvalidOperationException) { /* 終了中 */ }
    }

    private Icon BadgeIcon(LayerBadge badge)
    {
        if (!_badgeIcons.TryGetValue(badge.Badge, out var icon))
            _badgeIcons[badge.Badge] = icon = TrayIcons.WithBadge(_normalIcon, badge.Badge);
        return icon;
    }

    private void ApplySnapshot(TraySnapshot snapshot)
    {
        _snapshot = snapshot;
        _imeLink.Paused = snapshot.Status == TrayStatus.Paused; // 一時停止中は、前面のアプリの規則を止める
        _pauseItem.Checked = snapshot.Status == TrayStatus.Paused;
        _pauseItem.Enabled = snapshot.CanTogglePause;

        (Icon icon, string text) = snapshot.Status switch
        {
            TrayStatus.Paused => (_pausedIcon, "kasane — 一時停止中"),
            TrayStatus.DriverUnavailable => (_unavailableIcon, "kasane — ドライバに接続できません"),
            _ when snapshot.ActiveCount == 0 => (_normalIcon, "kasane — リマップ中のキーボードはありません"),
            _ => (_normalIcon, $"kasane — 動作中（{snapshot.ActiveCount}台）"),
        };
        var ime = _imeLink.Status;
        if (snapshot.Status == TrayStatus.Running && ime.Linked && ime.TargetCount > 0)
            text += ime.NicolaOn ? "・NICOLA 入力中" : "・NICOLA 待機（IME連動）";
        if (snapshot.Status == TrayStatus.Running && snapshot.ActiveCount > 0 && _showLayer && _layerBadge != null)
        {
            icon = BadgeIcon(_layerBadge);
            text = LayerDisplay.AppendToTooltip(text, _layerBadge);
        }
        if (!ReferenceEquals(_notifyIcon.Icon, icon)) _notifyIcon.Icon = icon;
        _notifyIcon.Text = text; // 127文字まで
    }

    // セッションの状態か一時停止が変わった（どのスレッドからでも来る）。3秒の周期を待たずに、UI スレッドで OFF / ON を合わせる。
    private void RefreshStateSoon()
    {
        try { _ui.BeginInvoke(new Action(RefreshState)); }
        catch (InvalidOperationException) { /* 終了中 */ }
    }

    // ログオフ・シャットダウン（SystemEvents のスレッド）。トレイのプロセスが終わる前に、リマップをOFFにする。
    private void OnSessionEnded(object? sender, Microsoft.Win32.SessionEndedEventArgs e) => ShutdownAutoOff();

    private void ShutdownAutoOff()
    {
        lock (_autoOff)
        {
            if (_autoOffShutdownDone) return;
            _autoOffShutdownDone = true;
        }
        try { _autoOff.Shutdown(_autoOffMode); }
        catch (Exception ex) { TrayLog.Write("AutoOff.Shutdown: " + ex); }
    }

    private void ExitTray()
    {
        Microsoft.Win32.SystemEvents.SessionEnded -= OnSessionEnded;
        ShutdownAutoOff(); // 「終了」メニュー。モードがONなら、リマップをOFFにしてから終わる。
        _timer.Stop();
        _layerWatch.Dispose();
        _helper.Dispose();  // キーフックと動作の実行を止める
        _imeLink.Dispose(); // 立てていたレイヤーを戻す
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _promoter?.Dispose();
            _desktopView?.Dispose();
            _layerWatch.Dispose();
            _helper.Dispose();
            _imeLink.Dispose();
            _ui.Dispose();
            _notifyIcon.Dispose();
            _menu.Dispose();
            _normalIcon.Dispose();
            _pausedIcon.Dispose();
            _unavailableIcon.Dispose();
            foreach (var icon in _badgeIcons.Values) icon.Dispose();
        }
        base.Dispose(disposing);
    }
}
