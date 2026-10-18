namespace Kasane.Tray;

/// <summary>
/// 設定（<see cref="Kasane.Model.ImeSourceMode"/>）に従って、IMM32 と TSF のどちらをどの順で使うかを切り替える取得元。
/// 設定の変更は UI スレッドから <see cref="Mode"/> に入り、読むのは IME 連動のスレッド（すぐ反映される）。
/// </summary>
public sealed class ImeSourceSelector : IImeSource
{
    private readonly IImeSource _imm;
    private readonly IImeSource _tsf;
    private readonly IImeSource _immThenTsf;
    private readonly IImeSource _tsfThenImm;
    private volatile int _mode = (int)Kasane.Model.ImeSourceMode.ImmThenTsf;

    public ImeSourceSelector(IImeSource imm, IImeSource tsf)
    {
        _imm = imm;
        _tsf = tsf;
        _immThenTsf = new FallbackImeSource(imm, tsf);
        _tsfThenImm = new FallbackImeSource(tsf, imm);
    }

    public Kasane.Model.ImeSourceMode Mode
    {
        get => (Kasane.Model.ImeSourceMode)_mode;
        set => _mode = (int)value;
    }

    public ImeState Read() => Mode switch
    {
        Kasane.Model.ImeSourceMode.Imm32 => _imm.Read(),
        Kasane.Model.ImeSourceMode.TsfThenImm => _tsfThenImm.Read(),
        _ => _immThenTsf.Read(),
    };
}
