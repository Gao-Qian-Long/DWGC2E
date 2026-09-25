using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using DwgTranslator.App.ViewModels;
using DwgTranslator.Core.Api;
namespace DwgTranslator.App.Views.Pages;
public partial class AccountPage : UserControl
{
    public AccountPage()
    {
        InitializeComponent();
        Loaded += (_, _) => { var w = Window.GetWindow(this); if(w != null) { w.Activated -= Owner_Activated; w.Activated += Owner_Activated; } };
        Unloaded += (_, _) => { var w = Window.GetWindow(this); if(w != null) w.Activated -= Owner_Activated; };
        IsVisibleChanged += async (_, _) => { if (IsVisible && DataContext is MainViewModel vm) await vm.RefreshMembershipOnActivationAsync(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) Dispatcher.BeginInvoke(new Action(() => AccountInput.Focus())); else PasswordInput.Clear(); };
    }
    private async void Owner_Activated(object? sender, EventArgs e) { if(IsVisible && DataContext is MainViewModel vm) await vm.RefreshMembershipOnActivationAsync(); }
    private void AccountMenu_Click(object sender, RoutedEventArgs e) { if(sender is Button b && b.ContextMenu is {} menu) { menu.DataContext=DataContext; menu.PlacementTarget=b; menu.IsOpen=true; } }
    /// <summary>
    /// 响应式判定统一改用**视口宽度**（用户批注 2026-09-25：「我调整窗口的时候右边会被裁切，是什么原因」）。
    /// 原来三个处理器都拿 e.NewSize.Width —— 那是**内容自身**的宽度：内容根是 MaxWidth=1200 的 StackPanel，
    /// 窗口变窄后它仍按 ~1200 上报，判定因此永远 ≥1000、永远走"三并排 / 两列"宽布局；
    /// 而根部 ScrollViewer 又显式禁用了横向滚动（HorizontalScrollBarVisibility="Disabled"），
    /// 溢出部分没有滚动条可看，只能被裁掉 —— 这正是"右边被裁切"的成因。
    /// 换成视口宽度后，窗口一变窄即按 620 / 1100 两个门槛重排为两行或单列。
    /// </summary>
    private double ResponsiveWidth()
    {
        var width = AccountScroll?.ViewportWidth ?? 0;
        if (double.IsNaN(width) || width <= 0) width = AccountScroll?.ActualWidth ?? 0;
        if (double.IsNaN(width) || width <= 0) width = ActualWidth;
        if (double.IsNaN(width) || width <= 0) width = 0;
        return width;
    }

