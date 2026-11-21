using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PhoneDeck.App.Services;
using PhoneDeck.Core;

namespace PhoneDeck.App.Views;

/// <summary>
/// The small button bar shown while input is going to the phone. It sits in the black bar below
/// the video and also owns the touch and pen reader and the viewer locator, so touch lives
/// exactly as long as this window. Nothing covers the video itself.
/// </summary>
public sealed class TouchOverlayWindow : Window
{
    private const int SendIntervalMs = 15;

    /// <summary>
    /// The phone releases a finger it has not heard about for about 100 ms (Linux's "sticky
    /// fingers" timer), so fingers held still are re-sent well inside that.
    /// </summary>
    private const int KeepAliveMs = 50;

    /// <summary>Most space left between the picture and the bar, in DIPs.</summary>
    private const double BarGap = 24;

    private abstract record Pending;
    private sealed record TouchFramePending(List<TouchPoint> Points, bool MoveOnly) : Pending;
    private sealed record PenPending(PenFlags Flags, int X, int Y, int Pressure) : Pending;

    private static readonly Geometry RecentsIcon = Geometry.Parse("M5,4 V20 M12,4 V20 M19,4 V20");
    private static readonly Geometry HomeIcon = new RectangleGeometry(new Rect(3, 3, 18, 18), 5, 5);
    private static readonly Geometry BackIcon = Geometry.Parse("M15,3 L6,12 L15,21");
    private static readonly Geometry SplitIcon = Geometry.Parse("M3,4 H21 V20 H3 Z M12,4 V20");
    private static readonly Geometry PauseIcon = Geometry.Parse("M8,5 V19 M16,5 V19");
    private static readonly Geometry FullScreenIcon = Geometry.Parse("M3,9 V3 H9 M15,3 H21 V9 M21,15 V21 H15 M9,21 H3 V15");

    private readonly Func<IReadOnlyList<TouchPoint>, Task> _sendTouch;
    private readonly Func<PenFlags, int, int, int, Task> _sendPen;
    private readonly Func<IReadOnlyList<KeyTap>, Task> _sendKeys;
    private readonly Action<string> _log;
    private readonly MultiTouchTracker _tracker = new();
    private readonly PenTracker _penTracker = new();
    private readonly RawTouchReader _reader;
    private readonly MpvVideoLocator _locator;
    private readonly DispatcherTimer _keepAlive;
    private readonly List<Pending> _queue = [];
    private readonly Stopwatch _sinceTouchSent = Stopwatch.StartNew();
    private HashSet<int> _queuedSlots = [];
    private bool _pumping;

    public TouchOverlayWindow(Func<IReadOnlyList<TouchPoint>, Task> sendTouch, Func<PenFlags, int, int, int, Task> sendPen,
        Func<IReadOnlyList<KeyTap>, Task> sendKeys, Action stopCapture, Action<string> log, string targetName,
        Func<int?> viewerProcessId)
    {
        _sendTouch = sendTouch;
        _sendPen = sendPen;
        _sendKeys = sendKeys;
        _log = log;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Black;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        SizeToContent = SizeToContent.Height;
        // Room for the navigation buttons centred and two action buttons on the right.
        Width = 720;

        var pauseButton = MakeButton("Stop capture", PauseIcon);
        pauseButton.Click += (_, _) => stopCapture();

        var fullScreenButton = MakeButton("Full screen or window", FullScreenIcon);
        fullScreenButton.Click += (_, _) => _ = ToggleFullScreenAsync();

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { fullScreenButton, pauseButton }
        };

        // Says where input is going, so a glance at the bar answers "is this on?".
        var status = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            Children =
            {
                new System.Windows.Shapes.Ellipse
                {
                    Width = 8, Height = 8, Fill = LiveBrush,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0)
                },
                new TextBlock
                {
                    Text = ShortName(targetName), Foreground = LiveBrush, FontSize = 13,
                    MaxWidth = 140, TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };

