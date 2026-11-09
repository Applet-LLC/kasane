using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Kasane.Views;

/// <summary>状態の種類（2026-11-09、UI の状態表示の見直し）。ステータス欄のメッセージ・検証の文・状態の印で、色と印を揃える。</summary>
public enum StatusKind
{
    /// <summary>情報（操作の結果など）。</summary>
    Info,
    /// <summary>注意（できなかったこと、気をつけること）。</summary>
    Warn,
    /// <summary>エラー（失敗）。</summary>
    Error,
    /// <summary>問題なし（検証が通ったとき）。</summary>
    Ok,
}

/// <summary>状態の種類ごとの印（文字）と色。色は<see cref="AppTheme.Palette"/>（ライト・ダークで別）。</summary>
public static class StatusStyle
{
    public static string Icon(StatusKind kind) => kind switch
    {
        StatusKind.Warn => "⚠",   // ⚠
        StatusKind.Error => "✖",  // ✖
        StatusKind.Ok => "✓",     // ✓
        _ => "ℹ",                 // ℹ
    };

    public static Brush Brush(StatusKind kind) => kind switch
    {
        StatusKind.Warn => AppTheme.Palette.WarnText,
        StatusKind.Error => AppTheme.Palette.ErrorText,
        StatusKind.Ok => AppTheme.Palette.OkText,
        _ => AppTheme.Palette.InfoText,
    };

    /// <summary>文の頭に印を付ける（検証の文など、印を別の部品にできないとき）。空の文には付けない。</summary>
    public static string WithIcon(StatusKind kind, string text) => text.Length == 0 ? text : Icon(kind) + " " + text;

    /// <summary>
    /// 状態の印（色の付いた丸と短い語）を 1 つ作る。tooltip に、詳しい文を入れる。
    /// <paramref name="automationId"/> は UI Automation 用（<c>Name</c> に語が出る）。
    /// </summary>
    public static Border Chip(string automationId, string text, Brush color, string? tooltip)
    {
        var dot = new Ellipse { Width = 9, Height = 9, Fill = color, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) };
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(dot);
        row.Children.Add(label);
        var chip = new ChipBorder
        {
            Child = row,
            BorderBrush = color,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(7, 1, 8, 1),
            Margin = new Thickness(0, 0, 6, 0),
            ToolTip = tooltip,
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(chip, automationId);
        System.Windows.Automation.AutomationProperties.SetName(chip, text);
        return chip;
    }
}

/// <summary>状態の印の枠。UI Automation から、名前つきの要素として見える（試験・支援技術用）。</summary>
public sealed class ChipBorder : Border
{
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new ChipPeer(this);

    private sealed class ChipPeer : System.Windows.Automation.Peers.FrameworkElementAutomationPeer
    {
        public ChipPeer(ChipBorder owner) : base(owner) { }
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
        protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() => System.Windows.Automation.Peers.AutomationControlType.Text;
    }
}
