using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PhoneDeck.Core;

/// <summary>
/// Short async waits that end on time. <see cref="Task.Delay(int)"/> rides the system timer,
/// which rounds a 10 ms wait up to the next tick (up to 15.6 ms unless the process holds a
/// 1 ms resolution, and Windows 11 can withdraw that from a process in the tray). A
/// high-resolution waitable timer (Windows 10 1803+) fires within about half a millisecond
/// whatever the system resolution is. Falls back to <see cref="Task.Delay(int)"/> where it is
/// unavailable. One wait at a time: the next must not start before the previous finishes.
/// </summary>
public sealed class PreciseTimer : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x2;
    private const uint TimerAllAccess = 0x1F0003;

    private readonly SafeWaitHandle? _handle;
    private readonly WaitHandle? _wait;

    public PreciseTimer()
    {
        var handle = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
        if (handle == IntPtr.Zero) return;
        _handle = new SafeWaitHandle(handle, ownsHandle: true);
        _wait = new TimerWaitHandle(_handle);
    }

    /// <summary>False when waits fall back to <see cref="Task.Delay(int)"/>.</summary>
    public bool IsHighResolution => _wait is not null;

    public Task WaitAsync(int milliseconds, CancellationToken token = default)
    {
        if (milliseconds <= 0) return Task.CompletedTask;
        if (_wait is null || _handle is null) return Task.Delay(milliseconds, token);
        token.ThrowIfCancellationRequested();

        // Negative due time means relative, in 100 ns units.
        var due = -milliseconds * 10_000L;
        if (!SetWaitableTimer(_handle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            return Task.Delay(milliseconds, token);

        // The thread pool's wait thread wakes on the signal itself, so no timer tick is involved.
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ThreadPool.RegisterWaitForSingleObject(
            _wait, (_, _) => fired.TrySetResult(), null, Timeout.Infinite, executeOnlyOnce: true);
        var cancellation = token.Register(() =>
        {
            CancelWaitableTimer(_handle);
            fired.TrySetCanceled(token);
        });
        return Finish(fired.Task, registration, cancellation);
    }

    private static async Task Finish(Task fired, RegisteredWaitHandle registration,
        CancellationTokenRegistration cancellation)
    {
        try { await fired.ConfigureAwait(false); }
        finally
        {
            registration.Unregister(null);
            cancellation.Dispose();
        }
    }

    public void Dispose() => _wait?.Dispose();

    private sealed class TimerWaitHandle : WaitHandle
    {
        public TimerWaitHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period,
        IntPtr completionRoutine, IntPtr completionArgument, bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelWaitableTimer(SafeWaitHandle timer);
}
