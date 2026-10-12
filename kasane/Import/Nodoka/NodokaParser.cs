// このファイルは nodoka4 の setting.cpp（SettingLoader）を、設定を読む部分だけ C# に移したもの。
// 移植元: nodoka4 (https://github.com/Applet-LLC/nodoka4)  Copyright 2008-2026 applet  License: EPL-2.0
// nodoka4 は「窓使いの憂鬱 (mayu)」(BSD-3-Clause) の派生。第三者の表示は THIRD-PARTY-NOTICES.txt を参照。

using System.Globalization;

namespace Kasane.Import.Nodoka;

/// <summary>include の探し方（実ファイルでも、テストの偽物でもよい）。</summary>
public interface INodokaFiles
{
    /// <summary>include "name" を探して読む。<paramref name="includedFrom"/> は読んでいるファイル（相対の手がかり）。見つからなければ null。</summary>
    (string Path, string Text)? Open(string name, string? includedFrom);
}

public sealed class NodokaParseException : Exception
{
    public NodokaParseException(string message) : base(message) { }
}

/// <summary>
/// nodoka4 の setting.cpp（SettingLoader）の写し。設定ファイルを読んで <see cref="NodokaSetting"/> にする。
/// 実行時の動作（ウィンドウ操作など）は持たないので、関数は名前と引数のトークンだけを覚える。
/// 読めない行は、診断に出して飛ばす（のどかと同じく、その行だけが無かったことになる）。
/// </summary>
public sealed class NodokaParser
{
    private readonly INodokaFiles _files;
    private readonly NodokaSetting _setting = new();
    private NodokaKeymap? _global;
    private int _keymapOrder;
    private int _depth;
    private const int MaxIncludeDepth = 16;

    public NodokaParser(INodokaFiles files, IEnumerable<string>? symbols = null)
    {
        _files = files;
        foreach (var s in symbols ?? Enumerable.Empty<string>()) _setting.Symbols.Add(s);
    }

    /// <summary>最初のファイルを読む。<paramref name="text"/> と <paramref name="path"/> は呼び出し側が用意する。</summary>
    public NodokaSetting Load(string path, string text)
    {
        _global = AddKeymap(new NodokaKeymap { Name = "Global", Kind = KeymapKind.Global, Order = _keymapOrder++, Src = new SourceRef(path, 0) });
        var loader = new Loader(this, path, _global, new NodokaModifier().Release(ModType.ImeComp), null);
        loader.Defaults();
        loader.Run(text);
        return _setting;
    }

    private NodokaKeymap AddKeymap(NodokaKeymap k)
    {
        var existing = _setting.FindKeymap(k.Name);
        if (existing != null) return existing;
        _setting.Keymaps.Add(k);
        return k;
    }

    private void Diag(DiagLevel level, SourceRef? src, string text) => _setting.Diagnostics.Add(new Diagnostic(level, src, text));

    /// <summary>1つのファイルを読むときの状態（SettingLoader 1個に当たる）。</summary>
    private sealed class Loader
    {
        private readonly NodokaParser _p;
        private readonly string _file;
        private NodokaKeymap _current;
        private NodokaModifier _defaultAssign;
        private NodokaModifier _defaultKeySeq;
        private readonly List<bool> _canRead = new();
        private List<Token> _tokens = new();
        private int _ti;
        private int _line;

        public Loader(NodokaParser p, string file, NodokaKeymap current, NodokaModifier defaultAssign, NodokaModifier? defaultKeySeq)
        {
            _p = p;
            _file = file;
            _current = current;
            _defaultAssign = defaultAssign.Clone();
            // SettingLoader のコンストラクタ: m_defaultKeySeqModifier = m_defaultAssignModifier.release(ImeComp)
            _defaultKeySeq = defaultKeySeq?.Clone() ?? defaultAssign.Clone();
        }

        public void Defaults() { }

        private NodokaSetting S => _p._setting;
        private SourceRef Here => new(_file, _line);

        private bool IsEol => _ti >= _tokens.Count;

        private Token Get()
        {
            if (IsEol) throw new NodokaParseException("too few words.");
            return _tokens[_ti++];
        }

