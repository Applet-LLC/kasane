using Kasane.Keycodes;
using Kasane.Model;

namespace Kasane.Tests;

/// <summary>
/// マニュアルの「設定の例」(docs/manual/src/ja/12-recipes.md)に書いたキーコードが、実装で解釈できること。
/// マニュアルの例と、実装がずれないようにするための試験。例を変えたら、ここも変える。
/// </summary>
public class ManualRecipeTokensTests
{
    [Theory]
    // Caps Lock の入れ替え、Mod-Tap、SandS、矢印のレイヤー
    [InlineData("KC_LCTRL")]
    [InlineData("KC_CAPSLOCK")]
    [InlineData("MT(MOD_LCTL,KC_ESC)")]
    [InlineData("MT(MOD_LSFT,KC_SPC)")]
    [InlineData("LT(1,KC_SPC)")]
    [InlineData("KC_LEFT")]
    [InlineData("KC_DOWN")]
    [InlineData("KC_UP")]
    [InlineData("KC_RIGHT")]
    [InlineData("KC_NO")]
    [InlineData("KC_LBRC")]
    [InlineData("KC_ZKHK")]
    [InlineData("KC_F13")]
    [InlineData("KC_F14")]
    [InlineData("LCTL(KC_LALT)")]
    // テンキーのレイヤーの例
    [InlineData("TO(0)")]
    [InlineData("TO(1)")]
    [InlineData("TO(2)")]
    [InlineData("LCTL(KC_RBRACKET)")]
    [InlineData("LCTL(KC_BSLASH)")]
    [InlineData("LCTL(KC_LBRACKET)")]
    [InlineData("LCTL(LSFT(KC_P))")]
    [InlineData("LCTL(LSFT(KC_O))")]
    [InlineData("KC_LSHIFT")]
    [InlineData("KC_LALT")]
    [InlineData("HLP(1)")]
    [InlineData("HLP(9)")]
    [InlineData("MACRO(0)")]
    public void RecipeKeycodes_Resolve(string token)
    {
        Assert.True(KeycodeTable.TryResolve(token, out _, out var error), token + ": " + error);
    }

    [Theory]
    [InlineData("MOD_LSFT", "KC_2", "LSFT(KC_QUOTE)")]     // Shift+2 を " にする(US 配列の Windows)
    [InlineData("MOD_LSFT", "KC_BSPACE", "KC_DELETE")]      // Shift+BS を Delete にする
    [InlineData("MOD_LSFT", "KC_ZKHK", "KC_CAPSLOCK")]      // Caps Lock を IME(半角/全角)にして、Shift+Caps Lock を Caps Lock にする
    [InlineData("MOD_LCTL", "KC_1", "MACRO(0)")]            // Ctrl+1 でマクロ 0
    public void RecipeKeyOverrides_AreAccepted(string mods, string key, string replacement)
    {
        var errors = new List<string>();
        var entry = ProjectService.KeyOverrideToEntry(new KeyOverrideDef { Id = 0, Mods = mods, Key = key, Replacement = replacement }, errors);
        Assert.Empty(errors);
        Assert.NotNull(entry);
    }

    [Fact]
    public void RecipeMacro_SelectAllAndCopy_IsAccepted()
    {
        var errors = new List<string>();
        var steps = ProjectService.MacroSteps(new MacroDef { Id = 0, Keys = new List<string> { "LCTL(KC_A)", "LCTL(KC_C)" } }, errors);
        Assert.Empty(errors);
        Assert.NotEmpty(steps);
    }

    [Fact]
    public void ShiftTwoToQuote_SendsShiftedApostropheKey()
    {
        // US 配列の Windows で、Shift+2 を " にするには、Shift を掛けたアポストロフィのキー(KC_QUOTE = 0x28)を送る。
        Assert.True(KeycodeTable.TryResolve("LSFT(KC_QUOTE)", out var action));
        Assert.Equal(ActionType.ModdedKey, action.Type);
        Assert.Equal(0x28, action.Param2);
        Assert.Equal(0x02, action.Param1); // MOD_LSFT
    }
}
