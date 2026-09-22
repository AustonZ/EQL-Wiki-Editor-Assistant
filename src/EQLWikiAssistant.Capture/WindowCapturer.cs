using EQLWikiAssistant.Capture.Interop;
using EQLWikiAssistant.Core.Ocr;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace EQLWikiAssistant.Capture;

/// <summary>
/// Captures a single frame of a specific window via Windows Graphics Capture — not a screen-region grab, so it
/// works correctly even if the window is partially covered by something outside the game (the OS composites
/// the window's own content for us) and does not require the window to be focused/foreground. Does NOT satisfy
/// the "no game memory access" constraint by itself needing anything special: this reads only composited
/// pixels the OS already displays, the same as a screenshot.
/// </summary>
public sealed class WindowCapturer : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice _direct3DDevice;

    public WindowCapturer()
    {
        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            null,
            out _device!).CheckError();
        _context = _device.ImmediateContext;
        _direct3DDevice = Direct3D11Interop.CreateDirect3DDevice(_device);
    }

    /// <summary>
    /// Captures one frame of the given window. Returns null if the window handle is no longer valid or the
    /// capture could not be started (e.g. the window was closed between being found and being captured).
    /// </summary>
    public async Task<CapturedImage?> CaptureAsync(IntPtr windowHandle, CancellationToken cancellationToken = default)
    {
        if (!GraphicsCaptureSession.IsSupported())
            throw new NotSupportedException("Windows Graphics Capture is not supported on this system.");

        GraphicsCaptureItem item;
        try
        {
            item = GraphicsCaptureItemInterop.CreateItemForWindow(windowHandle);
        }
        catch (Exception)
        {
            return null; // window handle no longer valid
        }

        var frameTcs = new TaskCompletionSource<Direct3D11CaptureFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        // CreateFreeThreaded (not Create): delivers FrameArrived without needing a DispatcherQueue pumped on
        // the calling thread — the App's WPF UI thread has one, but this shouldn't require the caller to.
        using Direct3D11CaptureFramePool framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _direct3DDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            numberOfBuffers: 1,
            item.Size);

        void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            Direct3D11CaptureFrame frame = sender.TryGetNextFrame();
            if (frame is not null)
                frameTcs.TrySetResult(frame);
        }

        framePool.FrameArrived += OnFrameArrived;
        using GraphicsCaptureSession session = framePool.CreateCaptureSession(item);

        using var registration = cancellationToken.Register(() => frameTcs.TrySetCanceled());

        session.StartCapture();
        Direct3D11CaptureFrame frame;
        try
        {
            frame = await frameTcs.Task;
        }
        finally
        {
            framePool.FrameArrived -= OnFrameArrived;
            session.Dispose();
        }

        using (frame)
        {
            return CopyFrameToCapturedImage(frame);
        }
    }

    private CapturedImage CopyFrameToCapturedImage(Direct3D11CaptureFrame frame)
    {
        using ID3D11Texture2D sourceTexture = frame.Surface.GetD3D11Texture2D();
        Texture2DDescription desc = sourceTexture.Description;

        var stagingDesc = new Texture2DDescription
        {
            Width = desc.Width,
            Height = desc.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = desc.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        };

        using ID3D11Texture2D stagingTexture = _device.CreateTexture2D(stagingDesc);
        _context.CopyResource(stagingTexture, sourceTexture);

        MappedSubresource mapped = _context.Map(stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int width = (int)desc.Width;
            int height = (int)desc.Height;
            var pixels = new byte[width * height * 4];
            int rowBytes = width * 4;

            unsafe
            {
                byte* src = (byte*)mapped.DataPointer;
                for (int y = 0; y < height; y++)
                {
                    Marshal2.Copy(src + y * mapped.RowPitch, pixels, y * rowBytes, rowBytes);
                }
            }

            return new CapturedImage(width, height, pixels);
        }
        finally
        {
            _context.Unmap(stagingTexture, 0);
        }
    }

    public void Dispose()
    {
        _context.Dispose();
        _device.Dispose();
        GC.SuppressFinalize(this);
    }
}

// System.Runtime.InteropServices.Marshal.Copy doesn't take an unmanaged source pointer directly in all TFMs
// in the exact overload shape used above; this tiny wrapper keeps the call site readable.
file static class Marshal2
{
    public static unsafe void Copy(byte* source, byte[] destination, int destinationOffset, int length)
    {
        new Span<byte>(source, length).CopyTo(destination.AsSpan(destinationOffset, length));
    }
}