        private Token Look()
        {
            if (IsEol) throw new NodokaParseException("too few words.");
            return _tokens[_ti];
        }

        public void Run(string text)
        {
            S.LoadedFiles.Add(_file);
            var lines = NodokaTokenizer.Tokenize(NodokaText.SplitLines(text), (line, msg) =>
                _p.Diag(DiagLevel.Error, new SourceRef(_file, line), msg));
            foreach (var l in lines)
            {
                _tokens = l.Tokens;
                _ti = 0;
                _line = l.Line;
                try
                {
                    LoadLine();
                    if (!IsEol) _p.Diag(DiagLevel.Warning, Here, "back garbage is ignored.");
                }
                catch (NodokaParseException ex)
                {
                    _p.Diag(DiagLevel.Error, Here, ex.Message);
                }
                catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException or InvalidOperationException)
                {
                    _p.Diag(DiagLevel.Error, Here, "読めませんでした: " + ex.Message); // 数値の範囲など。ほかの行の変換は続ける
                }
            }
            if (_canRead.Count > 0) _p.Diag(DiagLevel.Error, new SourceRef(_file, _line), "unbalanced `if'.  you forget `endif', didn't you?");
        }

        // ---- 行 ----

        private void LoadLine()
        {
            var t = Get();
            if (t.Is("if") || t.Is("and")) LoadIf();
            else if (t.Is("else")) LoadElse(false, t.Text);
            else if (t.Is("elseif") || t.Is("elsif") || t.Is("elif") || t.Is("or")) LoadElse(true, t.Text);
            else if (t.Is("endif")) LoadEndif("endif");
            else if (_canRead.Count > 0 && !_canRead[^1]) { while (!IsEol) Get(); }
            else if (t.Is("define")) S.Symbols.Add(Get().Text);
            else if (t.Is("include")) LoadInclude();
            else if (t.Is("def")) LoadKeyboardDefinition();
            else if (t.Is("keymap") || t.Is("keymap2") || t.Is("window")) LoadKeymapDefinition(t);
            else if (t.Is("key")) LoadKeyAssign();
            else if (t.Is("event")) LoadEventAssign();
            else if (t.Is("mod")) LoadModifierAssignment();
            else if (t.Is("keyseq")) LoadKeySeqDefinition();
            else if (t.Is("combo")) LoadCombo();
            else if (t.Is("taphold")) LoadTapHold();
            else if (t.Is("tapdance")) LoadTapDance();
            else throw new NodokaParseException($"syntax error `{t}'.");
        }

        private void LoadIf()
        {
            if (!Get().IsOpen) throw new NodokaParseException("there must be `(' after `if'.");
            var t = Get();
            bool not = false;
            if (t.Is("!")) { not = true; t = Get(); }
            bool exists = S.Symbols.Contains(t.Text);
            bool doesRead = (exists && !not) || (!exists && not);
            if (_canRead.Count > 0) doesRead = doesRead && _canRead[^1];
            if (!Get().IsClose) throw new NodokaParseException("there must be `)'.");

            _canRead.Add(doesRead);
            if (!IsEol)
            {
                int len = _canRead.Count;
                LoadLine();
                if (len < _canRead.Count)
                {
                    bool r = _canRead[^1];
                    _canRead.RemoveAt(_canRead.Count - 1);
                    _canRead[len - 1] = r && doesRead;
                }
                else if (len == _canRead.Count) _canRead.RemoveAt(_canRead.Count - 1);
            }
        }

        private void LoadElse(bool isElseIf, string token)
        {
            bool doesRead = !LoadEndif(token);
            if (_canRead.Count > 0) doesRead = doesRead && _canRead[^1];
            _canRead.Add(doesRead);
            if (!IsEol)
            {
                int len = _canRead.Count;
                if (isElseIf) LoadIf(); else LoadLine();
                if (len < _canRead.Count)
                {
                    bool r = _canRead[^1];
                    _canRead.RemoveAt(_canRead.Count - 1);
                    _canRead[len - 1] = doesRead && r;
                }
                else if (len == _canRead.Count) _canRead.RemoveAt(_canRead.Count - 1);
            }
        }

