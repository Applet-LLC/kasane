namespace Kasane.Tray;

/// <summary>
/// 設定（<see cref="Kasane.Model.ImeSourceMode"/>）に従って、IMM32 と TSF のどちらをどの順で使うかを切り替える取得元。
/// 設定の変更は UI スレッドから <see cref="Mode"/> に入り、読むのは IME 連動のスレッド（すぐ反映される）。
/// どの取得元で読めたか、IME の状態がどうだったかは、変わったときだけ <c>log</c> に出す（うまく動いているかを、ログで確かめるため）。
/// </summary>
public sealed class ImeSourceSelector : IImeSource
{
    private readonly IImeSource _imm;
    private readonly IImeSource _tsf;
    private readonly Action<string>? _log;
    private readonly Func<string>? _foreground;
    private volatile int _mode = (int)Kasane.Model.ImeSourceMode.ImmThenTsf;

    // 最後にログへ書いた内容（変わったときだけ書く）。IME 連動のスレッドだけが触る。
    private bool _logged;
    private string _lastSource = "";
    private ImeState _lastState;
    private string _lastForeground = "";

    /// <param name="log">変化のログの出力先（null なら出さない）。</param>
    /// <param name="foreground">前面のアプリの名前（ログに添える。null なら添えない）。</param>
    public ImeSourceSelector(IImeSource imm, IImeSource tsf, Action<string>? log = null, Func<string>? foreground = null)
    {
        _imm = imm;
        _tsf = tsf;
        _log = log;
        _foreground = foreground;
    }

    public Kasane.Model.ImeSourceMode Mode
    {
        get => (Kasane.Model.ImeSourceMode)_mode;
        set => _mode = (int)value;
    }

    public ImeState Read()
    {
        ImeState state;
        string source;
        switch (Mode)
        {
            case Kasane.Model.ImeSourceMode.Imm32:
                state = _imm.Read();
                source = "IMM32";
                break;
            case Kasane.Model.ImeSourceMode.TsfThenImm:
                state = Try(_tsf, "TSF", _imm, "IMM32", out source);
                break;
            default:
                state = Try(_imm, "IMM32", _tsf, "TSF", out source);
                break;
        }
        if (_log != null) LogIfChanged(source, state);
        return state;
    }

    // 先が読めたらそれを使い、読めなかった（Known でない）ときだけ後を試す。どちらも読めなければ Unknown（source は "-"）。
    private static ImeState Try(IImeSource first, string firstName, IImeSource second, string secondName, out string source)
    {
        var state = first.Read();
        if (state.Known) { source = firstName; return state; }
        state = second.Read();
        source = state.Known ? secondName : "-";
        return state;
    }

    private void LogIfChanged(string source, ImeState state)
    {
        string foreground = _foreground?.Invoke() ?? "";
        if (_logged && source == _lastSource && state == _lastState && foreground == _lastForeground) return;
        _logged = true;
        _lastSource = source;
        _lastState = state;
        _lastForeground = foreground;
        _log!($"IME: {source} {Describe(state)}" + (foreground.Length > 0 ? $" [{foreground}]" : ""));
    }

    // IME_CMODE_ROMAN。立っていればローマ字入力、立っていなければかな入力（キーボードの「かな」で打つ方式）。
    private const uint CModeRoman = 0x10;

    /// <summary>ログ用の文字列。「ON ローマ字入力」「ON カナ入力」「OFF」「取得できず」など。</summary>
    public static string Describe(ImeState state)
    {
        if (!state.Known) return "取得できず";
        if (!state.Open) return "OFF";
        string method = (state.ConversionMode & CModeRoman) != 0 ? "ローマ字入力" : "カナ入力";
        string mode = (state.ConversionMode & ImeConversion.Native) != 0 ? "日本語" : "半角英数";
        return $"ON {method}（{mode}、変換モード 0x{state.ConversionMode:X}）";
    }
}
