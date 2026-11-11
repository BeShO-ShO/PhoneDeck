using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using BleHid.App.Services;
using BleHid.Core;

namespace BleHid.App.Views;

/// <summary>
/// The small button bar shown while input is going to the phone. It sits in the black bar below
/// the video and also owns the touch reader, so touch lives exactly as long as this window.
/// Nothing covers the video itself.
/// </summary>
public sealed class TouchOverlayWindow : Window
{
    private const int SendIntervalMs = 15;

    private readonly record struct TouchEvent(TouchPhase Phase, int X, int Y);

    private static readonly Geometry RecentsIcon = Geometry.Parse("M5,4 V20 M12,4 V20 M19,4 V20");
    private static readonly Geometry HomeIcon = new RectangleGeometry(new Rect(3, 3, 18, 18), 5, 5);
    private static readonly Geometry BackIcon = Geometry.Parse("M15,3 L6,12 L15,21");
    private static readonly Geometry SplitIcon = Geometry.Parse("M3,4 H21 V20 H3 Z M12,4 V20");
    private static readonly Geometry PauseIcon = Geometry.Parse("M8,5 V19 M16,5 V19");

    private readonly Func<bool, int, int, Task> _send;
    private readonly Func<IReadOnlyList<KeyTap>, Task> _sendKeys;
    private readonly Action<string> _log;
    private readonly SingleTouchTracker _tracker = new();
    private readonly RawTouchReader _reader;
    private readonly List<TouchEvent> _queue = [];
    private bool _pumping;

    public TouchOverlayWindow(Func<bool, int, int, Task> send, Func<IReadOnlyList<KeyTap>, Task> sendKeys,
        Action stopCapture, Action<string> log, string targetName)
    {
        _send = send;
        _sendKeys = sendKeys;
        _log = log;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Black;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        SizeToContent = SizeToContent.Height;
        Width = 640;

        var pauseButton = MakeButton("Stop capture", PauseIcon);
        pauseButton.Click += (_, _) => stopCapture();

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
        Grid.SetColumn(pauseButton, 2);
        pauseButton.HorizontalAlignment = HorizontalAlignment.Right;
        layout.Children.Add(status);
        layout.Children.Add(navigation);
        layout.Children.Add(pauseButton);
        Content = layout;

        Loaded += (_, _) => PlaceInBlackBar();
        _reader = new RawTouchReader(OnContact, log);
    }

    private void PlaceInBlackBar()
    {
        var screenWidth = SystemParameters.PrimaryScreenWidth;
        var screenHeight = SystemParameters.PrimaryScreenHeight;
        var barHeight = Math.Max(0, (screenHeight - screenWidth / TouchMapper.DefaultVideoAspect) / 2);

        Left = (screenWidth - ActualWidth) / 2;
        Top = screenHeight - Math.Max(ActualHeight, (barHeight + ActualHeight) / 2);
        _log($"bar placed; screen {screenWidth:F0}x{screenHeight:F0}, black bar {barHeight:F0}");
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

    private void OnContact(int id, bool touching, double fractionX, double fractionY)
    {
        var width = SystemParameters.PrimaryScreenWidth;
        var height = SystemParameters.PrimaryScreenHeight;
        var x = fractionX * width;
        var y = fractionY * height;
        var inside = TouchMapper.IsInsideVideo(x, y, width, height, TouchMapper.DefaultVideoAspect);

        if (_tracker.Update(id, touching, x, y, inside) is { } output) Enqueue(output);
    }

    protected override void OnClosed(EventArgs e)
    {
        _reader.Dispose();
        // A contact left down would stay pressed on the phone.
        if (_tracker.Cancel() is { } output)
        {
            var (x, y) = Map(output);
            _ = _send(false, x, y);
        }
        base.OnClosed(e);
    }

    private static (int X, int Y) Map(TouchOutput output) => TouchMapper.Map(output.X, output.Y,
        SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight, TouchMapper.DefaultVideoAspect);

    private void Enqueue(TouchOutput output)
    {
        var (x, y) = Map(output);
        var touch = new TouchEvent(output.Phase, x, y);

        // The link cannot carry every move, but a press and a release must never be merged away.
        if (output.Phase == TouchPhase.Move && _queue.Count > 0 && _queue[^1].Phase == TouchPhase.Move)
            _queue[^1] = touch;
        else
            _queue.Add(touch);

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
                await _send(next.Phase != TouchPhase.Up, next.X, next.Y);
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