        private bool LoadEndif(string token)
        {
            if (_canRead.Count == 0) throw new NodokaParseException($"unbalanced `{token}'");
            bool r = _canRead[^1];
            _canRead.RemoveAt(_canRead.Count - 1);
            return r;
        }

        private void LoadInclude()
        {
            string name = GetString(Get());
            if (_p._depth >= MaxIncludeDepth) throw new NodokaParseException($"`{name}': include is too deep.");
            var found = _p._files.Open(name, _file);
            if (found == null) throw new NodokaParseException($"`{name}': no such file or other error.");
            _p._depth++;
            try
            {
                // 読み込んだファイルは Global キーマップから始まる（setting.cpp の load(Setting*, filename)）。
                var loader = new Loader(_p, found.Value.Path, _p._global!, _defaultAssign, _defaultKeySeq);
                loader.Run(found.Value.Text);
            }
            finally { _p._depth--; }
        }

        private static string GetString(Token t)
        {
            if (t.IsString) return t.Text;
            throw new NodokaParseException($"`{t}' is not a string.");
        }

        // ---- def ----

        private void LoadKeyboardDefinition()
        {
            var t = Get();
            if (t.Is("key")) LoadDefineKey();
            else if (t.Is("mod")) LoadDefineModifier();
            else if (t.Is("sync")) LoadDefineSync();
            else if (t.Is("alias")) LoadDefineAlias();
            else if (t.Is("subst")) LoadDefineSubst();
            else if (t.Is("option")) LoadDefineOption();
            else throw new NodokaParseException($"syntax error `{t}'.");
        }

        private void LoadScanCodes(NodokaKey key)
        {
            for (int j = 0; j < 4 && !IsEol; j++)
            {
                int flags = 0;
                while (true)
                {
                    var t = Get();
                    if (t.IsNumber) { key.ScanCodes.Add(((byte)t.Number, flags)); break; }
                    if (t.Is("E0-")) flags |= 1;
                    else if (t.Is("E1-")) flags |= 2;
                    else if (t.Is("E0E1-")) flags |= 3;
                    else throw new NodokaParseException($"`{t}': invalid modifier.");
                }
            }
        }

        private void LoadDefineKey()
        {
            var key = new NodokaKey();
            var t = Get();
            if (t.IsOpen)
            {
                key.Names.Add(GetString(Get()));
                while (true)
                {
                    t = Get();
                    if (t.IsClose) break;
                    key.Names.Add(GetString(t));
                }
                if (!Get().Is("=")) throw new NodokaParseException("there must be `=' after `)'.");
            }
            else
            {
                key.Names.Add(GetString(t));
                while (true)
                {
                    t = Get();
                    if (t.Is("=")) break;
                    key.Names.Add(GetString(t));
                }
            }
            LoadScanCodes(key);
            S.Keys.Add(key);
            foreach (var n in key.Names) S.KeysByName[n] = key;
        }

        private void LoadDefineModifier()
        {
            var t = Get();
            if (!(t.Is("shift") || t.Is("S") || t.Is("alt") || t.Is("meta") || t.Is("menu") || t.Is("A") || t.Is("M")
                  || t.Is("control") || t.Is("ctrl") || t.Is("C") || t.Is("windows") || t.Is("win") || t.Is("W")))
                throw new NodokaParseException($"`{t}': invalid modifier name.");
            if (!Get().Is("=")) throw new NodokaParseException("there must be `=' after modifier name.");
            while (!IsEol) Get(); // 修飾キーに使うキーの並び。変換では使わない
        }

        private void LoadDefineSync()
        {
            var key = new NodokaKey();
            key.Names.Add("sync");
            if (!Get().Is("=")) throw new NodokaParseException("there must be `=' after `sync'.");
            LoadScanCodes(key);
        }

