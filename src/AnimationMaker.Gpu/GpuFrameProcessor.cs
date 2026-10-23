using System.Numerics;
using System.Runtime.InteropServices;
using AnimationMaker.Core;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;

namespace AnimationMaker.Gpu;

public sealed class GpuFrameProcessor : IFrameProcessor
{
    private readonly object _sync = new();
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VertexShader _vertex;
    private readonly Dictionary<string, ID3D11PixelShader> _shaders = new();
    private readonly ID3D11Buffer _constants;
    private readonly ID3D11SamplerState _sampler;
    private Surface? _input, _mask, _plate, _keyed, _refined, _horizontal, _output, _background;
    private ID3D11Texture2D? _staging;
    private IDXGISwapChain1? _swapChain;
    private ID3D11RenderTargetView? _backBuffer;
    private uint _viewWidth, _viewHeight;
    private string? _plateKey, _maskKey;
    private ShaderConstants _settings;
    public string AdapterName { get; }
    public SizeI OutputSize { get; private set; }
    public IntPtr SwapChainPointer => _swapChain?.NativePointer ?? IntPtr.Zero;
    public IDXGISwapChain1? SwapChain => _swapChain;

    [StructLayout(LayoutKind.Sequential)]
    private struct ShaderConstants
    {
        public Vector4 Screen, Key, Matte, Edge, Sizes, Crop, Preview, Backdrop;
    }
    private sealed class Surface : IDisposable
    {
        public ID3D11Texture2D Texture { get; }
        public ID3D11ShaderResourceView Srv { get; }
        public ID3D11RenderTargetView? Rtv { get; }
        public int Width { get; }
        public int Height { get; }
        public Surface(ID3D11Device device, int width, int height, Format format, bool render)
        {
            Width = width; Height = height;
            Texture = device.CreateTexture2D(new Texture2DDescription(format, (uint)width, (uint)height, 1, 1, BindFlags.ShaderResource | (render ? BindFlags.RenderTarget : BindFlags.None)));
            Srv = device.CreateShaderResourceView(Texture);
            if (render) Rtv = device.CreateRenderTargetView(Texture);
        }
        public void Dispose() { Rtv?.Dispose(); Srv.Dispose(); Texture.Dispose(); }
    }
    public GpuFrameProcessor()
    {
        D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out _device, out _context).CheckError();
        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter(); AdapterName = adapter.Description.Description;
        using var stream = typeof(GpuFrameProcessor).Assembly.GetManifestResourceStream("AnimationMaker.Gpu.Shaders.Keyer.hlsl")!;
        using var reader = new StreamReader(stream); string code = reader.ReadToEnd();
        var vs = Compiler.Compile(code, "VS", "shader.hlsl", "vs_5_0", ShaderFlags.OptimizationLevel3);
        _vertex = _device.CreateVertexShader(vs.ToArray());
        foreach (string entry in new[] { "KeyPS", "RefinePS", "ScaleHorizontalPS", "EncodePS", "PreviewPS" })
        {
            var ps = Compiler.Compile(code, entry, "shader.hlsl", "ps_5_0", ShaderFlags.OptimizationLevel3);
            _shaders[entry] = _device.CreatePixelShader(ps.ToArray());
        }
        _constants = _device.CreateBuffer((uint)Marshal.SizeOf<ShaderConstants>(), BindFlags.ConstantBuffer);
        _sampler = _device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp));
    }
    private Surface Ensure(ref Surface? surface, int w, int h, bool render = true, bool floating = true)
    {
        if (surface != null && surface.Width == w && surface.Height == h) return surface;
        surface?.Dispose(); surface = new(_device, w, h, floating ? Format.R16G16B16A16_Float : Format.R8G8B8A8_UNorm, render); return surface;
    }
    private void Upload(Surface target, byte[] rgba) => _context.UpdateSubresource(rgba, target.Texture, 0, (uint)(target.Width * 4));
    private void Draw(string entry, ID3D11RenderTargetView target, int w, int h, params ID3D11ShaderResourceView[] sources)
    {
        _context.PSSetShaderResources(0, new ID3D11ShaderResourceView[] { null!, null!, null!, null! });
        _context.OMSetRenderTargets(target);
        _context.RSSetViewport(0, 0, w, h);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_vertex); _context.PSSetShader(_shaders[entry]);
        _context.UpdateSubresource(in _settings, _constants);
        _context.PSSetConstantBuffer(0, _constants); _context.PSSetSampler(0, _sampler);
        _context.PSSetShaderResources(0, sources); _context.Draw(3, 0);
        _context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        _context.PSSetShaderResources(0, new ID3D11ShaderResourceView[] { null!, null!, null!, null! });
    }
    public RgbaFrame Process(RgbaFrame source, KeyerSettings keyer, IReadOnlyList<PolygonMask> masks, double time, RectI crop, SizeI output)
    {
        lock (_sync) { Render(source, keyer, masks, time, crop, output); return Readback(); }
    }
    public void Render(RgbaFrame source, KeyerSettings keyer, IReadOnlyList<PolygonMask> masks, double time, RectI crop, SizeI output)
    {
        lock (_sync)
        {
            source.Validate(); FrameGeometry.ValidateCrop(crop, source.Width, source.Height);
            if (output.Width < 1 || output.Height < 1 || output.Width > 16384 || output.Height > 16384 || source.Width > 16384 || source.Height > 16384) throw new ArgumentOutOfRangeException(nameof(output), "Direct3D texture dimensions must be between 1 and 16384 pixels.");
            var input = Ensure(ref _input, source.Width, source.Height, false, false); Upload(input, source.Pixels);
            bool hasMasks = masks.Any(m => m.Enabled && m.Keyframes.Count > 0);
            var mask = Ensure(ref _mask, hasMasks ? source.Width : 1, hasMasks ? source.Height : 1, false, false);
            string maskKey = hasMasks ? System.Text.Json.JsonSerializer.Serialize(masks, ProjectStore.JsonOptions) + "|" + (masks.Any(m => m.Enabled && m.Keyframes.Count > 1) ? time.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "static") + $"|{mask.Width}x{mask.Height}" : "empty";
            if (_maskKey != maskKey) { Upload(mask, hasMasks ? Imaging.RasterizeMasks(source.Width, source.Height, masks, time) : new byte[4]); _maskKey = maskKey; }
            var plate = Ensure(ref _plate, keyer.CleanPlatePath is { Length: > 0 } ? source.Width : 1, keyer.CleanPlatePath is { Length: > 0 } ? source.Height : 1, false, false);
            string plateKey = (keyer.CleanPlatePath ?? "") + $"|{plate.Width}x{plate.Height}";
            if (_plateKey != plateKey)
            {
                if (keyer.CleanPlatePath is { Length: > 0 } path) { var image = Imaging.Load(path); if (image.Width != plate.Width || image.Height != plate.Height) image = Imaging.Resize(image, plate.Width, plate.Height); Upload(plate, image.Pixels); }
                else Upload(plate, new byte[4]);
                _plateKey = plateKey;
            }
            var screen = ColorMath.ParseHex(keyer.ScreenColor);
            _settings = new()
            {
                Screen = new(screen.R, screen.G, screen.B, 1),
                Key = new(keyer.Mode == KeyerMode.Fast ? 1 : 0, keyer.Similarity, keyer.Softness, keyer.Despill),
                Matte = new(keyer.ClipBlack, keyer.ClipWhite, keyer.RedWeight, keyer.BlueWeight),
                Edge = new(keyer.Choke, keyer.Feather, keyer.Enabled ? 1 : 0, keyer.CleanPlatePath is { Length: > 0 } ? 1 : 0),
                Sizes = new(source.Width, source.Height, output.Width, output.Height), Crop = new(crop.X, crop.Y, crop.Width, crop.Height), Preview = _settings.Preview
            };
            var keyed = Ensure(ref _keyed, source.Width, source.Height);
            Draw("KeyPS", keyed.Rtv!, keyed.Width, keyed.Height, input.Srv, mask.Srv, plate.Srv);
            var refined = Ensure(ref _refined, source.Width, source.Height);
            Draw("RefinePS", refined.Rtv!, refined.Width, refined.Height, keyed.Srv);
            var horizontal = Ensure(ref _horizontal, output.Width, crop.Height);
            Draw("ScaleHorizontalPS", horizontal.Rtv!, horizontal.Width, horizontal.Height, refined.Srv);
            bool resized = _output == null || _output.Width != output.Width || _output.Height != output.Height;
            var final = Ensure(ref _output, output.Width, output.Height, true, false);
            if (resized) { _staging?.Dispose(); _staging = null; }
            Draw("EncodePS", final.Rtv!, final.Width, final.Height, horizontal.Srv); OutputSize = output;
        }
    }
    public RgbaFrame Readback()
    {
        lock (_sync)
        {
            if (_output == null) throw new InvalidOperationException("No frame has been rendered.");
            _staging ??= _device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, (uint)_output.Width, (uint)_output.Height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
            _context.CopyResource(_staging, _output.Texture);
            return ReadPixels(_staging, _output.Width, _output.Height);
        }
    }
    private RgbaFrame ReadPixels(ID3D11Texture2D staging, int width, int height)
    {
        var mapped = _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var pixels = new byte[checked(width * height * 4)];
            for (int y = 0; y < height; y++) Marshal.Copy(mapped.DataPointer + (int)(y * mapped.RowPitch), pixels, y * width * 4, width * 4);
            return new(width, height, pixels);
        }
        finally { _context.Unmap(staging, 0); }
    }
    public void SetPreviewBackgroundImage(RgbaFrame? image)
    {
        lock (_sync)
        {
            if (image == null) { _background?.Dispose(); _background = null; return; }
            image.Validate();
            if (image.Width > 16384 || image.Height > 16384) throw new ArgumentOutOfRangeException(nameof(image), "Background image exceeds Direct3D texture limits.");
            // Replace the previous image only after the new texture was uploaded successfully.
            var replacement = new Surface(_device, image.Width, image.Height, Format.R8G8B8A8_UNorm, false);
            try { Upload(replacement, image.Pixels); }
            catch { replacement.Dispose(); throw; }
            _background?.Dispose(); _background = replacement;
        }
    }
    private void DrawPreview(ID3D11RenderTargetView target, int width, int height, PreviewBackground background, float zoom, BackgroundImageFit imageFit)
    {
        _settings.Preview = new((float)background, Math.Clamp(zoom, .00001f, 65536), width, height);
        _settings.Backdrop = new(_background?.Width ?? 1, _background?.Height ?? 1, (float)imageFit, _background != null ? 1 : 0);
        Draw("PreviewPS", target, width, height, _output!.Srv, null!, null!, _background?.Srv!);
    }
    // Read the same GPU composition used by the window without changing the transparent export surface.
    public RgbaFrame ReadPreview(SizeI viewport, PreviewBackground background, float zoom = 1, BackgroundImageFit imageFit = BackgroundImageFit.Fill)
    {
        lock (_sync)
        {
            if (_output == null) throw new InvalidOperationException("No frame has been rendered.");
            if (viewport.Width is < 1 or > 16384 || viewport.Height is < 1 or > 16384) throw new ArgumentOutOfRangeException(nameof(viewport));
            using var target = new Surface(_device, viewport.Width, viewport.Height, Format.R8G8B8A8_UNorm, true);
            DrawPreview(target.Rtv!, viewport.Width, viewport.Height, background, zoom, imageFit);
            using var staging = _device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, (uint)viewport.Width, (uint)viewport.Height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
            _context.CopyResource(staging, target.Texture);
            return ReadPixels(staging, viewport.Width, viewport.Height);
        }
    }
    public IntPtr CreateSwapChain(int width, int height)
    {
        lock (_sync)
        {
            _backBuffer?.Dispose(); _swapChain?.Dispose();
            using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
            _viewWidth = (uint)Math.Max(1, width); _viewHeight = (uint)Math.Max(1, height);
            _swapChain = factory.CreateSwapChainForComposition(_device, new SwapChainDescription1 { Width = _viewWidth, Height = _viewHeight, Format = Format.B8G8R8A8_UNorm, BufferCount = 2, BufferUsage = Usage.RenderTargetOutput, SampleDescription = new SampleDescription(1, 0), SwapEffect = SwapEffect.FlipSequential, Scaling = Scaling.Stretch, AlphaMode = AlphaMode.Ignore });
            using var buffer = _swapChain.GetBuffer<ID3D11Texture2D>(0); _backBuffer = _device.CreateRenderTargetView(buffer);
            return _swapChain.NativePointer;
        }
    }
    public void Present(int width, int height, PreviewBackground background, float zoom = 1, BackgroundImageFit imageFit = BackgroundImageFit.Fill)
    {
        lock (_sync)
        {
            if (_swapChain == null || _backBuffer == null || width <= 0 || height <= 0) return;
            if (_viewWidth != width || _viewHeight != height)
            {
                _context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>()); _backBuffer.Dispose();
                _swapChain.ResizeBuffers(2, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
                using var buffer = _swapChain.GetBuffer<ID3D11Texture2D>(0); _backBuffer = _device.CreateRenderTargetView(buffer);
                _viewWidth = (uint)width; _viewHeight = (uint)height;
            }
            if (_output != null)
            {
                DrawPreview(_backBuffer, width, height, background, zoom, imageFit);
            }
            else _context.ClearRenderTargetView(_backBuffer, new Vortice.Mathematics.Color4(.047f, .055f, .071f, 1));
            _swapChain.Present(1, PresentFlags.None).CheckError();
        }
    }
    public void Dispose()
    {
        lock (_sync)
        {
            _context.ClearState(); _context.Flush(); _backBuffer?.Dispose(); _swapChain?.Dispose(); _staging?.Dispose();
            foreach (var surface in new[] { _input, _mask, _plate, _keyed, _refined, _horizontal, _output, _background }) surface?.Dispose();
            foreach (var shader in _shaders.Values) shader.Dispose();
            _vertex.Dispose(); _sampler.Dispose(); _constants.Dispose(); _context.Dispose(); _device.Dispose();
        }
    }
}
