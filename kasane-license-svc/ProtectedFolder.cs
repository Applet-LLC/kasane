using System.Security.AccessControl;
using System.Security.Principal;

namespace KasaneLicenseSvc;

/// <summary>
/// ライセンスキーと署名鍵を置くフォルダ(%ProgramData%\kbdlayer-license\license-svc)の置き場所と、アクセス権。
/// %ProgramData% の既定では、一般ユーザー(BUILTIN\Users)に読み取りが付く。そのままだと、同じパソコンの
/// 一般ユーザーが、ライセンスキーを読める。ここでは継承を切って、SYSTEM と Administrators だけにする。
/// 親の %ProgramData%\kbdlayer-license は変えない(GUI が、一般ユーザーの権限で gui-debug.log を書くため)。
/// </summary>
public static class ProtectedFolder
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "kbdlayer-license", "license-svc");

    /// <summary>フォルダを作り、SYSTEM と Administrators だけがアクセスできるようにする。中のファイルにも継承で届く。</summary>
    public static void Ensure()
    {
        var info = Directory.CreateDirectory(Dir);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        }
        info.SetAccessControl(security);
    }
}