    /// <summary>
    /// 视口尺寸变化时重算三处响应式布局。
    /// 这一处是必需的：窗口变窄时内容宽度可能不变（仍是 MaxWidth 的 ~1200），
    /// 三个容器自身的 SizeChanged 不会触发，只有盯着 ScrollViewer 才能及时重排。
    /// </summary>
    private void AccountScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        MembershipDetails_SizeChanged(sender, e);
        AccountIdentityLayout_SizeChanged(sender, e);
        AccountNavTiles_SizeChanged(sender, e);
    }

    private void MembershipDetails_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // At compact widths two cards make their fixed label/description columns compete for space.
        // Stack them before either card becomes narrower than its content.
        // 判定必须用**视口宽度**（见 ResponsiveWidth 的说明）：用内容自身宽度会永远走宽布局分支。
        var compact = ResponsiveWidth() < 1100;
        Grid.SetColumn(DevicesCard, compact ? 0 : 1);
        Grid.SetRow(DevicesCard, compact ? 1 : 0);
        Grid.SetColumnSpan(PlansCard, compact ? 2 : 1);
        Grid.SetColumnSpan(DevicesCard, compact ? 2 : 1);
        PlansCard.Margin = new Thickness(0, 0, compact ? 0 : 16, 16);
    }
    private void AccountIdentityLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 不再依赖 sender：视口回调会以 ScrollViewer 为 sender 调用本方法，靠名字取元素更稳。
        if (AccountIdentityLayout == null || AccountIdentityHeader == null || AccountBenefits == null || AccountActionColumn == null) return;
        // The identity card has a fixed-size quota ring and action buttons; move that group below the
        // benefits on narrow cards, and let the quota bar itself follow the available width.
        var stacked = ResponsiveWidth() < 1100;
        AccountIdentityLayout.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : GridLength.Auto;
        Grid.SetColumn(AccountIdentityHeader, 0);
        Grid.SetColumnSpan(AccountIdentityHeader, stacked ? 2 : 1);
        Grid.SetRow(AccountBenefits, 1);
        Grid.SetColumn(AccountBenefits, 0);
        Grid.SetColumnSpan(AccountBenefits, stacked ? 2 : 1);
        Grid.SetRow(AccountActionColumn, stacked ? 2 : 0);
        Grid.SetColumn(AccountActionColumn, stacked ? 0 : 1);
        Grid.SetColumnSpan(AccountActionColumn, stacked ? 2 : 1);
        Grid.SetRowSpan(AccountActionColumn, stacked ? 1 : 2);
        AccountActionColumn.HorizontalAlignment = stacked ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        AccountActionColumn.Margin = stacked ? new Thickness(0, 16, 0, 0) : (Thickness)FindResource("Spacing.LeftGap");
    }
    private void AccountNavTiles_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (AccountNavColumn0 == null || TranslateNavTile == null || GlossaryNavTile == null || OutputNavTile == null) return;

        var width = ResponsiveWidth();
        var singleColumn = width < 620;
        var twoRows = !singleColumn && width < 1100;
        var gutter = (GridLength)FindResource("Size.CardGutter");

        AccountNavColumn0.Width = new GridLength(1, GridUnitType.Star);
        AccountNavColumn1.Width = singleColumn ? new GridLength(0) : gutter;
        AccountNavColumn2.Width = singleColumn ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        AccountNavColumn3.Width = twoRows || singleColumn ? new GridLength(0) : gutter;
        AccountNavColumn4.Width = twoRows || singleColumn ? new GridLength(0) : new GridLength(1, GridUnitType.Star);

        PlaceTile(TranslateNavTile, row: 0, column: 0, columnSpan: singleColumn ? 5 : 1,
            topMargin: 0);
        PlaceTile(GlossaryNavTile, row: singleColumn ? 1 : 0, column: singleColumn ? 0 : 2,
            columnSpan: singleColumn ? 5 : 1, topMargin: singleColumn ? 16 : 0);
        PlaceTile(OutputNavTile, row: singleColumn ? 2 : twoRows ? 1 : 0,
            column: twoRows || singleColumn ? 0 : 4,
            columnSpan: twoRows || singleColumn ? (singleColumn ? 5 : 3) : 1,
            topMargin: twoRows || singleColumn ? 16 : 0);
    }

    private static void PlaceTile(Button tile, int row, int column, int columnSpan, double topMargin)
    {
        Grid.SetRow(tile, row);
        Grid.SetColumn(tile, column);
        Grid.SetColumnSpan(tile, columnSpan);
        tile.Margin = new Thickness(0, topMargin, 0, 0);
    }
    private bool _submitting;
    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (_submitting || DataContext is not MainViewModel vm || vm.IsLoggingIn) return;
        AccountInput.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        var password = PasswordInput.Password;
        _submitting = true;
        try { await vm.SubmitLoginAsync(password); if(vm.IsAccountLoggedIn) PasswordInput.Clear(); }
        finally { _submitting = false; }
    }
    private void Password_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; Login_Click(sender, e); } }
}

/// <summary>
/// 环形用量图中心的比例文案。额度基数很大（1 亿级字符）时真实用量远低于 0.1%，
/// 直接四舍五入会显示成 "0%"，读起来像没有数据，所以低于 0.1% 一律写 "&lt;0.1%"。
/// 只做展示格式化，不改动任何额度数值。
/// </summary>
public sealed class UsagePercentTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not UsageInfo usage || usage.MonthlyQuota <= 0) return "—";
        var percent = 100d * usage.Used / usage.MonthlyQuota;
        if (percent <= 0) return "0%";
        if (percent < 0.1) return "<0.1%";
        return percent >= 100 ? "100%" : Math.Round(percent, percent >= 10 ? 0 : 1).ToString("0.#", culture) + "%";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// 「本周期额度重置时间」文案。后台没有下发 ResetAt 时返回空串，调用方
