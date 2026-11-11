using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BleHid.Core;

namespace BleHid.App.Views;

/// <summary>
/// Experiment: a see-through full-screen layer over a full-screen video of the phone. One finger
/// on the video becomes one touch at the same point on the phone.
/// </summary>
public sealed class TouchOverlayWindow : Window
{
    private const double VideoAspect = 16.0 / 9.0;
    private const int SendIntervalMs = 15;

    private enum Kind { Down, Move, Up }
    private readonly record struct TouchEvent(Kind Kind, int X, int Y);

    private readonly Func<bool, int, int, Task> _send;
    private readonly List<TouchEvent> _queue = [];
    private readonly Func<IReadOnlyList<KeyTap>, Task> _sendKeys;
    private readonly List<Button> _buttons = [];
    private TouchDevice? _contact;
    private (int X, int Y) _lastPoint;
    private bool _pumping;

    public TouchOverlayWindow(Func<bool, int, int, Task> send, Func<IReadOnlyList<KeyTap>, Task> sendKeys)
    {
        _send = send;
        _sendKeys = sendKeys;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        // Fully transparent pixels do not receive input, so this is the least visible that does.
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        Topmost = true;
        ShowInTaskbar = false;
        WindowState = WindowState.Maximized;
        Stylus.SetIsPressAndHoldEnabled(this, false);
        Stylus.SetIsFlicksEnabled(this, false);

        var exitButton = MakeButton("Exit touch (Esc)");
        exitButton.Margin = new Thickness(8);
        exitButton.HorizontalAlignment = HorizontalAlignment.Right;
        exitButton.VerticalAlignment = VerticalAlignment.Top;
        exitButton.Click += (_, _) => Close();

        // Bottom centre sits in the black bar when a 16:9 video is letterboxed on a 3:2 screen.
        var navigation = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 16),
            Children =
            {
                MakeNavigationButton("Back", PhoneNavigation.Back),
                MakeNavigationButton("Home", PhoneNavigation.Home),
                MakeNavigationButton("Recents", PhoneNavigation.Recents)
            }
        };

        Content = new Grid { Children = { exitButton, navigation } };

        Loaded += (_, _) => Activate();
    }

    private Button MakeButton(string text)
    {
        var button = new Button { Content = text, Padding = new Thickness(24, 12, 24, 12) };
        _buttons.Add(button);
        return button;
    }

    private Button MakeNavigationButton(string text, KeyTap[] taps)
    {
        var button = MakeButton(text);
        button.Margin = new Thickness(8, 0, 8, 0);
        button.Click += async (_, _) =>
        {
            try { await _sendKeys(taps); }
            catch (Exception) { /* A dropped link should not take the app down. */ }
        };
        return button;
    }

    private bool IsOverButton(TouchEventArgs e) =>
        _buttons.Any(button => button.InputHitTest(e.GetTouchPoint(button).Position) is not null);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        base.OnKeyDown(e);
    }

    protected override void OnTouchDown(TouchEventArgs e)
    {
        base.OnTouchDown(e);
        // Left unhandled so the button still receives its tap as a click.
        if (IsOverButton(e)) return;

        e.Handled = true;
        if (_contact is not null) return; // One finger only in this experiment.

        var point = e.GetTouchPoint(this).Position;
        if (!TouchMapper.IsInsideVideo(point.X, point.Y, ActualWidth, ActualHeight, VideoAspect)) return;

        _contact = e.TouchDevice;
        CaptureTouch(e.TouchDevice);
        Enqueue(Kind.Down, point);
    }

    protected override void OnTouchMove(TouchEventArgs e)
    {
        base.OnTouchMove(e);
        if (e.TouchDevice != _contact) return;
        e.Handled = true;
        Enqueue(Kind.Move, e.GetTouchPoint(this).Position);
    }

    protected override void OnTouchUp(TouchEventArgs e)
    {
        base.OnTouchUp(e);
        if (e.TouchDevice != _contact) return;
        e.Handled = true;
        Enqueue(Kind.Up, e.GetTouchPoint(this).Position);
        ReleaseTouchCapture(e.TouchDevice);
        _contact = null;
    }

    protected override void OnClosed(EventArgs e)
    {
        // A contact left down would stay pressed on the phone.
        if (_contact is not null) _ = _send(false, _lastPoint.X, _lastPoint.Y);
        base.OnClosed(e);
    }

    private void Enqueue(Kind kind, Point point)
    {
        _lastPoint = TouchMapper.Map(point.X, point.Y, ActualWidth, ActualHeight, VideoAspect);
        var touch = new TouchEvent(kind, _lastPoint.X, _lastPoint.Y);

        // The link cannot carry every move, but a press and a release must never be merged away.
        if (kind == Kind.Move && _queue.Count > 0 && _queue[^1].Kind == Kind.Move) _queue[^1] = touch;
        else _queue.Add(touch);

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
                await _send(next.Kind != Kind.Up, next.X, next.Y);
                await Task.Delay(SendIntervalMs);
            }
        }
        catch (Exception)
        {
            // A dropped link ends the experiment's input; it should not take the app down.
            _queue.Clear();
        }
        finally
        {
            _pumping = false;
        }
    }
}
