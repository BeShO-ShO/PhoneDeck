using System.Numerics;
using System.Runtime.InteropServices;
using PhoneDeck.Core;
using Windows.Media.Playback;
using Windows.System;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;

namespace PhoneDeck.App.Viewer;

/// <summary>
/// Shows the cast in the viewer window. Windows' media player draws each frame itself into a
/// composition surface on the window, at the picture's own pixel size: copying frames out through
/// the app halved the frame rate in testing. Everything here runs on the viewer's window thread,
/// which owns the dispatcher queue the compositor needs; <see cref="Show"/> and
/// <see cref="SetAspect"/> may be called from anywhere.
/// </summary>
internal sealed class CastPicture : IDisposable
{
    private readonly Action<string> _log;
    private readonly DispatcherQueue _queue;
    private readonly Compositor _compositor;
    private readonly DesktopWindowTarget _target;
    private readonly SpriteVisual _visual;

    // Window thread only.
    private MediaPlayer? _player;
    private MediaPlayerSurface? _surface;
    private CompositionSurfaceBrush? _brush;
    private int _width, _height;
    private double _aspect = TouchMapper.DefaultVideoAspect;
    private bool _disposed;

    /// <summary>On the window thread, after <see cref="EnsureDispatcherQueue"/>.</summary>
    public CastPicture(IntPtr window, int width, int height, Action<string> log)
    {
        _log = log;
        _queue = DispatcherQueue.GetForCurrentThread()
                 ?? throw new InvalidOperationException("the viewer thread has no dispatcher queue");
        _compositor = new Compositor();
        var interop = WinRT.CastExtensions.As<ICompositorDesktopInterop>(_compositor);
        interop.CreateDesktopWindowTarget(window, false, out var targetPointer);
        try { _target = DesktopWindowTarget.FromAbi(targetPointer); }
        finally { Marshal.Release(targetPointer); }

        var root = _compositor.CreateContainerVisual();
        _target.Root = root;
        _visual = _compositor.CreateSpriteVisual();
        root.Children.InsertAtTop(_visual);
        Resize(width, height);
    }

    /// <summary>
    /// Shows the player's picture, or black for null. Callable from any thread.
    /// <paramref name="retired"/> is a player the cast has finished with: it is closed only after
    /// this window has let go of its picture, and off this thread. Closing it while the window
    /// still drew from it hung the window when the phone left DeX.
    /// </summary>
    public void Show(MediaPlayer? player, MediaPlayer? retired = null)
    {
        if (!_queue.TryEnqueue(() =>
            {
                Attach(player);
                Retire(retired);
            }))
            Retire(retired);
    }

    private void Retire(MediaPlayer? retired)
    {
        if (retired is null || ReferenceEquals(retired, _player)) return;
        _ = Task.Run(() =>
        {
            try { retired.Dispose(); }
            catch (Exception ex) { _log($"cast: closing the old player: {ex.Message}"); }
        });
    }

    /// <summary>The cast's width over height. Callable from any thread.</summary>
    public void SetAspect(double aspect) => _queue.TryEnqueue(() =>
    {
        if (aspect <= 0 || Math.Abs(aspect - _aspect) < 0.001) return;
        _aspect = aspect;
        Layout();
    });

    /// <summary>The window's client size changed. Window thread.</summary>
    public void Resize(int width, int height)
    {
        _width = width;
        _height = height;
        Layout();
    }

    private void Attach(MediaPlayer? player)
    {
        if (_disposed || ReferenceEquals(player, _player)) return;
        Detach();
        // A cast that has ended leaves the window black rather than holding a closing player.
        if (player is null) return;
        _player = player;
        if (player is null) return;

        try
        {
            // Size first: without it the player draws at 640x360 and the picture comes out blurry.
            Layout();
            _surface = player.GetSurface(_compositor);
            _brush = _compositor.CreateSurfaceBrush(_surface.CompositionSurface);
            _brush.Stretch = CompositionStretch.Fill;
            _visual.Brush = _brush;
        }
        catch (Exception ex)
        {
            _log($"cast: could not show the picture: {ex.Message}");
        }
    }

    private void Detach()
    {
        _visual.Brush = null;
        // The receiver may already have closed the player this surface came from.
        try { _brush?.Dispose(); }
        catch (Exception ex) { _log($"cast: releasing the picture: {ex.Message}"); }
        try { _surface?.Dispose(); }
        catch (Exception ex) { _log($"cast: releasing the picture: {ex.Message}"); }
        _brush = null;
        _surface = null;
        _player = null;
    }

    /// <summary>The visual is the letterboxed picture itself, and the player draws at its size.</summary>
    private void Layout()
    {
        if (_disposed) return;
        var picture = VideoRect.Letterbox(0, 0, _width, _height, _aspect);
        _visual.Size = new Vector2((float)picture.Width, (float)picture.Height);
        _visual.Offset = new Vector3((float)picture.Left, (float)picture.Top, 0);
        if (_player is not null && picture.Width >= 1 && picture.Height >= 1)
        {
            try { _player.SetSurfaceSize(new global::Windows.Foundation.Size(picture.Width, picture.Height)); }
            catch (Exception ex) { _log($"cast: could not size the picture: {ex.Message}"); }
        }
    }

    /// <summary>Window thread.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        Detach();
        _disposed = true;
        _visual.Dispose();
        _target.Dispose();
        _compositor.Dispose();
    }

    private static object? _controller;

    /// <summary>
    /// The compositor needs a dispatcher queue on its thread. Called on the viewer's window thread
    /// before the window is made; the controller lives as long as the thread.
    /// </summary>
    public static void EnsureDispatcherQueue()
    {
        if (DispatcherQueue.GetForCurrentThread() is not null) return;
        var options = new DispatcherQueueOptions
        {
            dwSize = Marshal.SizeOf<DispatcherQueueOptions>(),
            threadType = DQTYPE_THREAD_CURRENT,
            apartmentType = DQTAT_COM_STA,
        };
        Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out var controller));
        // Kept so it is never released while the thread runs; one viewer thread at a time.
        _controller = controller;
    }

    private const int DQTYPE_THREAD_CURRENT = 2;
    private const int DQTAT_COM_STA = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int dwSize;
        public int threadType;
        public int apartmentType;
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);

    [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorDesktopInterop
    {
        void CreateDesktopWindowTarget(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool isTopmost, out IntPtr target);
    }
}
