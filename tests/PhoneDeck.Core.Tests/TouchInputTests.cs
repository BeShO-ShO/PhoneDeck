using PhoneDeck.Core;
using Xunit;

namespace PhoneDeck.Core.Tests;

public sealed class TouchInputTests
{
    private const double Wide = 16.0 / 9.0;

    /// <summary>Sums the input bits per report id, the way a host parses the descriptor.</summary>
    private static Dictionary<int, int> InputBitsByReport(byte[] map)
    {
        var bits = new Dictionary<int, int>();
        int reportId = 0, reportSize = 0, reportCount = 0;

        for (var i = 0; i < map.Length;)
        {
            var prefix = map[i];
            var size = (prefix & 0x03) == 3 ? 4 : prefix & 0x03;
            var value = 0;
            for (var b = 0; b < size; b++) value |= map[i + 1 + b] << (8 * b);

            switch (prefix & 0xFC)
            {
                case 0x84: reportId = value; break;
                case 0x74: reportSize = value; break;
                case 0x94: reportCount = value; break;
                case 0x80: bits[reportId] = bits.GetValueOrDefault(reportId) + reportSize * reportCount; break;
            }

            i += 1 + size;
        }

        return bits;
    }

    [Fact]
    public void Every_report_length_matches_the_descriptor()
    {
        var bits = InputBitsByReport(HidDescriptors.ReportMapValue);

        Assert.Equal(HidReports.KeyboardReportLength * 8, bits[HidDescriptors.KeyboardReportId]);
        Assert.Equal(HidReports.MouseReportLength * 8, bits[HidDescriptors.MouseReportId]);
        Assert.Equal(HidReports.TouchReportLength * 8, bits[HidDescriptors.TouchReportId]);
    }

    [Fact]
    public void Back_is_a_single_win_backspace()
    {
        // Single on purpose: unlike Home, going Back twice is not harmless.
        Assert.Equal([new KeyTap(KeyModifiers.LeftGui, 0x2A)], PhoneNavigation.Back);
    }

    [Fact]
    public void Split_screen_is_a_single_win_left()
    {
        Assert.Equal([new KeyTap(KeyModifiers.LeftGui, 0x50)], PhoneNavigation.SplitScreen);
    }

    [Fact]
    public void Home_and_recents_are_sent_with_the_meta_key()
    {
        Assert.All(PhoneNavigation.Home, tap => Assert.Equal(KeyModifiers.LeftGui, tap.Modifiers));
        Assert.All(PhoneNavigation.Recents, tap => Assert.Equal(KeyModifiers.LeftGui, tap.Modifiers));
    }

    [Fact]
    public void Descriptor_declares_the_feature_that_keeps_the_mouse_on_linux()
    {
        // The kernel matches exactly: vendor usage 0xFF0000C5, 8-bit fields, 256 of them.
        var map = HidDescriptors.ReportMapValue;
        byte[] usage = [0x06, 0x00, 0xFF, 0x09, 0xC5];
        byte[] shape = [0x75, 0x08, 0x96, 0x00, 0x01, 0xB1, 0x02];

        var usageAt = map.AsSpan().IndexOf(usage);
        Assert.True(usageAt >= 0, "vendor usage missing");
        Assert.True(map.AsSpan(usageAt).IndexOf(shape) >= 0, "256-byte feature missing");
        Assert.Equal(256, HidDescriptors.TouchCertificateLength);
    }

    [Fact]
    public void Report_ids_are_all_different()
    {
        byte[] ids =
        [
            HidDescriptors.KeyboardReportId, HidDescriptors.MouseReportId,
            HidDescriptors.TouchReportId, HidDescriptors.TouchCertificateReportId
        ];

        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public void Touch_report_id_is_distinct()
    {
        Assert.NotEqual(HidDescriptors.KeyboardReportId, HidDescriptors.TouchReportId);
        Assert.NotEqual(HidDescriptors.MouseReportId, HidDescriptors.TouchReportId);
    }

    [Fact]
    public void Touch_down_sets_tip_and_range_and_encodes_little_endian()
    {
        Assert.Equal(new byte[] { 0x03, 0x00, 0x34, 0x12, 0x78, 0x56, 0x01 },
            HidReports.Touch(true, 0x1234, 0x5678));
    }

    [Fact]
    public void Touch_up_clears_tip_and_range_but_keeps_the_contact_count()
    {
        var report = HidReports.Touch(false, 10, 20);

        Assert.Equal(0x00, report[0]);
        Assert.Equal(0x01, report[6]);
    }

    [Fact]
    public void Touch_coordinates_are_clamped_to_the_logical_range()
    {
        var report = HidReports.Touch(true, -5, 99999);

        Assert.Equal(0, BitConverter.ToUInt16(report, 2));
        Assert.Equal(HidReports.TouchLogicalMax, BitConverter.ToUInt16(report, 4));
    }

    [Fact]
    public void Centre_of_the_view_maps_to_the_centre_of_the_video()
    {
        var (x, y) = TouchMapper.Map(960, 640, 1920, 1280, Wide);

        Assert.InRange(x, 16383, 16384);
        Assert.InRange(y, 16383, 16384);
    }

    [Fact]
    public void Wide_video_on_a_three_by_two_screen_has_bars_top_and_bottom()
    {
        // 1920 wide at 16:9 is 1080 tall, leaving 100 above and below on a 1280-tall screen.
        Assert.False(TouchMapper.IsInsideVideo(960, 99, 1920, 1280, Wide));
        Assert.True(TouchMapper.IsInsideVideo(960, 100, 1920, 1280, Wide));
        Assert.True(TouchMapper.IsInsideVideo(960, 1180, 1920, 1280, Wide));
        Assert.False(TouchMapper.IsInsideVideo(960, 1181, 1920, 1280, Wide));
    }

    [Fact]
    public void Top_left_of_the_letterboxed_video_maps_to_the_origin()
    {
        Assert.Equal((0, 0), TouchMapper.Map(0, 100, 1920, 1280, Wide));
    }

    [Fact]
    public void Bottom_right_of_the_letterboxed_video_maps_to_the_maximum()
    {
        Assert.Equal((HidReports.TouchLogicalMax, HidReports.TouchLogicalMax),
            TouchMapper.Map(1920, 1180, 1920, 1280, Wide));
    }

    [Fact]
    public void Dragging_into_the_bars_stays_on_the_video_edge()
    {
        Assert.Equal(0, TouchMapper.Map(960, 10, 1920, 1280, Wide).Y);
    }

    [Fact]
    public void Narrow_video_on_a_wide_view_has_bars_at_the_sides()
    {
        // 1000 tall at 1:1 is 1000 wide, leaving 500 either side of a 2000-wide view.
        Assert.False(TouchMapper.IsInsideVideo(499, 500, 2000, 1000, 1.0));
        Assert.Equal(0, TouchMapper.Map(500, 500, 2000, 1000, 1.0).X);
    }
}
