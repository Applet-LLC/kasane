using System.Windows;
using System.Windows.Controls;
using Kasane.Model;

namespace Kasane.Views;

/// <summary>
/// 設定画面の「IME連動」タブ（2026-10-19）。IME の ON/OFF に合わせて、アプリ管理レイヤーを立てる・下ろす（NICOLA とは無関係）。
/// 対象は「このキーボード」か「全キーボード共通」（キーボードごとの設定が先。<see cref="HelperConfigSet.ImeLinkFor"/>）。
/// 編集は <see cref="HelperSettingsModel"/> のコピーに対して行い、書くのは設定画面の OK のとき。トレイが数秒で読み直す。
/// </summary>
internal sealed class ImeLayerTab : HelperTabBase
{
    private readonly CheckBox _enabled = new() { Name = "ImeLayerEnabledCheck", Content = "IME の ON/OFF に合わせて、レイヤーを立てる", Margin = new Thickness(0, 0, 0, 6) };
    private readonly CheckBox[] _layers = new CheckBox[16];
    private readonly RadioButton _modeOn = new() { Name = "ImeLayerModeOn", Content = "IME が ON のとき(Microsoft IME では、半角英数のモードは OFF と同じ扱いです)", GroupName = "ImeLayerMode", Margin = new Thickness(0, 2, 0, 2) };
    private readonly RadioButton _modeKana = new() { Name = "ImeLayerModeKana", Content = "かな入力のときだけ(IME が ON で、日本語の入力モード)", GroupName = "ImeLayerMode", Margin = new Thickness(0, 2, 0, 2) };
    private readonly Button _clear = Btn("ImeLayerClearButton", "この範囲の設定を消す", 150);
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.Palette.SubText, Margin = new Thickness(0, 6, 0, 0) };
    private bool _loading;

    public ImeLayerTab(HelperSettingsModel model, Window owner)
        : base(model, owner, "ImeLayer",
            "IME の ON/OFF に合わせて、選んだレイヤーを自動で立てます(OFF のときは下ろします)。" +
            "「このキーボード」の設定が「全キーボード共通」より先に使われ、変更は OK で保存されます。")
    {
        Stack.Children.Add(_enabled);

        var wrap = new WrapPanel { Margin = new Thickness(0, 2, 0, 4) };
        for (int i = 0; i < _layers.Length; i++)
        {
            _layers[i] = new CheckBox { Name = $"ImeLayer{i}", Content = i.ToString(), Width = 54, Margin = new Thickness(0, 2, 0, 2) };
            _layers[i].Click += (_, _) => Apply();
            wrap.Children.Add(_layers[i]);
        }
        Stack.Children.Add(new TextBlock { Text = "立てるレイヤー:", Margin = new Thickness(0, 6, 0, 2) });
        Stack.Children.Add(wrap);

        Stack.Children.Add(new TextBlock { Text = "立てる条件:", Margin = new Thickness(0, 8, 0, 2) });
        Stack.Children.Add(_modeOn);
        Stack.Children.Add(_modeKana);
        Stack.Children.Add(Buttons(_clear));
        Stack.Children.Add(_hint);

        _enabled.Click += (_, _) => Apply();
        _modeOn.Click += (_, _) => Apply();
        _modeKana.Click += (_, _) => Apply();
        _clear.Click += (_, _) =>
        {
            Config.ImeLayers = null;
            Refresh();
        };
        Init();
    }

    protected override void Refresh()
    {
        _loading = true;
        var link = Config.ImeLayers;
        _enabled.IsChecked = link?.Enabled ?? false;
        for (int i = 0; i < _layers.Length; i++) _layers[i].IsChecked = link?.Layers.Contains(i) ?? false;
        (link?.KanaOnly == true ? _modeKana : _modeOn).IsChecked = true;
        bool editable = Editable;
        _enabled.IsEnabled = _modeOn.IsEnabled = _modeKana.IsEnabled = editable;
        foreach (var c in _layers) c.IsEnabled = editable;
        _clear.IsEnabled = editable && link != null;
        _hint.Text = link == null
            ? (Scope == HelperScope.Device ? "このキーボードには、設定がありません(共通の設定があれば、それに従います)。" : "共通の設定がありません。")
            : (link.Enabled ? "" : "この範囲では、IME 連動をしません。");
        _loading = false;
    }

    // 画面の内容を、編集中の設定に写す（何も選んでいない・チェックも外れていて、元も無い設定のときは、設定を作らない）。
    private void Apply()
    {
        if (_loading || !Editable) return;
        var layers = Enumerable.Range(0, _layers.Length).Where(i => _layers[i].IsChecked == true).ToList();
        Config.ImeLayers = new ImeLayerLink
        {
            Enabled = _enabled.IsChecked == true,
            Layers = layers,
            Mode = _modeKana.IsChecked == true ? ImeLayerLink.ModeKana : ImeLayerLink.ModeOn,
        };
        Refresh();
    }
}
