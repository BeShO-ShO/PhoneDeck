using System.Windows;
using Forms = System.Windows.Forms;

namespace PhoneDeck.App.Services;

/// <summary>
/// Notification area presence. WPF-UI 4.3 ships no tray control, so this wraps the WinForms one.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly System.Drawing.Icon _current;
    private string _target = "nothing yet";
    private string? _lockWarning;

    public TrayIconService(Action open, Action exit)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open PhoneDeck", null, (_, _) => open());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());

        _current = LoadIcon();
        _icon = new Forms.NotifyIcon
        {
            Text = "PhoneDeck",
            ContextMenuStrip = menu,
            Icon = _current,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => open();
    }

    public void ShowMessage(string text) =>
        _icon.ShowBalloonTip(3000, "PhoneDeck", text, Forms.ToolTipIcon.Info);

    /// <summary>
    /// Keeps the notification area in step with where input is going and the state of the lock
    /// keys there. The physical keyboard lamp belongs to Windows and describes Windows, so the
    /// tray is the honest place to show a different machine's lock state.
    /// </summary>
    public void UpdateStatus(string target, string? lockWarning)
    {
        if (_target == target && _lockWarning == lockWarning) return;

        var previous = _lockWarning;
        _target = target;
        _lockWarning = lockWarning;

        // NotifyIcon.Text throws above 63 characters, and host names are user-controlled.
        var text = $"PhoneDeck - {target}" + (lockWarning is null ? string.Empty : $"\n{lockWarning}");
        _icon.Text = text.Length <= 63 ? text : text[..63];

        // Only on a rising edge: re-announcing an unchanged warning on every switch would nag.
        if (lockWarning is not null && lockWarning != previous)
            _icon.ShowBalloonTip(3000, "Lock keys", $"{target}: {lockWarning}", Forms.ToolTipIcon.Warning);
    }

    private System.Drawing.Icon? _live;
    private IntPtr _liveHandle;

    /// <summary>Red while the phone has the keyboard and mouse, PhoneDeck's blue otherwise.</summary>
    public void SetLive(bool live)
    {
        try
        {
            _icon.Icon = live ? (_live ??= MakeLive()) : _current;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            // A tray icon that cannot change colour is not worth failing capture over.
        }
    }

    private System.Drawing.Icon MakeLive()
    {
        using var bitmap = _current.ToBitmap();
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.A == 0) continue;
                var (r, g, b) = PhoneDeck.Core.LiveTint.Apply(pixel.R, pixel.G, pixel.B);
                bitmap.SetPixel(x, y, System.Drawing.Color.FromArgb(pixel.A, r, g, b));
            }
        }
        _liveHandle = bitmap.GetHicon();
        return System.Drawing.Icon.FromHandle(_liveHandle);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    // SmallIconSize is the DPI-scaled tray metric, so this selects the frame Windows is about to
    // ask for instead of letting it rescale a mismatched one.
    private static System.Drawing.Icon LoadIcon()
    {
        var uri = new Uri("pack://application:,,,/Assets/phonedeck.ico");
        using var stream = Application.GetResourceStream(uri)!.Stream;
        return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _current.Dispose();
        _live?.Dispose();
        if (_liveHandle != IntPtr.Zero) DestroyIcon(_liveHandle);
    }
}
