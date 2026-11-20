using Kasane.Model;
using Xunit;

namespace Kasane.Tests;

/// <summary>文字の入力の速さの設定（<see cref="TextTyping"/>。2026-11-20）。</summary>
public class TextTypingTests
{
    private static readonly TextTypingProfile Def = TextTypingProfile.Default;

    // 設定画面の単位の表示（範囲と既定）と合わせる。
    [Fact]
    public void Ranges_AndDefaults_AreWhatTheSettingsScreenShows()
    {
        Assert.Equal((1, 10), (TextTyping.MinJitterMs, TextTyping.MaxJitterMs));
        Assert.Equal(300, TextTyping.MaxCharMs);
        Assert.Equal(3000, TextTyping.MaxImeWaitMs);
        Assert.Equal(10, Def.JitterMs);
        Assert.Equal(30, Def.CharMs);
        Assert.Equal(20, Def.ImeWaitMs);
        Assert.True(Def.Jitter);
    }

    [Fact]
    public void BuiltInDefault()
    {
        Assert.Equal(new TextTypingProfile(true, 10, 30, 20), Def);
        Assert.Equal(Def, TextTyping.Resolve(null, null, null));
        Assert.Equal(Def, TextTyping.Resolve(new TextTypingConfig(), new TextTypingConfig(), 0xFFFF));
    }

    [Fact]
    public void DeviceDefault_BeatsGlobalDefault_PerField()
    {
        var global = new TextTypingConfig { CharMs = 10, ImeWaitMs = 50 };
        var device = new TextTypingConfig { CharMs = 40 };
        var p = TextTyping.Resolve(global, device, null);
        Assert.Equal(40, p.CharMs);   // キーボードごと
        Assert.Equal(50, p.ImeWaitMs); // 共通
        Assert.Equal(Def.Jitter, p.Jitter);
        Assert.Equal(Def.JitterMs, p.JitterMs);
    }

    [Fact]
    public void LayerOverride_AppliesOnlyWhenLayerIsActive()
    {
        var global = new TextTypingConfig { CharMs = 10, Layers = new() { new TextTypingLayer { Layer = 3, CharMs = 60 } } };
        Assert.Equal(10, TextTyping.Resolve(global, null, 0b0001).CharMs);
        Assert.Equal(60, TextTyping.Resolve(global, null, 0b1001).CharMs);
        Assert.Equal(10, TextTyping.Resolve(global, null, null).CharMs); // レイヤーを読めない
    }

    [Fact]
    public void Order_DeviceLayer_GlobalLayer_DeviceDefault_GlobalDefault()
    {
        var global = new TextTypingConfig { CharMs = 1, ImeWaitMs = 1, JitterMs = 1, Jitter = false, Layers = new() { new TextTypingLayer { Layer = 2, CharMs = 3, ImeWaitMs = 3 } } };
        var device = new TextTypingConfig { CharMs = 2, ImeWaitMs = 2, JitterMs = 2, Layers = new() { new TextTypingLayer { Layer = 2, CharMs = 4 } } };
        var p = TextTyping.Resolve(global, device, 1u << 2);
        Assert.Equal(4, p.CharMs);    // キーボードのレイヤー
        Assert.Equal(3, p.ImeWaitMs); // 共通のレイヤー
        Assert.Equal(2, p.JitterMs);  // キーボードの既定
        Assert.False(p.Jitter);       // 共通の既定
    }

    [Fact]
    public void SeveralActiveLayers_HigherLayerWins_UnsetFieldsFallThrough()
    {
        var global = new TextTypingConfig
        {
            Layers = new()
            {
                new TextTypingLayer { Layer = 1, CharMs = 11, ImeWaitMs = 111 },
                new TextTypingLayer { Layer = 5, CharMs = 55 },
            },
        };
        var p = TextTyping.Resolve(global, null, (1u << 1) | (1u << 5));
        Assert.Equal(55, p.CharMs);    // 上位のレイヤー
        Assert.Equal(111, p.ImeWaitMs); // 上位に無い値は、下位のレイヤー(キーマップの透過と同じ)
    }

    [Fact]
    public void OutOfRangeValues_AreClampedAtRuntime()
    {
        var p = TextTyping.Resolve(new TextTypingConfig { JitterMs = 99, CharMs = -5, ImeWaitMs = 99999 }, null, null);
        Assert.Equal(TextTyping.MaxJitterMs, p.JitterMs);
        Assert.Equal(0, p.CharMs);
        Assert.Equal(TextTyping.MaxImeWaitMs, p.ImeWaitMs);
    }

