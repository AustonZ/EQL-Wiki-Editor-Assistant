using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Windows.Graphics.DirectX.Direct3D11;

namespace EQLWikiAssistant.Capture.Interop;

/// <summary>
/// Bridges a raw Direct3D11 device (created via Vortice) to the WinRT
/// Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice that Windows.Graphics.Capture's frame pool needs.
/// There is no managed API for this — it's one native call (documented, exported from d3d11.dll) plus a
/// WinRT ABI marshal.
/// </summary>
internal static class Direct3D11Interop
{
    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", PreserveSig = false)]
    private static extern IntPtr CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice);

    public static IDirect3DDevice CreateDirect3DDevice(ID3D11Device d3dDevice)
    {
        using var dxgiDevice = d3dDevice.QueryInterface<Vortice.DXGI.IDXGIDevice>();
        IntPtr inspectable = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer);
        // FromAbi takes ownership of the reference; do not also Marshal.Release it (that would over-release).
        return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
    }
}
