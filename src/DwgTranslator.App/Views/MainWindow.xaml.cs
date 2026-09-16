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

    // Avoid a circular dependency between the scroll viewport and page measurement.
    private void PageViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (PageHost == null) return;
        var width = Math.Max(0, e.NewSize.Width - 2);
        var height = Math.Max(0, e.NewSize.Height - 2);
        var horizontal = width < 700;
        var vertical = height < 640;
        if (vertical && width - SystemParameters.VerticalScrollBarWidth < 700) horizontal = true;
        if (horizontal && height - SystemParameters.HorizontalScrollBarHeight < 640) vertical = true;
        PageViewport.HorizontalScrollBarVisibility = horizontal ? System.Windows.Controls.ScrollBarVisibility.Auto : System.Windows.Controls.ScrollBarVisibility.Disabled;
        PageViewport.VerticalScrollBarVisibility = vertical ? System.Windows.Controls.ScrollBarVisibility.Auto : System.Windows.Controls.ScrollBarVisibility.Disabled;
        PageHost.Width = Math.Max(700, width - (vertical ? SystemParameters.VerticalScrollBarWidth : 0));
        PageHost.Height = Math.Max(640, height - (horizontal ? SystemParameters.HorizontalScrollBarHeight : 0));
    }

    public MainWindow()
    {
        InitializeComponent();
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, area.Width);
        Height = Math.Min(Height, area.Height);

        // Resolve ViewModel from DI container (falls back to parameterless ctor if DI not ready)
        // 依赖全部由 DI 装配（MainViewModel 只有这一个构造函数）：容器没起来就是致命错误，
        // 不能悄悄退回"半装配"的另一套实例——那样界面会看不到任务层，翻译按钮会静默失效。
        _viewModel = App.Services?.GetService<MainViewModel>()
            ?? throw new InvalidOperationException("服务容器未初始化，无法创建主视图模型。");
        DataContext = _viewModel;

        // BUG FIX: 异步初始化术语库加载，避免 UI 线程同步阻塞
        // 操作结果统一走 Toast（§29）
        Services.ToastService.Attach(Toasts);

        Loaded += OnLoaded;
        Closing += (s, e) => { if (!_viewModel.ConfirmLeavePage()) { e.Cancel = true; return; } _viewModel.Dispose(); };
    }

    /// <summary>右上角用户入口：用主题化 ContextMenu 承载账号/置顶/设置等入口（§9）。</summary>
    private void GlobalAccount_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsAccountLoggedIn) { _viewModel.LoginAccountCommand.Execute(null); return; }
        if (sender is System.Windows.Controls.Button b && b.ContextMenu != null) b.ContextMenu.DataContext = _viewModel;
        AccountMenu_Click(sender, e);
    }

    private void AccountMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.ContextMenu is null) return;

        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        button.ContextMenu.HorizontalOffset = -8;
        button.ContextMenu.IsOpen = true;
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
