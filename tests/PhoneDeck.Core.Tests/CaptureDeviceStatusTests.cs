using PhoneDeck.Core;
using Xunit;

namespace PhoneDeck.Core.Tests;

public class CaptureDeviceStatusTests
{
    [Fact]
    public void ChosenAndPluggedIn()
    {
        var status = CaptureDeviceStatus.Describe("USB3 Video", ["Integrated Camera", "usb3 video"]);
        Assert.Equal("USB3 Video is plugged in.", status.Text);
        Assert.False(status.IsProblem);
    }

    [Fact]
    public void ChosenButMissing()
    {
        var status = CaptureDeviceStatus.Describe("USB3 Video", ["Integrated Camera"]);
        Assert.Equal("USB3 Video isn't plugged in.", status.Text);
        Assert.True(status.IsProblem);
    }

    [Fact]
    public void NotChosenListsWhatIsThere()
    {
        var status = CaptureDeviceStatus.Describe(null, ["Integrated Camera", "USB3 Video"]);
        Assert.Contains("Integrated Camera, USB3 Video", status.Text);
        // A separate monitor is used with no device chosen; that isn't an error.
        Assert.False(status.IsProblem);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void NothingChosenNothingFound(string? chosen)
    {
        var status = CaptureDeviceStatus.Describe(chosen, []);
        Assert.StartsWith("None chosen or found", status.Text);
        Assert.False(status.IsProblem);
    }
}
