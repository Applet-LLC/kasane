using Kasane.Model;
using Xunit;

namespace Kasane.Tests;

public class ClosePolicyTests
{
    [Fact]
    public void NothingUnapplied_NoPrompt()
    {
        Assert.Equal(UnappliedPrompt.None, ClosePolicy.Decide(0, driverConnected: true, readOnly: false));
        Assert.Equal(UnappliedPrompt.None, ClosePolicy.Decide(0, driverConnected: true, readOnly: true));
    }

    [Fact]
    public void DriverNotConnected_NoPrompt()
    {
        Assert.Equal(UnappliedPrompt.None, ClosePolicy.Decide(2, driverConnected: false, readOnly: true));
        Assert.Equal(UnappliedPrompt.None, ClosePolicy.Decide(2, driverConnected: false, readOnly: false));
    }

    [Fact]
    public void ReadOnly_WarnsThatItIsNotApplied()
    {
        Assert.Equal(UnappliedPrompt.WarnNotApplied, ClosePolicy.Decide(1, driverConnected: true, readOnly: true));
    }

    [Fact]
    public void Admin_AsksWhetherToApply()
    {
        Assert.Equal(UnappliedPrompt.AskApply, ClosePolicy.Decide(1, driverConnected: true, readOnly: false));
        Assert.Equal(UnappliedPrompt.AskApply, ClosePolicy.Decide(3, driverConnected: true, readOnly: false));
    }
}
