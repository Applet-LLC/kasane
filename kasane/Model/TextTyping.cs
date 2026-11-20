using System.Text.Json.Serialization;

namespace Kasane.Model;

/// <summary>
/// 文字の入力（Text と、Sequence の Text 手順）の速さの値（2026-11-20）。どれも省略でき、省略した値は、別の設定の値を使う
/// （<see cref="TextTyping.Resolve"/>）。
/// </summary>
public class TextTypingValues
{
    /// <summary>1 文字ごとの間隔に、0〜<see cref="JitterMs"/> ms の揺らぎを足すか。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Jitter { get; set; }

    /// <summary>揺らぎの範囲（ms。<see cref="TextTyping.MinJitterMs"/>〜<see cref="TextTyping.MaxJitterMs"/>）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? JitterMs { get; set; }

    /// <summary>1 文字ごとの間隔（ms）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CharMs { get; set; }

    /// <summary>文字を送ったあと、IME を操作する（切り替える・元に戻す）前に待つ時間（ms。1 回だけ。設定画面では「IME 操作前のウェイト」）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ImeWaitMs { get; set; }

    [JsonIgnore]
    public bool HasAnyValue => Jitter != null || JitterMs != null || CharMs != null || ImeWaitMs != null;
}

/// <summary>レイヤー番号ごとの上書き（そのレイヤーが有効なときに使う）。</summary>
public sealed class TextTypingLayer : TextTypingValues
{
    public int Layer { get; set; }
}

/// <summary>
/// <see cref="HelperConfig.TextTyping"/>。全キーボード共通（tray.json の Helper）と、キーボードごと（helper.json）に置ける。
/// 自身の値は、そのスコープの既定。<see cref="Layers"/> は、レイヤー番号ごとの上書き。
/// </summary>
public sealed class TextTypingConfig : TextTypingValues
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TextTypingLayer>? Layers { get; set; }

    [JsonIgnore]
    public bool IsEmpty => !HasAnyValue && (Layers == null || Layers.Count == 0);

    public TextTypingLayer? LayerOrNull(int layer) => Layers?.FirstOrDefault(l => l.Layer == layer);
}

/// <summary>1 回の動作で使う、決まった値。</summary>
public readonly record struct TextTypingProfile(bool Jitter, int JitterMs, int CharMs, int ImeWaitMs)
{
    /// <summary>内蔵の既定（揺らぎ 追加・範囲 10 ms、間隔 30 ms、IME 操作前のウェイト 20 ms）。</summary>
    public static readonly TextTypingProfile Default = new(true, 10, 30, 20);

    /// <summary>1 文字ごとに待つ時間。<paramref name="next"/> は 0 以上 maxExclusive 未満の乱数を返す関数。</summary>
    public int CharDelay(Func<int, int> next) => CharMs + (Jitter && JitterMs > 0 ? next(JitterMs + 1) : 0);
}

public static class TextTyping
{
    public const int MinJitterMs = 1, MaxJitterMs = 10;
    public const int MaxCharMs = 300;
    public const int MaxImeWaitMs = 3000;
    public const int MaxLayers = 16;

    /// <summary>
    /// その時点で使う値を決める。値ごとに、次の順で最初に見つかったものを使う。
    ///  1. 有効なレイヤー（<paramref name="activeLayerMask"/>）を番号の大きい順に見て、キーボードごと → 共通の、そのレイヤーの上書き
    ///     （キーマップと同じく、上位のレイヤーが優先。値を書いていないレイヤーは透過）
    ///  2. キーボードごとの既定 → 共通の既定 → 内蔵の既定（<see cref="TextTypingProfile.Default"/>）
    /// <paramref name="activeLayerMask"/> が null（レイヤーを読めない）なら、レイヤーの上書きは使わない。
    /// </summary>
    public static TextTypingProfile Resolve(TextTypingConfig? global, TextTypingConfig? device, uint? activeLayerMask)
    {
        var chain = new List<TextTypingValues>();
        if (activeLayerMask is uint mask)
        {
            for (int layer = MaxLayers - 1; layer >= 0; layer--)
            {
                if ((mask & (1u << layer)) == 0) continue;
                if (device?.LayerOrNull(layer) is { } d) chain.Add(d);
                if (global?.LayerOrNull(layer) is { } g) chain.Add(g);
            }
        }
        if (device != null) chain.Add(device);
        if (global != null) chain.Add(global);

        var def = TextTypingProfile.Default;
        return new TextTypingProfile(
            chain.Select(v => v.Jitter).FirstOrDefault(v => v != null) ?? def.Jitter,
            Math.Clamp(chain.Select(v => v.JitterMs).FirstOrDefault(v => v != null) ?? def.JitterMs, MinJitterMs, MaxJitterMs),
            Math.Clamp(chain.Select(v => v.CharMs).FirstOrDefault(v => v != null) ?? def.CharMs, 0, MaxCharMs),
            Math.Clamp(chain.Select(v => v.ImeWaitMs).FirstOrDefault(v => v != null) ?? def.ImeWaitMs, 0, MaxImeWaitMs));
    }

    /// <summary>設定の誤り。<paramref name="label"/> は「共通」「このキーボード」。</summary>
    public static void Validate(TextTypingConfig? c, string label, List<string> errors)
    {
        if (c == null) return;
        string owner = Loc.T($"{label}の文字入力の速さ", $"Typing speed ({label})");
        ValidateValues(c, owner, errors);
        var seen = new HashSet<int>();
        foreach (var l in c.Layers ?? new())
        {
            string lo = Loc.T($"{owner}(レイヤー {l.Layer})", $"{owner} (layer {l.Layer})");
            if (l.Layer is < 0 or >= MaxLayers) errors.Add(Loc.T($"{lo}: レイヤーは 0〜15 です", $"{lo}: the layer must be between 0 and 15"));
            else if (!seen.Add(l.Layer)) errors.Add(Loc.T($"{lo}: 同じレイヤーが重複しています", $"{lo}: the same layer appears twice"));
            ValidateValues(l, lo, errors);
        }
    }

    private static void ValidateValues(TextTypingValues v, string owner, List<string> errors)
    {
        if (v.JitterMs is int j && (j < MinJitterMs || j > MaxJitterMs))
            errors.Add(Loc.T($"{owner}: 揺らぎの範囲は {MinJitterMs}〜{MaxJitterMs} ms です", $"{owner}: the jitter range must be {MinJitterMs} to {MaxJitterMs} ms"));
        if (v.CharMs is int c && (c < 0 || c > MaxCharMs))
            errors.Add(Loc.T($"{owner}: 1 文字ごとの間隔は 0〜{MaxCharMs} ms です", $"{owner}: the interval between characters must be 0 to {MaxCharMs} ms"));
        if (v.ImeWaitMs is int w && (w < 0 || w > MaxImeWaitMs))
            errors.Add(Loc.T($"{owner}: IME 操作前のウェイトは 0〜{MaxImeWaitMs} ms です", $"{owner}: the wait before IME operations must be 0 to {MaxImeWaitMs} ms"));
    }
}
