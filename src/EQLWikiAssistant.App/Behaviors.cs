using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EQLWikiAssistant.App;

/// <summary>
/// Small attached behaviours the windows share, so a table or a block of text gets them by one attribute rather than
/// a handler per window.
/// </summary>
public static class Behaviors
{
    // ============================ selectable text ============================

    /// <summary>
    /// The first click into a read-only text box selects all of it; later clicks place the caret and drag-select as
    /// usual (user, 2026-10-07, for the proposed-changes table). Copying a whole value is the common case, and one
    /// click is all it should take — but picking out part of an effect line has to stay possible.
    /// </summary>
    public static readonly DependencyProperty SelectAllOnFirstClickProperty = DependencyProperty.RegisterAttached(
        "SelectAllOnFirstClick", typeof(bool), typeof(Behaviors),
        new PropertyMetadata(false, OnSelectAllOnFirstClickChanged));

    public static bool GetSelectAllOnFirstClick(DependencyObject element) =>
        (bool)element.GetValue(SelectAllOnFirstClickProperty);

    public static void SetSelectAllOnFirstClick(DependencyObject element, bool value) =>
        element.SetValue(SelectAllOnFirstClickProperty, value);

    private static void OnSelectAllOnFirstClickChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;
        box.PreviewMouseLeftButtonDown -= SelectAllOnFirstClick;
        if ((bool)e.NewValue) box.PreviewMouseLeftButtonDown += SelectAllOnFirstClick;
    }

    private static void SelectAllOnFirstClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox box || box.IsKeyboardFocusWithin) return;
        box.Focus();
        box.SelectAll();
        // Otherwise the click goes on to place the caret, which clears the selection just made.
        e.Handled = true;
    }

    // ============================ tables that do not select ============================

    /// <summary>
    /// A table whose rows are never selected (user, 2026-10-07): selecting a whole row did nothing useful, and in the
    /// proposed-changes table it competed with selecting the text inside a cell. A DataGrid has no way to switch
    /// selection off, so anything that gets selected is let go straight away.
    /// </summary>
    public static readonly DependencyProperty NoRowSelectionProperty = DependencyProperty.RegisterAttached(
        "NoRowSelection", typeof(bool), typeof(Behaviors), new PropertyMetadata(false, OnNoRowSelectionChanged));

    public static bool GetNoRowSelection(DependencyObject element) => (bool)element.GetValue(NoRowSelectionProperty);

    public static void SetNoRowSelection(DependencyObject element, bool value) =>
        element.SetValue(NoRowSelectionProperty, value);

    private static void OnNoRowSelectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid) return;
        grid.SelectionChanged -= Unselect;
        if ((bool)e.NewValue) grid.SelectionChanged += Unselect;
    }

    private static void Unselect(object sender, SelectionChangedEventArgs e)
    {
        // A cell's own selection changes also bubble up as this event; only the grid's own matters.
        if (sender is not DataGrid grid || e.OriginalSource != grid || grid.SelectedItems.Count == 0) return;
        // Once the grid has finished with the click: undone during its own handling, a second click on a row
        // selected it again regardless (user, 2026-10-08).
        grid.Dispatcher.BeginInvoke(() =>
        {
            grid.UnselectAll();
            grid.UnselectAllCells();
        });
    }
}
