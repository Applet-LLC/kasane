using System.Runtime.InteropServices;
using Kasane.Model;

namespace Kasane.Tray;

/// <summary>
/// トレイの色のモード（ライト／ダーク。tray.json の ColorMode。kasane の設定の「全般」タブで選ぶ。2026-10-21）。
/// 右クリックのメニューと、バージョン情報のダイアログに効く。モードは、メニューを開くたび・ダイアログを開くたびに読む
/// （設定を変えたら、次に開いたときから変わる）。ライトは何もしない（Windows の既定の見た目）。
/// </summary>
internal static class TrayTheme
{
    public static readonly Color Window = Color.FromArgb(0x1F, 0x20, 0x23);
    public static readonly Color Panel = Color.FromArgb(0x2B, 0x2D, 0x30);
    public static readonly Color Control = Color.FromArgb(0x38, 0x3B, 0x40);
    public static readonly Color Hover = Color.FromArgb(0x44, 0x48, 0x4E);
    public static readonly Color Border = Color.FromArgb(0x6A, 0x6F, 0x77);
    public static readonly Color Text = Color.FromArgb(0xE8, 0xE8, 0xE8);
    public static readonly Color TextDim = Color.FromArgb(0xA3, 0xA7, 0xAE);
    public static readonly Color TextDisabled = Color.FromArgb(0x6F, 0x73, 0x7A);
    public static readonly Color Selection = Color.FromArgb(0x2D, 0x5F, 0x9A);
    public static readonly Color Link = Color.FromArgb(0x7D, 0xB4, 0xF2);

    /// <summary>今のモードがダークか（tray.json を読む）。</summary>
    public static bool IsDark => ColorModeStore.Read() == ColorMode.Dark;

    // ---- メニュー ----

    /// <summary>メニューを開く直前に呼ぶ。ダークなら、暗いメニューにする（サブメニューも）。ライトなら、既定に戻す。</summary>
    public static void Apply(ContextMenuStrip menu)
    {
        bool dark = IsDark;
        if (dark)
        {
            menu.Renderer = new DarkRenderer();
            menu.BackColor = Panel;
            menu.ForeColor = Text;
        }
        else
        {
            menu.RenderMode = ToolStripRenderMode.ManagerRenderMode;
            menu.ResetBackColor();
            menu.ResetForeColor();
        }
        ApplyToDropDowns(menu.Items, dark);
    }

    // サブメニュー（レイヤーなど）。親のメニューの色を引き継がないので、ひとつずつ。
    private static void ApplyToDropDowns(ToolStripItemCollection items, bool dark)
    {
        foreach (ToolStripItem item in items)
        {
            if (item is not ToolStripDropDownItem { HasDropDownItems: true } parent) continue;
            var drop = parent.DropDown;
            if (dark)
            {
                drop.Renderer = new DarkRenderer();
                drop.BackColor = Panel;
                drop.ForeColor = Text;
            }
            else
            {
                drop.RenderMode = ToolStripRenderMode.ManagerRenderMode;
                drop.ResetBackColor();
                drop.ResetForeColor();
            }
            ApplyToDropDowns(drop.Items, dark);
        }
    }

    private sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Text : TextDisabled;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Text;
            base.OnRenderArrow(e);
        }
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        public DarkColors() => UseSystemColors = false;

        public override Color ToolStripDropDownBackground => Panel;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Selection;
        public override Color MenuItemSelected => Selection;
        public override Color MenuItemSelectedGradientBegin => Selection;
        public override Color MenuItemSelectedGradientEnd => Selection;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientMiddle => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color ImageMarginGradientBegin => Panel;
        public override Color ImageMarginGradientMiddle => Panel;
        public override Color ImageMarginGradientEnd => Panel;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
        public override Color CheckBackground => Selection;
        public override Color CheckSelectedBackground => Selection;
        public override Color CheckPressedBackground => Selection;
    }

    // ---- ダイアログ（バージョン情報） ----

    /// <summary>ダークなら、フォームとその中のコントロールを暗くする（ライトは何もしない）。タイトルバーも暗くする。</summary>
    public static void Apply(Form form)
    {
        if (!IsDark) return;
        form.BackColor = Window;
        form.ForeColor = Text;
        foreach (Control c in form.Controls) Style(c);
        form.HandleCreated += (_, _) => SetDarkTitleBar(form.Handle);
        if (form.IsHandleCreated) SetDarkTitleBar(form.Handle);
    }

    private static void Style(Control c)
    {
        switch (c)
        {
            case Button b:
                b.FlatStyle = FlatStyle.Flat;
                b.BackColor = Control;
                b.ForeColor = Text;
                b.FlatAppearance.BorderColor = Border;
                b.FlatAppearance.MouseOverBackColor = Hover;
                break;
            case LinkLabel l:
                l.LinkColor = Link;
                l.ActiveLinkColor = Text;
                l.VisitedLinkColor = Link;
                l.BackColor = Window;
                break;
            case TextBox t:
                t.BackColor = Window;
                t.ForeColor = TextDim;
                break;
            case Label l:
                // 薄い色（補足）として作ったラベルは、薄いまま（暗い背景で読める色に）。
                l.ForeColor = l.ForeColor == SystemColors.GrayText ? TextDim : Text;
                l.BackColor = Color.Transparent;
                break;
        }
        foreach (Control child in c.Controls) Style(child);
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static void SetDarkTitleBar(IntPtr hwnd)
    {
        try
        {
            int value = 1;
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref value, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // タイトルバーが暗くならないだけ
        }
    }
}
