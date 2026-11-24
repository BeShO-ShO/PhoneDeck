namespace PhoneDeck.Core;

/// <summary>The pixel layouts the built-in viewer can show. Anything else is skipped.</summary>
public enum CapturePixels
{
    Other,
    Nv12,
    Yuy2,
    Mjpg,
}

/// <summary>One mode a capture device offers: pixel layout, size and frame rate.</summary>
public readonly record struct CaptureFormat(CapturePixels Pixels, int Width, int Height, uint RateNumerator, uint RateDenominator)
{
    public double FramesPerSecond => RateDenominator == 0 ? 0 : (double)RateNumerator / RateDenominator;

    /// <summary>Sent as raw pixels, so nothing has to decode it.</summary>
    public bool IsUncompressed => Pixels is CapturePixels.Nv12 or CapturePixels.Yuy2;

    public override string ToString() => $"{Pixels} {Width}x{Height} at {FramesPerSecond:0.##} fps";
}

/// <summary>
/// The capture device's black and white levels, when what it says (or the usual default) is
/// wrong: too bright and grey blacks wants Limited, crushed blacks and blown whites want Full.
/// </summary>
public enum PictureRange
{
    Auto = 0,
    Full = 1,
    Limited = 2,
}

/// <summary>
/// Picture adjustments, each from <see cref="Min"/> to <see cref="Max"/> with 0 for unchanged.
/// They are folded into the colour conversion (<see cref="YuvToRgb"/>), so they add no delay.
/// </summary>
public readonly record struct PictureLevels(int Brightness, int Contrast, int Saturation)
{
    public const int Min = -50;
    public const int Max = 50;

    /// <summary>Brightness as steps of 1/255 (one 8-bit level each).</summary>
    public static double BrightnessUnits(int level) => Math.Clamp(level, Min, Max);

    /// <summary>Contrast and saturation as a factor: -50 halves it, 50 makes it 1.5 times.</summary>
    public static double Factor(int level) => 1 + Math.Clamp(level, Min, Max) / 100.0;
}

/// <summary>
/// How a capture device's YUV picture turns into RGB: which matrix and which range. Getting it
/// wrong shows washed-out or crushed blacks, or slightly wrong colours.
/// </summary>
public readonly record struct CaptureColor(bool Bt709, bool FullRange)
{
    // Media Foundation's values for MF_MT_YUV_MATRIX and MF_MT_VIDEO_NOMINAL_RANGE.
    public const uint MatrixBt709 = 1;
    public const uint MatrixBt601 = 2;
    public const uint RangeFull = 1;
    public const uint RangeStudio = 2;

    /// <summary>
    /// What the device says wins. Otherwise mpv's guesses, since its picture looks right:
    /// BT.709 from 720 lines up and BT.601 below, full range for JPEG (as decoders mark it)
    /// and studio range (16 to 235) for raw video.
    /// </summary>
    /// <param name="source">What the device sends, before any decoding.</param>
    public static CaptureColor Choose(CapturePixels source, int height, uint? matrix, uint? range,
        PictureRange chosen)
    {
        var detected = Choose(source, height, matrix, range);
        return chosen switch
        {
            PictureRange.Full => detected with { FullRange = true },
            PictureRange.Limited => detected with { FullRange = false },
            _ => detected,
        };
    }

    public static CaptureColor Choose(CapturePixels source, int height, uint? matrix, uint? range)
    {
        var bt709 = matrix switch
        {
            MatrixBt709 => true,
            MatrixBt601 => false,
            _ => height >= 720,
        };
        var full = range switch
        {
            RangeFull => true,
            RangeStudio => false,
            _ => source == CapturePixels.Mjpg,
        };
        return new CaptureColor(bt709, full);
    }
}

