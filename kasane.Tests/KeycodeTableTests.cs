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
    [InlineData("KC_F13", 0x0064)]
    [InlineData("KC_F23", 0x006E)]
    [InlineData("KC_F24", 0x0076)]   // F24 is not contiguous with F13-F23
    [InlineData("KC_WSCH", 0xE065)]
    [InlineData("KC_WWW_BACK", 0xE06A)]
    [InlineData("KC_MAIL", 0xE06C)]
    [InlineData("KC_SLEP", 0xE05F)]
    public void ExtendedKeys_ResolveToTheirScanCodes(string token, int expectedScanCode)
    {
        Assert.True(KeycodeTable.TryResolve(token, out var action, out var error));
        Assert.Null(error);
        Assert.Equal(ActionType.Key, action.Type);
        Assert.Equal((ushort)expectedScanCode, action.Param2);
    }

    [Theory]
    [InlineData("SC_E06A", 0xE06A)]
    [InlineData("SC_0064", 0x0064)]
    [InlineData("SC_e06a", 0xE06A)]  // hex digits are case-insensitive
    [InlineData("SC_E11D", 0xE11D)]
    public void RawScanCode_ResolvesToKey(string token, int expectedScanCode)
    {
        Assert.True(KeycodeTable.TryResolve(token, out var action, out var error));
        Assert.Null(error);
        Assert.Equal(ActionType.Key, action.Type);
        Assert.Equal((ushort)expectedScanCode, action.Param2);
    }

    [Fact]
    public void RawScanCode_WorksInsideModdedKeyAndTapArguments()
    {
        Assert.True(KeycodeTable.TryResolve("LCTL(SC_E06A)", out var modded, out _));
        Assert.Equal(new KeyAction(ActionType.ModdedKey, 0x01, 0xE06A), modded);
        Assert.True(KeycodeTable.TryResolve("MT(MOD_LSFT,SC_0064)", out var modTap, out _));
        Assert.Equal(new KeyAction(ActionType.ModTap, 1, 0x0064), modTap);
    }

    [Fact]
    public void UnnamedScanCode_RoundTripsAsRawScanCodeToken()
    {
        // A scan code with no name in the table (e.g. read back from the driver) is shown as SC_XXXX,
        // which resolves back to the same action (it used to be KC_UNKNOWN_XXXX, which did not).
        var action = new KeyAction(ActionType.Key, 0, 0xE07F);
        var token = KeycodeTable.ToToken(action);
        Assert.Equal("SC_E07F", token);
        Assert.True(KeycodeTable.TryResolve(token, out var reResolved, out _));
        Assert.Equal(action, reResolved);
        Assert.Equal("KC_F13", KeycodeTable.KeyToken(0x0064)); // named keys keep their name
        Assert.Equal("KC_NO", KeycodeTable.KeyToken(0));
    }

    [Theory]
    [InlineData("HLP(1)", 1)]
    [InlineData("HLP(0x102)", 0x102)]
    [InlineData("HLP(010)", 10)]     // leading zero is decimal, not octal (same as KeymapTool)
    [InlineData("HLP(65535)", 65535)]
    public void Helper_ResolvesToCustomAndRoundTrips(string token, int expectedId)
    {
        Assert.True(KeycodeTable.TryResolve(token, out var action, out var error));
        Assert.Null(error);
        Assert.Equal(ActionType.Custom, action.Type);
        Assert.Equal((byte)0, action.Param1);
        Assert.Equal((ushort)expectedId, action.Param2);
        Assert.Equal($"HLP({expectedId})", KeycodeTable.ToToken(action));
    }

    [Theory]
    [InlineData("SC_1234")]   // prefix must be 00, E0 or E1
    [InlineData("SC_0000")]
    [InlineData("SC_E000")]   // make code 0
    [InlineData("SC_E06")]
    [InlineData("SC_GGGG")]
    [InlineData("sc_E06A")]
    [InlineData("HLP(65536)")]
    [InlineData("HLP(-1)")]
    [InlineData("HLP()")]
    [InlineData("HLP(0x)")]
    public void InvalidExtendedTokens_AreRejected(string token)
    {
        Assert.False(KeycodeTable.TryResolve(token, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void EveryCatalogKeycode_Resolves()
    {
        foreach (var category in KeycodeCatalog.Categories)
        {
            foreach (var token in category.Keycodes)
            {
                Assert.True(KeycodeTable.TryResolve(token, out _, out var error), $"{category.Name}: {token}: {error}");
            }
        }
    }

    [Theory]
    [InlineData("KC_A")]
    [InlineData("KC_1")]
    [InlineData("SC_E06A")]
    [InlineData("HLP(7)")]
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
