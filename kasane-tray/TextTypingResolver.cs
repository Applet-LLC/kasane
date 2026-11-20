using Kasane.Model;

namespace Kasane.Tray;

/// <summary>
/// ヘルパー動作の文字の入力の速さを、動作ごとに決める（2026-11-20。<see cref="TextTyping.Resolve"/>）。
/// 設定にレイヤーごとの上書きがあるときだけ、そのキーボードでいま有効なレイヤーをドライバから読む（1 回の動作に 1 回）。
/// トリガー（キーフック）の動作は UniqId が分からないので、キーボードの識別（上位 16 ビット）が合うものを探す。
/// ドライバを読めなければ、レイヤーの上書きなしで決める（失敗は、続けて同じ理由なら 1 回だけ記録する）。
/// </summary>
public sealed class TextTypingResolver
{
    private readonly Func<HelperConfigSet> _config;
    private readonly Func<IReadOnlyList<uint>> _keyboards;
    private readonly Func<uint, uint> _effectiveLayers;
    private readonly Action<string> _log;
    private string? _lastError;

    /// <param name="config">いまのヘルパー設定（不変のスナップショット）。</param>
    /// <param name="keyboards">レイヤーマップのあるキーボードの UniqId（ドライバに繋がらなければ例外）。</param>
    /// <param name="effectiveLayers">そのキーボードで有効なレイヤーのマスク（ドライバに繋がらなければ例外）。</param>
    public TextTypingResolver(Func<HelperConfigSet> config, Func<IReadOnlyList<uint>> keyboards, Func<uint, uint> effectiveLayers, Action<string> log)
    {
        _config = config;
        _keyboards = keyboards;
        _effectiveLayers = effectiveLayers;
        _log = log;
    }

    public TextTypingProfile Resolve(ActionRequest r)
    {
        var set = _config();
        bool needsDevice = set.HasAnyTextTypingLayers || set.PerDevice.Values.Any(c => c.TextTyping != null);
        if (!needsDevice) return set.TextTypingFor(0, null);

        uint uniqId = r.UniqId;
        uint? mask = null;
        try
        {
            if (uniqId == 0) uniqId = FindByHash(r.DeviceHash16);
            if (uniqId != 0 && set.HasAnyTextTypingLayers) mask = _effectiveLayers(uniqId);
            _lastError = null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Kasane.Interop.KbdLayerException)
        {
            if (_lastError != ex.Message)
            {
                _lastError = ex.Message;
                _log(Loc.T($"文字入力の速さ: 有効なレイヤーを読めないため、レイヤーごとの設定を使わずに入力します({ex.Message})",
                           $"Typing speed: could not read the active layers, so per-layer settings are not used ({ex.Message})"));
            }
        }
        return set.TextTypingFor(uniqId, mask);
    }

    private uint FindByHash(ushort hash16)
    {
        if (hash16 == 0) return 0;
        foreach (var id in _keyboards())
            if ((ushort)(id >> 16) == hash16) return id;
        return 0;
    }
}
