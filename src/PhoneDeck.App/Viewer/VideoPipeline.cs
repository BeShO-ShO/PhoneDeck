using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using PhoneDeck.Core;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.MediaFoundation;

namespace PhoneDeck.App.Viewer;

/// <summary>
/// Reads the capture device and draws each frame as soon as it arrives, on a thread of its own.
/// <list type="number">
/// <item>Media Foundation's source reader in low-latency mode reads the device. MJPG is decoded
/// by Windows into NV12 or YUY2.</item>
/// <item>Each frame is copied into plain textures on the graphics card, one per plane.</item>
/// <item>A shader of PhoneDeck's own turns YUV into RGB at the card's size, with the exact
/// matrix and range (<see cref="YuvToRgb"/>), as mpv does. Graphics drivers' own video
/// processors don't always honour the range they are given.</item>
/// <item>A second shader scales that into the window's swap chain (Catmull-Rom), letterboxed.</item>
/// <item>It is presented without waiting for the display, and with one frame queued at most, the
/// same as the low-delay options PhoneDeck gives mpv.</item>
/// </list>
/// If the device is missing, busy or unplugged, it tries again every two seconds.
/// </summary>
internal sealed unsafe class VideoPipeline : IDisposable
{
    // MF_LOW_LATENCY.
    private static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private const int RetryMs = 2000;
    private static readonly TimeSpan StatsEvery = TimeSpan.FromSeconds(30);

    private const string Shaders = """
        cbuffer Convert : register(b0)
        {
            float4 RowR;
            float4 RowG;
            float4 RowB;
            float4 Size;   // width, height, 1/width, 1/height of the picture
        };

        Texture2D<float> Luma : register(t0);
        Texture2D<float2> Chroma : register(t1);
        Texture2D<float4> Packed : register(t2);
        Texture2D<float4> Picture : register(t3);
        SamplerState Linear : register(s0);

        struct VsOut
        {
            float4 Position : SV_Position;
            float2 Uv : TEXCOORD0;
        };

        // One triangle that covers the viewport.
        VsOut FullScreen(uint id : SV_VertexID)
        {
            VsOut output;
            float2 uv = float2((id << 1) & 2, id & 2);
            output.Uv = uv;
            output.Position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
            return output;
        }

        float4 ToRgb(float y, float u, float v)
        {
            float4 yuv = float4(y, u, v, 1);
            return float4(saturate(float3(dot(RowR, yuv), dot(RowG, yuv), dot(RowB, yuv))), 1);
        }

        float4 ConvertNv12(VsOut input) : SV_Target
        {
            int2 pixel = int2(input.Position.xy);
            float y = Luma.Load(int3(pixel, 0));
            float2 uv = Chroma.SampleLevel(Linear, (float2(pixel) + 0.5) * Size.zw, 0);
            return ToRgb(y, uv.x, uv.y);
        }

        // YUY2 read as RGBA at half width: Y0 U Y1 V.
        float4 ConvertYuy2(VsOut input) : SV_Target
        {
            uint2 pixel = uint2(input.Position.xy);
            float4 pair = Packed.Load(int3(pixel.x >> 1, pixel.y, 0));
            float y = (pixel.x & 1) ? pair.b : pair.r;
            return ToRgb(y, pair.g, pair.a);
        }

        // Catmull-Rom from nine bilinear samples (after Matt Pettineo's well-known version).
        float4 Present(VsOut input) : SV_Target
        {
            float2 position = input.Uv * Size.xy;
            float2 texel1 = floor(position - 0.5) + 0.5;
            float2 f = position - texel1;
            float2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
            float2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
            float2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
            float2 w3 = f * f * (-0.5 + 0.5 * f);
            float2 w12 = w1 + w2;
            float2 t0 = (texel1 - 1) * Size.zw;
            float2 t12 = (texel1 + w2 / w12) * Size.zw;
            float2 t3 = (texel1 + 2) * Size.zw;

            float3 colour = 0;
            colour += Picture.SampleLevel(Linear, float2(t0.x, t0.y), 0).rgb * w0.x * w0.y;
            colour += Picture.SampleLevel(Linear, float2(t12.x, t0.y), 0).rgb * w12.x * w0.y;
            colour += Picture.SampleLevel(Linear, float2(t3.x, t0.y), 0).rgb * w3.x * w0.y;
            colour += Picture.SampleLevel(Linear, float2(t0.x, t12.y), 0).rgb * w0.x * w12.y;
            colour += Picture.SampleLevel(Linear, float2(t12.x, t12.y), 0).rgb * w12.x * w12.y;
            colour += Picture.SampleLevel(Linear, float2(t3.x, t12.y), 0).rgb * w3.x * w12.y;
            colour += Picture.SampleLevel(Linear, float2(t0.x, t3.y), 0).rgb * w0.x * w3.y;
            colour += Picture.SampleLevel(Linear, float2(t12.x, t3.y), 0).rgb * w12.x * w3.y;
            colour += Picture.SampleLevel(Linear, float2(t3.x, t3.y), 0).rgb * w3.x * w3.y;
            return float4(saturate(colour), 1);
        }
        """;

