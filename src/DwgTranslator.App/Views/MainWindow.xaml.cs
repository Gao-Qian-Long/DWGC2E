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
        var vm = App.Services?.GetService<MainViewModel>() ?? new MainViewModel();
        DataContext = vm;

        // BUG FIX: 异步初始化术语库加载，避免 UI 线程同步阻塞
        Loaded += async (s, e) => await vm.InitializeAsync();

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
