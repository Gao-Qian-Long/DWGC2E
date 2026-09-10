using System.IO;
using System.Windows;
using DwgTranslator.App.ViewModels;
using DwgTranslator.Core.Resources;
using Microsoft.Extensions.DependencyInjection;

namespace DwgTranslator.App.Views;

/// <summary>
/// Interaction logic for MainWindow
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        // Resolve ViewModel from DI container (falls back to parameterless ctor if DI not ready)
        _viewModel = App.Services?.GetService<MainViewModel>() ?? new MainViewModel();
        DataContext = _viewModel;

        // BUG FIX: 异步初始化术语库加载，避免 UI 线程同步阻塞
        Loaded += OnLoaded;
        Closing += (s, e) => _viewModel.Dispose();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();
        await ImportStartupFilesAsync();

        // A freshly installed copy is started with --env-check so the user lands on the screen that
        // finishes the setup (installing the CAD plugin) instead of an empty workspace.
        if (App.OpenEnvironmentCheckOnStart)
        {
            App.ClearEnvironmentCheckRequest();
            _viewModel.ShowEnvironmentCheckCommand.Execute(null);
        }
    }

    /// <summary>
    /// Imports files passed on the command line (for example "Open with" from Explorer or a
    /// shortcut). They share the exact code path used by drag-and-drop.
    /// </summary>
    private async Task ImportStartupFilesAsync()
    {
        var startupFiles = App.StartupFiles;
        if (startupFiles.Length == 0) return;

        App.ClearStartupFiles();
        await _viewModel.ImportDroppedFilesAsync(startupFiles);
    }

    // ───────────────────────── Drag & drop ─────────────────────────

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        if (!TryGetDroppedFiles(e.Data, out var files))
        {
            // Not a file drop: leave it alone so text selection drags inside the grid keep working.
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            DropOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        ShowDropOverlay(files);
    }

    private void OnPreviewDragLeave(object sender, DragEventArgs e) =>
        DropOverlay.Visibility = Visibility.Collapsed;

    private async void OnPreviewDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (!TryGetDroppedFiles(e.Data, out var files)) return;

        e.Handled = true;
        await _viewModel.ImportDroppedFilesAsync(files);
    }

    /// <summary>
    /// Reads a file-drop payload. Returns false for any other payload so the window does not
    /// claim drags it cannot handle.
    /// </summary>
    private static bool TryGetDroppedFiles(IDataObject? data, out string[] files)
    {
        files = [];
        if (data == null || !data.GetDataPresent(DataFormats.FileDrop)) return false;
        if (data.GetData(DataFormats.FileDrop) is not string[] dropped || dropped.Length == 0) return false;

        files = dropped;
        return true;
    }

    private void ShowDropOverlay(string[] files)
    {
        DropOverlay.Visibility = Visibility.Visible;

        if (files.Length == 0)
        {
            DropOverlayFiles.Visibility = Visibility.Collapsed;
            return;
        }

        var accepted = files.Count(MainViewModel.IsSupportedFile);
        var preview = string.Join("、", files.Take(3).Select(Path.GetFileName));
        if (files.Length > 3) preview += " …";

        DropOverlayFiles.Visibility = Visibility.Visible;
        DropOverlayFiles.Text = accepted == files.Length
            ? Strings.Get("DropHintFiles", files.Length, preview)
            : Strings.Get("DropHintFilesPartial", accepted, files.Length);
    }
}