    [StructLayout(LayoutKind.Sequential)]
    private struct ConvertConstants
    {
        public Vector4 RowR, RowG, RowB, Size;
    }

    /// <summary>One plane of a frame: where it sits in the reader's buffer and its textures.</summary>
    private sealed class Plane : IDisposable
    {
        public required int Rows;
        public required int RowBytes;
        public required ID3D11Texture2D Texture;
        public required ID3D11ShaderResourceView View;
        public required ID3D11Texture2D[] Staging;

        public void Dispose()
        {
            View.Dispose();
            Texture.Dispose();
            foreach (var staging in Staging) staging.Dispose();
        }
    }

    private readonly string _deviceName;
    private readonly IntPtr _window;
    private readonly Func<(int Width, int Height)> _clientSize;
    private readonly Action<int, int> _videoSize;
    private readonly Action<string> _log;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stopping = new();
    private readonly object _sourceGate = new();
    private IMFMediaSource? _source;
    private volatile bool _stop;
    private string _lastProblem = "";

    // Graphics, used only on the pipeline thread.
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIFactory2? _factory;
    private IDXGISwapChain1? _swapChain;
    private int _swapWidth, _swapHeight;
    private ID3D11VertexShader? _fullScreen;
    private ID3D11PixelShader? _convertNv12, _convertYuy2, _present;
    private ID3D11SamplerState? _linear;
    private ID3D11Buffer? _constants;

    // The current frame size and layout, and what was made for it.
    private bool _planar;
    private int _width, _height;
    private CaptureColor _color;
    private readonly List<Plane> _planes = [];
    private int _nextStaging;
    private ID3D11Texture2D? _rgb;
    private ID3D11RenderTargetView? _rgbTarget;
    private ID3D11ShaderResourceView? _rgbView;

    // What the reader says about colour, kept so a chosen range can be applied over it.
    private CapturePixels _sourcePixels;
    private uint? _typeMatrix, _typeRange;

    // Picture settings, set from the UI thread and applied before the next frame is drawn.
    private readonly object _pictureGate = new();
    private PictureRange _range;
    private PictureLevels _levels;
    private int _pictureVersion, _appliedVersion = -1;

