using PhoneDeck.Core;
using Xunit;

namespace PhoneDeck.Core.Tests;

public class WindowPlacementTests
{
    [Fact]
    public void RoundTrips()
    {
        var placement = new WindowPlacement(-1200, 40, 1280, 720);
        Assert.Equal("-1200,40,1280,720", placement.ToString());
        Assert.Equal(placement, WindowPlacement.Parse(placement.ToString()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("10,20,1280")]
    [InlineData("10,20,1280,720,5")]
    [InlineData("a,20,1280,720")]
    [InlineData("10,20,100,720")]
    [InlineData("10,20,1280,100")]
    public void RejectsMissingBrokenOrTinyValues(string? text) => Assert.Null(WindowPlacement.Parse(text));

    [Fact]
    public void ToleratesSpaces() =>
        Assert.Equal(new WindowPlacement(1, 2, 800, 600), WindowPlacement.Parse(" 1, 2 ,800,600 "));

    [Fact]
    public void ChecksTheTitleBarMiddle()
    {
        var placement = new WindowPlacement(100, 50, 800, 600);
        (int X, int Y) asked = default;
        Assert.True(placement.TitleIsOnScreen((x, y) => { asked = (x, y); return true; }));
        Assert.Equal((500, 60), asked);

        // A monitor to the left that has since been unplugged.
        var gone = new WindowPlacement(-1500, 50, 800, 600);
        Assert.False(gone.TitleIsOnScreen((x, _) => x >= 0));
    }
}
