namespace Kasane.Model;

/// <summary>キーの押下・解放を渡した結果。<see cref="Slot"/> が null なら、どのキーにも対応しないスキャンコード。</summary>
public readonly record struct InputCheckResult(ushort Scan, int? Slot, bool Changed);

/// <summary>
/// キャリブレーションの「入力確認モード」の状態: いま押しているキー、一度でも押したキー（確認済み）。
/// スキャンコード（Enc16。拡張キーは 0xE0xx）からキー（スロット番号）への対応を持ち、押下・解放を渡すと状態を更新する。
/// 画面の部品に依存しない純粋なクラスにして、単体テストできるようにしてある。
/// </summary>
public sealed class InputCheckModel
{
    private Dictionary<ushort, int> _map;
    private readonly HashSet<int> _down = new();
    private readonly HashSet<int> _checked = new();

    public InputCheckModel(IReadOnlyDictionary<ushort, int> scanToSlot)
    {
        _map = new Dictionary<ushort, int>(scanToSlot);
    }

    /// <summary>対応を差し替える（実測値が変わったとき）。押下中・確認済みの状態は残す。</summary>
    public void SetMap(IReadOnlyDictionary<ushort, int> scanToSlot) => _map = new Dictionary<ushort, int>(scanToSlot);

    public bool IsDown(int slot) => _down.Contains(slot);
    public bool IsChecked(int slot) => _checked.Contains(slot);
    public int CheckedCount => _checked.Count;

    /// <summary>
    /// 押下（<paramref name="isDown"/> = true）か解放を渡す。オートリピートの押下は、状態が変わらないので Changed = false。
    /// 対応しないスキャンコードは Slot = null（状態は変わらない）。
    /// </summary>
    public InputCheckResult OnKey(ushort scan, bool isDown)
    {
        if (!_map.TryGetValue(scan, out int slot)) return new InputCheckResult(scan, null, false);
        bool changed;
        if (isDown)
        {
            changed = _down.Add(slot);
            changed |= _checked.Add(slot);
        }
        else
        {
            changed = _down.Remove(slot);
        }
        return new InputCheckResult(scan, slot, changed);
    }

    /// <summary>押している状態を全部解く（ウィンドウが非アクティブになって、解放が見えなくなるとき）。色を戻すキーを返す。</summary>
    public IReadOnlyList<int> ReleaseAll()
    {
        var released = _down.ToList();
        _down.Clear();
        return released;
    }

    /// <summary>確認済みを消す。色を戻すキー（確認済みだったもの）を返す。</summary>
    public IReadOnlyList<int> ResetChecked()
    {
        var was = _checked.ToList();
        _checked.Clear();
        return was;
    }
}
