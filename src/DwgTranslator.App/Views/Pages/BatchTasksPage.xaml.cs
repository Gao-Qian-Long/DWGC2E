using DwgTranslator.App.ViewModels;
using DwgTranslator.Core.Models;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace DwgTranslator.App.Views.Pages;

/// <summary>BatchTasksPage — 任务中心页（原「批量任务」，2026-10-02 更名），由 MainWindow 的侧栏切换。</summary>
public partial class BatchTasksPage : UserControl
{
    public BatchTasksPage()
    {
        InitializeComponent();
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e) { if(DataContext is MainViewModel vm) { vm.BatchSearch=""; vm.BatchStatusFilter=0; vm.BatchDateFilter=0; } }
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

