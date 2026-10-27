using System.Xml.Linq;
using Kasane.Model;

namespace Kasane.Tests;

/// <summary>管理者保護のときの自動起動（タスクスケジューラ）の定義。</summary>
public class AutostartTests
{
    [Theory]
    [InlineData(@"T480S\applet", "kasane-tray-applet")]
    [InlineData("applet", "kasane-tray-applet")]
    [InlineData(@"PC\user name", "kasane-tray-user_name")]
    public void TaskName_UsesUserPartOnly(string account, string expected) =>
        Assert.Equal(expected, Autostart.TaskNameFor(account));

    [Fact]
    public void TaskXml_IsValidAndRunsTrayHighestAvailableForTheUser()
    {
        const string sid = "S-1-5-21-1-2-3-1003";
        string xml = Autostart.BuildTaskXml(@"C:\Program Files\applet\kasane\tray\kasane-tray.exe", sid, @"T480S\applet");
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var doc = XDocument.Parse(xml);

        Assert.Equal(sid, doc.Descendants(ns + "Principal").Single().Element(ns + "UserId")!.Value);
        Assert.Equal("HighestAvailable", doc.Descendants(ns + "RunLevel").Single().Value);
        Assert.Equal("InteractiveToken", doc.Descendants(ns + "LogonType").Single().Value);
        Assert.Equal(@"T480S\applet", doc.Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId")!.Value);
        Assert.Equal("false", doc.Descendants(ns + "StopIfGoingOnBatteries").Single().Value);
        Assert.Equal("powershell.exe", doc.Descendants(ns + "Command").Single().Value);
        Assert.Contains(@"Start-Process -FilePath 'C:\Program Files\applet\kasane\tray\kasane-tray.exe'", doc.Descendants(ns + "Arguments").Single().Value);
    }

    [Fact]
    public void TaskXml_EscapesSpecialCharactersInPath()
    {
        string xml = Autostart.BuildTaskXml(@"C:\R&D\it's\kasane-tray.exe", "S-1-5-21-1-2-3-1003", @"PC\u");
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        string args = XDocument.Parse(xml).Descendants(ns + "Arguments").Single().Value;
        Assert.Contains(@"'C:\R&D\it''s\kasane-tray.exe'", args); // PowerShell の単一引用符は2つ重ねる
    }

    [Fact]
    public void StartupApprovedValues_UseTaskManagerFlags()
    {
        Assert.Equal(12, Autostart.ApprovedEnabled.Length);
        Assert.Equal(2, Autostart.ApprovedEnabled[0]);
        Assert.Equal(12, Autostart.ApprovedDisabled.Length);
        Assert.Equal(3, Autostart.ApprovedDisabled[0]);
    }
}
