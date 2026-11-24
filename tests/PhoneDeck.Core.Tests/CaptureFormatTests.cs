using PhoneDeck.Core;
using Xunit;

namespace PhoneDeck.Core.Tests;

public class CaptureFormatTests
{
    private static CaptureFormat F(CapturePixels pixels, int width, int height, uint rate, uint denominator = 1) =>
        new(pixels, width, height, rate, denominator);

    [Fact]
    public void FrameRateWinsOverUncompressed()
    {
        // The usual cheap card: raw 1080p only at 5 fps, MJPG at 60.
        var formats = new[]
        {
            F(CapturePixels.Yuy2, 1920, 1080, 5),
            F(CapturePixels.Mjpg, 1920, 1080, 60),
        };
        Assert.Equal(1, CaptureFormatChooser.Choose(formats));
    }

    [Fact]
    public void UncompressedWinsAtTheSameRateAndSize()
    {
        var formats = new[]
        {
            F(CapturePixels.Mjpg, 1920, 1080, 60),
            F(CapturePixels.Nv12, 1920, 1080, 60),
            F(CapturePixels.Yuy2, 1920, 1080, 60),
        };
        // YUY2 (4:2:2, the card's own) over NV12 (4:2:0, converted in the card) over MJPG.
        Assert.Equal(2, CaptureFormatChooser.Choose(formats));
        Assert.Equal(1, CaptureFormatChooser.Choose([formats[0], formats[1]]));
    }

    [Fact]
    public void LargerWinsAtTheSameRate()
    {
        var formats = new[]
        {
            F(CapturePixels.Nv12, 1280, 720, 60),
            F(CapturePixels.Mjpg, 1920, 1080, 60),
        };
        Assert.Equal(1, CaptureFormatChooser.Choose(formats));
    }

    [Fact]
    public void RatesAboveSixtyDoNotBeatALargerPicture()
    {
        var formats = new[]
        {
            F(CapturePixels.Mjpg, 1280, 720, 120),
            F(CapturePixels.Mjpg, 1920, 1080, 60),
        };
        Assert.Equal(1, CaptureFormatChooser.Choose(formats));
    }

    [Fact]
    public void NtscRateCountsAsSixty()
    {
        var formats = new[]
        {
            F(CapturePixels.Mjpg, 1920, 1080, 60),
            F(CapturePixels.Nv12, 1920, 1080, 60000, 1001),
        };
        Assert.Equal(1, CaptureFormatChooser.Choose(formats));
    }

    [Fact]
    public void UnknownLayoutsAndEmptyModesAreSkipped()
    {
        var formats = new[]
        {
            F(CapturePixels.Other, 3840, 2160, 60),
            F(CapturePixels.Nv12, 0, 0, 60),
            F(CapturePixels.Yuy2, 1920, 1080, 0, 0),
            F(CapturePixels.Mjpg, 640, 480, 30),
        };
        Assert.Equal(3, CaptureFormatChooser.Choose(formats));
    }

    [Fact]
    public void NothingUsableGivesNull()
    {
        Assert.Null(CaptureFormatChooser.Choose([]));
        Assert.Null(CaptureFormatChooser.Choose([F(CapturePixels.Other, 1920, 1080, 60)]));
    }

    [Theory]
    // What the device says wins.
    [InlineData(CapturePixels.Mjpg, 1080, CaptureColor.MatrixBt709, CaptureColor.RangeStudio, true, false)]
    [InlineData(CapturePixels.Nv12, 1080, CaptureColor.MatrixBt601, CaptureColor.RangeFull, false, true)]
    // JPEG is full range; the matrix follows the size, as mpv guesses it.
    [InlineData(CapturePixels.Mjpg, 1080, null, null, true, true)]
    [InlineData(CapturePixels.Mjpg, 480, null, null, false, true)]
    // Raw HD is BT.709 at studio range, raw SD BT.601.
    [InlineData(CapturePixels.Yuy2, 1080, null, null, true, false)]
    [InlineData(CapturePixels.Nv12, 720, null, null, true, false)]
    [InlineData(CapturePixels.Yuy2, 480, null, null, false, false)]
    // An unknown value counts as not said.
    [InlineData(CapturePixels.Nv12, 1080, 0u, 0u, true, false)]
    public void ColourFollowsTheDeviceThenTheUsualDefaults(CapturePixels source, int height, uint? matrix, uint? range,
        bool bt709, bool full)
    {
        Assert.Equal(new CaptureColor(bt709, full), CaptureColor.Choose(source, height, matrix, range));
    }

    [Theory]
    [InlineData(PictureRange.Auto, true)]
    [InlineData(PictureRange.Full, true)]
    [InlineData(PictureRange.Limited, false)]
    public void ChosenRangeOverridesOnlyTheRange(PictureRange chosen, bool full)
    {
        // MJPG at 1080 detects as BT.709 full; the matrix must stay whatever the range.
        var color = CaptureColor.Choose(CapturePixels.Mjpg, 1080, null, null, chosen);
        Assert.Equal(new CaptureColor(true, full), color);
        Assert.True(CaptureColor.Choose(CapturePixels.Nv12, 1080, null, null, PictureRange.Full).FullRange);
    }

