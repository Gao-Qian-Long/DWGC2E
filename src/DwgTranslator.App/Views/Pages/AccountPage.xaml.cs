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
    private void MembershipDetails_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Stack complete sections on compact windows instead of clipping their actions.
        var compact = e.NewSize.Width < 760;
        Grid.SetColumn(DevicesCard, compact ? 0 : 1);
        Grid.SetRow(DevicesCard, compact ? 1 : 0);
        Grid.SetColumnSpan(PlansCard, compact ? 2 : 1);
        Grid.SetColumnSpan(DevicesCard, compact ? 2 : 1);
        PlansCard.Margin = new Thickness(0, 0, compact ? 0 : 16, 16);
    }
    private bool _submitting;
    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (_submitting || DataContext is not MainViewModel vm || vm.IsLoggingIn || vm.IsAccountRefreshing) return;
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