        private void LoadDefineAlias()
        {
            var name = Get();
            if (!Get().Is("=")) throw new NodokaParseException("there must be `=' after `alias'.");
            var t = Get();
            var key = S.FindKey(GetString(t)) ?? throw new NodokaParseException($"`{t}': invalid key name @ define alias");
            S.KeysByName[GetString(name)] = key;
        }

        private void LoadDefineSubst()
        {
            var lefts = new List<(NodokaModifier Mod, string Key)>();
            do
            {
                var mod = LoadModifier(NodokaModifier.Assign, _defaultAssign, out _);
                lefts.Add((mod, GetString(Get())));
            } while (!(Look().Is("=>") || Look().Is("=")));
            Get();
            var seq = LoadKeySequence("", false, NodokaModifier.Assign);
            var first = seq.Items.OfType<SeqKey>().FirstOrDefault() ?? throw new NodokaParseException("no key is specified for substitute.");
            foreach (var (mod, key) in lefts)
                S.Substitutes.Add(new Substitute { Mod = mod, KeyName = key, To = first, Src = Here });
        }

        private void LoadDefineOption()
        {
            var name = Get();
            string n = name.Text;
            if (n.Equals("UnitID", StringComparison.OrdinalIgnoreCase)) { LoadUnitId(); return; }
            // 値が1つのオプション（ComboWindow など）は覚える。それ以外は、行の残りを読み捨てる（変換に関係しない）。
            if (!IsEol && Look().Is("=")) Get();
            var values = new List<string>();
            while (!IsEol) values.Add(Get().Text);
            S.Options[n] = (string.Join(" ", values), Here);
        }

        private void LoadUnitId()
        {
            if (!Get().Is("=")) throw new NodokaParseException("there must be `=' after `def option UnitID'.");
            var k = Get();
            int keyNum = -1;
            for (int i = 0; i <= 7; i++) if (k.Is($"K{i}")) keyNum = i;
            if (keyNum < 0) throw new NodokaParseException($"`{k}': invalid KeyNumber. Use K1 to K7.");
            var rest = new List<Token>();
            while (!IsEol) rest.Add(Get());
            if (rest.Count >= 2 && rest[0].Is("ExtraInfo") && rest[1].IsNumber)
            {
                // 数値は int に収まらないことがあるので、元のテキストを読み直す（setting.cpp と同じ）。
                string text = rest[1].Text;
                uint extra = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? uint.Parse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
                    : uint.Parse(text, CultureInfo.InvariantCulture);
                S.UnitIds.Add(new UnitIdEntry(keyNum, extra, Here));
            }
            else
            {
                _p.Diag(DiagLevel.Warning, Here, $"def option UnitID = K{keyNum} {string.Join(" ", rest.Select(r => r.Text))}: ExtraInfo の形ではないため、キーボードを特定できません（ベンダーID・製品IDの形は対応しません）");
            }
        }

        // ---- 修飾子 ----

        private static readonly (string Prefix, ModType Type)[] ModMap = BuildModMap();

        private static (string, ModType)[] BuildModMap()
        {
            var list = new List<(string, ModType)>
            {
                ("S-", ModType.Shift), ("C-", ModType.Control), ("A-", ModType.Alt), ("M-", ModType.Alt), ("W-", ModType.Windows),
                ("U-", ModType.Up), ("D-", ModType.Down),
                ("R-", ModType.Repeat), ("IL-", ModType.ImeLock), ("IC-", ModType.ImeComp), ("I-", ModType.ImeComp),
                ("NL-", ModType.NumLock), ("CL-", ModType.CapsLock), ("SL-", ModType.ScrollLock), ("KL-", ModType.KanaLock),
                ("MAX-", ModType.Maximized), ("MIN-", ModType.Minimized), ("MMAX-", ModType.MdiMaximized), ("MMIN-", ModType.MdiMinimized),
                ("T-", ModType.Touchpad), ("TS-", ModType.TouchpadSticky), ("TL-", ModType.TouchpadL), ("TLS-", ModType.TouchpadLSticky),
                ("TR-", ModType.TouchpadR), ("TRS-", ModType.TouchpadRSticky),
            };
            for (int i = 0; i <= 9; i++) list.Add(($"M{i}-", ModType.Mod0 + i));
            for (int i = 0; i <= 15; i++) list.Add(($"L{i:X}-", ModType.Lock0 + i));
            for (int i = 0; i <= 7; i++) list.Add(($"K{i}-", ModType.Keyboard0 + i));
            list.AddRange(new[] { ("IW-", ModType.ImeCandi), ("IH-", ModType.Harf), ("IK-", ModType.Katakana), ("IJ-", ModType.Native), ("DP-", ModType.DP) });
            return list.ToArray();
        }

