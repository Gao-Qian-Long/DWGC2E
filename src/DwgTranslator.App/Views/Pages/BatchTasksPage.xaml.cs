using DwgTranslator.App.ViewModels;
using DwgTranslator.Core.Models;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace DwgTranslator.App.Views.Pages;

/// <summary>BatchTasksPage — 批量任务页，由 MainWindow 的侧栏切换。</summary>
public partial class BatchTasksPage : UserControl
{
    public BatchTasksPage()
    {
        InitializeComponent();
    }

    /// <summary>Explicit source updates keep Escape from changing the saved translation.</summary>
    private void TranslationGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit
            || e.EditingElement is not TextBox editor
            || e.Row.Item is not TextEntity entity
            || DataContext is not MainViewModel vm)
            return;

        if (vm.IsProcessing)
        {
            e.Cancel = true;
            return;
        }

        if (entity.TranslatedText == editor.Text) return;
        entity.TranslatedText = editor.Text;
        entity.Status = string.IsNullOrWhiteSpace(editor.Text)
            ? TranslationStatus.Pending
            : TranslationStatus.Reviewed;

        // Rebuilding the filtered collection during CellEditEnding interrupts DataGrid commit.
        Dispatcher.InvokeAsync(() => vm.MarkTranslationEdited(entity), DispatcherPriority.Background);
    }
    /// <summary>行内 "···" 按钮：左键也能展开任务操作菜单（§24）。</summary>
    private void RowMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null) return;

        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = PlacementMode.Bottom;
        button.ContextMenu.HorizontalOffset = -120;
        button.ContextMenu.IsOpen = true;
    }
}

