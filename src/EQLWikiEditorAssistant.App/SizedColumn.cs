using System.Windows;
using System.Windows.Controls;

namespace EQLWikiEditorAssistant.App;

/// <summary>
/// A vertical stack exactly as wide as one of its children: the child marked <see cref="SetsWidthProperty"/> is
/// measured first, and every other child is fitted to its width.
///
/// **For the review screen's icon panel** (user, 2026-10-11), whose prose has to wrap at the width of the icon row
/// above it. That width varies — two tiles or three, and fewer per line once a narrow window wraps the row — so no
/// fixed <c>MaxWidth</c> can follow it, and a <c>StackPanel</c> would let a long line of text widen the whole column
/// instead. With nothing marked, or a marked child that takes no width, it behaves as a <c>StackPanel</c> does.
/// </summary>
public sealed class SizedColumn : Panel
{
    public static readonly DependencyProperty SetsWidthProperty = DependencyProperty.RegisterAttached(
        "SetsWidth", typeof(bool), typeof(SizedColumn),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static bool GetSetsWidth(UIElement element) => (bool)element.GetValue(SetsWidthProperty);

    public static void SetSetsWidth(UIElement element, bool value) => element.SetValue(SetsWidthProperty, value);

    private double _width;

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = InternalChildren.Cast<UIElement>().ToList();
        UIElement? sizer = children.FirstOrDefault(GetSetsWidth);

        sizer?.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        double width = sizer?.DesiredSize.Width ?? 0;
        bool followsSizer = width > 0;

        double height = 0;
        double widest = width;
        foreach (UIElement child in children)
        {
            if (child != sizer)
                child.Measure(new Size(followsSizer ? width : availableSize.Width, double.PositiveInfinity));
            height += child.DesiredSize.Height;
            widest = Math.Max(widest, child.DesiredSize.Width);
        }

        _width = followsSizer ? width : widest;
        return new Size(_width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Arrange(new Rect(0, y, _width, child.DesiredSize.Height));
            y += child.DesiredSize.Height;
        }

        return finalSize;
    }
}
