// このファイルの仮想キー名は nodoka4 の vkeytable.cpp（g_vkeyTable）に由来する。
// 移植元: nodoka4 (https://github.com/Applet-LLC/nodoka4)  Copyright 2008-2026 applet  License: EPL-2.0
// nodoka4 は「窓使いの憂鬱 (mayu)」(BSD-3-Clause) の派生。第三者の表示は THIRD-PARTY-NOTICES.txt を参照。

namespace Kasane.Import.Nodoka;

/// <summary>&amp;VK(...) の仮想キー名（nodoka4 の vkeytable.cpp）→ kasane で出せるキー（スキャンコード）かマウスのボタン。</summary>
public static class NodokaVKeys
{
    public sealed record Target(ushort? ScanCode, string? MouseButton);

    private static readonly Dictionary<string, Target> Table = Build();

    public static Target? Find(string name) => Table.TryGetValue(name, out var t) ? t : null;

    private static Dictionary<string, Target> Build()
    {
        var d = new Dictionary<string, Target>(StringComparer.OrdinalIgnoreCase);
        void K(string n, ushort sc) => d[n] = new Target(sc, null);
        void M(string n, string button) => d[n] = new Target(null, button);

        M("LBUTTON", "Left"); M("RBUTTON", "Right"); M("MBUTTON", "Middle"); M("XBUTTON1", "X1"); M("XBUTTON2", "X2");
        K("BACK", 0x0E); K("TAB", 0x0F); K("RETURN", 0x1C); K("SHIFT", 0x2A); K("CONTROL", 0x1D); K("MENU", 0x38);
        K("CAPITAL", 0x3A); K("KANA", 0x70); K("HANGEUL", 0x70); K("HANGUL", 0x70); K("KANJI", 0x29); K("HANJA", 0x29);
        K("ESCAPE", 0x01); K("CONVERT", 0x79); K("NONCONVERT", 0x7B); K("SPACE", 0x39);
        K("PRIOR", 0xE049); K("NEXT", 0xE051); K("END", 0xE04F); K("HOME", 0xE047);
        K("LEFT", 0xE04B); K("UP", 0xE048); K("RIGHT", 0xE04D); K("DOWN", 0xE050);
        K("SNAPSHOT", 0xE037); K("INSERT", 0xE052); K("DELETE", 0xE053);
        ushort[] digits = { 0x0B, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A };
        for (int i = 0; i <= 9; i++) K($"_{i}", digits[i]);
        const string qwerty = "QWERTYUIOP";
        for (int i = 0; i < qwerty.Length; i++) K(qwerty[i].ToString(), (ushort)(0x10 + i));
        const string asdf = "ASDFGHJKL";
        for (int i = 0; i < asdf.Length; i++) K(asdf[i].ToString(), (ushort)(0x1E + i));
        const string zxcv = "ZXCVBNM";
        for (int i = 0; i < zxcv.Length; i++) K(zxcv[i].ToString(), (ushort)(0x2C + i));
        K("LWIN", 0xE05B); K("RWIN", 0xE05C); K("APPS", 0xE05D); K("SLEEP", 0xE05F);
        ushort[] pad = { 0x52, 0x4F, 0x50, 0x51, 0x4B, 0x4C, 0x4D, 0x47, 0x48, 0x49 };
        for (int i = 0; i <= 9; i++) K($"NUMPAD{i}", pad[i]);
        K("MULTIPLY", 0x37); K("ADD", 0x4E); K("SUBTRACT", 0x4A); K("DECIMAL", 0x53); K("DIVIDE", 0xE035);
        for (int i = 1; i <= 10; i++) K($"F{i}", (ushort)(0x3A + i));
        K("F11", 0x57); K("F12", 0x58);
        for (int i = 13; i <= 23; i++) K($"F{i}", (ushort)(0x64 + i - 13));
        K("F24", 0x76);
        K("NUMLOCK", 0x45); K("SCROLL", 0x46);
        K("LSHIFT", 0x2A); K("RSHIFT", 0x36); K("LCONTROL", 0x1D); K("RCONTROL", 0xE01D); K("LMENU", 0x38); K("RMENU", 0xE038);
        K("BROWSER_BACK", 0xE06A); K("BROWSER_FORWARD", 0xE069); K("BROWSER_REFRESH", 0xE067); K("BROWSER_STOP", 0xE068);
        K("BROWSER_SEARCH", 0xE065); K("BROWSER_FAVORITES", 0xE066); K("BROWSER_HOME", 0xE032);
        K("VOLUME_MUTE", 0xE020); K("VOLUME_DOWN", 0xE02E); K("VOLUME_UP", 0xE030);
        K("MEDIA_NEXT_TRACK", 0xE019); K("MEDIA_PREV_TRACK", 0xE010); K("MEDIA_STOP", 0xE024); K("MEDIA_PLAY_PAUSE", 0xE022);
        K("LAUNCH_MAIL", 0xE06C); K("LAUNCH_MEDIA_SELECT", 0xE06D); K("LAUNCH_APP1", 0xE06B); K("LAUNCH_APP2", 0xE021);
        K("OEM_1", 0x27); K("OEM_PLUS", 0x0D); K("OEM_COMMA", 0x33); K("OEM_MINUS", 0x0C); K("OEM_PERIOD", 0x34);
        K("OEM_2", 0x35); K("OEM_3", 0x29); K("OEM_4", 0x1A); K("OEM_5", 0x2B); K("OEM_6", 0x1B); K("OEM_7", 0x28); K("OEM_102", 0x73);
        return d;
    }
}
