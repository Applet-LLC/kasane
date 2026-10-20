using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Kasane.Model;

namespace Kasane.Views;

/// <summary>
/// 色のモード（ライト／ダーク。<see cref="ColorMode"/>。設定の「全般」タブ。tray.json の ColorMode。既定はライト。2026-10-21）。
/// ライトは、以前の見た目のまま（何も足さない）。ダークは、<c>Themes/Dark.xaml</c>（コントロールの見た目と色）を
/// <see cref="Application.Resources"/> に足し、ウィンドウの背景・文字色とタイトルバー（Windows の機能）を暗くする。
/// コードで直接決めている色（キーの色など）は、<see cref="Palette"/> からモードに合わせて取る。
/// モードを変えたら <see cref="Changed"/> が起こる（キー図など、描き直しが要る画面が購読する）。
/// 開いている画面は、リソースを参照している部分（コントロールの色）がすぐ変わる。コードで色を決めた部分は、描き直したときに変わる。
/// </summary>
public static class AppTheme
{
    /// <summary>モードごとの、コードで決める色。ライトは、これまで直接書いていた値と同じ。</summary>
    public sealed class PaletteSet
    {
        public required Brush KeyDefault { get; init; }
        public required Brush KeyBorder { get; init; }
        public required Brush KeyText { get; init; }
        public required Brush KeyChanged { get; init; }
        public required Brush KeyAction { get; init; }
        public required Brush KeyTransparent { get; init; }
        public required Brush KeyTransparentText { get; init; }
        public required Brush KeyMeasuredBorder { get; init; }
        public required Brush KeyUnknownBorder { get; init; }
        public required Brush KeySelected { get; init; }
        public required Brush KeySelectedBorder { get; init; }
        public required Brush KeyDisabledText { get; init; }
        public required Brush ReportBackground { get; init; }
        public required Brush StrongText { get; init; }
        public required Brush SubText { get; init; }
        public required Brush OkText { get; init; }
        public required Brush CheckFaceOff { get; init; }
        public required Brush CheckFaceOn { get; init; }
        public required Brush CalibCurrent { get; init; }
        public required Brush CalibDone { get; init; }
        public required Brush CalibTarget { get; init; }
        public required Brush CalibDown { get; init; }
        public required Brush KleConfirmed { get; init; }
        public required Brush KleInferred { get; init; }
        public required Brush KleUnknown { get; init; }
    }

    private static SolidColorBrush Rgb(int rgb)
    {
        var b = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        b.Freeze();
        return b;
    }

    private static readonly PaletteSet LightPalette = new()
    {
        KeyDefault = Rgb(0xF4F4F4), KeyBorder = Rgb(0x9A9A9A), KeyText = Brushes.Black,
        KeyChanged = Rgb(0xCFE8FF), KeyAction = Rgb(0xD6F5D6), KeyTransparent = Rgb(0xFBFBFB), KeyTransparentText = Brushes.Silver,
        KeyMeasuredBorder = Rgb(0xF08C00), KeyUnknownBorder = Rgb(0xD02020), KeySelected = Rgb(0xBFDBFF), KeySelectedBorder = Brushes.SteelBlue,
        KeyDisabledText = Brushes.Silver,
        ReportBackground = Rgb(0xF7F7F7), StrongText = Brushes.Black, SubText = Brushes.DimGray, OkText = Brushes.DarkGreen,
        CheckFaceOff = Brushes.WhiteSmoke, CheckFaceOn = Brushes.LightGreen,
        CalibCurrent = Rgb(0xFFE066), CalibDone = Rgb(0xC8EEC8), CalibTarget = Rgb(0xFFFFFF), CalibDown = Rgb(0xFF9F43),
        KleConfirmed = Rgb(0xD5EED5), KleInferred = Rgb(0xFFF1B8), KleUnknown = Rgb(0xF6C9C9),
    };

