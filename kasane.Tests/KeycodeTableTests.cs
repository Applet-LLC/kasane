using Kasane.Keycodes;

namespace Kasane.Tests;

public class KeycodeTableTests
{
    [Fact]
    public void PlainKey_ResolvesToKeyType()
    {
        Assert.True(KeycodeTable.TryResolve("KC_A", out var action, out var error));
        Assert.Null(error);
        Assert.Equal(ActionType.Key, action.Type);
        Assert.Equal(0x001E, action.Param2);
    }

    [Theory]
    [InlineData("KC_TRNS")]
    [InlineData("_______")]
    [InlineData("KC_TRANSPARENT")]
    public void TransparentAliases_ResolveToTransparentType(string token)
    {
        Assert.True(KeycodeTable.TryResolve(token, out var action, out var error));
        Assert.Null(error);
        Assert.Equal(ActionType.Transparent, action.Type);
    }

    [Theory]
    [InlineData("KC_NO")]
    [InlineData("XXXXXXX")]
    public void NoneAliases_ResolveToNoneType(string token)
    {
        // KC_NO is None (0), not Key -- it's a placeholder, e.g. for an unused tap-dance slot
        // or the ignored tap-argument in MT(mod,KC_NO).
        Assert.True(KeycodeTable.TryResolve(token, out var action, out var error));
        Assert.Null(error);
        Assert.Equal(ActionType.None, action.Type);
        Assert.Equal(0, action.Param2);
    }

    [Theory]
    [InlineData("LCTL(KC_C)", 0x01)]
    [InlineData("LSFT(KC_1)", 0x02)]
    [InlineData("C(KC_C)", 0x01)]   // short alias
    [InlineData("S(KC_1)", 0x02)]   // short alias
    public void ModdedKey_ResolvesToModdedKeyType(string token, byte expectedMask)
    {
        Assert.True(KeycodeTable.TryResolve(token, out var action, out var error));
        Assert.Null(error);
        Assert.Equal(ActionType.ModdedKey, action.Type);
        Assert.Equal(expectedMask, action.Param1);
        Assert.NotEqual((ushort)0, action.Param2);
    }

    [Fact]
    public void ModdedKey_Nested_CombinesMask()
    {
        // LCTL(LSFT(KC_T)) -- nested modifier functions OR their masks together.
        Assert.True(KeycodeTable.TryResolve("LCTL(LSFT(KC_T))", out var action, out var error));
        Assert.Null(error);
        Assert.Equal(ActionType.ModdedKey, action.Type);
        Assert.Equal((byte)(0x01 | 0x02), action.Param1); // LCTL | LSFT
    }

    [Fact]
    public void PlainKeyWithParens_IsNotTreatedAsModdedKey()
    {
        // A bare key has no modifier-function wrapper, so it resolves as a plain KEY via the
        // BaseKeys table lookup (checked before modded-key parsing), not as MODDED_KEY.
        Assert.True(KeycodeTable.TryResolve("KC_C", out var action, out _));
        Assert.Equal(ActionType.Key, action.Type);
    }

    [Theory]
    [InlineData("MT(MOD_LCTL,KC_A)", (byte)0)]
    [InlineData("MT(MOD_LSFT,KC_B)", (byte)1)]
    public void ModTap_Resolves(string token, byte expectedBit)
    {
        Assert.True(KeycodeTable.TryResolve(token, out var action, out var error));
        Assert.Null(error);
        Assert.Equal(ActionType.ModTap, action.Type);
        Assert.Equal(expectedBit, action.Param1);
    }

    [Fact]
    public void LayerTap_Resolves()
    {
        Assert.True(KeycodeTable.TryResolve("LT(1,KC_SPC)", out var action, out var error));
        Assert.Null(error);
        Assert.Equal(ActionType.LayerTap, action.Type);
        Assert.Equal((byte)1, action.Param1);
    }

    [Theory]
    [InlineData("LM(1,MOD_LCTL)", (byte)1, (ushort)0x01)]
    [InlineData("LM(2,MOD_LCTL|MOD_LSFT)", (byte)2, (ushort)0x03)]
    public void LayerMod_Resolves(string token, byte expectedLayer, ushort expectedMask)
    {
        Assert.True(KeycodeTable.TryResolve(token, out var action, out var error));
        Assert.Null(error);
        Assert.Equal(ActionType.LayerMod, action.Type);
        Assert.Equal(expectedLayer, action.Param1);
        Assert.Equal(expectedMask, action.Param2);
    }

    [Fact]
    public void Macro_Resolves()
    {
        Assert.True(KeycodeTable.TryResolve("MACRO(0)", out var action, out var error));
        Assert.Null(error);
        Assert.Equal(ActionType.Macro, action.Type);
        Assert.Equal((byte)0, action.Param1);
    }

    [Fact]
    public void TapDance_Resolves()
    {
        Assert.True(KeycodeTable.TryResolve("TAPDANCE(0)", out var action, out var error));
        Assert.Null(error);
        Assert.Equal(ActionType.TapDance, action.Type);
    }

    [Theory]
    [InlineData("TO(2)", ActionType.LayerTo)]
    [InlineData("TT(3)", ActionType.LayerTapToggle)]
    public void LayerToAndTapToggle_Resolve(string token, byte expectedType)
    {
        Assert.True(KeycodeTable.TryResolve(token, out var action, out var error));
        Assert.Null(error);
        Assert.Equal(expectedType, action.Type);
        Assert.True(ActionType.ReferencesLayer(action.Type));
        Assert.Equal(KeycodeTable.ToToken(action), token);
    }

    [Theory]
    [InlineData("OSM(MOD_LSFT)", 0x02)]
    [InlineData("OSM(MOD_LCTL|MOD_LSFT)", 0x03)]
    public void OneShotMod_ResolvesAndRoundTrips(string token, byte expectedMask)
    {
        Assert.True(KeycodeTable.TryResolve(token, out var action, out var error));
        Assert.Null(error);
        Assert.Equal(ActionType.OneShotMod, action.Type);
        Assert.Equal(expectedMask, action.Param1);
        Assert.Equal(0, action.Param2);
        Assert.Equal(token, KeycodeTable.ToToken(action));
    }

    [Theory]
    [InlineData("OSM()")]
    [InlineData("OSM(SHIFT)")]
    [InlineData("TO(99)")]       // out of range (KbdLayerClient.MaxLayers)
    [InlineData("TT(99)")]
    [InlineData("NOT_A_REAL_TOKEN")]
    [InlineData("LCTL()")]
    [InlineData("MT(BOGUS_MOD,KC_A)")]
    [InlineData("")]
    [InlineData("MACRO(999)")]   // out of range (KbdLayerClient.MaxMacroCount)
    [InlineData("LT(99,KC_A)")]  // out of range (KbdLayerClient.MaxLayers)
    public void InvalidTokens_AreRejectedWithAnError(string token)
    {
        Assert.False(KeycodeTable.TryResolve(token, out _, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("KC_A")]
    [InlineData("KC_1")]
    [InlineData("LCTL(KC_C)")]
    [InlineData("LSFT(KC_1)")]
    [InlineData("LCTL(LSFT(KC_T))")]
    public void ToToken_RoundTripsBackToTheSameAction(string token)
    {
        Assert.True(KeycodeTable.TryResolve(token, out var action, out _));
        var roundTripped = KeycodeTable.ToToken(action);
        Assert.True(KeycodeTable.TryResolve(roundTripped, out var reResolved, out _));
        Assert.Equal(action, reResolved);
    }
}
