using System.Buffers;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ScreenTranslator.Capture;

internal sealed class WindowsGraphicsCaptureSession : IDisposable
{
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private ID3D11Texture2D? _staging;
    private SizeInt32 _size;
    private volatile bool _closed;
    private readonly string _monitorId;

    public WindowsGraphicsCaptureSession(MonitorInfo monitor)
    {
        _monitorId = monitor.Id;
        try
        {
            if (!GraphicsCaptureSession.IsSupported()) throw new NotSupportedException("当前系统/会话不支持 Windows Graphics Capture。");
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint ai = 0; _device is null && factory.EnumAdapters1(ai, out var adapter).Success; ai++)
            {
                using (adapter)
                {
                    for (uint oi = 0; adapter.EnumOutputs(oi, out var output).Success; oi++)
                    {
                        using (output)
                        {
                            if (output.Description.Monitor != monitor.Handle) continue;
                            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                                new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 }, out _device, out _context).CheckError();
                            break;
                        }
                    }
                }
            }
            if (_device is null) throw new InvalidOperationException("所选显示器的显卡不可用，请刷新显示器列表。");
            _winrtDevice = WinRtCaptureInterop.CreateDevice(_device);
            _item = WinRtCaptureInterop.CreateItem(monitor.Handle);
            _item.Closed += ItemClosed;
            _size = _item.Size;
            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
            _session = _framePool.CreateCaptureSession(_item);
            _session.IsCursorCaptureEnabled = true;
            _session.StartCapture();
        }
        catch { Dispose(); throw; }
    }
    private void ItemClosed(GraphicsCaptureItem sender, object args) => _closed = true;

    public CapturedFrame? TryCapture(ArrayPool<byte> pool)
    {
        if (_closed) throw new InvalidOperationException("捕获显示器已关闭或断开。");
        var gpuFrame = _framePool!.TryGetNextFrame();
        if (gpuFrame is null) return null;
        CapturedFrame? frame = null;
        var contentSize = _size;
        try
        {
            // Keep every acquired frame inside the disposal scope, including access-loss errors.
            var newer = _framePool.TryGetNextFrame();
            if (newer is not null) { gpuFrame.Dispose(); gpuFrame = newer; }
            contentSize = gpuFrame.ContentSize;
            if (contentSize.Width <= 0 || contentSize.Height <= 0) return null;
            if (contentSize.Width != _size.Width || contentSize.Height != _size.Height) return null;
            using var surface = gpuFrame.Surface;
            using var texture = WinRtCaptureInterop.GetTexture(surface);
            var desc = texture.Description;
            if (_staging is null || _staging.Description.Width != desc.Width || _staging.Description.Height != desc.Height)
            {
                _staging?.Dispose(); _staging = null;
                desc.Usage = ResourceUsage.Staging; desc.BindFlags = BindFlags.None;
                desc.CPUAccessFlags = CpuAccessFlags.Read; desc.MiscFlags = ResourceOptionFlags.None;
                _staging = _device!.CreateTexture2D(desc);
            }
            _context!.CopyResource(_staging, texture);
            _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped).CheckError();
            try
            {
                frame = new CapturedFrame(contentSize.Width, contentSize.Height, _monitorId, pool);
                PixelCopy.CopyBgraRows(mapped.DataPointer, checked((int)mapped.RowPitch), frame);
            }
            finally { _context.Unmap(_staging, 0); }
            return frame;
        }
        catch { frame?.Dispose(); throw; }
        finally
        {
            gpuFrame.Dispose();
            if (contentSize.Width > 0 && contentSize.Height > 0 && (contentSize.Width != _size.Width || contentSize.Height != _size.Height))
            {
                _size = contentSize;
                _framePool.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
            }
        }
    }

    public void Dispose()
    {
        if (_item is not null) { _item.Closed -= ItemClosed; _item = null; }
        _session?.Dispose(); _session = null;
        _framePool?.Dispose(); _framePool = null;
        _staging?.Dispose(); _staging = null;
        _winrtDevice?.Dispose(); _winrtDevice = null;
        _context?.Dispose(); _context = null;
        _device?.Dispose(); _device = null;
    }
}
