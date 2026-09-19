using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DwgTranslator.App.Views.Controls;

/// <summary>Window-local DIP layout state; never scales text or changes business state.</summary>
public static class ResponsiveLayout
{
    private const int WheelLineThreshold = 40;

    public static readonly DependencyProperty IsShortProperty = DependencyProperty.RegisterAttached(
        "IsShort", typeof(bool), typeof(ResponsiveLayout), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public static bool GetIsShort(DependencyObject element) => (bool)element.GetValue(IsShortProperty);
    public static void SetIsShort(DependencyObject element, bool value) => element.SetValue(IsShortProperty, value);

    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.RegisterAttached(
        "IsCompact", typeof(bool), typeof(ResponsiveLayout), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public static bool GetIsCompact(DependencyObject element) => (bool)element.GetValue(IsCompactProperty);
    public static void SetIsCompact(DependencyObject element, bool value) => element.SetValue(IsCompactProperty, value);

    /// <summary>
    /// Gives precision touchpads deterministic line scrolling and hands wheel input from an inner
    /// ScrollViewer (including a DataGrid's template viewer) to the nearest scrollable parent only
    /// after the inner surface reaches its boundary.
    /// </summary>
    public static readonly DependencyProperty HandoffMouseWheelAtBoundaryProperty =
        DependencyProperty.RegisterAttached(
            "HandoffMouseWheelAtBoundary",
            typeof(bool),
            typeof(ResponsiveLayout),
            new FrameworkPropertyMetadata(false, OnHandoffMouseWheelChanged));

    private static readonly DependencyProperty MouseWheelRemainderProperty =
        DependencyProperty.RegisterAttached(
            "MouseWheelRemainder",
            typeof(int),
            typeof(ResponsiveLayout),
            new PropertyMetadata(0));

    public static bool GetHandoffMouseWheelAtBoundary(DependencyObject element) =>
        (bool)element.GetValue(HandoffMouseWheelAtBoundaryProperty);

    public static void SetHandoffMouseWheelAtBoundary(DependencyObject element, bool value) =>
        element.SetValue(HandoffMouseWheelAtBoundaryProperty, value);

    private static void OnHandoffMouseWheelChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not UIElement element) return;
        if ((bool)e.NewValue)
            element.PreviewMouseWheel += HandoffMouseWheel;
        else
        {
            element.PreviewMouseWheel -= HandoffMouseWheel;
            element.ClearValue(MouseWheelRemainderProperty);
        }
    }

    private static void HandoffMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0 || sender is not UIElement host) return;

        var current = ResolveCurrentScrollViewer(host, e.OriginalSource as DependencyObject);
        if (current is null) return;

        var target = CanScroll(current, e.Delta)
            ? current
            : FindScrollableParent(current, e.Delta);
        if (target is null) return;

        e.Handled = true;
        ScrollByPrecisionDelta(target, e.Delta);
    }

    private static void ScrollByPrecisionDelta(ScrollViewer viewer, int delta)
    {
        var remainder = (int)viewer.GetValue(MouseWheelRemainderProperty);
        if (remainder != 0 && Math.Sign(remainder) != Math.Sign(delta)) remainder = 0;

        var accumulated = remainder + delta;
        var lines = accumulated / WheelLineThreshold;
        viewer.SetValue(MouseWheelRemainderProperty, accumulated % WheelLineThreshold);
        if (lines == 0) return;

        var count = Math.Abs(lines);
        if (lines > 0)
        {
            for (var i = 0; i < count && viewer.VerticalOffset > 0.5; i++) viewer.LineUp();
        }
        else
        {
            for (var i = 0; i < count && viewer.VerticalOffset < viewer.ScrollableHeight - 0.5; i++) viewer.LineDown();
        }
    }

    private static bool CanScroll(ScrollViewer viewer, int delta)
    {
        if (viewer.ScrollableHeight <= 0.5) return false;
        return delta > 0
            ? viewer.VerticalOffset > 0.5
            : viewer.VerticalOffset < viewer.ScrollableHeight - 0.5;
    }

    private static ScrollViewer? ResolveCurrentScrollViewer(UIElement host, DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is ScrollViewer viewer) return viewer;
            if (ReferenceEquals(current, host)) break;
            current = GetParent(current);
        }

        if (host is ScrollViewer hostViewer) return hostViewer;
        return FindDescendantScrollViewer(host);
    }

    private static ScrollViewer? FindScrollableParent(DependencyObject child, int delta)
    {
        var current = GetParent(child);
        while (current is not null)
        {
            if (current is ScrollViewer viewer && CanScroll(viewer, delta)) return viewer;
            current = GetParent(current);
        }
        return null;
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer viewer) return viewer;
            var nested = FindDescendantScrollViewer(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static DependencyObject? GetParent(DependencyObject child)
    {
        if (child is Visual or System.Windows.Media.Media3D.Visual3D)
            return VisualTreeHelper.GetParent(child);
        if (child is FrameworkContentElement content) return content.Parent;
        return LogicalTreeHelper.GetParent(child);
    }
}

// AdaptiveCardGrid used to live here: a Grid that decided its own column count from its content
// width and, in doing so, overwrote each child's Margin and Grid.Column. Its last two call sites
// are gone - AccountPage and (in the about section) SettingsPage now declare explicit Grids, because
// the 600/900 width breakpoints did not match the layouts they were dropped into. SettingsPage asked
// for a 2x2 block of shortcut cards and got 3 columns above 900 DIP, stranding the fourth card on its
// own row. An explicit grid is both simpler and correct there. Removed rather than kept as dead code.

/// <summary>
/// A bounded toolbar for legacy dialogs. It only becomes scrollable when the host
/// is genuinely short; normal desktop windows keep the toolbar fully visible.
/// </summary>
public sealed class AdaptiveToolbar : ScrollViewer
{
    public AdaptiveToolbar()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        CanContentScroll = true;
        Focusable = false;
        SetValue(ResponsiveLayout.HandoffMouseWheelAtBoundaryProperty, true);
        SizeChanged += (_, _) => UpdateLimit();
        Loaded += (_, _) => UpdateLimit();
    }

    private void UpdateLimit()
    {
        var height = Window.GetWindow(this)?.ActualHeight ?? 800;
        var limit = height < 540 ? 104d : height < 700 ? 170d : 240d;
        if (Math.Abs(MaxHeight - limit) > 0.1) MaxHeight = limit;
    }
}
