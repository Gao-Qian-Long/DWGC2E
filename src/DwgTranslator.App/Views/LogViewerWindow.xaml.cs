using System.Windows;
using DwgTranslator.App.ViewModels;

namespace DwgTranslator.App.Views;

public partial class LogViewerWindow : Window
{
    public LogViewerWindow(LogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) =>
        {
            viewModel.IsVisible = true;
            viewModel.RefreshEntriesCommand.Execute(null);
        };
        Closed += (_, _) => viewModel.IsVisible = false;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            e.Handled = true;
            Close();
        };
    }
}