        private enum Flag { Press, Release, Dontcare }

        /// <summary>load_MODIFIER の写し。<paramref name="mode"/> は、その文脈で使える修飾子の範囲（keyseq なら <see cref="NodokaModifier.KeySeq"/>）。</summary>
        private NodokaModifier LoadModifier(ModType mode, NodokaModifier start, out ModType? outMode)
        {
            outMode = ModType.Shift; // Type_begin
            var modifier = start.Clone();
            var spec = new bool[(int)ModType.End];
            var flag = Flag.Press;

            for (int i = (int)mode; i < (int)NodokaModifier.Assign; i++)
            {
                modifier.Dontcare((ModType)i);
                spec[i] = true;
            }

            while (!IsEol)
            {
                var t = Look();
                bool matched = false;
                foreach (var (prefix, mt) in ModMap)
                {
                    if (!t.Is(prefix)) continue;
                    Get();
                    if ((int)mode <= (int)mt) throw new NodokaParseException($"`{t}': invalid modifier at this context.");
                    switch (flag)
                    {
                        case Flag.Press: modifier.Press(mt); break;
                        case Flag.Release: modifier.Release(mt); break;
                        default: modifier.Dontcare(mt); break;
                    }
                    spec[(int)mt] = true;
                    flag = Flag.Press;
                    if (outMode is ModType om && om < mt)
                    {
                        if (mt < NodokaModifier.Basic) outMode = NodokaModifier.Basic;
                        else if (mt < NodokaModifier.KeySeq) outMode = NodokaModifier.KeySeq;
                        else if (mt < NodokaModifier.Assign) outMode = NodokaModifier.Assign;
                    }
                    matched = true;
                    break;
                }
                if (matched) continue;
                if (t.Is("*")) { Get(); flag = Flag.Dontcare; continue; }
                if (t.Is("~")) { Get(); flag = Flag.Release; continue; }
                break;
            }

            for (int i = 0; i < (int)ModType.End; i++)
            {
                if (spec[i]) continue;
                if (flag == Flag.Release) modifier.Release((ModType)i);
                else if (flag == Flag.Dontcare) modifier.Dontcare((ModType)i);
            }

            // Up / Down の整合
            bool dcUp = modifier.IsDontcare(ModType.Up), dcDown = modifier.IsDontcare(ModType.Down);
            bool onUp = modifier.IsPressed(ModType.Up), onDown = modifier.IsPressed(ModType.Down);
            if (dcUp && dcDown) { }
            else if (dcUp) modifier.Press(ModType.Up, !onDown);
            else if (dcDown) modifier.Press(ModType.Down, !onUp);
            else if (onUp == onDown) { modifier.Dontcare(ModType.Up); modifier.Dontcare(ModType.Down); }

            if (!spec[(int)ModType.Repeat]) modifier.Dontcare(ModType.Repeat);
            return modifier;
        }

        // ---- キーマップ ----

