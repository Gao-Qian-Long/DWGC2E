using System.Windows;
using System.Windows.Controls;
namespace DwgTranslator.App.Views.Controls;
/// <summary>Shared presentation-only empty state with an optional action slot.</summary>
public sealed class EmptyState : HeaderedContentControl
{
 public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(nameof(Description), typeof(string), typeof(EmptyState), new PropertyMetadata(""));
 public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty,value); }
}
