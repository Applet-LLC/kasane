using Kasane.Keycodes;
using Kasane.Layout;

namespace Kasane.Model;

/// <summary>番号で参照される定義の種類（MACRO(n)・TAPDANCE(n)・HLP(n)）。コンボとキー置換は、番号をほかから参照されない。</summary>
public enum RefKind { Macro, TapDance, Helper }

/// <summary>参照のある場所。</summary>
public enum RefPlace { Keymap, ComboOutput, TapDanceField, KeyOverrideReplacement }

/// <summary>
/// 1件の参照。<paramref name="Layer"/>・<paramref name="Slot"/> はキーマップのとき、<paramref name="DefId"/> と <paramref name="Field"/> は定義の中のとき
/// （<paramref name="Field"/> はタップダンスの欄: SingleTap / SingleHold / DoubleTap / TripleTap）。
/// </summary>
public sealed record ProjectReference(RefPlace Place, int Layer, int Slot, int DefId, string? Field);

public static partial class ProjectService
{
    /// <summary>トークンが、その種類の番号 <paramref name="id"/> を指すか（MACRO(3)、TAPDANCE(3)、HLP(3)）。</summary>
    public static bool RefersTo(string? token, RefKind kind, int id)
    {
        if (string.IsNullOrWhiteSpace(token) || !KeycodeTable.TryResolve(token, out var a)) return false;
        return kind switch
        {
            RefKind.Macro => a.Type == ActionType.Macro && a.Param1 == id,
            RefKind.TapDance => a.Type == ActionType.TapDance && a.Param1 == id,
            RefKind.Helper => a.Type == ActionType.Custom && a.Param2 == id,
            _ => false,
        };
    }

    /// <summary>
    /// プロジェクトの中で、その番号を参照している場所（2026-11-06、GUI 計画 1-3）。キーマップの全レイヤー、コンボの出力、タップダンスの各欄、キー置換の置き換え先。
    /// 無効にした定義の中も数える。ほかのキーボードのプロジェクト（helper.json を共有するとき）は調べない。
    /// </summary>
    public static List<ProjectReference> FindReferences(KeymapProject project, RefKind kind, int id)
    {
        var refs = new List<ProjectReference>();
        for (int layer = 0; layer < project.Layers.Count; layer++)
            for (int slot = 0; slot < project.Layers[layer].Count; slot++)
                if (RefersTo(project.Layers[layer][slot], kind, id)) refs.Add(new ProjectReference(RefPlace.Keymap, layer, slot, -1, null));
        foreach (var c in project.Combos.OrderBy(c => c.Id))
            if (RefersTo(c.Output, kind, id)) refs.Add(new ProjectReference(RefPlace.ComboOutput, -1, -1, c.Id, null));
        foreach (var t in project.TapDances.OrderBy(t => t.Id))
            foreach (var (field, value) in TapDanceFields(t))
                if (RefersTo(value, kind, id)) refs.Add(new ProjectReference(RefPlace.TapDanceField, -1, -1, t.Id, field));
        foreach (var k in project.KeyOverrides.OrderBy(k => k.Id))
            if (RefersTo(k.Replacement, kind, id)) refs.Add(new ProjectReference(RefPlace.KeyOverrideReplacement, -1, -1, k.Id, null));
        return refs;
    }

    /// <summary>
    /// その番号への参照を外す。キーマップは透過（KC_TRNS）に戻し、タップダンスの欄は空（動作なし）にする。コンボの出力・キー置換の置き換え先は、
    /// 空にすると定義として成り立たないので、定義を無効にする（中身は残す）。4つの欄が全部空になったタップダンスは、未使用にする。外した件数を返す。
    /// </summary>
    public static int RemoveReferences(KeymapProject project, RefKind kind, int id)
    {
        int n = 0;
        foreach (var row in project.Layers)
            for (int slot = 0; slot < row.Count; slot++)
                if (RefersTo(row[slot], kind, id)) { row[slot] = "KC_TRNS"; n++; }
        foreach (var c in project.Combos)
            if (RefersTo(c.Output, kind, id) && !c.Disabled) { c.Disabled = true; n++; }
        foreach (var t in project.TapDances)
            n += ClearTapDanceRefs(t, kind, id);
        project.TapDances.RemoveAll(IsEmptyTapDance);
        foreach (var k in project.KeyOverrides)
            if (RefersTo(k.Replacement, kind, id) && !k.Disabled) { k.Disabled = true; n++; }
        return n;
    }

