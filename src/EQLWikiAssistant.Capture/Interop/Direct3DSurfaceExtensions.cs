using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Vortice.Direct3D11;
using Windows.Graphics.DirectX.Direct3D11;

namespace EQLWikiAssistant.Capture.Interop;

/// <summary>See the note on IGraphicsCaptureItemInterop — must be [GeneratedComInterface], not [ComImport].</summary>
[GeneratedComInterface]
[Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
internal partial interface IDirect3DDxgiInterfaceAccess
{
    IntPtr GetInterface(in Guid iid);
}

/// <summary>Bridges a captured WinRT IDirect3DSurface (from a Direct3D11CaptureFrame) back to the underlying
/// Vortice ID3D11Texture2D, so it can be copied/mapped with ordinary Direct3D11 calls.</summary>
internal static class Direct3DSurfaceExtensions
{
    private static readonly Guid Texture2DGuid = typeof(ID3D11Texture2D).GUID;

    public static ID3D11Texture2D GetD3D11Texture2D(this IDirect3DSurface surface)
    {
        // A direct C# cast doesn't reliably reach a [GeneratedComInterface] interface on a CsWinRT
        // ComWrappers-sourced object (same underlying reason as the IGraphicsCaptureItemInterop note) —
        // WinRT.CastExtensions.As<T>() is the confirmed-working way to QueryInterface it.
        var access = WinRT.CastExtensions.As<IDirect3DDxgiInterfaceAccess>(surface);
        IntPtr texturePointer = access.GetInterface(Texture2DGuid);
        return new ID3D11Texture2D(texturePointer);
    }
}
