using System.Collections.Concurrent;
using System.Diagnostics;

namespace PhoneDeck.Core;

/// <summary>
/// Runs a capture session: local hooks in, paced HID reports out. Shared by the interactive
/// `capture` command, background mode, and the desktop UI.
/// </summary>
public static class CaptureSession
{
    internal enum Queued { Report, MouseReport, SwitchHost, GoLocal }

    internal readonly record struct InputReport(Queued Kind,
        KeyModifiers Modifiers = KeyModifiers.None, byte[]? Usages = null,
        MouseButtons Buttons = MouseButtons.None, int Dx = 0, int Dy = 0, int Wheel = 0);

    /// <summary>
    /// Two ways to batch pointer motion between reports. Coalescing (the original) sums whatever
    /// arrived. Even pacing passes motion through <see cref="PointerResampler"/> so every report
    /// carries an equal share of a steady movement, and never merges a button change: the motion
    /// before a click is flushed first, so the click lands where the pointer was.
    /// </summary>
    internal sealed class ReportQueue(Func<double>? clockMs = null)
    {
        private static readonly double TicksPerMs = Stopwatch.Frequency / 1000.0;
        private readonly Func<double> _clockMs = clockMs ?? (() => Stopwatch.GetTimestamp() / TicksPerMs);
        private readonly ConcurrentQueue<InputReport> _ordered = new();
        private readonly object _mouseLock = new();
        private InputReport? _pendingMouse;

        private readonly PointerResampler _resampler = new();
        private bool _even;
        private MouseButtons _buttons;
        private int _evenWheel;

        public bool HasMouseReport
        {
            get
            {
                lock (_mouseLock)
                    return _pendingMouse.HasValue || _resampler.HasPending || _evenWheel != 0;
            }
        }

        /// <summary>Switches batching; motion already queued is handed over, never dropped.</summary>
        public void SetEvenPacing(bool on)
        {
            lock (_mouseLock)
            {
                if (on == _even) return;
                if (on)
                {
                    if (_pendingMouse is { } mouse) _ordered.Enqueue(mouse);
                    _pendingMouse = null;
                }
                else
                {
                    FlushEven();
                }
                _even = on;
            }
        }

        public void EnqueueKeyboard(KeyModifiers modifiers, byte[] usages) =>
            _ordered.Enqueue(new(Queued.Report, modifiers, usages));

        public void EnqueueMouse(MouseButtons buttons, int dx, int dy, int wheel)
        {
            lock (_mouseLock)
            {
                if (!_even)
                {
                    var pending = _pendingMouse.GetValueOrDefault();
                    _pendingMouse = new(Queued.MouseReport, Buttons: buttons,
                        Dx: pending.Dx + dx, Dy: pending.Dy + dy, Wheel: pending.Wheel + wheel);
                    _buttons = buttons;
                    return;
                }

                if (buttons != _buttons)
                {
                    FlushEven();
                    _ordered.Enqueue(new(Queued.MouseReport, Buttons: buttons, Dx: dx, Dy: dy, Wheel: wheel));
                    _buttons = buttons;
                    return;
                }

                if (dx != 0 || dy != 0) _resampler.Add(_clockMs(), dx, dy);
                _evenWheel += wheel;
            }
        }

        public void EnqueueTargetChange(Queued kind)
        {
            lock (_mouseLock)
            {
                if (_pendingMouse is { } mouse)
                {
                    _ordered.Enqueue(mouse);
                    _pendingMouse = null;
                }
                FlushEven();
                _ordered.Enqueue(new(kind));
            }
        }

        public bool TryDequeue(out InputReport report) => _ordered.TryDequeue(out report);

        /// <summary>
        /// False when nothing is due. With even pacing that can happen while motion is still
        /// pending (<see cref="HasMouseReport"/> stays true), so the caller looks again shortly.
        /// </summary>
        public bool TryTakeMouseReport(out InputReport report)
        {
            lock (_mouseLock)
            {
                report = default;
                if (!_ordered.IsEmpty) return false;

                if (!_even)
                {
                    report = _pendingMouse.GetValueOrDefault();
                    var available = _pendingMouse.HasValue;
                    _pendingMouse = null;
                    return available;
                }

                var (dx, dy) = _resampler.Take(_clockMs());
                var wheel = _evenWheel;
                _evenWheel = 0;
                if (dx == 0 && dy == 0 && wheel == 0) return false;
                report = new(Queued.MouseReport, Buttons: _buttons, Dx: dx, Dy: dy, Wheel: wheel);
                return true;
            }
        }

