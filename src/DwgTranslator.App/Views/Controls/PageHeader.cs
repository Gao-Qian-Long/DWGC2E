using System.Windows;
using System.Windows.Controls;

namespace DwgTranslator.App.Views.Controls;

/// <summary>Shared page identity with a right-aligned action slot; inherits the page's data context.</summary>
public sealed class PageHeader : HeaderedContentControl
{
    public static readonly DependencyProperty IsStackedProperty = DependencyProperty.Register(
        nameof(IsStacked), typeof(bool), typeof(PageHeader), new PropertyMetadata(false));
    public bool IsStacked { get => (bool)GetValue(IsStackedProperty); set => SetValue(IsStackedProperty, value); }
    public PageHeader() { SizeChanged += (_, _) => IsStacked = ActualWidth < 420; }
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(PageHeader), new PropertyMetadata(string.Empty));
    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }
}
