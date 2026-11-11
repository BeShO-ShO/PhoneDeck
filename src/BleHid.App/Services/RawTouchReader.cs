using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace BleHid.App.Services;

/// <summary>
/// Reads the touchscreen's own HID reports through Raw Input. Unlike touch events this needs no
/// window under the finger, so nothing has to cover the video: a full-screen see-through window
/// was measurably slowing the pointer down.
/// </summary>
public sealed class RawTouchReader : IDisposable
{
    private const int WM_INPUT = 0x00FF;
    private const uint RIDEV_REMOVE = 0x00000001;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDI_PREPARSEDDATA = 0x20000005;
    private const uint RIM_TYPEHID = 2;
    private const ushort DigitizerPage = 0x0D;
    private const ushort TouchScreenUsage = 0x04;
    private const ushort TipSwitchUsage = 0x42;
    private const ushort ContactIdUsage = 0x51;
    private const ushort GenericDesktopPage = 0x01;
    private const ushort XUsage = 0x30;
    private const ushort YUsage = 0x31;
    private const int HidpInput = 0;
    private const int HidpSuccess = 0x00110000;
    private const int ValueCapsSize = 72;

    private sealed record Axis(int Minimum, int Maximum);
    private sealed record Finger(ushort Link, Axis X, Axis Y);
    private sealed record Device(IntPtr Prepared, List<Finger> Fingers);

    private readonly Action<int, bool, double, double> _contact;
    private readonly Action<string> _log;
    private readonly Dictionary<IntPtr, Device?> _devices = [];
    private readonly HwndSource _window;
    private int _loggedReports;

    /// <param name="contact">Contact id, whether it is touching, and X and Y as 0-1 screen fractions.</param>
    public RawTouchReader(Action<int, bool, double, double> contact, Action<string> log)
    {
        _contact = contact;
        _log = log;

        // A message-only window: it receives WM_INPUT but is never shown or composited.
        _window = new HwndSource(new HwndSourceParameters("BleHidRawTouch")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            WindowStyle = 0
        });
        _window.AddHook(WndProc);

