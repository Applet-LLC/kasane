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
    private readonly NotifyIcon _notifyIcon;
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
    private volatile bool _showLayer = true;
    private LayerBadge? _layerBadge;
    private TraySnapshot _snapshot = new(TrayStatus.DriverUnavailable, 0, 0);

    public TrayContext()
    {
        _pause = new PauseController(new KbdLayerDriverAccess(), new JsonPauseStateStore());
        _imeLink = new ImeLinkHost();
        _ = _ui.Handle; // このスレッド（UI スレッド）にハンドルを作っておく
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

        _timer.Tick += (_, _) => RefreshState();
        _timer.Start();
        RefreshState();
    }

    // 「レイヤー」サブメニュー（B-5a）。開くたびに、今の状態から作り直す。選ぶと、"action" の入力源でレイヤーを反転する（IME連動・規則と OR で合わさる）。
    private void RebuildLayerMenu()
    {
        _layerItem.DropDownItems.Clear();
        var keyboards = LayerMenuModel.Build(_layerWatch.Snapshot());
        _layerItem.Enabled = _snapshot.Status == TrayStatus.Running && keyboards.Count > 0;
        if (!_layerItem.Enabled) return;

        foreach (var kb in keyboards)
        {
            var target = keyboards.Count == 1 ? _layerItem : new ToolStripMenuItem(kb.Label) { Name = "TrayLayers_" + kb.UniqId.ToString("X8") };
            foreach (var item in kb.Items)
            {
                var entry = new ToolStripMenuItem($"レイヤー {item.Layer}") { Name = $"TrayLayer_{kb.UniqId:X8}_{item.Layer}", Checked = item.Checked, CheckOnClick = false };
                uint uniq = kb.UniqId;
                int layer = item.Layer;
                entry.Click += (_, _) => _imeLink.RequestLayer(new LayerRequest(uniq, 0, new[] { layer }, LayerOp.Toggle));
                target.DropDownItems.Add(entry);
            }
            if (target != _layerItem) _layerItem.DropDownItems.Add(target);
        }
    }

    private void OpenEditor() => Notify(AppLauncher.OpenOrActivate(AppLauncher.FindEditor(), "kasane"));

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

    private void RefreshState()
    {
        try
        {
            if (_imeLink.ReloadConfig()) _helper.OnConfigChanged();
            _showLayer = TrayConfig.Load().ShowLayerOnIcon;
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

    private void ExitTray()
    {
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
