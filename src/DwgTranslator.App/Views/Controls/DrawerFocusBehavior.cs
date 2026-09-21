using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DwgTranslator.App.Views.Controls;

/// <summary>
/// Moves keyboard focus into a modal drawer and returns it to the exact trigger when the drawer
/// closes. A stable page-level fallback is used when a virtualized row has already been recycled.
/// </summary>
public static class DrawerFocusBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(DrawerFocusBehavior),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty FocusOnOpenProperty = DependencyProperty.RegisterAttached(
        "FocusOnOpen",
        typeof(UIElement),
        typeof(DrawerFocusBehavior),
        new PropertyMetadata(null));

    public static readonly DependencyProperty RestoreFocusFallbackProperty = DependencyProperty.RegisterAttached(
        "RestoreFocusFallback",
        typeof(UIElement),
        typeof(DrawerFocusBehavior),
        new PropertyMetadata(null));

    private static readonly DependencyProperty PreviousFocusProperty = DependencyProperty.RegisterAttached(
        "PreviousFocus",
        typeof(IInputElement),
        typeof(DrawerFocusBehavior),
        new PropertyMetadata(null));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);
    public static UIElement? GetFocusOnOpen(DependencyObject element) => (UIElement?)element.GetValue(FocusOnOpenProperty);
    public static void SetFocusOnOpen(DependencyObject element, UIElement? value) => element.SetValue(FocusOnOpenProperty, value);
    public static UIElement? GetRestoreFocusFallback(DependencyObject element) => (UIElement?)element.GetValue(RestoreFocusFallbackProperty);
    public static void SetRestoreFocusFallback(DependencyObject element, UIElement? value) => element.SetValue(RestoreFocusFallbackProperty, value);

    private static void OnIsEnabledChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not FrameworkElement drawer) return;
        drawer.IsVisibleChanged -= DrawerIsVisibleChanged;
        drawer.Unloaded -= DrawerUnloaded;
        if ((bool)e.NewValue)
        {
            drawer.IsVisibleChanged += DrawerIsVisibleChanged;
            drawer.Unloaded += DrawerUnloaded;
            if (drawer.IsVisible) OpenDrawer(drawer);
        }
        else
        {
            drawer.ClearValue(PreviousFocusProperty);
        }
    }

    private static void DrawerIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement drawer || !GetIsEnabled(drawer)) return;
        if ((bool)e.NewValue) OpenDrawer(drawer); else CloseDrawer(drawer);
    }

    private static void DrawerUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement drawer) drawer.ClearValue(PreviousFocusProperty);
    }

    private static void OpenDrawer(FrameworkElement drawer)
    {
        var focused = Keyboard.FocusedElement;
        if (focused is not null && (focused is not DependencyObject dependencyObject || !IsDescendantOf(dependencyObject, drawer)))
            drawer.SetValue(PreviousFocusProperty, focused);

        SlideIn(drawer);

        drawer.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!drawer.IsVisible || !drawer.IsEnabled) return;
            var target = GetFocusOnOpen(drawer) ?? FindFocusableDescendant(drawer);
            TryFocus(target);
        }));
    }

    /// <summary>
    /// 用户批注（2026-09-21）：「从右到左弹出来就行了」——抽屉打开时从右侧滑入。
    /// RenderTransform 不参与布局，ActualWidth/ActualHeight/焦点命中全部不变，冒烟几何断言不受影响。
    /// 动画时长 220ms + EaseOut（快进缓停），短于冒烟 WaitUntil 的轮询粒度，不会让断言等到中间帧；
    /// 幂等：重复可见（如 1280 冒烟里 IsTaskDetailOpen 重开）会先停掉旧动画再放新的。
    /// 滑入只作用于挂了本行为的抽屉（任务详情/术语编辑），MergeDrawer 等无行为挂载的不动。
    /// </summary>
    private static void SlideIn(FrameworkElement drawer)
    {
        // 必须在测量之后取 ActualWidth：可见性刚翻转时 ActualWidth 可能还是 0（首次打开）。
        drawer.UpdateLayout();
        var translate = new System.Windows.Media.TranslateTransform(drawer.ActualWidth, 0);
        drawer.RenderTransform = translate;
        var animation = new System.Windows.Media.Animation.DoubleAnimation(
            drawer.ActualWidth, 0, new System.Windows.Duration(TimeSpan.FromMilliseconds(220)))
        {
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
            FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
        };
        // FillBehavior.Stop + 动画结束后清 RenderTransform：transform 归零后不再持有偏移，
        // 后续 RenderTransform 变更（如未来加关闭动画）不会叠加。
        translate.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, animation);
    }

    private static void CloseDrawer(FrameworkElement drawer)
    {
        drawer.RenderTransform = null;
        var previous = (IInputElement?)drawer.GetValue(PreviousFocusProperty);
        drawer.ClearValue(PreviousFocusProperty);
        var fallback = GetRestoreFocusFallback(drawer);

        // Let the close command, visibility binding and virtualized row regeneration finish first.
        // A short dispatcher timer is deterministic even while background work is active; idle
        // callbacks can otherwise be starved and leave focus on the hidden close button.
        var timer = new DispatcherTimer(DispatcherPriority.Input, drawer.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(1)
        };
        EventHandler? tick = null;
        tick = (_, _) =>
        {
            timer.Stop();
            timer.Tick -= tick;
            if (drawer.IsVisible || !drawer.IsLoaded) return;
            if (!TryFocus(previous)) TryFocus(fallback);
        };
        timer.Tick += tick;
        timer.Start();
    }

    private static bool TryFocus(IInputElement? target)
    {
        if (target is UIElement ui)
        {
            if (!ui.IsVisible || !ui.IsEnabled || !ui.Focusable) return false;
            if (ui is FrameworkElement { IsLoaded: false }) return false;
            return ui.Focus() && (ui.IsKeyboardFocused || ui.IsKeyboardFocusWithin);
        }

        if (target is ContentElement content)
        {
            if (!content.IsEnabled || !content.Focusable) return false;
            return content.Focus() && content.IsKeyboardFocused;
        }

        return false;
    }

    private static UIElement? FindFocusableDescendant(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is UIElement { IsVisible: true, IsEnabled: true, Focusable: true } focusable) return focusable;
            var nested = FindFocusableDescendant(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static bool IsDescendantOf(DependencyObject child, DependencyObject ancestor)
    {
        var current = child;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor)) return true;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }
}