    [Fact]
    public void CharDelay_AddsJitterOnlyWhenOn()
    {
        Assert.Equal(30, new TextTypingProfile(false, 5, 30, 0).CharDelay(_ => 4));
        Assert.Equal(34, new TextTypingProfile(true, 5, 30, 0).CharDelay(_ => 4));
    }

    [Fact]
    public void Validate_ReportsRangeAndDuplicateLayer()
    {
        var c = new TextTypingConfig
        {
            JitterMs = 0, CharMs = 301, ImeWaitMs = -1,
            Layers = new() { new TextTypingLayer { Layer = 2 }, new TextTypingLayer { Layer = 2 }, new TextTypingLayer { Layer = 16 } },
        };
        var errors = new List<string>();
        TextTyping.Validate(c, "共通", errors);
        Assert.Equal(5, errors.Count);
        Assert.All(errors, e => Assert.DoesNotContain("の規則", e)); // 設定画面がアプリ別レイヤーのタブへ送らない
    }

    [Fact]
    public void Validate_AcceptsGoodValues()
    {
        var errors = new List<string>();
        TextTyping.Validate(new TextTypingConfig { Jitter = true, JitterMs = 1, CharMs = 0, ImeWaitMs = 3000, Layers = new() { new TextTypingLayer { Layer = 15, JitterMs = 10, CharMs = 300 } } }, "共通", errors);
        Assert.Empty(errors);
    }

    [Fact]
    public void Json_OmitsUnsetValues_AndOldFilesLoad()
    {
        var c = new HelperConfig { TextTyping = new TextTypingConfig { CharMs = 10, Layers = new() { new TextTypingLayer { Layer = 2, Jitter = false } } } };
        string json = c.ToJson();
        Assert.Contains("\"CharMs\": 10", json);
        Assert.DoesNotContain("ImeWaitMs", json);
        Assert.DoesNotContain("JitterMs", json);
        var back = HelperConfig.FromJson(json)!;
        Assert.Equal(10, back.TextTyping!.CharMs);
        Assert.False(back.TextTyping.Layers![0].Jitter);
        Assert.Equal(2, back.TextTyping.Layers[0].Layer);

        Assert.DoesNotContain("TextTyping", new HelperConfig().ToJson());
        Assert.Null(HelperConfig.FromJson("{ \"Version\": 1 }")!.TextTyping);
    }

    [Fact]
    public void ImeRestore_OmittedUnlessSet()
    {
        Assert.DoesNotContain("ImeRestore", new HelperConfig { Actions = { new HelperAction { Id = 1, Action = new HelperActionBody { Type = "Text", Text = "a" } } } }.ToJson());
        Assert.Contains("\"ImeRestore\": false", new HelperConfig { Actions = { new HelperAction { Id = 1, Action = new HelperActionBody { Type = "Text", Text = "a", ImeOffDuring = true, ImeRestore = false } } } }.ToJson());
    }

    [Fact]
    public void ConfigSet_TextTypingFor_UsesDeviceThenGlobal()
    {
        var set = new HelperConfigSet(
            new HelperConfig { TextTyping = new TextTypingConfig { CharMs = 10 } },
            new Dictionary<uint, HelperConfig> { [7] = new HelperConfig { TextTyping = new TextTypingConfig { CharMs = 70, Layers = new() { new TextTypingLayer { Layer = 1, CharMs = 71 } } } } });
        Assert.Equal(70, set.TextTypingFor(7, 0).CharMs);
        Assert.Equal(71, set.TextTypingFor(7, 0b10).CharMs);
        Assert.Equal(10, set.TextTypingFor(8, 0b10).CharMs);
        Assert.True(set.HasAnyTextTypingLayers);
        Assert.False(HelperConfigSet.Empty.HasAnyTextTypingLayers);
    }

    [Fact]
    public void IsEmpty_AndHasContent_SeeTextTyping()
    {
        var c = new HelperConfig { TextTyping = new TextTypingConfig { CharMs = 10 } };
        Assert.False(HelperSettingsModel.IsEmpty(c));
        Assert.True(Kasane.Model.Portable.PortableExporter.HasContent(c));
        Assert.True(HelperSettingsModel.IsEmpty(new HelperConfig { TextTyping = new TextTypingConfig() }));
    }
}
