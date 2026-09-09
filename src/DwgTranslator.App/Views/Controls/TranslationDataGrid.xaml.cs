using System.Windows.Controls;
using DwgTranslator.App.ViewModels;

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
        if (vm.TotalCount == 0)
        {
            EmptyStatePanel.Visibility = System.Windows.Visibility.Visible;
            EmptyStateIcon.Text = "\U0001F4C2";
            EmptyStateTitle.Text = "导入 DWG 开始翻译";
            EmptyStateDescription.Text = "支持 .dwg / .dxf，自动识别中英文文字";
            EmptyStatePrimaryButton.Content = "选择文件";
            EmptyStatePrimaryButton.Command = vm.ImportDwgCommand;
            EmptyStatePrimaryButton.Visibility = System.Windows.Visibility.Visible;
            EmptyStateSecondaryButton.Visibility = System.Windows.Visibility.Collapsed;
        }
        else if (vm.TranslatedCount == 0 && !vm.IsProcessing)
        {
            EmptyStatePanel.Visibility = System.Windows.Visibility.Visible;
            EmptyStateIcon.Text = "\U0001F4DD";
            EmptyStateTitle.Text = $"共 {vm.TotalCount} 条文字待翻译";
            EmptyStateDescription.Text = "点击翻译按钮开始自动翻译";
            EmptyStatePrimaryButton.Content = "\u25B6 开始翻译";
            EmptyStatePrimaryButton.Command = vm.TranslateCommand;
            EmptyStatePrimaryButton.Visibility = System.Windows.Visibility.Visible;
            EmptyStateSecondaryButton.Visibility = System.Windows.Visibility.Collapsed;
        }
        else
        {
            EmptyStatePanel.Visibility = System.Windows.Visibility.Collapsed;
        }
    }
}