using PhoneDeck.Core;
using Xunit;

namespace PhoneDeck.Core.Tests;

public class WirelessDisplayTests
{
    // Measured on a Surface Pro 11 at 1x: invisible borders of 7/8/7 px, 32 px title bar.
    private static readonly ScreenRect Frame = new(237, 161, 1378, 987);
    private static readonly ScreenRect Content = new(244, 194, 1370, 980);
    private static readonly ScreenRect Monitor = new(0, 0, 2880, 1920);

    [Fact]
    public void FillTarget_PutsThePictureAreaExactlyOnTheMonitor()
    {
        var target = WirelessDisplay.FillTarget(Frame, Content, Monitor);

        Assert.Equal(new ScreenRect(-7, -33, 2888, 1927), target);
        // The same offsets applied to the content area land on the monitor.
        Assert.Equal(Monitor.Left, target.Left + (Content.Left - Frame.Left));
        Assert.Equal(Monitor.Top, target.Top + (Content.Top - Frame.Top));
        Assert.Equal(Monitor.Right, target.Right - (Frame.Right - Content.Right));
        Assert.Equal(Monitor.Bottom, target.Bottom - (Frame.Bottom - Content.Bottom));
    }

    [Fact]
    public void FillTarget_WorksOnASecondMonitor()
    {
        var second = new ScreenRect(-1920, 0, 0, 1080);
        var target = WirelessDisplay.FillTarget(Frame, Content, second);
        Assert.Equal(new ScreenRect(-1927, -33, 8, 1087), target);
    }

    [Fact]
    public void FillTarget_KeepsThePictureAboveTheTaskbar()
    {
        var workArea = new ScreenRect(0, 0, 2880, 1824);
        var target = WirelessDisplay.FillTarget(Frame, Content, workArea);
        Assert.Equal(1824, target.Bottom - (Frame.Bottom - Content.Bottom));
    }

    [Fact]
    public void Locate_Letterboxes16By9BelowTheTitleBar()
    {
        var found = new WirelessDisplay.Found(new IntPtr(1), Frame, Content, Minimized: false);
        var picture = WirelessDisplayLocator.Locate(found)!.Value;

        // 1126 x 786 is taller than 16:9, so the width is used and the height is centred.
        Assert.Equal(244, picture.Left);
        Assert.Equal(1126, picture.Width);
        Assert.Equal(1126 * 9.0 / 16.0, picture.Height, 3);
        Assert.Equal(194 + (786 - picture.Height) / 2, picture.Top, 3);
    }

    [Fact]
    public void Locate_GivesNothingForAMissingOrMinimisedWindow()
    {
        Assert.Null(WirelessDisplayLocator.Locate(null));
        Assert.Null(WirelessDisplayLocator.Locate(new WirelessDisplay.Found(new IntPtr(1), Frame, Content, Minimized: true)));
    }
}

public class PromotedInputTests
{
    private static IntPtr Touch => new(unchecked((long)0xFF515780));
    private static IntPtr Pen => new(unchecked((long)0xFF515700));

    [Fact]
    public void PenAndTouchSignaturesAreToldApart()
    {
        Assert.True(InputCapture.IsTouchPromoted(Touch));
        Assert.True(InputCapture.IsTouchPromoted(Pen));
        Assert.False(InputCapture.IsPenPromoted(Touch));
        Assert.True(InputCapture.IsPenPromoted(Pen));
        Assert.False(InputCapture.IsPenPromoted(IntPtr.Zero));
    }

    [Theory]
    // isPen, touchToHost, penToHost, passThrough, onVideo, dropped
    [InlineData(false, true, true, false, true, true)]    // capture device: touch on the video goes to the phone
    [InlineData(true, true, true, false, true, true)]     // capture device: pen too
    [InlineData(false, false, true, false, true, false)]  // wireless display: native touch reaches the window
    [InlineData(true, false, true, false, true, true)]    // wireless display: the pen goes over Bluetooth
    [InlineData(true, false, true, false, false, false)]  // off the video: the bar still gets its taps
    [InlineData(true, false, true, true, true, false)]    // input on this PC: nothing is dropped
    public void DropsPromoted_FollowsTheTouchAndPenSettings(bool isPen, bool touch, bool pen, bool passThrough, bool onVideo, bool dropped) =>
        Assert.Equal(dropped, InputCapture.DropsPromoted(isPen, touch, pen, passThrough, onVideo));
}
