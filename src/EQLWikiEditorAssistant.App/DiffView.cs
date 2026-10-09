using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using EQLWikiEditorAssistant.Pipeline;

namespace EQLWikiEditorAssistant.App;

/// <summary>
/// A line diff the user can select and copy from (user, 2026-10-08) — one read-only document rather than a text block
/// per line, so a selection can run across lines.
///
/// **Copying takes the wikitext and nothing else.** The line numbers and the +/- marker sit in a fixed-width gutter
/// that is not part of the text, and a copy is rebuilt from each line's own text, so what lands on the clipboard can be
/// pasted straight back into a page. The gutter also gives a wrapped line a hanging indent, as the old two-column
/// layout did.
/// </summary>
public sealed class DiffView : RichTextBox
{
    public static readonly DependencyProperty LinesProperty = DependencyProperty.Register(
        nameof(Lines), typeof(IEnumerable), typeof(DiffView), new PropertyMetadata(null, OnLinesChanged));

    /// <summary>The diff, as <see cref="DiffLineViewModel"/>s. An observable collection is followed as it changes.</summary>
    public IEnumerable? Lines
    {
        get => (IEnumerable?)GetValue(LinesProperty);
        set => SetValue(LinesProperty, value);
    }

    /// <summary>Each line's own text, by paragraph, which is all a copy takes.</summary>
    private readonly Dictionary<Paragraph, Run> _content = [];

    public DiffView()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsDocumentEnabled = false;
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        FontFamily = new FontFamily("Consolas");
        FontSize = 12;
        Foreground = Palette.Text;
        SelectionBrush = (Brush)Application.Current.FindResource("TextSelectionBrush");
        // Translucent for the reason the TextBox style gives: an opaque selection hides the text under it.
        SelectionOpacity = 0.35;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Document = new FlowDocument { PagePadding = new Thickness(0) };

        DataObject.AddCopyingHandler(this, OnCopying);
        // Its own scroller would swallow the wheel and stop the page scrolling under the cursor.
        PreviewMouseWheel += OnPassWheelToPage;
    }

    private static void OnLinesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (DiffView)d;
        if (e.OldValue is INotifyCollectionChanged oldList) oldList.CollectionChanged -= view.OnCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged newList) newList.CollectionChanged += view.OnCollectionChanged;
        view.Rebuild();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        _content.Clear();
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = FontFamily,
            FontSize = FontSize,
        };

        List<DiffLineViewModel> lines = Lines?.OfType<DiffLineViewModel>().ToList() ?? [];
        double gutter = GutterWidth(lines);
        foreach (DiffLineViewModel line in lines)
        {
            var paragraph = new Paragraph
            {
                Margin = new Thickness(0),
                Padding = new Thickness(gutter, 0, 4, 0),
                TextIndent = -gutter,
                Background = line.Background,
            };

            paragraph.Inlines.Add(new InlineUIContainer(new TextBlock
            {
                Text = Gutter(line),
                Width = gutter,
                FontSize = 11,
                Foreground = line.Line.Kind == DiffLineKind.Unchanged || line.Line.Kind == DiffLineKind.Gap
                    ? Palette.Dim
                    : line.Foreground,
            }) { BaselineAlignment = BaselineAlignment.Baseline });

            var text = new Run(line.Line.Text) { Foreground = line.Foreground };
            paragraph.Inlines.Add(text);
            // A folded run of unchanged lines is the diff talking, not wikitext, so a copy leaves it out.
            if (line.Line.Kind != DiffLineKind.Gap) _content[paragraph] = text;
            document.Blocks.Add(paragraph);
        }

        Document = document;
    }

    /// <summary>Both line numbers and the marker, as the old layout showed them.</summary>
    private static string Gutter(DiffLineViewModel line) => line.LineNumbers + (line.Line.Kind switch
    {
        DiffLineKind.Removed => "  - ",
        DiffLineKind.Added => "  + ",
        _ => "    ",
    });

    private double GutterWidth(IReadOnlyList<DiffLineViewModel> lines)
    {
        string widest = lines.Select(Gutter).OrderByDescending(s => s.Length).FirstOrDefault() ?? "";
        var measured = new FormattedText(
            widest, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 11,
            Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return Math.Ceiling(measured.WidthIncludingTrailingWhitespace) + 4;
    }

    /// <summary>Replaces the copy with the selected part of each line's own text, one line per line.</summary>
    private void OnCopying(object sender, DataObjectCopyingEventArgs e)
    {
        var copied = new StringBuilder();
        bool first = true;
        foreach (Block block in Document.Blocks)
        {
            if (block is not Paragraph paragraph || !_content.TryGetValue(paragraph, out Run? run)) continue;

            if (Selection.End.CompareTo(run.ContentStart) < 0 || Selection.Start.CompareTo(run.ContentEnd) > 0) continue;
            TextPointer start = Later(Selection.Start, run.ContentStart);
            TextPointer end = Earlier(Selection.End, run.ContentEnd);

            if (!first) copied.Append(Environment.NewLine);
            copied.Append(new TextRange(start, end).Text);
            first = false;
        }

        e.CancelCommand();
        if (copied.Length > 0) Clipboard.SetText(copied.ToString());
    }

    private static TextPointer Later(TextPointer a, TextPointer b) => a.CompareTo(b) >= 0 ? a : b;
    private static TextPointer Earlier(TextPointer a, TextPointer b) => a.CompareTo(b) <= 0 ? a : b;

    private void OnPassWheelToPage(object sender, MouseWheelEventArgs e)
    {
        DependencyObject? parent = VisualTreeHelper.GetParent(this);
        while (parent is not null and not ScrollViewer) parent = VisualTreeHelper.GetParent(parent);
        if (parent is not ScrollViewer outer) return;

        e.Handled = true;
        outer.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = Mouse.MouseWheelEvent,
            Source = this,
        });
    }
}