    public VideoPipeline(string deviceName, IntPtr window, Func<(int Width, int Height)> clientSize,
        Action<int, int> videoSize, Action<string> log)
    {
        _deviceName = deviceName;
        _window = window;
        _clientSize = clientSize;
        _videoSize = videoSize;
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = "PhoneDeck viewer video" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    private void Run()
    {
        var started = MediaFactory.MFStartup(false).Success;
        try
        {
            while (!_stop)
            {
                try
                {
                    RunSession();
                }
                catch (Exception ex) when (!_stop)
                {
                    Problem($"video stopped: {ex.Message}");
                    // A lost graphics device is made again on the next try.
                    if (ex is SharpGenException) ReleaseGraphics();
                }

                if (_stop || _stopping.Wait(RetryMs)) break;
            }
        }
        finally
        {
            ReleaseGraphics();
            if (started) MediaFactory.MFShutdown();
        }
    }

    /// <summary>Logs a problem once, not every two seconds while it lasts.</summary>
    private void Problem(string message)
    {
        if (message == _lastProblem) return;
        _lastProblem = message;
        _log($"viewer: {message}");
    }

    private void RunSession()
    {
        var source = OpenDevice();
        if (source is null)
        {
            Problem($"capture device \"{_deviceName}\" not found");
            return;
        }

        lock (_sourceGate)
        {
            if (_stop)
            {
                source.Shutdown();
                source.Dispose();
                return;
            }
            _source = source;
        }

        try
        {
            EnsureGraphics();

            using var attributes = MediaFactory.MFCreateAttributes(2);
            attributes.Set(LowLatency, true);
            // Lets the reader hand MJPG over as NV12 or YUY2, whichever the decoder gives.
            attributes.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, true);
            using var reader = MediaFactory.MFCreateSourceReaderFromMediaSource(source, attributes);
            reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);

            var chosen = ChooseMode(reader);
            var output = SetOutput(reader, chosen);
            _lastProblem = "";
            _log($"viewer: {chosen} from \"{_deviceName}\", shown as {output}, {Describe(_color)}");

            ReadFrames(reader, chosen);
        }
        finally
        {
            lock (_sourceGate) _source = null;
            try { source.Shutdown(); } catch (SharpGenException) { }
            source.Dispose();
        }
    }

    private static string Describe(CaptureColor color) =>
        $"{(color.Bt709 ? "BT.709" : "BT.601")} {(color.FullRange ? "full" : "limited")} range";

    private IMFMediaSource? OpenDevice()
    {
        using var attributes = MediaFactory.MFCreateAttributes(1);
        attributes.Set(CaptureDeviceAttributeKeys.SourceType, CaptureDeviceAttributeKeys.SourceTypeVidcap);
        using var devices = MediaFactory.MFEnumDeviceSources(attributes);

        IMFActivate? exact = null, partial = null;
        foreach (var device in devices)
        {
            string name;
            try { name = device.GetString(CaptureDeviceAttributeKeys.FriendlyName); }
            catch (SharpGenException) { continue; }

            if (exact is null && string.Equals(name, _deviceName, StringComparison.OrdinalIgnoreCase)) exact = device;
            else if (partial is null && name.Contains(_deviceName, StringComparison.OrdinalIgnoreCase)) partial = device;
        }

        var match = exact ?? partial;
        return match?.ActivateObject<IMFMediaSource>();
    }

    private static CapturePixels PixelsOf(Guid subtype) =>
        subtype == VideoFormatGuids.NV12 ? CapturePixels.Nv12
        : subtype == VideoFormatGuids.YUY2 ? CapturePixels.Yuy2
        : subtype == VideoFormatGuids.Mjpg ? CapturePixels.Mjpg
        : CapturePixels.Other;

    private static CaptureFormat Describe(IMFMediaType type)
    {
        var pixels = PixelsOf(type.GetGUID(MediaTypeAttributeKeys.Subtype));
        var size = TryGetUInt64(type, MediaTypeAttributeKeys.FrameSize);
        var rate = TryGetUInt64(type, MediaTypeAttributeKeys.FrameRate);
        return new CaptureFormat(pixels, (int)(size >> 32), (int)(size & 0xFFFFFFFF),
            (uint)(rate >> 32), (uint)(rate & 0xFFFFFFFF));
    }

    private static ulong TryGetUInt64(IMFAttributes attributes, Guid key) =>
        attributes.GetUInt64(key, out var value).Success ? value : 0;

    private static uint? TryGetUInt32(IMFAttributes attributes, Guid key) =>
        attributes.GetUInt32(key, out var value).Success ? value : null;

    /// <summary>Sets the device to the best mode it offers (see <see cref="CaptureFormatChooser"/>).</summary>
    private static CaptureFormat ChooseMode(IMFSourceReader reader)
    {
        var types = new List<IMFMediaType>();
        try
        {
            for (var i = 0; ; i++)
            {
                try { types.Add(reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, i)); }
                catch (SharpGenException) { break; }
            }

            var formats = types.Select(Describe).ToList();
            if (CaptureFormatChooser.Choose(formats) is not { } best)
                throw new InvalidOperationException("the capture device offers no NV12, YUY2 or MJPG mode");

            reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, types[best]);
            return formats[best];
        }
        finally
        {
            foreach (var type in types) type.Dispose();
        }
    }

    /// <summary>
    /// Raw modes are shown as they come. MJPG is decoded by Windows into NV12, or YUY2 if the
    /// decoder only gives that. Returns what it settled on.
    /// </summary>
    private CapturePixels SetOutput(IMFSourceReader reader, CaptureFormat mode)
    {
        if (mode.IsUncompressed) return UseCurrentType(reader, mode.Pixels);

        foreach (var subtype in new[] { VideoFormatGuids.NV12, VideoFormatGuids.YUY2 })
        {
            using var type = MediaFactory.MFCreateMediaType();
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            type.Set(MediaTypeAttributeKeys.Subtype, subtype);
            MediaFactory.MFSetAttributeSize(type, MediaTypeAttributeKeys.FrameSize, (uint)mode.Width, (uint)mode.Height);
            MediaFactory.MFSetAttributeRatio(type, MediaTypeAttributeKeys.FrameRate, mode.RateNumerator, mode.RateDenominator);
            try
            {
                reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, type);
                return UseCurrentType(reader, mode.Pixels);
            }
            catch (SharpGenException) { }
        }

        throw new InvalidOperationException($"Windows could not decode {mode.Pixels} into NV12 or YUY2");
    }

    /// <summary>Reads back what the reader delivers and gets the textures ready for it.</summary>
    private CapturePixels UseCurrentType(IMFSourceReader reader, CapturePixels source)
    {
        using var type = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
        var format = Describe(type);
        if (format.Pixels is not (CapturePixels.Nv12 or CapturePixels.Yuy2))
            throw new InvalidOperationException($"the reader delivers {format.Pixels}");

        _sourcePixels = source;
        _typeMatrix = TryGetUInt32(type, MediaTypeAttributeKeys.YuvMatrix);
        _typeRange = TryGetUInt32(type, MediaTypeAttributeKeys.VideoNominalRange);
        PrepareTextures(format.Pixels == CapturePixels.Nv12, format.Width, format.Height);
        ApplyPicture(log: false);
        _videoSize(format.Width, format.Height);
        return format.Pixels;
    }

    private void ReadFrames(IMFSourceReader reader, CaptureFormat mode)
    {
        var stats = Stopwatch.StartNew();
        var frames = 0;
        var busy = TimeSpan.Zero;

        while (!_stop)
        {
            using var sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream, 0,
                out _, out var flags, out _);
            if (_stop) return;
            if ((flags & SourceReaderFlag.Error) != 0) throw new InvalidOperationException("the capture device stopped");
            if ((flags & SourceReaderFlag.EndOfStream) != 0) throw new InvalidOperationException("the capture device ended");
            if ((flags & SourceReaderFlag.CurrentMediaTypeChanged) != 0)
            {
                var shown = UseCurrentType(reader, mode.Pixels);
                _log($"viewer: the picture changed to {_width}x{_height} {shown}");
            }
            if (sample is null) continue;

            var start = Stopwatch.GetTimestamp();
            Upload(sample);
            Draw();
            busy += Stopwatch.GetElapsedTime(start);
            frames++;

            if (stats.Elapsed >= StatsEvery)
            {
                _log($"viewer: {frames / stats.Elapsed.TotalSeconds:0.0} fps, " +
                     $"{busy.TotalMilliseconds / Math.Max(1, frames):0.0} ms to copy and draw each frame");
                frames = 0;
                busy = TimeSpan.Zero;
                stats.Restart();
            }
        }
    }

    private void EnsureGraphics()
    {
        if (_device is not null) return;

        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
        D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels,
            out _device, out _context).CheckError();
        var device = _device!;

        using var dxgiDevice = device.QueryInterface<IDXGIDevice1>();
        // At most one frame waiting to be shown: older frames are never worth showing late.
        dxgiDevice.SetMaximumFrameLatency(1);
        using var adapter = dxgiDevice.GetAdapter();
        _factory = adapter.GetParent<IDXGIFactory2>();

        _fullScreen = device.CreateVertexShader(Compile("FullScreen", "vs_4_0"));
        _convertNv12 = device.CreatePixelShader(Compile("ConvertNv12", "ps_4_0"));
        _convertYuy2 = device.CreatePixelShader(Compile("ConvertYuy2", "ps_4_0"));
        _present = device.CreatePixelShader(Compile("Present", "ps_4_0"));
        _linear = device.CreateSamplerState(SamplerDescription.LinearClamp);
        _constants = device.CreateBuffer((uint)sizeof(ConvertConstants), BindFlags.ConstantBuffer,
            ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
    }

    private static byte[] Compile(string entryPoint, string profile) =>
        Compiler.Compile(Shaders, entryPoint, "PhoneDeckViewer", profile, ShaderFlags.OptimizationLevel3, EffectFlags.None).ToArray();

    private void PrepareTextures(bool planar, int width, int height)
    {
        if (planar == _planar && width == _width && height == _height && _rgb is not null) return;
        ReleaseFrameResources();

        _planar = planar;
        _width = width;
        _height = height;

        if (planar)
        {
            _planes.Add(MakePlane(Format.R8_UNorm, width, height, width));
            _planes.Add(MakePlane(Format.R8G8_UNorm, width / 2, (height + 1) / 2, width));
        }
        else
        {
            // Each RGBA texel holds two pixels: Y0 U Y1 V.
            _planes.Add(MakePlane(Format.R8G8B8A8_UNorm, width / 2, height, width * 2));
        }

        _rgb = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        _rgbTarget = _device.CreateRenderTargetView(_rgb, null);
        _rgbView = _device.CreateShaderResourceView(_rgb, null);
    }

    private Plane MakePlane(Format format, int width, int height, int rowBytes)
    {
        var description = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
        };
        var texture = _device!.CreateTexture2D(description);
        var view = _device.CreateShaderResourceView(texture, null);

        // Frames are written into staging textures the CPU can reach, then copied on the card.
        // Two of them, so writing one never waits for the copy of the other.
        description.Usage = ResourceUsage.Staging;
        description.BindFlags = BindFlags.None;
        description.CPUAccessFlags = CpuAccessFlags.Write;
        var staging = new[] { _device.CreateTexture2D(description), _device.CreateTexture2D(description) };

        return new Plane { Rows = height, RowBytes = rowBytes, Texture = texture, View = view, Staging = staging };
    }

    /// <summary>Copies a frame from the reader into the plane textures the shader reads.</summary>
    private void Upload(IMFSample sample)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        var slot = _nextStaging;
        _nextStaging = (_nextStaging + 1) % 2;

        var rowBytes = _planar ? _width : _width * 2;
        var chromaRows = _planar ? (_height + 1) / 2 : 0;

        using var twoD = buffer.QueryInterfaceOrNull<IMF2DBuffer>();
        IntPtr source;
        int pitch;
        int length;
        if (twoD is not null)
        {
            twoD.Lock2D(out source, out pitch);
            length = buffer.CurrentLength;
        }
        else
        {
            buffer.Lock(out source, out _, out length);
            pitch = rowBytes;
        }

        try
        {
            // A bottom-up or short buffer is not a frame this can show.
            if (pitch < rowBytes || length < pitch * (_height + chromaRows)) return;

            // Decoders may pad the height (1088 for 1080), which moves the chroma plane down.
            var paddedRows = _planar ? Math.Max(_height, length * 2 / 3 / pitch) : _height;
            if (_planar && pitch * (paddedRows + chromaRows) > length) paddedRows = _height;

            var from = (byte*)source;
            CopyPlane(_planes[0], slot, from, pitch);
            if (_planar) CopyPlane(_planes[1], slot, from + (long)paddedRows * pitch, pitch);
        }
        finally
        {
            if (twoD is not null) twoD.Unlock2D();
            else buffer.Unlock();
        }
    }

    private void CopyPlane(Plane plane, int slot, byte* from, int pitch)
    {
        var staging = plane.Staging[slot];
        var mapped = _context!.Map(staging, 0, MapMode.Write, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var to = (byte*)mapped.DataPointer;
            var toPitch = (long)mapped.RowPitch;
            for (var row = 0; row < plane.Rows; row++)
                Buffer.MemoryCopy(from + (long)row * pitch, to + row * toPitch, plane.RowBytes, plane.RowBytes);
        }
        finally
        {
            _context.Unmap(staging, 0);
        }
        _context.CopyResource(plane.Texture, staging);
    }

    /// <summary>Converts the frame to RGB, then scales it into the window and presents it.</summary>
    private void Draw()
    {
        var (width, height) = _clientSize();
        if (width <= 0 || height <= 0) return;

        EnsureSwapChain(width, height);
        if (Volatile.Read(ref _pictureVersion) != _appliedVersion) ApplyPicture(log: true);

        var context = _context!;
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(_fullScreen);
        context.PSSetConstantBuffer(0, _constants);
        context.PSSetSampler(0, _linear);

        // 1. YUV to RGB at the picture's own size, pixel for pixel.
        context.OMSetRenderTargets(_rgbTarget!, null);
        context.RSSetViewport(0, 0, _width, _height, 0, 1);
        context.PSSetShader(_planar ? _convertNv12 : _convertYuy2);
        if (_planar)
        {
            context.PSSetShaderResource(0, _planes[0].View);
            context.PSSetShaderResource(1, _planes[1].View);
        }
        else
        {
            context.PSSetShaderResource(2, _planes[0].View);
        }
        context.Draw(3, 0);

        // 2. Scaled into the window, letterboxed on black.
        using var backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        using var target = _device!.CreateRenderTargetView(backBuffer, null);
        context.OMSetRenderTargets(target, null);
        context.ClearRenderTargetView(target, new Color4(0, 0, 0, 1));
        var picture = VideoRect.Letterbox(0, 0, width, height, (double)_width / _height);
        context.RSSetViewport((float)picture.Left, (float)picture.Top, (float)picture.Width, (float)picture.Height, 0, 1);
        context.PSSetShader(_present);
        context.PSSetShaderResource(3, _rgbView!);
        context.Draw(3, 0);

        // The RGB texture is drawn into again next frame, so it must not stay bound for reading.
        context.PSSetShaderResource(3, null!);

        // Sync interval 0: show the newest frame at the next refresh without waiting for it here.
        _swapChain.Present(0, PresentFlags.None).CheckError();
    }

    private void EnsureSwapChain(int width, int height)
    {
        if (_swapChain is null)
        {
            var description = new SwapChainDescription1
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = Format.B8G8R8A8_UNorm,
                BufferCount = 2,
                BufferUsage = Usage.RenderTargetOutput,
                SwapEffect = SwapEffect.FlipDiscard,
                Scaling = Scaling.Stretch,
                AlphaMode = AlphaMode.Ignore,
                SampleDescription = new SampleDescription(1, 0),
            };
            _swapChain = _factory!.CreateSwapChainForHwnd(_device!, _window, description, null, null);
            _factory.MakeWindowAssociation(_window, WindowAssociationFlags.IgnoreAltEnter);
            _swapWidth = width;
            _swapHeight = height;
            return;
        }

        if (width == _swapWidth && height == _swapHeight) return;
        _swapChain.ResizeBuffers(2, (uint)width, (uint)height, Format.Unknown, SwapChainFlags.None).CheckError();
        _swapWidth = width;
        _swapHeight = height;
    }

    /// <summary>
    /// Range, brightness, contrast and colour from Settings. They are folded into the shader's
    /// conversion numbers, so they cost nothing per frame and add no delay.
    /// </summary>
    public void SetPicture(PictureRange range, PictureLevels levels)
    {
        lock (_pictureGate)
        {
            _range = range;
            _levels = levels;
            _pictureVersion++;
        }
    }

    private void ApplyPicture(bool log)
    {
        if (_constants is null || _context is null) return;

        PictureRange range;
        PictureLevels levels;
        lock (_pictureGate)
        {
            range = _range;
            levels = _levels;
            _appliedVersion = _pictureVersion;
        }

        var color = CaptureColor.Choose(_sourcePixels, _height, _typeMatrix, _typeRange, range);
        if (log && color != _color) _log($"viewer: colour now {Describe(color)}");
        _color = color;

        var rows = YuvToRgb.Rows(color, levels);
        var constants = new ConvertConstants
        {
            RowR = new Vector4(rows[0], rows[1], rows[2], rows[3]),
            RowG = new Vector4(rows[4], rows[5], rows[6], rows[7]),
            RowB = new Vector4(rows[8], rows[9], rows[10], rows[11]),
            Size = _width > 0 && _height > 0
                ? new Vector4(_width, _height, 1f / _width, 1f / _height)
                : Vector4.One,
        };
        _context.UpdateSubresource(in constants, _constants, 0, 0, 0, null);
    }

    private void ReleaseFrameResources()
    {
        foreach (var plane in _planes) plane.Dispose();
        _planes.Clear();
        _rgbView?.Dispose();
        _rgbTarget?.Dispose();
        _rgb?.Dispose();
        _rgbView = null;
        _rgbTarget = null;
        _rgb = null;
    }

    private void ReleaseGraphics()
    {
        ReleaseFrameResources();
        _width = _height = 0;
        _constants?.Dispose();
        _linear?.Dispose();
        _present?.Dispose();
        _convertYuy2?.Dispose();
        _convertNv12?.Dispose();
        _fullScreen?.Dispose();
        _swapChain?.Dispose();
        _factory?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _constants = null;
        _linear = null;
        _present = null;
        _convertYuy2 = null;
        _convertNv12 = null;
        _fullScreen = null;
        _swapChain = null;
        _factory = null;
        _context = null;
        _device = null;
    }

    public void Dispose()
    {
        if (_stop) return;
        _stop = true;
        _stopping.Set();
        // Shutting the device down makes a waiting ReadSample return at once.
        lock (_sourceGate)
        {
            try { _source?.Shutdown(); }
            catch (SharpGenException) { }
        }
        _thread.Join(TimeSpan.FromSeconds(3));
    }
}