        private void LoadKeymapDefinition(Token which)
        {
            var name = Get();
            var kind = KeymapKind.Keymap;
            string cls = "", title = "";
            bool doesLoadDefault = false;

            if (!IsEol)
            {
                var t = Look();
                if (which.Is("window"))
                {
                    if (t.IsOpen)
                    {
                        Get();
                        cls = GetRegexp(Get());
                        t = Get();
                        if (t.Is("&&")) kind = KeymapKind.WindowAnd;
                        else if (t.Is("||")) kind = KeymapKind.WindowOr;
                        else throw new NodokaParseException($"`{t}': unknown operator.");
                        title = GetRegexp(Get());
                        if (!Get().IsClose) throw new NodokaParseException("there must be `)'.");
                    }
                    else if (t.IsRegexp)
                    {
                        Get();
                        kind = KeymapKind.WindowAnd;
                        cls = t.Text;
                    }
                }
                else if (which.Is("keymap2")) kind = KeymapKind.Keymap2;
                if (!IsEol) doesLoadDefault = true;
            }

            var created = new NodokaKeymap
            {
                Name = GetString(name), Kind = kind, ClassRegex = cls, TitleRegex = title, Order = _p._keymapOrder, Src = Here,
            };
            var existing = S.FindKeymap(created.Name);
            if (existing == null) { _p._keymapOrder++; S.Keymaps.Add(created); }
            _current = existing ?? created;

            KeySeqDef? keySeq = null;
            string? parent = null;
            if (doesLoadDefault)
            {
                var t = Look();
                if (t.Is(":"))
                {
                    Get();
                    t = Get();
                    var pk = S.FindKeymap(GetString(t)) ?? throw new NodokaParseException($"`{t}': unknown keymap name.");
                    parent = pk.Name;
                }
                if (!IsEol)
                {
                    t = Get();
                    if (!(t.Is("=>") || t.Is("="))) throw new NodokaParseException($"`{t}': syntax error.");
                    keySeq = LoadKeySequence("", false, NodokaModifier.KeySeq);
                }
            }
            if (existing == null || (_current.Default == null && _current.Parent == null))
            {
                _current.Default ??= keySeq;
                _current.Parent ??= parent;
            }
        }

        private static string GetRegexp(Token t)
        {
            if (t.IsRegexp) return t.Text;
            throw new NodokaParseException($"`{t}' is not a regexp.");
        }

        // ---- キーシーケンス ----

        private KeySeqDef LoadKeySequence(string name, bool inParen, ModType mode)
        {
            var seq = new KeySeqDef { Name = name };
            while (!IsEol)
            {
                var modifier = LoadModifier(mode, _defaultKeySeq, out _);
                var t = Look();
                if (t.IsClose && inParen) break;
                if (t.IsOpen)
                {
                    Get();
                    var nested = LoadKeySequence("", true, mode);
                    Get(); // 閉じ括弧
                    seq.Items.Add(new SeqNested { Seq = nested, Src = Here });
                }
                else if (t.Is("$"))
                {
                    Get();
                    t = Get();
                    if (!S.KeySeqs.TryGetValue(GetString(t), out var referenced)) throw new NodokaParseException($"`${t}': unknown keyseq name.");
                    seq.Items.Add(new SeqNested { Seq = referenced, Src = Here });
                }
                else if (t.Is("&"))
                {
                    Get();
                    var fn = Get();
                    seq.Items.Add(new SeqFunc { Mod = modifier, Name = GetString(fn), Args = LoadFunctionArguments(), Src = Here });
                }
                else
                {
                    var key = Get();
                    seq.Items.Add(new SeqKey { Mod = modifier, KeyName = GetString(key), Src = Here });
                }
            }
            if (name.Length > 0) S.KeySeqs[name] = seq;
            return seq;
        }

        /// <summary>関数の引数: "(" から対応する ")" までを、一番外側のカンマで分ける。括弧が無ければ null。</summary>
        private List<List<Token>>? LoadFunctionArguments()
        {
            if (IsEol || !Look().IsOpen) return null;
            Get();
            var args = new List<List<Token>> { new() };
            int depth = 0;
            while (true)
            {
                var t = Get();
                if (t.IsOpen) depth++;
                else if (t.IsClose)
                {
                    if (depth == 0) break;
                    depth--;
                }
                else if (t.IsComma && depth == 0) { args.Add(new List<Token>()); continue; }
                args[^1].Add(t);
            }
            if (args.Count == 1 && args[0].Count == 0) args.Clear();
            return args;
        }

