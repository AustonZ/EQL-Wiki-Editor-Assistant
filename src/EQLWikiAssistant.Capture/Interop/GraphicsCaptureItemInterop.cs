using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Windows.Graphics.Capture;

namespace EQLWikiAssistant.Capture.Interop;

/// <summary>
/// GraphicsCaptureItem has no public "create from window handle" factory in the plain WinRT projection — it's
/// reached through this documented COM interop interface on the class's activation factory. This is the
/// standard pattern for Windows Graphics Capture from Win32/desktop apps (see Microsoft's
/// "Screen capture for a HWND" sample, learn.microsoft.com/windows/uwp/audio-video-camera/screen-capture).
///
/// Must use [GeneratedComInterface] (source-generated, .NET 8+ "built-in COM"), not the classic [ComImport] —
/// confirmed by testing against the live game window that [ComImport] throws InvalidCastException ("Specified
/// cast is not valid") when calling through an interface obtained from a CsWinRT ComWrappers-based object
/// (which .As&lt;T&gt;() on a WinRT-projected type returns); GeneratedComInterface is compatible with that.
/// </summary>
[ComVisible(true)]
[GeneratedComInterface]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
internal partial interface IGraphicsCaptureItemInterop
{
    IntPtr CreateForWindow(IntPtr window, in Guid iid);
    IntPtr CreateForMonitor(IntPtr monitor, in Guid iid);
}

internal static class GraphicsCaptureItemInterop
{
    // The IID of Windows.Graphics.Capture.IGraphicsCaptureItem (not GraphicsCaptureItem's own, unrelated
    // reflection GUID — typeof(GraphicsCaptureItem).GUID is the wrong value and makes CreateForWindow's
    // internal QueryInterface fail with E_NOINTERFACE / InvalidCastException).
    private static readonly Guid GraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    public static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        IntPtr abi = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>().CreateForWindow(hwnd, GraphicsCaptureItemGuid);
        return GraphicsCaptureItem.FromAbi(abi);
    }
}
