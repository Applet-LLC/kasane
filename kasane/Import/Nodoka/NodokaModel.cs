namespace Kasane.Import.Nodoka;

/// <summary>のどかの修飾子の種類（keyboard.h の Modifier::Type と同じ順）。</summary>
public enum ModType
{
    Shift, Control, Alt, Windows,
    Up, Down,
    Repeat, ImeLock, ImeComp, NumLock, CapsLock, ScrollLock, KanaLock,
    Maximized, Minimized, MdiMaximized, MdiMinimized,
    Touchpad, TouchpadSticky, TouchpadL, TouchpadLSticky, TouchpadR, TouchpadRSticky,
    Mod0, Mod1, Mod2, Mod3, Mod4, Mod5, Mod6, Mod7, Mod8, Mod9,
    Lock0, Lock1, Lock2, Lock3, Lock4, Lock5, Lock6, Lock7, Lock8, Lock9, LockA, LockB, LockC, LockD, LockE, LockF,
    Keyboard0, Keyboard1, Keyboard2, Keyboard3, Keyboard4, Keyboard5, Keyboard6, Keyboard7,
    ImeCandi, Harf, Katakana, Native, DP,
    End,
}

/// <summary>のどかの Modifier の写し: 各修飾子について「押している／離している」と「気にしない（dontcare）」を持つ。</summary>
public sealed class NodokaModifier
{
    public const ModType Basic = ModType.Up;      // Type_BASIC: ここより前が S/C/A/W
    public const ModType KeySeq = ModType.Repeat; // Type_KEYSEQ: ここより前が S/C/A/W/U/D
    public const ModType Assign = ModType.End;    // Type_ASSIGN

    private ulong _mods;
    private ulong _dontcares;

    private static ulong Bit(ModType t) => 1UL << (int)t;

    public NodokaModifier()
    {
        // keyboard.cpp の Modifier::Modifier(): 次の修飾子は最初から「気にしない」。S/C/A/W と M0〜M9 は「離している」を気にする。
        foreach (var t in new[]
        {
            ModType.Up, ModType.Down, ModType.Repeat, ModType.ImeLock, ModType.ImeComp, ModType.NumLock, ModType.CapsLock,
            ModType.ScrollLock, ModType.KanaLock, ModType.Maximized, ModType.Minimized, ModType.MdiMaximized, ModType.MdiMinimized,
            ModType.Touchpad, ModType.TouchpadSticky, ModType.TouchpadL, ModType.TouchpadLSticky, ModType.TouchpadR, ModType.TouchpadRSticky,
            ModType.Lock0, ModType.Lock1, ModType.Lock2, ModType.Lock3, ModType.Lock4, ModType.Lock5, ModType.Lock6, ModType.Lock7,
            ModType.Lock8, ModType.Lock9, ModType.LockA, ModType.LockB, ModType.LockC, ModType.LockD, ModType.LockE, ModType.LockF,
            ModType.Keyboard0, ModType.Keyboard1, ModType.Keyboard2, ModType.Keyboard3, ModType.Keyboard4, ModType.Keyboard5,
            ModType.Keyboard6, ModType.Keyboard7, ModType.ImeCandi, ModType.Harf, ModType.Katakana, ModType.Native, ModType.DP,
        })
            Dontcare(t);
    }

    public NodokaModifier Clone() => new() { _mods = _mods, _dontcares = _dontcares };

    public NodokaModifier Press(ModType t) { _mods |= Bit(t); return Care(t); }
    public NodokaModifier Release(ModType t) { _mods &= ~Bit(t); return Care(t); }
    public NodokaModifier Care(ModType t) { _dontcares &= ~Bit(t); return this; }
    public NodokaModifier Dontcare(ModType t) { _dontcares |= Bit(t); return this; }
    public NodokaModifier DontcareAll() { _dontcares = ~0UL; return this; }
    public NodokaModifier Press(ModType t, bool pressed) => pressed ? Press(t) : Release(t);

    public bool IsPressed(ModType t) => (_mods & Bit(t)) != 0;
    public bool IsDontcare(ModType t) => (_dontcares & Bit(t)) != 0;
    /// <summary>押している状態を条件にしている（気にしないではなく、押している）。</summary>
    public bool RequiresPressed(ModType t) => IsPressed(t) && !IsDontcare(t);
}

public sealed record SourceRef(string File, int Line)
{
    public override string ToString() => $"{File}:{Line}";
}

public sealed class NodokaKey
{
    public List<string> Names { get; } = new();
    /// <summary>(スキャンコード, フラグ)。フラグは 1=E0, 2=E1, 3=E0E1（のどかの ScanCode::E0 など）。</summary>
    public List<(byte Scan, int Flags)> ScanCodes { get; } = new();

    /// <summary>キーボード（kasane の Enc16: 00xx / E0xx）で表せるか。表せなければ null（E1 の疑似キー、複数のスキャンコードなど）。</summary>
    public ushort? Enc16 =>
        ScanCodes.Count == 1 && ScanCodes[0].Scan != 0 && (ScanCodes[0].Flags == 0 || ScanCodes[0].Flags == 1)
            ? (ushort)((ScanCodes[0].Flags == 1 ? 0xE000 : 0) | ScanCodes[0].Scan)
            : null;
}

