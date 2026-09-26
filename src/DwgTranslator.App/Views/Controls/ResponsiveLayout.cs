using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

    // ── 宽度分档行为（§自适应 2026-09-25 用户批注「用鼠标把界面横向一直缩小，界面没有任何的
    //    自适应调节」）────────────────────────────────────────────────────────────────────
    // 为什么用附加属性 + SizeChanged，而不是 DataTrigger / VisualStateManager：WPF 没有基于
    // "容器实际宽度"的内置触发器。文件末尾记录的 AdaptiveCardGrid 就是前车之鉴——它把 600/900
    // 写死在自己内部、与落点不符，最后被删掉。所以断点必须由 XAML 按落点声明
    // （{DynamicResource Size.Breakpoint*}），行为只负责"档位翻转时切换"。
    // 三条共同约束：
    //   ① 判定一律用元素**自身** Math.Round(ActualWidth)，与外壳的 IsCompact 解耦，页面之间互不影响；
    //   ② 档位没变就直接返回，因此不会自激，也不需要定时器或轮询；
    //   ③ 只处理自己这一层的子元素，不递归可视化树。

    /// <summary>
    /// 附加在 Grid 上：自身可用宽低于该值时，把标记了 <see cref="StackTargetProperty"/> 的子元素
    /// 折到其余内容下方并跨满整行（右侧栏/槽列让位）；回升到阈值以上时逐字还原原来的行、列、跨列与边距。
    /// 这是 AccountPage 三处手写折行的通用化：页面侧只需声明阈值 + 标记"谁要折"。
    /// </summary>
    public static readonly DependencyProperty StackBelowProperty = DependencyProperty.RegisterAttached(
        "StackBelow", typeof(double), typeof(ResponsiveLayout), new PropertyMetadata(double.NaN, OnStackBelowChanged));

    public static double GetStackBelow(DependencyObject element) => (double)element.GetValue(StackBelowProperty);
    public static void SetStackBelow(DependencyObject element, double value) => element.SetValue(StackBelowProperty, value);

    /// <summary>附加在 StackBelow 宿主 Grid 的直接子元素上：窄档时该元素折到下方。</summary>
    public static readonly DependencyProperty StackTargetProperty = DependencyProperty.RegisterAttached(
        "StackTarget", typeof(bool), typeof(ResponsiveLayout), new PropertyMetadata(false));

    public static bool GetStackTarget(DependencyObject element) => (bool)element.GetValue(StackTargetProperty);
    public static void SetStackTarget(DependencyObject element, bool value) => element.SetValue(StackTargetProperty, value);

    /// <summary>折行后补的上间距：沿用元素原有上边距，没有就用这个值（与 Spacing.Stack 同值）。</summary>
    private const double StackGap = 16;

    private sealed class GridSlot
    {
        public int Row { get; init; }
        public int Column { get; init; }
        public int ColumnSpan { get; init; }
        public int RowSpan { get; init; }
        public Thickness Margin { get; init; }
    }

    private static readonly DependencyProperty StackSlotProperty = DependencyProperty.RegisterAttached(
        "StackSlot", typeof(GridSlot), typeof(ResponsiveLayout), new PropertyMetadata(null));
    private static readonly DependencyProperty StackStateProperty = DependencyProperty.RegisterAttached(
        "StackState", typeof(bool?), typeof(ResponsiveLayout), new PropertyMetadata(null));

    private static void OnStackBelowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Grid grid) return;
        grid.SizeChanged -= OnStackHostSizeChanged;
        grid.Loaded -= OnStackHostLoaded;
        if (!double.IsNaN((double)e.NewValue))
        {
            grid.SizeChanged += OnStackHostSizeChanged;
            grid.Loaded += OnStackHostLoaded;
        }
        ApplyStack(grid);
    }

    private static void OnStackHostLoaded(object sender, RoutedEventArgs e) => ApplyStack((Grid)sender);
    private static void OnStackHostSizeChanged(object sender, SizeChangedEventArgs e) => ApplyStack((Grid)sender);

    private static void ApplyStack(Grid grid)
    {
        var threshold = GetStackBelow(grid);
        if (double.IsNaN(threshold) || threshold <= 0) return;
        var width = Math.Round(grid.ActualWidth);
        if (width <= 0) return;
        var stacked = width < threshold;
        if (grid.GetValue(StackStateProperty) is bool previous && previous == stacked) return;
        grid.SetValue(StackStateProperty, stacked);

        var targets = grid.Children.OfType<FrameworkElement>().Where(GetStackTarget).ToList();
        if (targets.Count == 0) return;
        var columns = Math.Max(1, grid.ColumnDefinitions.Count);
        // 折行后从"未标记子元素占用的最后一行"往下排，避免与原有内容重叠。
        var row = grid.Children.OfType<FrameworkElement>()
            .Where(child => !GetStackTarget(child))
            .Select(Grid.GetRow)
            .DefaultIfEmpty(0)
            .Max() + 1;
        foreach (var target in targets)
        {
            if (stacked)
            {
                if (target.GetValue(StackSlotProperty) is not GridSlot)
                    target.SetValue(StackSlotProperty, new GridSlot
                    {
                        Row = Grid.GetRow(target),
                        Column = Grid.GetColumn(target),
                        ColumnSpan = Grid.GetColumnSpan(target),
                        RowSpan = Grid.GetRowSpan(target),
                        Margin = target.Margin
                    });
                var slot = (GridSlot)target.GetValue(StackSlotProperty)!;
                Grid.SetRow(target, row);
                Grid.SetColumn(target, 0);
                Grid.SetColumnSpan(target, columns);
                Grid.SetRowSpan(target, 1);
                var gap = slot.Margin.Top > 0 ? slot.Margin.Top : StackGap;
                target.Margin = new Thickness(slot.Margin.Left, gap, slot.Margin.Right, slot.Margin.Bottom);
                row++;
            }
            else if (target.GetValue(StackSlotProperty) is GridSlot slot)
            {
                Grid.SetRow(target, slot.Row);
                Grid.SetColumn(target, slot.Column);
                Grid.SetColumnSpan(target, slot.ColumnSpan);
                Grid.SetRowSpan(target, slot.RowSpan);
                target.Margin = slot.Margin;
            }
        }
    }

    /// <summary>
    /// 附加在 UniformGrid 上：自身可用宽低于该值时改用 <see cref="UniformColumnsProperty"/> 指定的列数
    /// （行数交回自动），回升后逐字还原 XAML 里声明的 Columns/Rows。用于 7 项指标带折成两行。
    /// </summary>
    public static readonly DependencyProperty UniformColumnsBelowProperty = DependencyProperty.RegisterAttached(
        "UniformColumnsBelow", typeof(double), typeof(ResponsiveLayout), new PropertyMetadata(double.NaN, OnUniformColumnsBelowChanged));

    public static double GetUniformColumnsBelow(DependencyObject element) => (double)element.GetValue(UniformColumnsBelowProperty);
    public static void SetUniformColumnsBelow(DependencyObject element, double value) => element.SetValue(UniformColumnsBelowProperty, value);

    /// <summary>窄档列数；宽档沿用 XAML 里声明的 Columns。</summary>
    public static readonly DependencyProperty UniformColumnsProperty = DependencyProperty.RegisterAttached(
        "UniformColumns", typeof(int), typeof(ResponsiveLayout), new PropertyMetadata(1));

    public static int GetUniformColumns(DependencyObject element) => (int)element.GetValue(UniformColumnsProperty);
    public static void SetUniformColumns(DependencyObject element, int value) => element.SetValue(UniformColumnsProperty, value);

    private sealed class UniformSlot
    {
        public int Columns { get; init; }
        public int Rows { get; init; }
    }

    private static readonly DependencyProperty UniformSlotProperty = DependencyProperty.RegisterAttached(
        "UniformSlot", typeof(UniformSlot), typeof(ResponsiveLayout), new PropertyMetadata(null));
    private static readonly DependencyProperty UniformStateProperty = DependencyProperty.RegisterAttached(
        "UniformState", typeof(bool?), typeof(ResponsiveLayout), new PropertyMetadata(null));

    private static void OnUniformColumnsBelowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UniformGrid grid) return;
        grid.SizeChanged -= OnUniformHostSizeChanged;
        grid.Loaded -= OnUniformHostLoaded;
        if (!double.IsNaN((double)e.NewValue))
        {
            grid.SizeChanged += OnUniformHostSizeChanged;
            grid.Loaded += OnUniformHostLoaded;
        }
        ApplyUniformColumns(grid);
    }

    private static void OnUniformHostLoaded(object sender, RoutedEventArgs e) => ApplyUniformColumns((UniformGrid)sender);
    private static void OnUniformHostSizeChanged(object sender, SizeChangedEventArgs e) => ApplyUniformColumns((UniformGrid)sender);

    private static void ApplyUniformColumns(UniformGrid grid)
    {
        var threshold = GetUniformColumnsBelow(grid);
        if (double.IsNaN(threshold) || threshold <= 0) return;
        var width = Math.Round(grid.ActualWidth);
        if (width <= 0) return;
        var narrow = width < threshold;
        if (grid.GetValue(UniformStateProperty) is bool previous && previous == narrow) return;
        grid.SetValue(UniformStateProperty, narrow);
        if (narrow)
        {
            if (grid.GetValue(UniformSlotProperty) is not UniformSlot)
                grid.SetValue(UniformSlotProperty, new UniformSlot { Columns = grid.Columns, Rows = grid.Rows });
            grid.Columns = Math.Max(1, GetUniformColumns(grid));
            grid.Rows = 0;
        }
        else if (grid.GetValue(UniformSlotProperty) is UniformSlot slot)
        {
            grid.Columns = slot.Columns;
            grid.Rows = slot.Rows;
        }
    }

    /// <summary>
    /// 附加在页面级 ScrollViewer 上：自身可用宽低于该值时把纵向滚动从 Disabled 改为 Auto。
    /// 页面在窄档把并排两栏折成上下之后总高会超过视口，必须靠这一层滚动兜住，否则底部内容被裁掉。
    /// 宽档还原为原值（Disabled 时滚动条不出现、内容仍按视口约束，所以宽档几何逐字不变）。
    /// </summary>
    public static readonly DependencyProperty PageScrollBelowProperty = DependencyProperty.RegisterAttached(
        "PageScrollBelow", typeof(double), typeof(ResponsiveLayout), new PropertyMetadata(double.NaN, OnPageScrollBelowChanged));

    public static double GetPageScrollBelow(DependencyObject element) => (double)element.GetValue(PageScrollBelowProperty);
    public static void SetPageScrollBelow(DependencyObject element, double value) => element.SetValue(PageScrollBelowProperty, value);

    private static readonly DependencyProperty PageScrollOriginalProperty = DependencyProperty.RegisterAttached(
        "PageScrollOriginal", typeof(ScrollBarVisibility?), typeof(ResponsiveLayout), new PropertyMetadata(null));
    private static readonly DependencyProperty PageScrollStateProperty = DependencyProperty.RegisterAttached(
        "PageScrollState", typeof(bool?), typeof(ResponsiveLayout), new PropertyMetadata(null));

    private static void OnPageScrollBelowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer viewer) return;
        viewer.SizeChanged -= OnPageScrollHostSizeChanged;
        viewer.Loaded -= OnPageScrollHostLoaded;
        if (!double.IsNaN((double)e.NewValue))
        {
            viewer.SizeChanged += OnPageScrollHostSizeChanged;
            viewer.Loaded += OnPageScrollHostLoaded;
        }
        ApplyPageScroll(viewer);
    }

    private static void OnPageScrollHostLoaded(object sender, RoutedEventArgs e) => ApplyPageScroll((ScrollViewer)sender);
    private static void OnPageScrollHostSizeChanged(object sender, SizeChangedEventArgs e) => ApplyPageScroll((ScrollViewer)sender);

    private static void ApplyPageScroll(ScrollViewer viewer)
    {
        var threshold = GetPageScrollBelow(viewer);
        if (double.IsNaN(threshold) || threshold <= 0) return;
        var width = Math.Round(viewer.ActualWidth);
        if (width <= 0) return;
        var scrollable = width < threshold;
        if (viewer.GetValue(PageScrollStateProperty) is bool previous && previous == scrollable) return;
        viewer.SetValue(PageScrollStateProperty, scrollable);
        if (scrollable)
        {
            if (viewer.GetValue(PageScrollOriginalProperty) is not ScrollBarVisibility)
                viewer.SetValue(PageScrollOriginalProperty, viewer.VerticalScrollBarVisibility);
            viewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
        else if (viewer.GetValue(PageScrollOriginalProperty) is ScrollBarVisibility original)
        {
            viewer.VerticalScrollBarVisibility = original;
        }
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