    /// <summary>タップダンスの欄のうち、その番号を指すものを空にする（編集画面の作業用コピーにも使う）。空にした欄の数。</summary>
    public static int ClearTapDanceRefs(TapDanceDef t, RefKind kind, int id)
    {
        int n = 0;
        if (RefersTo(t.SingleTap, kind, id)) { t.SingleTap = ""; n++; }
        if (RefersTo(t.SingleHold, kind, id)) { t.SingleHold = ""; n++; }
        if (RefersTo(t.DoubleTap, kind, id)) { t.DoubleTap = ""; n++; }
        if (RefersTo(t.TripleTap, kind, id)) { t.TripleTap = ""; n++; }
        return n;
    }

    public static bool IsEmptyTapDance(TapDanceDef t) =>
        new[] { t.SingleTap, t.SingleHold, t.DoubleTap, t.TripleTap }.All(string.IsNullOrWhiteSpace);

    private static IEnumerable<(string Field, string Value)> TapDanceFields(TapDanceDef t)
    {
        yield return (nameof(TapDanceDef.SingleTap), t.SingleTap);
        yield return (nameof(TapDanceDef.SingleHold), t.SingleHold);
        yield return (nameof(TapDanceDef.DoubleTap), t.DoubleTap);
        yield return (nameof(TapDanceDef.TripleTap), t.TripleTap);
    }

    /// <summary>参照の説明（削除の確認に並べる）。例: 「レイヤー0 #12 J」「タップダンス2 の 2回タップ」「コンボ1 の出力(無効にします)」。</summary>
    public static string DescribeReference(KeymapProject project, KeyboardLayout? layout, ProjectReference r) => r.Place switch
    {
        RefPlace.Keymap => $"{LayerLabel(project, r.Layer)} {SlotLabel(layout, r.Slot)}",
        RefPlace.ComboOutput => Loc.T($"{KeyMarks.Describe("コンボ", r.DefId, project.ComboNames)} の出力(無効にします)",
                                      $"Output of {KeyMarks.Describe("Combo", r.DefId, project.ComboNames)} (will be disabled)"),
        RefPlace.TapDanceField => Loc.T($"{KeyMarks.Describe("タップダンス", r.DefId, project.TapDanceNames)} の{TapDanceFieldLabel(r.Field)}",
                                        $"{TapDanceFieldLabel(r.Field)} of {KeyMarks.Describe("Tap dance", r.DefId, project.TapDanceNames)}"),
        RefPlace.KeyOverrideReplacement => Loc.T($"{KeyMarks.Describe("キー置換", r.DefId, project.KeyOverrideNames)} の置き換え先(無効にします)",
                                                 $"Replacement of {KeyMarks.Describe("Key override", r.DefId, project.KeyOverrideNames)} (will be disabled)"),
        _ => "",
    };

    private static string SlotLabel(KeyboardLayout? layout, int slot) =>
        layout?.Slots.FirstOrDefault(s => s.Index == slot)?.Description ?? Loc.T($"キー{slot}", $"Key {slot}");

    private static string TapDanceFieldLabel(string? field) => field switch
    {
        nameof(TapDanceDef.SingleTap) => Loc.T("1回タップ", "single tap"),
        nameof(TapDanceDef.SingleHold) => Loc.T("長押し", "hold"),
        nameof(TapDanceDef.DoubleTap) => Loc.T("2回タップ", "double tap"),
        nameof(TapDanceDef.TripleTap) => Loc.T("3回タップ", "triple tap"),
        _ => field ?? "",
    };
}