/// （「套餐与订单」卡里那个 TextBlock）据此整行收起，而不是显示占位符。
/// 只做展示格式化，不改动任何额度数值。
/// </summary>
public sealed class QuotaResetTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not UsageInfo usage || usage.ResetAt is not DateTime resetAt) return string.Empty;
        var local = resetAt.ToLocalTime();
        var days = (local.Date - DateTime.Now.Date).Days;
        var when = days switch
        {
            <= 0 => "今天",
            1 => "明天",
            _ => local.ToString("M'月'd'日'", culture)
        };
        return $"本周期额度将于 {when} 重置";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// 额度环形用量图：把 Used / Quota 画成一段从 12 点顺时针的圆弧（底环 + 进度环），
/// 纯绘制元素，不持有业务状态、不发起任何请求；数字全部来自绑定。
/// </summary>
public sealed class QuotaRing : FrameworkElement
{
    public static readonly DependencyProperty UsedProperty = DependencyProperty.Register(
        nameof(Used), typeof(double), typeof(QuotaRing), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty QuotaProperty = DependencyProperty.Register(
        nameof(Quota), typeof(double), typeof(QuotaRing), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RingThicknessProperty = DependencyProperty.Register(
        nameof(RingThickness), typeof(double), typeof(QuotaRing), new FrameworkPropertyMetadata(10d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(QuotaRing), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(QuotaRing), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Used { get => (double)GetValue(UsedProperty); set => SetValue(UsedProperty, value); }
    public double Quota { get => (double)GetValue(QuotaProperty); set => SetValue(QuotaProperty, value); }
    public double RingThickness { get => (double)GetValue(RingThicknessProperty); set => SetValue(RingThicknessProperty, value); }
    public Brush? TrackBrush { get => (Brush?)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public Brush? FillBrush { get => (Brush?)GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }

    /// <summary>已用占比（0..1）；额度未知时为 0，由页面自行决定是否显示该图。</summary>
    public double UsageRatio => Quota > 0 ? Math.Clamp(Used / Quota, 0d, 1d) : 0d;

    protected override void OnRender(DrawingContext drawingContext)
    {
        var thickness = Math.Max(1d, RingThickness);
        var radius = (Math.Min(ActualWidth, ActualHeight) - thickness) / 2;
        if (radius <= 0) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);

        if (TrackBrush is { } track) drawingContext.DrawEllipse(null, new Pen(track, thickness), center, radius, radius);

        var ratio = UsageRatio;
        if (FillBrush is not { } fill || ratio <= 0) return;
        var progress = new Pen(fill, thickness) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
        if (ratio >= 0.9999)
        {
            drawingContext.DrawEllipse(null, progress, center, radius, radius);
            return;
        }

        // 单段 ArcTo 无法表达大于 180° 的弧，用 isLargeArc 让 WPF 走长边；此处 ratio < 1 已排除整圆。
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(PointOnRing(center, radius, 0), false, false);
            context.ArcTo(PointOnRing(center, radius, 360 * ratio), new Size(radius, radius), 0, ratio > 0.5, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        drawingContext.DrawGeometry(null, progress, geometry);

        // 额度基数到亿级时，真实用量算出的弧长常常不足 0.2°（亚像素），看起来像根本没画。
        // 这时在弧的终点补一个同色圆点：位置就是真实弧长所在处，不夸大弧长，只让「有微量用量」可读。
        if (ratio < 0.02) drawingContext.DrawEllipse(fill, null, PointOnRing(center, radius, 360 * ratio), thickness * 0.45, thickness * 0.45);
    }

    /// <summary>0 度 = 12 点方向，顺时针为正。</summary>
    private static Point PointOnRing(Point center, double radius, double degrees)
    {
        var radians = (degrees - 90) * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }
}
