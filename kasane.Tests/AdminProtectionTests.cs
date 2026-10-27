using Kasane.Model;

namespace Kasane.Tests;

/// <summary>管理者保護の検出と、書き込み試験。</summary>
public class AdminProtectionTests
{
    [Theory]
    [InlineData(2, true)]
    [InlineData(1, false)]
    [InlineData(0, false)]
    [InlineData(null, false)]
    public void IsEnabled_OnlyMode2(int? mode, bool expected) =>
        Assert.Equal(expected, AdminProtection.IsEnabled(mode));

    [Fact]
    public void ProbeWrite_WritableFolder_Succeeds()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kasane-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal("できる", AdminProtection.ProbeWrite(dir));
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void ProbeWrite_InvalidPath_ReportsFailureInsteadOfThrowing() =>
        Assert.StartsWith("できない(", AdminProtection.ProbeWrite("NUL:" + Path.DirectorySeparatorChar + "x"));

    [Fact]
    public void Describe_ContainsDataDirectory()
    {
        string dir = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        Assert.Contains(dir, AdminProtection.Describe(dir));
    }
}

/// <summary>--ensure-data-acl（保存先に、いま動いているユーザー自身の変更権限を付ける）。</summary>
public class EnsureDataAclTests
{
    private sealed class ListLog : Kasane.Cli.HeadlessCommands.ILog
    {
        public List<string> Lines { get; } = new();
        public void Write(string line) => Lines.Add(line);
    }

    private static bool HasUserModify(string dir)
    {
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var acl = new DirectoryInfo(dir).GetAccessControl();
        foreach (System.Security.AccessControl.FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
            if (rule.IdentityReference.Equals(sid)
                && rule.AccessControlType == System.Security.AccessControl.AccessControlType.Allow
                && (rule.FileSystemRights & System.Security.AccessControl.FileSystemRights.Modify) == System.Security.AccessControl.FileSystemRights.Modify)
                return true;
        return false;
    }

    [Fact]
    public void GrantsCurrentUser_WhenInheritedAccessIsRemoved_AndIsIdempotent()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kasane-acl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var info = new DirectoryInfo(dir);
            var acl = info.GetAccessControl();
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false); // 継承した権限をすべて外す（所有者は WRITE_DAC を持つ）
            info.SetAccessControl(acl);
            Assert.False(HasUserModify(dir));

            var log = new ListLog();
            Assert.Equal(0, Kasane.Cli.HeadlessCommands.RunEnsureDataAcl(dir, log));
            Assert.True(HasUserModify(dir));

            int before = new DirectoryInfo(dir).GetAccessControl().GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier)).Count;
            Assert.Equal(0, Kasane.Cli.HeadlessCommands.RunEnsureDataAcl(dir, log));
            int after = new DirectoryInfo(dir).GetAccessControl().GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier)).Count;
            Assert.Equal(before, after); // 2回目は何も足さない
        }
        finally
        {
            try
            {
                // 権限を戻してから消す（外した状態のままだと、消せないことがある）。
                RunEnsure(dir);
                Directory.Delete(dir, true);
            }
            catch (Exception) { }
        }
    }

    private static void RunEnsure(string dir) => Kasane.Cli.HeadlessCommands.RunEnsureDataAcl(dir, new ListLog());
}

public class ShellUserTests
{
    // 対話セッションでなければ（CI など）エクスプローラーが無く null。あれば、SID として読める。例外は出さない。
    [Fact]
    public void GetShellUserSid_DoesNotThrow()
    {
        var sid = AdminProtection.GetShellUserSid();
        if (sid != null) Assert.StartsWith("S-1-5-", sid.Value);
    }
}
