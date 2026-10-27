using Kasane.Model;
using Microsoft.Win32;

namespace Kasane.Tray;

/// <summary>
/// Windows 11 のタスクバー設定（HKCU\Control Panel\NotifyIconSettings）で、このトレイのアイコンを「表に表示」にする。
/// 項目はエクスプローラーがアイコン登録の後に作る。IsPromoted が無いうちだけ 1 を書く（利用者がオフにすると、シェルが 0 を書くので、その選択は上書きしない）。
/// 項目は exe のフルパスごとに作られる。パスは Program Files などを GUID で表した形（{GUID}\applet\kasane\tray\kasane-tray.exe）で保存されることがある。
/// </summary>
internal sealed class TrayIconPromoter : IDisposable
{
    private const string BaseKey = @"Control Panel\NotifyIconSettings";
    private const int IntervalMs = 1000;
    private const int MaxAttempts = 30;

    private readonly string _exePath;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = IntervalMs };
    private int _attempts;

    public TrayIconPromoter(string exePath)
    {
        _exePath = exePath;
        _timer.Tick += (_, _) => Tick();
    }

    public void Start() => _timer.Start();

    private void Tick()
    {
        _attempts++;
        bool done = false;
        try { done = TryPromote(); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            TrayLog.Write("promote-icon: " + ex.Message);
            done = true;
        }
        if (done || _attempts >= MaxAttempts) _timer.Stop();
    }

    /// <summary>自分の項目が見つかったら true（書いた・既に利用者の選択がある）。まだ無ければ false。</summary>
    private bool TryPromote()
    {
        using var root = OpenRoot();
        if (root == null) return false;
        foreach (string name in root.GetSubKeyNames())
        {
            using var sub = root.OpenSubKey(name, writable: true);
            if (sub == null || !PathMatches(sub.GetValue("ExecutablePath") as string, _exePath)) continue;
            if (sub.GetValue("IsPromoted") == null) sub.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
            return true;
        }
        return false;
    }

    /// <summary>
    /// タスクバーの設定の置き場。タスクバーはエクスプローラーを動かしているユーザーのもの。管理者保護では、トレイが ADMIN_ユーザー名 で動くので、
    /// 自分の HKCU ではなく、エクスプローラーのユーザーの HKCU（HKEY_USERS\&lt;SID&gt;）を開く。昇格していないトレイ（Administrators が deny-only）は、
    /// そこを開けない（UnauthorizedAccessException。呼び出し側がログに記録して諦める）。
    /// </summary>
    private static RegistryKey? OpenRoot()
    {
        var shell = AdminProtection.GetShellUserSid();
        var self = System.Security.Principal.WindowsIdentity.GetCurrent().User;
        if (shell != null && self != null && !shell.Equals(self))
            return Registry.Users.OpenSubKey(shell.Value + @"\" + BaseKey);
        return Registry.CurrentUser.OpenSubKey(BaseKey);
    }

    /// <summary>保存されたパス（フルパス、または {GUID}\… の形）が、実行中の exe のパスと同じか。</summary>
    internal static bool PathMatches(string? stored, string exePath)
    {
        if (string.IsNullOrEmpty(stored)) return false;
        if (string.Equals(stored, exePath, StringComparison.OrdinalIgnoreCase)) return true;
        if (stored[0] != '{') return false;
        int slash = stored.IndexOf('\\');
        if (slash < 0 || slash == stored.Length - 1) return false;
        return exePath.EndsWith(stored[slash..], StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _timer.Dispose();
}
