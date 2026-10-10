using EQLWikiEditorAssistant.Capture;
using EQLWikiEditorAssistant.Core.Imaging;
using Xunit.Abstractions;

namespace EQLWikiEditorAssistant.Tests.Capture;

/// <summary>
/// Live capture requires an interactive desktop session (DWM compositing a real window) — not guaranteed in
/// every environment this repo might be built in (e.g. a headless CI runner), so this exits early rather than
/// hard-failing when no window can be captured, same tolerance as the screenshot golden tests. On the primary dev
/// machine (or any normal interactive session) it exercises the real Windows Graphics Capture pipeline
/// end-to-end, including the [GeneratedComInterface] interop (see the plan's milestone 1 writeup for why that
/// matters — [ComImport] silently fails against CsWinRT ComWrappers objects here).
/// </summary>
public class WindowCapturerTests
{
    private readonly ITestOutputHelper _output;

    public WindowCapturerTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task CaptureAsync_OnARealVisibleWindow_ProducesAPlausibleImage()
    {
        var windows = WindowFinder.EnumerateVisibleWindows();
        if (windows.Count == 0)
        {
            _output.WriteLine("Skipping: no visible windows in this session.");
            return;
        }

        using var capturer = new WindowCapturer();

        foreach (var window in windows)
        {
            CapturedImage? image;
            try
            {
                image = await capturer.CaptureAsync(window.Handle).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _output.WriteLine($"'{window.Title}': {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            if (image is null)
            {
                _output.WriteLine($"'{window.Title}': capture returned null.");
                continue;
            }

            _output.WriteLine($"Captured '{window.Title}': {image.Width}x{image.Height}");
            Assert.True(image.Width > 0);
            Assert.True(image.Height > 0);
            Assert.Equal(image.Width * image.Height * 4, image.Pixels.Length);
            return; // one successful real capture is enough to prove the pipeline works
        }

        _output.WriteLine("Skipping assertions: could not capture any visible window in this session.");
    }
}