/// <summary>
/// The YUV to RGB conversion the viewer's shader does, as three rows of
/// (Y, U, V, constant) weights for Y, U and V read as 0 to 1. Brightness, contrast and colour
/// are folded in, so the whole picture adjustment costs nothing per frame.
/// </summary>
public static class YuvToRgb
{
    /// <returns>12 numbers: the red row, then green, then blue.</returns>
    public static float[] Rows(CaptureColor color, PictureLevels levels)
    {
        var (kr, kb) = color.Bt709 ? (0.2126, 0.0722) : (0.299, 0.114);
        var kg = 1 - kr - kb;

        // Y' = lumaScale * y + lumaOffset, and the same for both chroma channels around 0.
        double lumaScale, lumaOffset, chromaScale, chromaOffset;
        if (color.FullRange)
        {
            (lumaScale, lumaOffset) = (1, 0);
            (chromaScale, chromaOffset) = (1, -128.0 / 255);
        }
        else
        {
            (lumaScale, lumaOffset) = (255.0 / 219, -16.0 / 219);
            (chromaScale, chromaOffset) = (255.0 / 224, -128.0 / 224);
        }

        // Contrast around mid grey, brightness as a shift, colour as a chroma gain.
        var contrast = PictureLevels.Factor(levels.Contrast);
        var saturation = PictureLevels.Factor(levels.Saturation);
        var brightness = PictureLevels.BrightnessUnits(levels.Brightness) / 255;
        var y = contrast * lumaScale;
        var yConstant = contrast * (lumaOffset - 0.5) + 0.5 + brightness;
        var c = saturation * chromaScale;
        var cConstant = saturation * chromaOffset;

        var rFromV = 2 * (1 - kr);
        var bFromU = 2 * (1 - kb);
        var gFromU = -2 * kb * (1 - kb) / kg;
        var gFromV = -2 * kr * (1 - kr) / kg;

        return
        [
            (float)y, 0f, (float)(rFromV * c), (float)(yConstant + rFromV * cConstant),
            (float)y, (float)(gFromU * c), (float)(gFromV * c), (float)(yConstant + (gFromU + gFromV) * cConstant),
            (float)y, (float)(bFromU * c), 0f, (float)(yConstant + bFromU * cConstant),
        ];
    }

    /// <summary>Applies <see cref="Rows"/> to one pixel, as the shader does (clamped to 0 to 1).</summary>
    public static (double R, double G, double B) Apply(float[] rows, double y, double u, double v)
    {
        double Row(int i) => Math.Clamp(rows[i] * y + rows[i + 1] * u + rows[i + 2] * v + rows[i + 3], 0, 1);
        return (Row(0), Row(4), Row(8));
    }
}

/// <summary>
/// Finds a capture device's own sound input when none is chosen: Windows gives a device's
/// camera and microphone the same container id.
/// </summary>
public static class CaptureSound
{
    // Built-in cameras and microphones all share this "part of the PC" container, so it says
    // nothing about which belong together.
    private static readonly Guid BuiltIn = new("00000000-0000-0000-ffff-ffffffffffff");

    /// <returns>The name of the sound input in the video device's container, or null.</returns>
    public static string? SameDevice(Guid? videoContainer, IEnumerable<(string Name, Guid? Container)> soundInputs)
    {
        if (videoContainer is not { } container || container == Guid.Empty || container == BuiltIn) return null;
        return soundInputs.FirstOrDefault(input => input.Container == container).Name;
    }
}

/// <summary>
/// Picks the mode the built-in viewer asks the capture device for. Smoothness matters most for
/// the pointer, so frame rate (up to 60) comes first, then size, then a mode that needs no
/// decoding. Many cheap cards give 1080p at 60 only as MJPG, and raw 1080p only at 5 or 10.
/// </summary>
public static class CaptureFormatChooser
{
    /// <summary>Rates above this don't help: the phone sends 60.</summary>
    public const int MaxUsefulRate = 60;

    /// <returns>The index of the best mode, or null if none can be shown.</returns>
    public static int? Choose(IReadOnlyList<CaptureFormat> formats)
    {
        int? best = null;
        for (var i = 0; i < formats.Count; i++)
        {
            if (!IsUsable(formats[i])) continue;
            if (best is not { } current || Compare(formats[i], formats[current]) > 0) best = i;
        }
        return best;
    }

    private static bool IsUsable(CaptureFormat format) =>
        format.Pixels != CapturePixels.Other && format.Width > 0 && format.Height > 0 && format.FramesPerSecond > 0;

    /// <summary>Positive when <paramref name="a"/> is the better mode.</summary>
    public static int Compare(CaptureFormat a, CaptureFormat b)
    {
        // Rounded, so 59.94 and 60 count as the same rate.
        var rate = UsefulRate(a).CompareTo(UsefulRate(b));
        if (rate != 0) return rate;

        var size = ((long)a.Width * a.Height).CompareTo((long)b.Width * b.Height);
        if (size != 0) return size;

        return Preference(a.Pixels).CompareTo(Preference(b.Pixels));
    }

    private static int UsefulRate(CaptureFormat format) =>
        (int)Math.Min(Math.Round(format.FramesPerSecond), MaxUsefulRate);

    // YUY2 keeps colour at full height (4:2:2) and is what cards capture natively; their NV12
    // (4:2:0) is converted inside the card, with half the colour detail and, on some, the wrong
    // range. mpv takes YUY2 too. MJPG has to be decoded first.
    private static int Preference(CapturePixels pixels) => pixels switch
    {
        CapturePixels.Yuy2 => 3,
        CapturePixels.Nv12 => 2,
        CapturePixels.Mjpg => 1,
        _ => 0,
    };
}
