using Kasane.Interop;
using Kasane.Model;

namespace Kasane.Views;

/// <summary>
/// 番号で選ぶものの選択肢（レイヤー・マクロ・タップダンス）。名前を付けてあれば「3: 名前」、無ければ「3」と出す。
/// 値は番号（Number）。名前の無いものは、これまでと同じ「3」の表示。
/// </summary>
public sealed record NamedChoice(int Number, string? Name)
{
    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Number.ToString() : $"{Number}: {Name}";

    public static List<NamedChoice> Layers(KeymapProject? project) =>
        Enumerable.Range(0, KbdLayerClient.MaxLayers)
            .Select(i => new NamedChoice(i, project == null ? null : ProjectService.LayerName(project, i))).ToList();

    public static List<NamedChoice> Macros(KeymapProject? project) => FromNames(KbdLayerClient.MaxMacroCount, project?.MacroNames);

    public static List<NamedChoice> TapDances(KeymapProject? project) => FromNames(KbdLayerClient.MaxTapDanceCount, project?.TapDanceNames);

    private static List<NamedChoice> FromNames(int count, Dictionary<int, string>? names) =>
        Enumerable.Range(0, count)
            .Select(i => new NamedChoice(i, names != null && names.TryGetValue(i, out var n) ? n : null)).ToList();
}
