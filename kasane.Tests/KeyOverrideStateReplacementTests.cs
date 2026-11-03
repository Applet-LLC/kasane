using Kasane.Interop;
using Kasane.Keycodes;
using Kasane.Model;

namespace Kasane.Tests;

/// <summary>
/// キー置換の置き換え先に、レイヤー操作（OSL・TG・TO・DF）とヘルパー動作（HLP）を指定する（2026-11-03）。
/// 件数を 32→128 へ増やしたことの確認も含む。
/// </summary>
public class KeyOverrideStateReplacementTests
{
    private static KeyOverrideEntry? Convert(string replacement, List<string> errors) =>
        ProjectService.KeyOverrideToEntry(
            new KeyOverrideDef { Id = 0, Mods = "MOD_LCTL", Key = "KC_X", Replacement = replacement }, errors);

    [Theory]
    [InlineData("OSL(1)", ActionType.OneShotLayer, 1, 0)]
    [InlineData("TG(2)", ActionType.Toggle, 2, 0)]
    [InlineData("TO(3)", ActionType.LayerTo, 3, 0)]
    [InlineData("DF(1)", ActionType.Default, 1, 0)]
    [InlineData("HLP(7)", ActionType.Custom, 0, 7)]
    [InlineData("HLP(0)", ActionType.Custom, 0, 0)]
    [InlineData("MARK(on)", ActionType.Mark, 0, 0)]
    [InlineData("MARK(off)", ActionType.Mark, 1, 0)]
    [InlineData("MARK(toggle)", ActionType.Mark, 2, 0)]
    public void StateReplacement_IsAccepted(string replacement, byte type, int param1, int param2)
    {
        var errors = new List<string>();
        var entry = Convert(replacement, errors);

        Assert.Empty(errors);
        Assert.NotNull(entry);
        Assert.Equal(type, entry!.Replacement.Type);
        Assert.Equal(param1, entry.Replacement.Param1);
        Assert.Equal(param2, entry.Replacement.Param2);
        Assert.Equal(0, entry.Flags);
    }

    [Theory]
    [InlineData("MO(1)")]               // 押している間だけ有効にする動作は、置き換え先に使えない（解放の処理がないため）
    [InlineData("TT(1)")]
    [InlineData("LT(1, KC_A)")]
    [InlineData("OSM(MOD_LSFT)")]
    [InlineData("LM(1, MOD_LSFT)")]
    [InlineData("TD(0)")]
    [InlineData("KC_NO")]
    public void OtherActions_AreRejected(string replacement)
    {
        var errors = new List<string>();
        Assert.Null(Convert(replacement, errors));
        Assert.Contains(errors, e => e.Contains("置き換え先"));
    }

    [Theory]
    [InlineData("MARK(on)")]
    [InlineData("MARK(off)")]
    [InlineData("MARK(toggle)")]
    public void Mark_RoundTripsThroughTheTokenText(string token)
    {
        Assert.True(KeycodeTable.TryResolve(token, out var action, out var error), error);
        Assert.Equal(token, KeycodeTable.ToToken(action));
    }