        var registered = Register(RIDEV_INPUTSINK, _window.Handle);
        _log(registered
            ? "raw touch registered"
            : $"raw touch registration failed (err {Marshal.GetLastWin32Error()})");
    }

    private static bool Register(uint flags, IntPtr target) => RegisterRawInputDevices(
        [new RAWINPUTDEVICE { UsagePage = DigitizerPage, Usage = TouchScreenUsage, Flags = flags, Target = target }],
        1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_INPUT) return IntPtr.Zero;

        try { ReadInput(lParam); }
        catch (Exception ex) { _log($"raw touch error: {ex.Message}"); }
        return IntPtr.Zero;
    }

    private void ReadInput(IntPtr handle)
    {
        var headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = 0;
        GetRawInputData(handle, RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0) return;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(handle, RID_INPUT, buffer, ref size, headerSize) != size) return;

            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
            if (header.Type != RIM_TYPEHID) return;

            var device = GetDevice(header.Device);
            if (device is null) return;

            // RAWHID follows the header: report size, report count, then the reports back to back.
            var offset = (int)headerSize;
            var reportSize = Marshal.ReadInt32(buffer, offset);
            var reportCount = Marshal.ReadInt32(buffer, offset + 4);

            for (var i = 0; i < reportCount; i++)
            {
                var report = new byte[reportSize];
                Marshal.Copy(buffer + offset + 8 + i * reportSize, report, 0, reportSize);
                ReadReport(device, report);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void ReadReport(Device device, byte[] report)
    {
        var length = (uint)report.Length;

        foreach (var finger in device.Fingers)
        {
            if (HidP_GetUsageValue(HidpInput, GenericDesktopPage, finger.Link, XUsage, out var x,
                    device.Prepared, report, length) != HidpSuccess) continue;
            if (HidP_GetUsageValue(HidpInput, GenericDesktopPage, finger.Link, YUsage, out var y,
                    device.Prepared, report, length) != HidpSuccess) continue;

            var usages = new ushort[16];
            var usageCount = (uint)usages.Length;
            if (HidP_GetUsages(HidpInput, DigitizerPage, finger.Link, usages, ref usageCount,
                    device.Prepared, report, length) != HidpSuccess) continue;
            var touching = usages.Take((int)usageCount).Contains(TipSwitchUsage);

            var id = HidP_GetUsageValue(HidpInput, DigitizerPage, finger.Link, ContactIdUsage, out var contactId,
                device.Prepared, report, length) == HidpSuccess ? (int)contactId : finger.Link;

            var fractionX = Fraction(x, finger.X);
            var fractionY = Fraction(y, finger.Y);
            if (_loggedReports++ < 6) _log($"raw contact {id} touching={touching} at {fractionX:F3},{fractionY:F3}");
            _contact(id, touching, fractionX, fractionY);
        }
    }

    private static double Fraction(uint value, Axis axis) =>
        Math.Clamp(((double)value - axis.Minimum) / (axis.Maximum - axis.Minimum), 0, 1);

    private Device? GetDevice(IntPtr handle)
    {
        if (_devices.TryGetValue(handle, out var known)) return known;

        var device = Describe(handle);
        _devices[handle] = device;
        _log(device is null
            ? "raw touch device could not be read"
            : $"raw touch device has {device.Fingers.Count} contact slot(s)");
        return device;
    }

    private static Device? Describe(IntPtr handle)
    {
        uint size = 0;
        GetRawInputDeviceInfo(handle, RIDI_PREPARSEDDATA, IntPtr.Zero, ref size);
        if (size == 0) return null;

        var prepared = Marshal.AllocHGlobal((int)size);
        if (GetRawInputDeviceInfo(handle, RIDI_PREPARSEDDATA, prepared, ref size) == uint.MaxValue ||
            HidP_GetCaps(prepared, out var caps) != HidpSuccess)
        {
            Marshal.FreeHGlobal(prepared);
            return null;
        }

        var count = caps.NumberInputValueCaps;
        var raw = new byte[count * ValueCapsSize];
        if (HidP_GetValueCaps(HidpInput, raw, ref count, prepared) != HidpSuccess)
        {
            Marshal.FreeHGlobal(prepared);
            return null;
        }

        var xAxes = new Dictionary<ushort, Axis>();
        var yAxes = new Dictionary<ushort, Axis>();
        for (var i = 0; i < count; i++)
        {
            var entry = raw.AsSpan(i * ValueCapsSize, ValueCapsSize);
            if (BinaryPrimitives.ReadUInt16LittleEndian(entry) != GenericDesktopPage) continue;

            var link = BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]);
            var axis = new Axis(BinaryPrimitives.ReadInt32LittleEndian(entry[40..]),
                BinaryPrimitives.ReadInt32LittleEndian(entry[44..]));
            if (axis.Maximum <= axis.Minimum) continue;

            var usage = BinaryPrimitives.ReadUInt16LittleEndian(entry[56..]);
            if (usage == XUsage) xAxes[link] = axis;
            else if (usage == YUsage) yAxes[link] = axis;
        }

        var fingers = xAxes.Where(pair => yAxes.ContainsKey(pair.Key))
            .Select(pair => new Finger(pair.Key, pair.Value, yAxes[pair.Key]))
            .ToList();
        return new Device(prepared, fingers);
    }

    public void Dispose()
    {
        Register(RIDEV_REMOVE, IntPtr.Zero);
        _window.RemoveHook(WndProc);
        _window.Dispose();
        foreach (var device in _devices.Values)
            if (device is not null) Marshal.FreeHGlobal(device.Prepared);
        _devices.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort UsagePage, Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint Type, Size;
        public IntPtr Device, WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
        public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW")]
    private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data, ref uint size);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr prepared, out HIDP_CAPS caps);

    [DllImport("hid.dll")]
    private static extern int HidP_GetValueCaps(int reportType, [Out] byte[] caps, ref ushort length, IntPtr prepared);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort link, ushort usage,
        out uint value, IntPtr prepared, byte[] report, uint reportLength);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort link, [In, Out] ushort[] usages,
        ref uint usageLength, IntPtr prepared, byte[] report, uint reportLength);
}