        // Caller holds _mouseLock.
        private void FlushEven()
        {
            var (dx, dy) = _resampler.Flush();
            if (dx == 0 && dy == 0 && _evenWheel == 0) return;
            _ordered.Enqueue(new(Queued.MouseReport, Buttons: _buttons, Dx: dx, Dy: dy, Wheel: _evenWheel));
            _evenWheel = 0;
        }
    }

    private static readonly TimeSpan ShutdownStepTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Runs one shutdown step without letting it block or abort the steps after it: a notification
    /// to a host that just vanished can hang, and key release still has to be attempted.
    /// </summary>
    internal static async Task<bool> CompletesWithinAsync(
        Func<Task> step, TimeSpan timeout, Action<string> log, string name)
    {
        try
        {
            await step().WaitAsync(timeout);
            return true;
        }
        catch (TimeoutException)
        {
            log($"  [stop] {name} timed out");
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
        catch (Exception ex)
        {
            log($"  [stop] {name} failed: {ex.Message}");
            return false;
        }
    }

    /// <param name="mouseIntervalMs">
    /// Read per report rather than captured, so the UI can retune pacing without restarting.
    /// </param>
    /// <param name="stopEndsSession">
    /// Console mode ends on the stop hotkey (Ctrl+Alt+Q unless <paramref name="stopHotkey"/> says
    /// otherwise). Background mode has no console to return to, so the
    /// same hotkey drops to the local target instead and capture keeps running.
    /// </param>
    /// <param name="chordSwitch">Whether Ctrl+D+C switches target as well as Ctrl+Alt+CapsLock.</param>
    /// <param name="penGoesToHost">Null means the same as <paramref name="touchGoesToHost"/>.</param>
    /// <param name="evenPacing">
    /// Read per report like <paramref name="mouseIntervalMs"/>. Null means on.
    /// See <see cref="PointerResampler"/>.
    /// </param>
    public static async Task<int> RunAsync(
        BleHidPeripheral peripheral,
        Action<string> log,
        bool verbose,
        Func<int> mouseIntervalMs,
        bool stopEndsSession,
        CancellationToken cancellationToken,
        bool chordSwitch = true,
        bool touchGoesToHost = false,
        bool capsLockSwitch = true,
        Func<bool>? evenPacing = null,
        bool? penGoesToHost = null,
        Hotkey? stopHotkey = null,
        bool ctrlAltTapReturns = false,
        bool copilotKeyReturns = false)
    {
        evenPacing ??= () => true;
        var hotkey = stopHotkey is { IsValid: true } chosen ? chosen : Hotkey.Default;
        using var capture = new InputCapture { Verbose = verbose };
        capture.SetStopHotkey(hotkey);
        capture.SetReturnKeys(ctrlAltTapReturns, copilotKeyReturns);
        capture.SetChordSwitch(chordSwitch);
        capture.SetCapsLockSwitch(capsLockSwitch);
        capture.SetTouchGoesToHost(touchGoesToHost);
        capture.SetPenGoesToHost(penGoesToHost ?? touchGoesToHost);
        var stopped = new TaskCompletionSource();
        using var registration = cancellationToken.Register(() => stopped.TrySetResult());

        // The pointer switch gesture is opt-in via a JSON file, and that file is watched so a
        // toggle from the UI or another CLI instance reaches this session without a restart.
        using var gestureWatcher = new SwitchGestureWatcher(AppPaths.SwitchGestureConfig);
        ApplyGesture(gestureWatcher.Current);
        gestureWatcher.Changed += ApplyGesture;

        void ApplyGesture(SwitchGestureOptions options)
        {
            capture.SetSwitchGesture(options);
            if (options.Warning is { } warning) log($"  [gesture] {warning}");
            log(options.Enabled
                ? $"  [gesture] shake to switch enabled ({options.RequiredReversals} reversals of {options.MinStrokePx}px within {options.WindowMs}ms)"
                : "  [gesture] shake to switch disabled");
        }

        // Keystrokes must all be delivered, but pointer motion is coalesced: the hook produces
        // far more events than the BLE link can carry. Target changes travel through the same
        // queue so they take effect only after the key-release report has gone to the old host.
        var reports = new ReportQueue();
        reports.SetEvenPacing(evenPacing());
        var sent = 0;

        // The radio interleaves connection events across every subscribed link, so a second host
        // starves the one we are notifying even when it receives nothing. Broadcast pays again on
        // top of that: measured, 2 hosts needed 40 ms rather than 20 ms.
        int PointerIntervalMs()
        {
            var hostIntervalMs = peripheral.MouseReportIntervalMs(mouseIntervalMs());
            var links = Math.Max(1, peripheral.SubscribedMouseClients);
            var broadcasting = peripheral.SelectedHostId is null && !peripheral.IsLocalTarget;
            return broadcasting ? hostIntervalMs * 2 * links : hostIntervalMs;
        }

        // Signal-driven rather than polled: Task.Delay has ~15 ms granularity on Windows,
        // which alone made pointer motion feel sluggish.
        var signal = new SemaphoreSlim(0, 1);
        void Wake()
        {
            try { signal.Release(); }
            catch (SemaphoreFullException) { }
        }

        using var pumpCancellation = new CancellationTokenSource();
        using var pacingTimer = new PreciseTimer();
        if (verbose) log($"  [pump] high-resolution pacing timer: {pacingTimer.IsHighResolution}");
        var pump = Task.Run(async () =>
        {
            var clock = Stopwatch.StartNew();
            var iterations = 0;
            long lastMouseSend = -1000;
            if (verbose) log("  [pump] started");
            while (!pumpCancellation.IsCancellationRequested)
            {
                iterations++;
                try
                {
                    await signal.WaitAsync(pumpCancellation.Token);
                }
                catch (OperationCanceledException) { break; }

                try
                {
                    reports.SetEvenPacing(evenPacing());
                    while (reports.TryDequeue(out var key))
                    {
                        if (key.Kind == Queued.MouseReport)
                        {
                            await peripheral.SendMouseAsync(key.Buttons, key.Dx, key.Dy, key.Wheel);
                            lastMouseSend = clock.ElapsedMilliseconds;
                            Interlocked.Increment(ref sent);
                            continue;
                        }

                        if (key.Kind is Queued.SwitchHost or Queued.GoLocal)
                        {
                            // Read before the target moves: this is the host we are leaving, and
                            // a lock left on there is invisible once we stop sending to it.
                            var leftHost = peripheral.IsLocalTarget ? null : peripheral.SelectedHostDisplay;
                            var leftLocks = peripheral.TargetLockWarning;

                            await peripheral.RefreshHostNamesAsync();
                            var target = key.Kind == Queued.GoLocal
                                ? peripheral.SelectLocal()
                                : peripheral.SelectNextHost();
                            capture.SetPassThrough(peripheral.IsLocalTarget);

                            var locks = peripheral.TargetLockWarning;
                            var arriving = locks is null ? string.Empty : $" - {locks}";
                            log($"  [host] -> {target} (pointer interval {PointerIntervalMs()} ms){arriving}");
                            if (leftLocks is not null && leftHost is not null)
                                log($"  [host] left {leftHost} with {leftLocks}");
                            continue;
                        }

                        var started = clock.ElapsedMilliseconds;
                        await peripheral.SendKeyboardAsync(key.Modifiers, key.Usages!);
                        var elapsed = clock.ElapsedMilliseconds - started;
                        if (verbose && sent < 40) log($"  [pump] key notify #{sent} took {elapsed} ms");
                        Interlocked.Increment(ref sent);
                    }

                    if (!reports.HasMouseReport) continue;

                    // NotifyValueAsync returns on queueing, so overshoot is invisible here and
                    // shows up as pointer drift after the user stops moving.
                    var interval = PointerIntervalMs();

                    var sinceLast = clock.ElapsedMilliseconds - lastMouseSend;
                    if (sinceLast < interval)
                        await pacingTimer.WaitAsync((int)(interval - sinceLast), pumpCancellation.Token);

                    if (!reports.TryTakeMouseReport(out var mouse))
                    {
                        // Even pacing: motion is queued but not due yet. Look again shortly
                        // rather than waiting for the next input event, which may never come.
                        if (reports.HasMouseReport)
                        {
                            await pacingTimer.WaitAsync(2, pumpCancellation.Token);
                            Wake();
                        }
                        continue;
                    }

                    {
                        var started = clock.ElapsedMilliseconds;
                        await peripheral.SendMouseAsync(mouse.Buttons, mouse.Dx, mouse.Dy, mouse.Wheel);
                        lastMouseSend = clock.ElapsedMilliseconds;
                        if (verbose && sent < 40) log($"  [pump] mouse notify #{sent} ({mouse.Dx},{mouse.Dy}) took {lastMouseSend - started} ms");
                        Interlocked.Increment(ref sent);
                    }

                    // The resampler releases motion over time, so keep ticking until it drains.
                    if (reports.HasMouseReport) Wake();
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    log($"  [pump] send error: {ex}");
                }
            }

            if (verbose) log($"  [pump] exited after {iterations} iterations, {clock.ElapsedMilliseconds} ms");
        });

        capture.Log += message => log(message);
        capture.KeyboardReport += (modifiers, usages) =>
        {
            reports.EnqueueKeyboard(modifiers, usages);
            Wake();
        };
        capture.MouseReport += (buttons, dx, dy, wheel) =>
        {
            reports.EnqueueMouse(buttons, dx, dy, wheel);
            Wake();
        };
        capture.SwitchHostRequested += () =>
        {
            reports.EnqueueTargetChange(Queued.SwitchHost);
            Wake();
        };
        capture.StopRequested += () =>
        {
            if (stopEndsSession)
            {
                stopped.TrySetResult();
                return;
            }

            // Local recovery must not wait behind a notification to a host that just vanished.
            // Keep GoLocal queued as well so a healthy host still receives key releases first.
            capture.SetPassThrough(true);
            reports.EnqueueTargetChange(Queued.GoLocal);
            Wake();
        };

        // The UI can retarget mid-session, and the hook has to stop swallowing input when it does.
        void OnTargetChanged() => capture.SetPassThrough(peripheral.IsLocalTarget);

        try
        {
            await peripheral.RefreshHostNamesAsync();
            capture.SetPassThrough(peripheral.IsLocalTarget);
            peripheral.TargetChanged += OnTargetChanged;

            log($"  pointer report interval: {PointerIntervalMs()} ms");
            log($"  sending to: {peripheral.SelectedHostDisplay}");
            var switchKeys = new[] { capsLockSwitch ? "Ctrl+Alt+CapsLock" : null, chordSwitch ? "Ctrl+D+C" : null }
                .Where(key => key is not null).ToList();
            if (switchKeys.Count > 0) log($"  {string.Join(" or ", switchKeys)} switches target.");
            var ways = new[] { hotkey.ToString(), ctrlAltTapReturns ? "a tap of Ctrl+Alt" : null,
                copilotKeyReturns ? "the Copilot key" : null }.Where(way => way is not null);
            log(stopEndsSession
                ? $"  capturing - {string.Join(" or ", ways)} stops."
                : $"  capturing - {string.Join(" or ", ways)} returns input to this PC.");

            capture.Start();
            await stopped.Task;
        }
        finally
        {
            capture.Stop();
            peripheral.TargetChanged -= OnTargetChanged;

            // In the finally so a failed start cannot leave the pump running or a key held down.
            pumpCancellation.Cancel();
            await CompletesWithinAsync(() => pump, ShutdownStepTimeout, log, "report pump");
            await CompletesWithinAsync(peripheral.ReleaseKeysAsync, ShutdownStepTimeout, log, "key release");
            await CompletesWithinAsync(peripheral.ReleaseMouseAsync, ShutdownStepTimeout, log, "mouse release");
        }

        log($"  capture stopped. keyboard events={capture.KeyboardEvents}, mouse events={capture.MouseEvents}, reports sent={sent}");
        return sent;
    }
}