    private static readonly PaletteSet DarkPalette = new()
    {
        KeyDefault = Rgb(0x3A3D41), KeyBorder = Rgb(0x6E7178), KeyText = Rgb(0xEAEAEA),
        KeyChanged = Rgb(0x1F4E79), KeyAction = Rgb(0x2A5A33), KeyTransparent = Rgb(0x2B2D30), KeyTransparentText = Rgb(0x777B82),
        KeyMeasuredBorder = Rgb(0xF08C00), KeyUnknownBorder = Rgb(0xE04848), KeySelected = Rgb(0x2D5F9A), KeySelectedBorder = Rgb(0x6CA6E8),
        KeyDisabledText = Rgb(0x666A70),
        ReportBackground = Rgb(0x26282B), StrongText = Rgb(0xF0F0F0), SubText = Rgb(0xA3A7AE), OkText = Rgb(0x7FD38A),
        CheckFaceOff = Rgb(0x3A3D41), CheckFaceOn = Rgb(0x2F6B3A),
        CalibCurrent = Rgb(0x8A6D00), CalibDone = Rgb(0x2F5B35), CalibTarget = Rgb(0x2B2D30), CalibDown = Rgb(0xB3590F),
        KleConfirmed = Rgb(0x2F5B35), KleInferred = Rgb(0x6B5A12), KleUnknown = Rgb(0x6B2B2B),
    };

    private static ResourceDictionary? _darkDictionary;
    private static bool _initialized;

    /// <summary>今のモード。</summary>
    public static ColorMode Mode { get; private set; } = ColorMode.Light;

    /// <summary>コードで決める色（今のモードのもの）。</summary>
    public static PaletteSet Palette => Mode == ColorMode.Dark ? DarkPalette : LightPalette;

    /// <summary>モードが変わった（<see cref="Apply"/>）。</summary>
    public static event Action? Changed;

    /// <summary>起動時に1回（最初の画面を作る前に）。保存されたモードを適用し、これから開く画面にも反映するよう登録する。</summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => StyleWindow((Window)s)));
        Apply(TrayConfig.Load().ColorModeValue);
    }

    /// <summary>モードを切り替える。同じモードなら何もしない。</summary>
    public static void Apply(ColorMode mode)
    {
        var app = Application.Current;
        if (app == null) { Mode = mode; return; }
        bool changed = mode != Mode || (mode == ColorMode.Dark && _darkDictionary == null);
        Mode = mode;

        var merged = app.Resources.MergedDictionaries;
        if (mode == ColorMode.Dark)
        {
            _darkDictionary ??= new ResourceDictionary { Source = new Uri("pack://application:,,,/Themes/Dark.xaml", UriKind.Absolute) };
            if (!merged.Contains(_darkDictionary)) merged.Add(_darkDictionary);
        }
        else if (_darkDictionary != null)
        {
            merged.Remove(_darkDictionary);
        }

        foreach (Window w in app.Windows) StyleWindow(w);
        if (changed) Changed?.Invoke();
    }

    // ウィンドウの背景・文字色（明示していないもの）と、タイトルバー。ダークのときだけ変える。ライトは、元の既定に戻す。
    private static void StyleWindow(Window w)
    {
        if (Mode == ColorMode.Dark)
        {
            w.SetResourceReference(Control.BackgroundProperty, "Theme.WindowBackground");
            w.SetResourceReference(Control.ForegroundProperty, "Theme.Text");
        }
        else
        {
            w.ClearValue(Control.BackgroundProperty);
            w.ClearValue(Control.ForegroundProperty);
        }
        SetDarkTitleBar(w, Mode == ColorMode.Dark);
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // タイトルバーを暗く（Windows 10 20H1 以降・11。対応していなければ何も起きない）。
    private static void SetDarkTitleBar(Window w, bool dark)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(w).EnsureHandle();
            int value = dark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref value, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            // タイトルバーが暗くならないだけ
        }
    }
}
