using System.Windows;
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
        Closing += (s, e) => vm.Dispose();
    }
}
