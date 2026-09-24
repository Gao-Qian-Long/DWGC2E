using DwgTranslator.App.ViewModels;
using DwgTranslator.Core.Models;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace DwgTranslator.App.Views.Pages;

/// <summary>BatchTasksPage — 任务中心页（原「批量任务」，2026-10-02 更名），由 MainWindow 的侧栏切换。</summary>
public partial class BatchTasksPage : UserControl
{
    public BatchTasksPage()
    {
        InitializeComponent();
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e) { if(DataContext is MainViewModel vm) { vm.BatchSearch=""; vm.BatchStatusFilter=0; vm.BatchDateFilter=0; } }

    private void TaskTable_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid table || e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(table, source) is not DataGridRow row) return;

        // A right-click does not reliably change DataGrid selection. Context-menu commands bind
        // to SelectedBatchTask, so update both the grid and its view model to the clicked row.
        row.Focus();
        table.SelectedItem = row.Item;
        if (table.DataContext is MainViewModel vm && row.Item is DrawingFileItem task)
            vm.SelectedBatchTask = task;
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
        vm.TrackProofreadingEdit(entity);
        entity.TranslatedText = editor.Text;
        entity.Status = string.IsNullOrWhiteSpace(editor.Text)
            ? TranslationStatus.Pending
            : TranslationStatus.Reviewed;

        // Rebuilding the filtered collection during CellEditEnding interrupts DataGrid commit.
        Dispatcher.InvokeAsync(() => { if (vm.HasUnsavedProofreading) vm.StatusMessage = "校对更改尚未保存。请保存更改或取消编辑。"; }, DispatcherPriority.Background);
    }
}

