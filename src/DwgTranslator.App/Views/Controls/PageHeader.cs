using System.Windows;
using System.Windows.Controls;

namespace DwgTranslator.App.Views.Controls;

/// <summary>Shared page identity with a right-aligned action slot; inherits the page's data context.</summary>
public sealed class PageHeader : HeaderedContentControl
{
    public static readonly DependencyProperty IsStackedProperty = DependencyProperty.Register(
        nameof(IsStacked), typeof(bool), typeof(PageHeader), new PropertyMetadata(false));
    public bool IsStacked { get => (bool)GetValue(IsStackedProperty); set => SetValue(IsStackedProperty, value); }
    /// <summary>动作区折行阈值：读共享断点令牌，取不到时退回原来的 420（§自适应 2026-09-25）。</summary>
    private const double FallbackStackWidth = 420;
    public PageHeader()
    {
        SizeChanged += (_, _) => IsStacked = ActualWidth < ResolveStackWidth();
        Loaded += (_, _) => IsStacked = ActualWidth < ResolveStackWidth();
    }

    private double ResolveStackWidth() =>
        TryFindResource("Size.BreakpointPageHeader") is double token ? token : FallbackStackWidth;
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(PageHeader), new PropertyMetadata(string.Empty));
    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>
    /// 页头琥珀波浪的品牌变体（1-5，对应 Icon.Deco.Wave / Wave2..Wave5）。
    /// 同一条波浪出现在所有页头会让五个页面缺少区分度（2026-10-02 用户反馈），
    /// 每页挑一种节奏即可；默认 1 保持旧页面不变。
    /// </summary>
    public static readonly DependencyProperty WaveVariantProperty = DependencyProperty.Register(
        nameof(WaveVariant), typeof(int), typeof(PageHeader), new PropertyMetadata(1));
    public int WaveVariant
    {
        get => (int)GetValue(WaveVariantProperty);
        set => SetValue(WaveVariantProperty, value);
    }
}
