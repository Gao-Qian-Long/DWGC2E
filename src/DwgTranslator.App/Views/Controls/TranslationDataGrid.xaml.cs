using System.Windows.Controls;
using DwgTranslator.App.ViewModels;
using DwgTranslator.Core.Models;

namespace DwgTranslator.App.Views.Controls;

public partial class TranslationDataGrid : UserControl
{
    public TranslationDataGrid()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel oldVm)
        {
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
        }
        if (e.NewValue is MainViewModel newVm)
        {
            newVm.PropertyChanged += OnViewModelPropertyChanged;
            UpdateEmptyState(newVm);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm && (e.PropertyName == nameof(MainViewModel.TotalCount) ||
            e.PropertyName == nameof(MainViewModel.TranslatedCount) || e.PropertyName == nameof(MainViewModel.IsProcessing)))
        {
            UpdateEmptyState(vm);
        }
    }

    private void UpdateEmptyState(MainViewModel vm)
    {
        // Only cover the grid when there is genuinely nothing to show. Previously the overlay
        // also covered the imported rows until the first translation finished, so after an
        // import (dialog, drag & drop or command line) the user could not see or check what
        // had just been loaded.
        if (vm.TotalCount == 0)
        {
            EmptyStatePanel.Visibility = System.Windows.Visibility.Visible;
            // The icon is a vector Path in XAML now; only the texts are set here.
            EmptyStateTitle.Text = "导入 DWG 开始翻译";
            EmptyStateDescription.Text = "支持 .dwg / .dxf，自动识别中英文文字；也可以把多个文件直接拖进窗口";
            EmptyStatePrimaryButton.Content = "选择文件";
            EmptyStatePrimaryButton.Command = vm.ImportDwgCommand;
            EmptyStatePrimaryButton.Visibility = System.Windows.Visibility.Visible;
            EmptyStateSecondaryButton.Visibility = System.Windows.Visibility.Collapsed;
        }
        else
        {
            EmptyStatePanel.Visibility = System.Windows.Visibility.Collapsed;
        }
    }

    private void DrawingFileList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm && DrawingFileList.SelectedItem is DrawingFileItem item)
        {
            vm.OpenDrawingFile(item);
            e.Handled = true;
        }
    }

    private void TranslationTextBox_LostFocus(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not TextBox textBox || textBox.DataContext is not TextEntity entity ||
            DataContext is not MainViewModel vm) return;

        textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        vm.MarkTranslationEdited(entity);
    }
}
