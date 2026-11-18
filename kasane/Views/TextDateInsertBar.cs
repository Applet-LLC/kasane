using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Kasane.Model;

namespace Kasane.Views;

/// <summary>
/// 「文字列を入力」の欄に、日付・時刻の置き換え({date:long} など。<see cref="TextTemplate"/>)を入れる補助ボタン。
/// 欄そのものはそのままで、記法を直接書くこともできる。挿入は、カーソル位置(選択があればそれを置き換える)。
/// 部品の Name は「欄の Name + DateTime」、メニューの項目は「欄の Name + DateTime_ + Id」(UI Automation 用)。
/// </summary>
public static class TextDateInsertBar
{
    /// <summary>メニューに出す項目(表示, 挿入する記法, Id)。空の記法は区切り。</summary>
    internal static (string Label, string Template, string Id)[] Items => new (string Label, string Template, string Id)[]
    {
        (Loc.T("短い日付", "Short date"), "{date:short}", "date_short"),
        (Loc.T("長い日付", "Long date"), "{date:long}", "date_long"),
        (Loc.T("短い時刻", "Short time"), "{time:short}", "time_short"),
        (Loc.T("長い時刻", "Long time"), "{time:long}", "time_long"),
        ("", "", ""),
        (Loc.T("年月日 (曜日) 時刻 am/pm", "Date (weekday) time am/pm"), "{now:yyyy-MM-dd (ddd) h:mm}{ampm}", "custom_weekday_ampm"),
        (Loc.T("年月日 時:分:秒", "Date and time with seconds"), "{now:yyyy-MM-dd HH:mm:ss}", "custom_seconds"),
    };

    public static Button Create(TextBox box)
    {
        var button = new Button
        {
            Name = box.Name + "DateTime",
            Content = Loc.T("日付・時刻を挿入 ▾", "Insert date/time ▾"),
            Padding = new Thickness(8, 1, 8, 1),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 3),
            ToolTip = Loc.T("キーを押した時点の日付・時刻を入力する記法を挿入します。書式は直接書き換えられます",
                            "Inserts a placeholder that types the date or time at the moment the key is pressed. You can edit the format directly"),
        };
        var menu = new ContextMenu();
        button.ContextMenu = menu;
        button.Click += (_, _) =>
        {
            Rebuild(box, menu); // 見本は、開くたびに今の時刻で作り直す
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        };
        return button;
    }

    private static void Rebuild(TextBox box, ContextMenu menu)
    {
        menu.Items.Clear();
        var now = DateTime.Now;
        var culture = TextTemplate.CurrentUserCulture();
        foreach (var (label, template, id) in Items)
        {
            if (template.Length == 0) { menu.Items.Add(new Separator()); continue; }
            string sample = TextTemplate.Expand(template, now, culture);
            var item = new MenuItem
            {
                Name = box.Name + "DateTime_" + id,
                Header = $"{label}  {sample}".Replace("_", "__"), // _ はアクセスキーの印になるので重ねる
                ToolTip = template,
            };
            string text = template;
            item.Click += (_, _) => Insert(box, text);
            menu.Items.Add(item);
        }
    }

    private static void Insert(TextBox box, string template)
    {
        int start = box.SelectionStart;
        box.SelectedText = template;
        box.CaretIndex = start + template.Length;
        box.Focus();
    }
}
