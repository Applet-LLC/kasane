namespace Kasane.Model;

/// <summary>設定画面を閉じるとき、キーボードへの反映（適用）について、利用者に何を確かめるか。</summary>
public enum UnappliedPrompt
{
    /// <summary>何も聞かずに閉じる。</summary>
    None,
    /// <summary>
    /// 一般権限（読み取り専用）。「設定は保存されたが、キーボードに反映されていない。必要に応じて管理者権限で反映してください」と知らせ、
    /// 閉じるか、取り消して戻るかを選ばせる。
    /// </summary>
    WarnNotApplied,
    /// <summary>管理者権限。反映してから閉じる／反映せずに閉じる／取り消す、を選ばせる。</summary>
    AskApply,
}

/// <summary>
/// 閉じるときの確認の判断。保存（自動）は別に済んでいる前提で、「保存したがキーボードに反映していない」ものがあるときだけ聞く。
/// 画面の部品に依存しない純粋な関数にして、単体テストできるようにしてある。
/// </summary>
public static class ClosePolicy
{
    /// <param name="unappliedCount">反映していない（接続中の）キーボードの台数。</param>
    /// <param name="driverConnected">ドライバに接続できている（未接続なら、反映のしようがないので聞かない）。</param>
    /// <param name="readOnly">管理者権限が無く、ドライバを読み取り専用で開いている。</param>
    public static UnappliedPrompt Decide(int unappliedCount, bool driverConnected, bool readOnly)
    {
        if (unappliedCount <= 0 || !driverConnected) return UnappliedPrompt.None;
        return readOnly ? UnappliedPrompt.WarnNotApplied : UnappliedPrompt.AskApply;
    }
}
