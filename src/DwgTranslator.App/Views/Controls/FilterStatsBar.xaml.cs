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
}