        private void LoadKeyAssign()
        {
            var mod = LoadModifier(NodokaModifier.Assign, _defaultAssign, out _);
            if (Look().Is("="))
            {
                Get();
                _defaultKeySeq = LoadModifier(NodokaModifier.KeySeq, _defaultKeySeq, out _);
                _defaultAssign = mod;
                return;
            }

            var keys = new List<(NodokaModifier Mod, string Key)>();
            while (true)
            {
                keys.Add((mod, GetString(Get())));
                if (Look().Is("=>") || Look().Is("=")) break;
                mod = LoadModifier(NodokaModifier.Assign, _defaultAssign, out _);
            }
            Get();
            var seq = LoadKeySequence("", false, NodokaModifier.KeySeq);
            foreach (var (m, k) in keys)
                _current.Assignments.Add(new Assignment { Mod = m, KeyName = k, Seq = seq, Src = Here });
        }

        private void LoadEventAssign()
        {
            var name = Get();
            var t = Get();
            if (!(t.Is("=>") || t.Is("="))) throw new NodokaParseException("`=' is expected.");
            LoadKeySequence("", false, NodokaModifier.KeySeq);
            _current.Events.Add((GetString(name), Here));
        }

        private void LoadKeySeqDefinition()
        {
            if (!Get().Is("$")) throw new NodokaParseException("there must be `$' after `keyseq'");
            var name = Get();
            if (!Get().Is("=")) throw new NodokaParseException("there must be `=' after keyseq name");
            LoadKeySequence(GetString(name), false, NodokaModifier.Assign);
        }

        // ---- mod ----

        private static ModType? ModifierName(Token t)
        {
            if (t.Is("shift") || t.Is("S")) return ModType.Shift;
            if (t.Is("alt") || t.Is("meta") || t.Is("menu") || t.Is("A") || t.Is("M")) return ModType.Alt;
            if (t.Is("control") || t.Is("ctrl") || t.Is("C")) return ModType.Control;
            if (t.Is("windows") || t.Is("win") || t.Is("W")) return ModType.Windows;
            for (int i = 0; i <= 9; i++) if (t.Is($"mod{i}") || t.Is($"M{i}")) return ModType.Mod0 + i;
            return null;
        }

        private static AssignMode? BangMode(Token t) =>
            t.Is("!!!!") ? AssignMode.OneShot2 : t.Is("!!!") ? AssignMode.OneShotRepeatable : t.Is("!!") ? AssignMode.OneShot : t.Is("!") ? AssignMode.True : null;

        private void LoadModifierAssignment()
        {
            var t = Get();
            ModType mt;
            while (true)
            {
                AssignMode am = AssignMode.NotModifier;
                if (BangMode(t) is AssignMode bm) { am = bm; t = Get(); }
                mt = ModifierName(t) ?? throw new NodokaParseException($"`{t}': invalid modifier name @ assign mode.");
                if (am == AssignMode.NotModifier) break;
                _current.ModAssigns.Add(new ModAssign { Target = mt, Op = AssignOp.New, Mode = am, KeyName = null, Src = Here });
                if (IsEol) return;
                t = Get();
            }

            t = Get();
            AssignOp op;
            if (t.Is("=")) op = AssignOp.New;
            else if (t.Is("+=")) op = AssignOp.Add;
            else if (t.Is("-=")) op = AssignOp.Sub;
            else throw new NodokaParseException($"`{t}': is unknown operator.");

            while (!IsEol)
            {
                t = Get();
                AssignMode am = AssignMode.Normal;
                if (BangMode(t) is AssignMode bm) { am = bm; t = Get(); }
                _current.ModAssigns.Add(new ModAssign { Target = mt, Op = op, Mode = am, KeyName = GetString(t), Src = Here });
                if (op == AssignOp.New) op = AssignOp.Add;
            }
        }

        // ---- combo / taphold / tapdance ----

        private NodokaModifier LoadModifierForRule() => LoadModifier(NodokaModifier.Assign, new NodokaModifier().DontcareAll(), out _);

