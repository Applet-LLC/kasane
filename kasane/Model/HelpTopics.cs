namespace Kasane.Model;

/// <summary>マニュアルの節（章のファイルと、節のアンカー）。アンカーが null なら、章の先頭。</summary>
public sealed record HelpSection(string Page, string? Anchor);

/// <summary>
/// 画面（タブ・パネル）と、それを説明するマニュアルの節の対応（2026-11-08、UI の見直し）。画面の「?」ボタンと F1 が使う。
/// 画面には説明の段落を出さず、ここで指す節に書く。アンカーは、マニュアルの原稿（docs/manual/src/ja）に <c>&lt;a id="…"&gt;&lt;/a&gt;</c> で明示して置く
/// （見出しの自動の id は、節を足すとずれるため）。すべての項目が、作り直したマニュアルに実際にあることを、単体テスト（HelpTopicsTests）で確かめる。
/// </summary>
public static class HelpTopics
{
    // ---- 画面の名前（HelpButton と F1 に渡す） ----
    public const string Editor = "editor";
    public const string SelectKeyboard = "select-keyboard";
    public const string KleImport = "kle-import";
    public const string KeyboardView = "keyboard-view";
    public const string RemapText = "remap-text";
    public const string CalibrationCheck = "calibration-check";
    public const string CalibrationSingle = "calibration-single";
    public const string CalibrationAll = "calibration-all";
    public const string Emergency = "emergency";
    public const string EmergencyTest = "emergency-test";
    public const string KeyAssign = "key-assign";
    public const string KeyAssignCapture = "key-assign-capture";
    public const string KeyAssignKeyboardView = "key-assign-keyboard-view";
    public const string KeyAssignLayer = "key-assign-layer";
    public const string KeyAssignModTap = "key-assign-mod-tap";
    public const string KeyAssignMacroHelper = "key-assign-macro-helper";
    public const string KeyAssignComboOverride = "key-assign-combo-override";
    public const string EditorsCommon = "editors-common";
    public const string ListOps = "list-ops";
    public const string Macro = "macro";
    public const string Combo = "combo";
    public const string TapDance = "tapdance";
    public const string KeyOverride = "key-override";
    public const string ImeLayer = "ime-layer";
    public const string NicolaTable = "nicola-table";
    public const string NicolaImeLink = "nicola-ime-link";
    public const string HelperTab = "helper-tab";
    public const string HelperAction = "helper-action";
    public const string HelperSequence = "helper-sequence";
    public const string HelperIme = "helper-ime";
    public const string HelperTrigger = "helper-trigger";
    public const string AppRules = "app-rules";
    public const string SettingsGeneral = "settings-general";
    public const string SettingsFeatures = "settings-features";
    public const string SettingsTiming = "settings-timing";
    public const string SettingsScope = "settings-scope";
    public const string SettingsDataDir = "settings-data-dir";
    public const string SettingsI8042 = "settings-i8042";
    public const string SettingsDesktop = "settings-desktop";
    public const string SettingsAbout = "settings-about";
    public const string Export = "export";
    public const string Import = "import";
    public const string NodokaImport = "nodoka-import";
    public const string Reset = "reset";

    /// <summary>画面の名前 → 節。</summary>
    public static readonly IReadOnlyDictionary<string, HelpSection> All = new Dictionary<string, HelpSection>
    {
        [Editor] = new("05-editor-basics.html", "editor-parts"),
        [SelectKeyboard] = new("05-editor-basics.html", "select-keyboard"),
        [KleImport] = new("05-editor-basics.html", "kle-import"),
        [KeyboardView] = new("05-editor-basics.html", "keyboard-view"),
        [RemapText] = new("05-editor-basics.html", "remap-text"),
        [CalibrationCheck] = new("06-calibration.html", "calibration-check"),
        [CalibrationSingle] = new("06-calibration.html", "calibration-single"),
        [CalibrationAll] = new("06-calibration.html", "calibration-all"),
        [Emergency] = new("07-emergency.html", null),
        [EmergencyTest] = new("07-emergency.html", "emergency-test"),
        [KeyAssign] = new("08-key-assign.html", "categories"),
        [KeyAssignCapture] = new("08-key-assign.html", "capture"),
        [KeyAssignKeyboardView] = new("08-key-assign.html", "keyboard-view-pick"),
        [KeyAssignLayer] = new("08-key-assign.html", "layer-actions"),
        [KeyAssignModTap] = new("08-key-assign.html", "mod-tap"),
        [KeyAssignMacroHelper] = new("08-key-assign.html", "macro-tapdance-helper"),
        [KeyAssignComboOverride] = new("08-key-assign.html", "combo-keyoverride"),
        [EditorsCommon] = new("09-advanced.html", "editors-common"),
        [ListOps] = new("09-advanced.html", "list-ops"),
        [Macro] = new("09-advanced.html", "macro"),
        [Combo] = new("09-advanced.html", "combo"),
        [TapDance] = new("09-advanced.html", "tapdance"),
        [KeyOverride] = new("09-advanced.html", "key-override"),
        [ImeLayer] = new("10-ime-nicola.html", "ime-layer"),
        [NicolaTable] = new("10-ime-nicola.html", "nicola-table"),
        [NicolaImeLink] = new("10-ime-nicola.html", "nicola-ime-link"),
        [HelperTab] = new("11-helper-apprules.html", "helper-tab"),
        [HelperAction] = new("11-helper-apprules.html", "helper-kinds"),
        [HelperSequence] = new("11-helper-apprules.html", "helper-sequence"),
        [HelperIme] = new("11-helper-apprules.html", "helper-ime"),
        [HelperTrigger] = new("11-helper-apprules.html", "trigger-notes"),
        [AppRules] = new("11-helper-apprules.html", "app-rules"),
        [SettingsGeneral] = new("13-settings.html", "general"),
        [SettingsFeatures] = new("13-settings.html", "features"),
        [SettingsTiming] = new("13-settings.html", "timing"),
        [SettingsScope] = new("13-settings.html", "scope"),
        [SettingsDataDir] = new("13-settings.html", "data-dir"),
        [SettingsI8042] = new("13-settings.html", "i8042"),
        [SettingsDesktop] = new("13-settings.html", "desktop"),
        [SettingsAbout] = new("13-settings.html", "about"),
        [Export] = new("14-import-export.html", "export"),
        [Import] = new("14-import-export.html", "import"),
        [NodokaImport] = new("14-import-export.html", "nodoka"),
        [Reset] = new("14-import-export.html", "reset"),
    };

    /// <summary>画面の名前の節（知らない名前なら、エディタの節）。</summary>
    public static HelpSection For(string topic) => All.TryGetValue(topic, out var s) ? s : All[Editor];
}
