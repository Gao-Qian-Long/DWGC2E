using System.Windows.Controls;
using System.Windows.Input;

namespace DwgTranslator.App.Views.Controls;

public partial class FilterStatsBar : UserControl
{
    public FilterStatsBar()
    {
        InitializeComponent();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (DataContext is ViewModels.MainViewModel vm)
            {
                vm.ApplySearchCommand.Execute(null);
            }
            e.Handled = true;
        }
    }

    private void ClearSearch_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel vm)
        {
            vm.SearchText = string.Empty;
            vm.ApplySearchCommand.Execute(null);
        }
        SearchBox.Focus();
    }
}