        private void LoadCombo()
        {
            var rule = new ComboRuleBuilder { Mod = LoadModifierForRule() };
            while (!IsEol)
            {
                var t = Look();
                if (t.Is("=")) { Get(); break; }
                if (t.Is("window"))
                {
                    Get();
                    if (!Get().Is("=")) throw new NodokaParseException("'=' expected after 'window' in combo definition.");
                    Get(); // 数値
                    continue;
                }
                rule.Keys.Add(GetString(Get()));
            }
            if (rule.Keys.Count < 2 || rule.Keys.Count > 6) throw new NodokaParseException("combo requires 2 to 6 keys.");
            var action = LoadKeySequence("", false, NodokaModifier.KeySeq);
            var c = new ComboRule { Mod = rule.Mod, Action = action, Src = Here };
            c.Keys.AddRange(rule.Keys);
            _current.Combos.Add(c);
        }

        private sealed class ComboRuleBuilder
        {
            public NodokaModifier Mod { get; init; } = new();
            public List<string> Keys { get; } = new();
        }

        private KeySeqDef LoadSingleKeySequence()
        {
            var seq = new KeySeqDef();
            var basis = _defaultKeySeq.Clone();
            for (int i = 0; i < (int)NodokaModifier.Basic; i++) basis.Dontcare((ModType)i);
            var modifier = LoadModifier(NodokaModifier.KeySeq, basis, out _);
            if (IsEol) throw new NodokaParseException("action expected.");
            var t = Look();
            if (t.IsOpen)
            {
                Get();
                var nested = LoadKeySequence("", true, NodokaModifier.KeySeq);
                Get();
                seq.Items.Add(new SeqNested { Seq = nested, Src = Here });
            }
            else if (t.Is("&"))
            {
                Get();
                var fn = Get();
                seq.Items.Add(new SeqFunc { Mod = modifier, Name = GetString(fn), Args = LoadFunctionArguments(), Src = Here });
            }
            else
            {
                seq.Items.Add(new SeqKey { Mod = modifier, KeyName = GetString(Get()), Src = Here });
            }
            return seq;
        }

        private void LoadTapHold()
        {
            var mod = LoadModifierForRule();
            var rule = new TapHoldRule { Mod = mod, KeyName = GetString(Get()), Src = Here };
            if (!IsEol && Look().Is("=")) Get();
            while (!IsEol)
            {
                var t = Look();
                if (t.Is("tap")) { Get(); ExpectEq("tap"); rule.Tap = LoadSingleKeySequence(); }
                else if (t.Is("hold")) { Get(); ExpectEq("hold"); rule.Hold = LoadSingleKeySequence(); }
                else if (t.Is("threshold") || t.Is("interrupt") || t.Is("permissive_hold") || t.Is("hold_on_other_key") || t.Is("quick_tap_term"))
                {
                    Get();
                    ExpectEq(t.Text);
                    Get(); // 値（キーボード全体の設定として扱うので、規則ごとの値は使わない）
                }
                else throw new NodokaParseException($"unexpected token `{t}' in taphold definition.");
            }
            if (rule.Tap == null) throw new NodokaParseException("taphold: 'tap=' is required.");
            if (rule.Hold == null) throw new NodokaParseException("taphold: 'hold=' is required.");
            _current.TapHolds.Add(rule);
        }

        private void LoadTapDance()
        {
            var mod = LoadModifierForRule();
            var rule = new TapDanceRule { Mod = mod, KeyName = GetString(Get()), Src = Here };
            if (!IsEol && Look().Is("=")) Get();
            while (!IsEol)
            {
                var t = Look();
                int idx = t.Is("tap1") ? 0 : t.Is("tap2") ? 1 : t.Is("tap3") ? 2 : -1;
                if (idx >= 0) { Get(); ExpectEq(t.Text); rule.Taps[idx] = LoadSingleKeySequence(); }
                else if (t.Is("timeout")) { Get(); ExpectEq("timeout"); Get(); }
                else throw new NodokaParseException($"unexpected token `{t}' in tapdance definition.");
            }
            if (rule.Taps[0] == null) throw new NodokaParseException("tapdance: 'tap1=' is required.");
            _current.TapDances.Add(rule);
        }

        private void ExpectEq(string what)
        {
            if (!Get().Is("=")) throw new NodokaParseException($"'=' expected after '{what}'.");
        }
    }
}
