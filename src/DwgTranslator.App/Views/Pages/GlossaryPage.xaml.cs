using System.Windows;
using System.Windows.Controls;
using DwgTranslator.App.ViewModels;
namespace DwgTranslator.App.Views.Pages;
public partial class GlossaryPage : UserControl
{
    public GlossaryPage() { InitializeComponent(); }
    private void Terms_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var width = Math.Max(200, e.NewSize.Width - 26);
        var columns = Math.Clamp((int)(width / 320), 1, 3);
        vm.TermWidth = Math.Max(170, width / columns - 8);
    }
}