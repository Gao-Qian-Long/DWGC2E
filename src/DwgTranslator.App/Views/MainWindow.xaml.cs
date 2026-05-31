using System.Windows;
using System.Windows.Controls;
using DwgTranslator.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DwgTranslator.App.Views;

/// <summary>
/// Interaction logic for MainWindow
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Resolve ViewModel from DI container (falls back to parameterless ctor if DI not ready)
        DataContext = App.Services?.GetService<MainViewModel>() ?? new MainViewModel();

        StateChanged += OnWindowStateChanged;
    }

    private void OnWindowStateChanged(object? sender, System.EventArgs e)
    {
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        SystemCommands.MinimizeWindow(this);
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        SystemCommands.CloseWindow(this);
    }
}