    [Theory]
    [InlineData("MARK(set)")]
    [InlineData("MARK()")]
    [InlineData("MARK(on, off)")]
    public void Mark_WithAWrongModeIsRejected(string token)
    {
        Assert.False(KeycodeTable.TryResolve(token, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void ValidityCheck_MatchesTheDriversRanges()
    {
        Assert.True(ProjectService.IsValidOverrideReplacement(new KeyAction(ActionType.Mark, 2, 0)));
        Assert.False(ProjectService.IsValidOverrideReplacement(new KeyAction(ActionType.Mark, 3, 0)));
        Assert.False(ProjectService.IsValidOverrideReplacement(new KeyAction(ActionType.Mark, 0, 1)));
        // ドライバの IsValidKeyOverrideEntry と同じ: レイヤー番号は 0〜15、Param2 は 0。HLP は Param1 が 0。
        Assert.True(ProjectService.IsValidOverrideReplacement(new KeyAction(ActionType.OneShotLayer, 15, 0)));
        Assert.False(ProjectService.IsValidOverrideReplacement(new KeyAction(ActionType.OneShotLayer, 16, 0)));
        Assert.False(ProjectService.IsValidOverrideReplacement(new KeyAction(ActionType.Toggle, 1, 1)));
        Assert.True(ProjectService.IsValidOverrideReplacement(new KeyAction(ActionType.Custom, 0, 65535)));
        Assert.False(ProjectService.IsValidOverrideReplacement(new KeyAction(ActionType.Custom, 1, 0)));
        Assert.False(ProjectService.IsValidOverrideReplacement(new KeyAction(ActionType.Key, 0, 0)));
        Assert.False(ProjectService.IsValidOverrideReplacement(new KeyAction(ActionType.Momentary, 1, 0)));
    }

    [Fact]
    public void RepeatFlag_IsIgnoredForStateReplacements()
    {
        // ドライバは、マクロ以外のFLAG_REPEATを拒否するので、送らない。
        var errors = new List<string>();
        var entry = ProjectService.KeyOverrideToEntry(
            new KeyOverrideDef { Id = 0, Mods = "MOD_LCTL", Key = "KC_X", Replacement = "OSL(1)", RepeatMacro = true }, errors);

        Assert.Empty(errors);
        Assert.Equal(0, entry!.Flags);
    }

    [Fact]
    public void CapacityIs128_AndTheLastEntryIsAtTheRightOffset()
    {
        Assert.Equal(128, KbdLayerClient.MaxKeyOverrideCount);

        var blob = PersistedSettings.SerializeKeyOverrides(new Dictionary<int, KeyOverrideEntry?>
        {
            [127] = new KeyOverrideEntry(0x01, 0x002D, new KeyAction(ActionType.OneShotLayer, 1, 0)),
        });
        Assert.Equal(128 * 12, blob.Length);
        int off = 127 * 12;
        Assert.Equal(1u, BitConverter.ToUInt32(blob, off));
        Assert.Equal(0x01, blob[off + 4]);
        Assert.Equal(0x002D, BitConverter.ToUInt16(blob, off + 6));
        Assert.Equal(ActionType.OneShotLayer, blob[off + 8]);
        Assert.Equal(1, blob[off + 9]);
        Assert.Equal(0u, BitConverter.ToUInt32(blob, 0)); // 他の件は未設定
    }

    [Fact]
    public void IdOutOfRange_IsRejectedWhenSerializing()
    {
        Assert.Throws<InvalidOperationException>(() => PersistedSettings.SerializeKeyOverrides(new Dictionary<int, KeyOverrideEntry?>
        {
            [128] = new KeyOverrideEntry(0x01, 0x002D, new KeyAction(ActionType.Key, 0, 0x001E)),
        }));
    }

    [Fact]
    public void Compile_RejectsAnOverrideThatPointsToAMissingLayer()
    {
        var layout = LayoutFixture.TenKey;
        var p = ProjectService.CreateDefault(0xCDEC0011, "HID\\VID_T", layout, bundled: true);
        p.KeyOverrides.Add(new KeyOverrideDef { Id = 0, Mods = "MOD_LCTL", Key = "KC_X", Replacement = "OSL(5)" });

        var ex = Assert.Throws<InvalidOperationException>(() => ProjectService.Compile(p, layout));
        Assert.Contains("レイヤー5", ex.Message);
    }

    [Fact]
    public void Compile_AcceptsAnOverrideThatPointsToAnExistingLayer()
    {
        var layout = LayoutFixture.TenKey;
        var p = ProjectService.CreateDefault(0xCDEC0012, "HID\\VID_T", layout, bundled: true);
        p.Layers.Add(ProjectService.DefaultLayer(layout, 1));
        p.KeyOverrides.Add(new KeyOverrideDef { Id = 0, Mods = "MOD_LCTL", Key = "KC_X", Replacement = "OSL(1)" });
        p.KeyOverrides.Add(new KeyOverrideDef { Id = 127, Mods = "MOD_LCTL", Key = "KC_Y", Replacement = "HLP(3)" });

        var settings = ProjectService.Compile(p, layout);
        Assert.Equal(ActionType.OneShotLayer, settings.KeyOverrides[0]!.Replacement.Type);
        Assert.Equal(ActionType.Custom, settings.KeyOverrides[127]!.Replacement.Type);
    }
}
