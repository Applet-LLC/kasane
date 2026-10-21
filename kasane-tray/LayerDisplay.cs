using Kasane.Interop;

namespace Kasane.Tray;

/// <summary>トレイに出すレイヤーの表示。<see cref="Badge"/> はアイコンに重ねる1文字（1〜9、10以上は A〜F）、<see cref="Text"/> はツールチップに足す文。</summary>
public sealed record LayerBadge(int Layer, char Badge, string Text);

/// <summary>
/// 状態（キーボードごとの <see cref="LayerState"/>）から、トレイの表示を決める純粋なロジック。
///  - レイヤーマップがONのキーボードだけを見る。有効なレイヤー（EffectiveLayerMask）の最上位が 0（基底のみ）なら無表示（null）。
///  - 複数台なら、最上位のレイヤーが最も高い台を採る（同じなら UniqId の小さい台）。
/// </summary>
public static class LayerDisplay
{
    public const int TooltipMax = 127; // NotifyIcon.Text の上限

    public static LayerBadge? Compute(IReadOnlyDictionary<uint, LayerState> states)
    {
        int best = 0;
        bool nicola = false;
        foreach (var (_, s) in states.OrderBy(p => p.Key))
        {
            if (!s.LayerMapEnabled) continue;
            int top = TopLayer(s.EffectiveLayerMask);
            if (top > best) { best = top; nicola = s.NicolaActive; }
        }
        if (best == 0) return null;
        char badge = best < 10 ? (char)('0' + best) : (char)('A' + best - 10);
        return new LayerBadge(best, badge, $"レイヤー {best}" + (nicola ? "(NICOLA)" : ""));
    }

    /// <summary>立っているビットのうち最も上位のレイヤー番号（0〜15）。何も立っていなければ 0。</summary>
    public static int TopLayer(uint mask)
    {
        mask &= (1u << KbdLayerClient.MaxLayers) - 1;
        return mask == 0 ? 0 : 31 - System.Numerics.BitOperations.LeadingZeroCount(mask);
    }

    /// <summary>ツールチップに追記して、127文字に収める（先頭の <c>kasane</c> は残す）。</summary>
    public static string AppendToTooltip(string tooltip, LayerBadge? badge)
    {
        string text = badge == null ? tooltip : tooltip + "・" + badge.Text;
        return text.Length <= TooltipMax ? text : text[..TooltipMax];
    }
}