        var navigation = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                // Split screen, then Samsung's navigation bar order: Recents, Home, Back.
                MakeNavigationButton("Split screen", SplitIcon, PhoneNavigation.SplitScreen),
                MakeNavigationButton("Recents", RecentsIcon, PhoneNavigation.Recents),
                MakeNavigationButton("Home", HomeIcon, PhoneNavigation.Home),
                MakeNavigationButton("Back", BackIcon, PhoneNavigation.Back)
            }
        };

        var layout = new Grid { Margin = new Thickness(4) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(navigation, 1);
        Grid.SetColumn(actions, 2);
        layout.Children.Add(status);
        layout.Children.Add(navigation);
        layout.Children.Add(actions);
        Content = layout;

        Loaded += (_, _) => PlaceBar();
        VideoRegion.Changed += OnVideoMoved;
        _locator = new MpvVideoLocator(viewerProcessId, message => log($"viewer: {message}"));
        _reader = new RawTouchReader(OnFrame, OnPen, log);

        _keepAlive = new DispatcherTimer(TimeSpan.FromMilliseconds(KeepAliveMs / 2), DispatcherPriority.Input,
            (_, _) => KeepFingersDown(), Dispatcher);
        _keepAlive.Start();
    }

    private void OnVideoMoved() => Dispatcher.BeginInvoke(PlaceBar);

    /// <summary>
    /// A double tap on the video belongs to the phone, so mpv's own double-click full screen is
    /// switched off; the bar's button does it instead, without leaving capture.
    /// </summary>
    private async Task ToggleFullScreenAsync()
    {
        if (!await MpvVideoLocator.SendCommandAsync("cycle", "fullscreen"))
            _log("viewer: full screen needs the mpv PhoneDeck starts (or --input-ipc-server); nothing changed");
    }

    /// <summary>
    /// Centred under the video, in the black area below it when there is room; otherwise above
    /// it; otherwise over the bottom of the video, where touches on the bar stay the bar's.
    /// </summary>
    private void PlaceBar()
    {
        if (!IsLoaded) return;

        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var video = VideoRegion.Resolve();
        var screenWidth = ScreenMetrics.PrimaryWidth / scale;
        var screenHeight = ScreenMetrics.PrimaryHeight / scale;
        var videoTop = video.Top / scale;
        var videoBottom = (video.Top + video.Height) / scale;
        var below = screenHeight - videoBottom;

        Left = Math.Clamp((video.Left + video.Width / 2) / scale - ActualWidth / 2, 0, Math.Max(0, screenWidth - ActualWidth));
        // Centred in a thin black strip, as before; under a windowed picture it stays close by
        // rather than drifting to the bottom of the screen.
        if (below >= ActualHeight) Top = videoBottom + Math.Min((below - ActualHeight) / 2, BarGap);
        else if (videoTop >= ActualHeight) Top = videoTop - ActualHeight - Math.Min((videoTop - ActualHeight) / 2, BarGap);
        else Top = screenHeight - ActualHeight;

        var overlaps = below < ActualHeight && videoTop < ActualHeight;
        VideoRegion.Excluded = overlaps
            ? new VideoRect(Left * scale, Top * scale, ActualWidth * scale, ActualHeight * scale)
            : null;
        _log($"bar placed at {Left:F0},{Top:F0}; video {video.Left:F0},{video.Top:F0} {video.Width:F0}x{video.Height:F0}{(overlaps ? " (over the video)" : "")}");
    }

    private static readonly Brush LiveBrush = new SolidColorBrush(Color.FromRgb(0xE2, 0x4B, 0x4A));

    /// <summary>The device name without the Bluetooth address the target display appends.</summary>
    private static string ShortName(string target)
    {
        var bracket = target.IndexOf(" [", StringComparison.Ordinal);
        return bracket > 0 ? target[..bracket] : target;
    }

    // A flat, borderless template: icons alone read cleaner on a black bar than boxed buttons.
    // Transparent rather than null, so the whole padded area still takes the tap.
    private static ControlTemplate FlatTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.PaddingProperty, new Thickness(24, 12, 24, 12));

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);

        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    private static UIElement MakeIcon(Geometry icon) => new System.Windows.Shapes.Path
    {
        Data = icon,
        Width = 24,
        Height = 24,
        Stroke = Brushes.White,
        StrokeThickness = 2.4,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round
    };

    private static Button MakeButton(string name, Geometry icon)
    {
        var button = new Button { Content = MakeIcon(icon), Template = FlatTemplate() };
        AutomationProperties.SetName(button, name);
        return button;
    }

    private Button MakeNavigationButton(string name, Geometry icon, KeyTap[] taps)
    {
        var button = MakeButton(name, icon);
        button.Margin = new Thickness(8, 0, 8, 0);
        button.Click += (_, _) => _ = SendKeysAsync(taps);
        return button;
    }

    private async Task SendKeysAsync(KeyTap[] taps)
    {
        try { await _sendKeys(taps); }
        catch (Exception) { /* A dropped link should not take the app down. */ }
    }

    // Raw Input fractions are of the touchscreen, which is the main display.
    private static (double X, double Y) ToScreen(double fractionX, double fractionY) =>
        (fractionX * ScreenMetrics.PrimaryWidth, fractionY * ScreenMetrics.PrimaryHeight);

    private void OnFrame(IReadOnlyList<RawContact> frame)
    {
        var onScreen = frame.Select(contact =>
        {
            var (x, y) = ToScreen(contact.X, contact.Y);
            return contact with { X = x, Y = y };
        }).ToList();

        if (_tracker.Update(onScreen, VideoRegion.IsOnVideo) is { } output) EnqueueTouch(output);
    }

    private void OnPen(RawPen pen)
    {
        var (x, y) = ToScreen(pen.X, pen.Y);
        if (_penTracker.Update(pen with { X = x, Y = y }, VideoRegion.IsOnVideo(x, y)) is { } output) EnqueuePen(output);
    }

    /// <summary>Re-sends fingers held still, so the phone does not decide they have lifted.</summary>
    private void KeepFingersDown()
    {
        if (!_tracker.HasContacts || _pumping || _queue.Count > 0) return;
        if (_sinceTouchSent.ElapsedMilliseconds < KeepAliveMs) return;
        EnqueueTouch(_tracker.Current());
    }

    protected override void OnClosed(EventArgs e)
    {
        _keepAlive.Stop();
        VideoRegion.Changed -= OnVideoMoved;
        _reader.Dispose();
        _locator.Dispose();
        VideoRegion.Excluded = null;

        // A finger or pen left down would stay pressed on the phone.
        var video = VideoRegion.Resolve();
        if (_tracker.Cancel() is { } lifted) _ = _sendTouch(lifted.Select(contact => ToPoint(contact, video)).ToList());
        if (_penTracker.Cancel() is { } away)
        {
            var (x, y) = TouchMapper.Map(away.X, away.Y, video);
            _ = _sendPen(away.Flags, x, y, 0);
        }
        base.OnClosed(e);
    }

    private static TouchPoint ToPoint(TrackedContact contact, VideoRect video)
    {
        var (x, y) = TouchMapper.Map(contact.X, contact.Y, video);
        return new TouchPoint(contact.Slot, contact.Down, x, y);
    }

    private void EnqueueTouch(List<TrackedContact> frame)
    {
        var video = VideoRegion.Resolve();
        var points = frame.Select(contact => ToPoint(contact, video)).ToList();
        var down = frame.Where(contact => contact.Down).Select(contact => contact.Slot).ToHashSet();

        // Only frames that just move the same fingers may be merged: a press or a lift never is.
        var moveOnly = frame.All(contact => contact.Down) && down.SetEquals(_queuedSlots);
        _queuedSlots = down;

        if (moveOnly && _queue.Count > 0 && _queue[^1] is TouchFramePending { MoveOnly: true })
            _queue[^1] = new TouchFramePending(points, true);
        else
            _queue.Add(new TouchFramePending(points, moveOnly));

        _ = PumpAsync();
    }

    private void EnqueuePen(PenOutput output)
    {
        var (x, y) = TouchMapper.Map(output.X, output.Y, VideoRegion.Resolve());
        var pen = new PenPending(output.Flags, x, y, (int)Math.Round(output.Pressure * HidReports.PenPressureMax));

        // Hover and drawing moves merge; a change of tip, button or range never does.
        if (_queue.Count > 0 && _queue[^1] is PenPending last && last.Flags == pen.Flags)
            _queue[^1] = pen;
        else
            _queue.Add(pen);

        _ = PumpAsync();
    }

    private async Task PumpAsync()
    {
        if (_pumping) return;
        _pumping = true;
        try
        {
            while (_queue.Count > 0)
            {
                var next = _queue[0];
                _queue.RemoveAt(0);
                switch (next)
                {
                    case TouchFramePending touch:
                        await _sendTouch(touch.Points);
                        _sinceTouchSent.Restart();
                        break;
                    case PenPending pen:
                        await _sendPen(pen.Flags, pen.X, pen.Y, pen.Pressure);
                        break;
                }
                await Task.Delay(SendIntervalMs);
            }
        }
        catch (Exception ex)
        {
            // A dropped link ends the touch input; it should not take the app down.
            _log($"send failed: {ex.Message}");
            _queue.Clear();
        }
        finally
        {
            _pumping = false;
        }
    }
}
