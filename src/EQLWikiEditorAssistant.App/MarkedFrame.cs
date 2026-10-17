using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using EQLWikiEditorAssistant.Core.Imaging;
using Rect = EQLWikiEditorAssistant.Core.Imaging.Rect;

namespace EQLWikiEditorAssistant.App;

/// <summary>
/// A whole captured frame with one window's Description tab ringed, for a window that could not be traced (user,
/// 2026-10-12): all that is known of it is the tab, and a crop of the tab said nothing about what was wrong.
///
/// **Kept deliberately plain, because almost nobody will look at it** (user: "nobody's going to keep an eye on it to
/// make sure it never breaks"). The ring is two rectangles laid over the image at fixed frame coordinates, so it
/// cannot drift from the window whatever scale the frame is drawn at; the pane shrinks the whole thing in a Viewbox,
/// and the full-size view is the same picture in a scroller. No layout code, no measuring.
/// </summary>
internal static class MarkedFrame
{
    /// <summary>The frame at its own size, with the ring over the tab. A new element on every call, since one element
    /// can only be shown in one place.</summary>
    public static FrameworkElement Build(CapturedImage frame, Rect tab)
    {
        // Thick enough to stay visible with the frame shrunk to a pane a fraction of its width: about 3.5 screen
        // pixels with a 2560px frame in a 790px pane (rendered, 2026-10-13). The pad makes a ring big enough to find,
        // where the tab itself would shrink to a few pixels' strip.
        double stroke = Math.Max(4, frame.Width / 256.0);
        double pad = stroke * 5;

        var grid = new Grid { Width = frame.Width, Height = frame.Height, ClipToBounds = true };
        grid.Children.Add(new Image { Source = Bitmaps.From(frame), Stretch = Stretch.None });
        // A dark ring under the colored one, so it reads against a bright game world as well as a dark UI. Both follow
        // one center line with one corner radius, so the dark one is an even outline either side of the red; a radius
        // taken from each ring's own thickness rounded the dark one twice as much (user, 2026-10-13).
        double radius = stroke * 2;
        grid.Children.Add(Ring(tab, pad, stroke * 2, radius, Brushes.Black));
        grid.Children.Add(Ring(tab, pad, stroke, radius, Palette.Attention));
        return grid;
    }

    /// <summary>A ring whose stroke is centered <paramref name="pad"/> outside the tab. WPF draws a Rectangle's stroke
    /// inside its bounds and rounds that inset outline, so growing the bounds by half the stroke puts the center line
    /// in the same place for any thickness, and the same radius then gives the same corner.</summary>
    private static Rectangle Ring(Rect tab, double pad, double thickness, double radius, Brush brush)
    {
        double grow = pad + thickness / 2;
        return new Rectangle
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(tab.X - grow, tab.Y - grow, 0, 0),
            Width = tab.Width + 2 * grow,
            Height = tab.Height + 2 * grow,
            Stroke = brush,
            StrokeThickness = thickness,
            RadiusX = radius,
            RadiusY = radius,
        };
    }

    /// <summary>
    /// The frame at full size in a window of its own, scrolled to the ring. Not modal, so it can sit beside the review;
    /// Escape or the close button closes it, and it closes with the main window.
    /// </summary>
    public static void ShowFullSize(Window owner, CapturedImage frame, Rect tab)
    {
        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = Build(frame, tab),
        };
        var window = new Window
        {
            Title = "Captured screenshot",
            Owner = owner,
            Style = (Style)owner.FindResource("DarkWindow"),
            Content = scroller,
            Width = Math.Max(600, owner.ActualWidth * 0.9),
            Height = Math.Max(400, owner.ActualHeight * 0.9),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        DarkTitleBar.Apply(window);
        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) window.Close();
        };
        // Center the ring once the scroller knows its viewport. Out-of-range offsets are clamped by the scroller.
        window.Loaded += (_, _) =>
        {
            scroller.ScrollToHorizontalOffset(tab.X + tab.Width / 2.0 - scroller.ViewportWidth / 2);
            scroller.ScrollToVerticalOffset(tab.Y + tab.Height / 2.0 - scroller.ViewportHeight / 2);
        };
        window.Show();
    }
}