    [Fact]
    public void LevelsMapToRealUnits()
    {
        Assert.Equal(0, PictureLevels.BrightnessUnits(0));
        Assert.Equal(-50, PictureLevels.BrightnessUnits(-80));
        Assert.Equal(1.0, PictureLevels.Factor(0));
        Assert.Equal(0.5, PictureLevels.Factor(-50));
        Assert.Equal(1.5, PictureLevels.Factor(90));
    }

    private static void AssertRgb((double R, double G, double B) actual, double r, double g, double b)
    {
        Assert.Equal(r, actual.R, 2);
        Assert.Equal(g, actual.G, 2);
        Assert.Equal(b, actual.B, 2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LimitedRangeBlackAndWhiteLandOnZeroAndOne(bool bt709)
    {
        var rows = YuvToRgb.Rows(new CaptureColor(bt709, FullRange: false), default);
        AssertRgb(YuvToRgb.Apply(rows, 16 / 255.0, 128 / 255.0, 128 / 255.0), 0, 0, 0);
        AssertRgb(YuvToRgb.Apply(rows, 235 / 255.0, 128 / 255.0, 128 / 255.0), 1, 1, 1);
        AssertRgb(YuvToRgb.Apply(rows, 125.5 / 255.0, 128 / 255.0, 128 / 255.0), 0.5, 0.5, 0.5);
    }

    [Fact]
    public void FullRangeBlackAndWhiteLandOnZeroAndOne()
    {
        var rows = YuvToRgb.Rows(new CaptureColor(true, FullRange: true), default);
        AssertRgb(YuvToRgb.Apply(rows, 0, 128 / 255.0, 128 / 255.0), 0, 0, 0);
        AssertRgb(YuvToRgb.Apply(rows, 1, 128 / 255.0, 128 / 255.0), 1, 1, 1);
    }

    [Fact]
    public void Bt709LimitedRedIsRed()
    {
        // Pure red in BT.709 studio range: Y 63, Cb 102, Cr 240.
        var rows = YuvToRgb.Rows(new CaptureColor(true, false), default);
        AssertRgb(YuvToRgb.Apply(rows, 62.56 / 255, 102.34 / 255, 240 / 255.0), 1, 0, 0);
    }

    [Fact]
    public void Bt601FullBlueIsBlue()
    {
        // Pure blue in BT.601 full range (JPEG): Y = Kb, Cb = +0.5, Cr = -Kb / (2 (1 - Kr)),
        // with chroma centred on 128.
        var rows = YuvToRgb.Rows(new CaptureColor(false, true), default);
        var mid = 128 / 255.0;
        AssertRgb(YuvToRgb.Apply(rows, 0.114, mid + 0.5, mid - 0.114 / 1.402), 0, 0, 1);
    }

    [Fact]
    public void LevelsChangeThePicture()
    {
        var color = new CaptureColor(true, true);
        var grey = (128 / 255.0, 128 / 255.0, 128 / 255.0);

        var brighter = YuvToRgb.Apply(YuvToRgb.Rows(color, new PictureLevels(20, 0, 0)), grey.Item1, grey.Item2, grey.Item3);
        Assert.Equal(128 / 255.0 + 20 / 255.0, brighter.R, 3);

        // Contrast pivots on mid grey, so white moves and mid grey stays.
        var lowContrast = YuvToRgb.Rows(color, new PictureLevels(0, -50, 0));
        Assert.Equal(0.75, YuvToRgb.Apply(lowContrast, 1, grey.Item2, grey.Item3).R, 3);
        Assert.Equal(0.5, YuvToRgb.Apply(lowContrast, 0.5, grey.Item2, grey.Item3).R, 2);

        // No colour leaves only grey.
        var noColour = YuvToRgb.Rows(color, new PictureLevels(0, 0, -50));
        var halfRed = YuvToRgb.Apply(noColour, 0.5, 0.5, 0.9);
        var fullRed = YuvToRgb.Apply(YuvToRgb.Rows(color, default), 0.5, 0.5, 0.9);
        Assert.True(halfRed.R - halfRed.G < fullRed.R - fullRed.G);
    }

    [Fact]
    public void SoundInputComesFromTheSameDevice()
    {
        var card = Guid.NewGuid();
        var builtIn = new Guid("00000000-0000-0000-ffff-ffffffffffff");
        var inputs = new (string, Guid?)[]
        {
            ("Microphone Array", builtIn),
            ("Digital Audio Interface (USB3 Digital Audio)", card),
            ("Headset", null),
        };
        Assert.Equal("Digital Audio Interface (USB3 Digital Audio)", CaptureSound.SameDevice(card, inputs));
        // A built-in camera must not pick the built-in microphone, and unknown means no guess.
        Assert.Null(CaptureSound.SameDevice(builtIn, inputs));
        Assert.Null(CaptureSound.SameDevice(null, inputs));
        Assert.Null(CaptureSound.SameDevice(Guid.Empty, inputs));
        Assert.Null(CaptureSound.SameDevice(Guid.NewGuid(), inputs));
    }

    [Fact]
    public void FramesPerSecondHandlesAZeroDenominator()
    {
        Assert.Equal(0, F(CapturePixels.Nv12, 1920, 1080, 60, 0).FramesPerSecond);
        Assert.Equal(29.97, F(CapturePixels.Nv12, 1920, 1080, 30000, 1001).FramesPerSecond, 2);
    }
}
