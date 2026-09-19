using System.Windows;

namespace DwgTranslator.App.Views;

public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            e.Handled = true;
            Close();
        };
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
