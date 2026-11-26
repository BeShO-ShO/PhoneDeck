using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PhoneDeck.Core;

namespace PhoneDeck.App.Views;

/// <summary>
/// A short note near the top of the phone's picture when control changes hands: who has the
/// keyboard and mouse and how to get back. It is click-through and never takes focus, so it
/// changes nothing about which window is in front. (The tray icon shows the state the rest
/// of the time.)
/// </summary>
public sealed class ControlIndicator
{
    private static readonly Color LiveColor = Color.FromRgb(0xE2, 0x4B, 0x4A);

    private HwndSource? _note;
    private TextBlock? _noteText;
    private readonly DispatcherTimer _noteTimer;

    public ControlIndicator()
    {
        _noteTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _noteTimer.Tick += (_, _) => HideNote();
    }

    public void Show(string message) => ShowNote(message);

    public void Close()
    {
        HideNote();
        _note?.Dispose();
        _note = null;
    }

    private void ShowNote(string message)
    {
        if (_note is null)
        {
            _noteText = new TextBlock { Foreground = Brushes.White, FontSize = 14 };
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x20, 0x20, 0x20)),
                BorderBrush = new SolidColorBrush(LiveColor),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 8, 14, 8),
                Child = _noteText
            };
            // Sized by WPF to its text; only moved from here, never resized.
            _note = new HwndSource(new HwndSourceParameters("PhoneDeckNote")
            {
                WindowStyle = unchecked((int)Native.WS_POPUP),
                ExtendedWindowStyle = Native.ClickThroughStyle,
                UsesPerPixelOpacity = true,
                PositionX = -10000,
                PositionY = -10000
            })
            {
                SizeToContent = SizeToContent.WidthAndHeight,
                RootVisual = border
            };
        }

        _noteText!.Text = message;
        var root = (UIElement)_note.RootVisual;
        root.BeginAnimation(UIElement.OpacityProperty, null);
        root.Opacity = 1;
        root.UpdateLayout();

        // Centred near the top of the picture, or of the main screen when there is none.
        Native.GetWindowRect(_note.Handle, out var rect);
        var width = rect.Right - rect.Left;
        var area = VideoRegion.Current ?? new VideoRect(0, 0, ScreenMetrics.PrimaryWidth, ScreenMetrics.PrimaryHeight);
        var x = (int)Math.Round(area.Left + (area.Width - width) / 2);
        var y = (int)Math.Round(area.Top + Math.Min(32 * Native.SystemScale(), area.Height / 10));
        Native.SetWindowPos(_note.Handle, Native.HWND_TOPMOST, x, y, 0, 0,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW | Native.SWP_NOSIZE);

        _noteTimer.Stop();
        _noteTimer.Start();
    }

    private void HideNote()
    {
        _noteTimer.Stop();
        if (_note is not { } note || !Native.IsWindowVisible(note.Handle)) return;
        var root = (UIElement)note.RootVisual;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
        fade.Completed += (_, _) =>
        {
            if (root.Opacity == 0) Native.ShowWindow(note.Handle, Native.SW_HIDE);
        };
        root.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private static class Native
    {
        public const uint WS_POPUP = 0x80000000;
        private const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80;
        private const int WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
        public const int ClickThroughStyle = WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE;
        public const uint SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
        public const int SW_HIDE = 0;
        public static readonly IntPtr HWND_TOPMOST = -1;

        public static double SystemScale()
        {
            try { return GetDpiForSystem() / 96.0; }
            catch (EntryPointNotFoundException) { return 1; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr window, out RECT rect);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForSystem();
    }
}
