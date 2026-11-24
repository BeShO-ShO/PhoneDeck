using PhoneDeck.Core;
using Xunit;

namespace PhoneDeck.Core.Tests;

public class DisplayRouteTests
{
    [Theory]
    // Older settings: the Wireless Display yes/no.
    [InlineData(null, false, DisplayRoute.CaptureDevice)]
    [InlineData(null, true, DisplayRoute.Cast)]
    // A saved route wins over the old yes/no.
    [InlineData(0, true, DisplayRoute.CaptureDevice)]
    [InlineData(1, false, DisplayRoute.Cast)]
    [InlineData(2, false, DisplayRoute.WirelessDisplayApp)]
    // Out of range is treated as never saved.
    [InlineData(7, false, DisplayRoute.CaptureDevice)]
    [InlineData(-1, true, DisplayRoute.Cast)]
    public void SavedRouteAndOldSetting(int? saved, bool legacyWireless, DisplayRoute expected)
    {
        Assert.Equal(expected, DisplayRoutes.FromSaved(saved, legacyWireless));
    }

    [Theory]
    [InlineData(DisplayRoute.CaptureDevice, true, true, true)]
    [InlineData(DisplayRoute.CaptureDevice, false, false, false)]
    // PhoneDeck's own receiver has no touch of its own, so it is the capture device's rules.
    [InlineData(DisplayRoute.Cast, true, true, true)]
    [InlineData(DisplayRoute.Cast, false, false, false)]
    // The app carries touch but not the pen.
    [InlineData(DisplayRoute.WirelessDisplayApp, true, false, true)]
    [InlineData(DisplayRoute.WirelessDisplayApp, false, false, true)]
    public void TouchAndPenFollowTheRoute(DisplayRoute route, bool touchLayer, bool touch, bool pen)
    {
        Assert.Equal(touch, DisplayRoutes.SendsTouch(route, touchLayer));
        Assert.Equal(pen, DisplayRoutes.SendsPen(route, touchLayer));
    }

    [Fact]
    public void CastStatusSaysWhatToDo()
    {
        Assert.Contains("cast DeX", new CastStatus(CastState.Waiting).Describe());
        Assert.Equal("Showing Galaxy S26 Ultra.", new CastStatus(CastState.Connected, "Galaxy S26 Ultra").Describe());
        Assert.Equal("Showing the phone.", new CastStatus(CastState.Connected, " ").Describe());
        Assert.Contains("Wireless Display app", new CastStatus(CastState.Unsupported).Describe());
        Assert.Contains("(blocked)", new CastStatus(CastState.Failed, Problem: "blocked").Describe());
        Assert.DoesNotContain("()", new CastStatus(CastState.Failed).Describe());

        Assert.True(new CastStatus(CastState.Failed).IsProblem);
        Assert.True(new CastStatus(CastState.Unsupported).IsProblem);
        Assert.False(new CastStatus(CastState.Waiting).IsProblem);
    }

    [Fact]
    public void HomeWirelessCoversBothReceivers()
    {
        Assert.False(DisplayRoutes.IsWireless(DisplayRoute.CaptureDevice));
        Assert.True(DisplayRoutes.IsWireless(DisplayRoute.Cast));
        Assert.True(DisplayRoutes.IsWireless(DisplayRoute.WirelessDisplayApp));

        Assert.Equal(DisplayRoute.Cast, DisplayRoutes.Wireless(useApp: false));
        Assert.Equal(DisplayRoute.WirelessDisplayApp, DisplayRoutes.Wireless(useApp: true));
    }

    [Theory]
    [InlineData(null, DisplayRoute.CaptureDevice, false)]
    [InlineData(null, DisplayRoute.Cast, false)]
    // Settings from the three-way choice keep the app if it was the one chosen.
    [InlineData(null, DisplayRoute.WirelessDisplayApp, true)]
    // The saved receiver wins, also while the capture device is chosen.
    [InlineData(true, DisplayRoute.CaptureDevice, true)]
    [InlineData(false, DisplayRoute.WirelessDisplayApp, false)]
    public void ReceiverChoiceFromSettings(bool? saved, DisplayRoute route, bool expected) =>
        Assert.Equal(expected, DisplayRoutes.UsesApp(saved, route));
}
