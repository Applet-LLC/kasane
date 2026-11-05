namespace Kasane.Views;

/// <summary>開く編集画面の種類。</summary>
public enum EditorTarget { Combo, KeyOverride, Macro, TapDance }

/// <summary>
/// 割り当て画面・右クリックのメニューから、メイン画面へ出す「編集画面を開く」要求（2026-11-05、GUI 計画 1-2）。
/// <paramref name="Id"/> があれば、その番号を選んで開く。無ければ、最初の空き番号に、このキーの初期値を入れて開く
/// （コンボはキー1、キー置換は条件のキー、タップダンスは1回タップ）。どちらも、編集画面の適用・OK まで、プロジェクトは変わらない。
/// </summary>
/// <param name="Slot">対象のキー（コンボのキー1）。</param>
/// <param name="Key">キー置換の条件のキー、またはタップダンスの1回タップに入れるトークン（無ければ null）。</param>
public sealed record EditorRequest(EditorTarget Target, int? Id, int Slot, string? Key);