public abstract class SeqItem { public SourceRef? Src { get; init; } }

public sealed class SeqKey : SeqItem
{
    public NodokaModifier Mod { get; init; } = new();
    public string KeyName { get; init; } = "";
}

public sealed class SeqFunc : SeqItem
{
    public NodokaModifier Mod { get; init; } = new();
    public string Name { get; init; } = "";
    /// <summary>括弧の中を、一番外側のカンマで分けた引数。括弧が無ければ null。</summary>
    public List<List<Token>>? Args { get; init; }
}

public sealed class SeqNested : SeqItem
{
    public KeySeqDef Seq { get; init; } = new();
}

public sealed class KeySeqDef
{
    public string Name { get; init; } = "";
    public List<SeqItem> Items { get; } = new();
}

public enum KeymapKind { Global, Keymap, Keymap2, WindowAnd, WindowOr }

public sealed class Assignment
{
    public NodokaModifier Mod { get; init; } = new();
    public string KeyName { get; init; } = "";
    public KeySeqDef Seq { get; init; } = new();
    public SourceRef Src { get; init; } = new("", 0);
}

public enum AssignOp { New, Add, Sub }
public enum AssignMode { NotModifier, Normal, True, OneShot, OneShotRepeatable, OneShot2 }

public sealed class ModAssign
{
    public ModType Target { get; init; }
    public AssignOp Op { get; init; }
    public AssignMode Mode { get; init; }
    /// <summary>キー名。「mod !!shift」のようにキーを伴わない形では null。</summary>
    public string? KeyName { get; init; }
    public SourceRef Src { get; init; } = new("", 0);
}

public sealed class ComboRule
{
    public NodokaModifier Mod { get; init; } = new();
    public List<string> Keys { get; } = new();
    public KeySeqDef Action { get; init; } = new();
    public SourceRef Src { get; init; } = new("", 0);
}

public sealed class TapHoldRule
{
    public NodokaModifier Mod { get; init; } = new();
    public string KeyName { get; init; } = "";
    public KeySeqDef? Tap { get; set; }
    public KeySeqDef? Hold { get; set; }
    public SourceRef Src { get; init; } = new("", 0);
}

public sealed class TapDanceRule
{
    public NodokaModifier Mod { get; init; } = new();
    public string KeyName { get; init; } = "";
    public KeySeqDef?[] Taps { get; } = new KeySeqDef?[3];
    public SourceRef Src { get; init; } = new("", 0);
}

public sealed class NodokaKeymap
{
    public string Name { get; init; } = "";
    public KeymapKind Kind { get; init; }
    public string ClassRegex { get; init; } = "";
    public string TitleRegex { get; init; } = "";
    public string? Parent { get; set; }
    public KeySeqDef? Default { get; set; }
    public int Order { get; init; }
    public SourceRef Src { get; init; } = new("", 0);
    public List<Assignment> Assignments { get; } = new();
    public List<ModAssign> ModAssigns { get; } = new();
    public List<ComboRule> Combos { get; } = new();
    public List<TapHoldRule> TapHolds { get; } = new();
    public List<TapDanceRule> TapDances { get; } = new();
    public List<(string Event, SourceRef Src)> Events { get; } = new();

    public bool IsWindow => Kind is KeymapKind.WindowAnd or KeymapKind.WindowOr;
}

/// <summary>def subst: 左の (修飾子, キー) を、右の最初の (修飾子, キー) に置き換える。</summary>
public sealed class Substitute
{
    public NodokaModifier Mod { get; init; } = new();
    public string KeyName { get; init; } = "";
    public SeqKey To { get; init; } = new();
    public SourceRef Src { get; init; } = new("", 0);
}

/// <summary>def option UnitID = Kn ExtraInfo 0xXXXX0000（キーボードの識別）。</summary>
public sealed record UnitIdEntry(int KeyNumber, uint ExtraInfo, SourceRef Src);

public enum DiagLevel { Error, Warning, Info }

public sealed record Diagnostic(DiagLevel Level, SourceRef? Src, string Text);

/// <summary>読み込んだのどかの設定の全体。</summary>
public sealed class NodokaSetting
{
    public Dictionary<string, NodokaKey> KeysByName { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<NodokaKey> Keys { get; } = new();
    public Dictionary<string, KeySeqDef> KeySeqs { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<NodokaKeymap> Keymaps { get; } = new();
    public List<Substitute> Substitutes { get; } = new();
    public List<UnitIdEntry> UnitIds { get; } = new();
    public Dictionary<string, (string Value, SourceRef Src)> Options { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Symbols { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<Diagnostic> Diagnostics { get; } = new();
    public List<string> LoadedFiles { get; } = new();

    public NodokaKeymap? FindKeymap(string name) => Keymaps.FirstOrDefault(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase));

    public NodokaKey? FindKey(string name) => KeysByName.TryGetValue(name, out var k) ? k : null;
}
