using System.Windows;
using System.Windows.Controls;
using DwgTranslator.App.ViewModels;
using DwgTranslator.Core.Services;
namespace DwgTranslator.App.Views.Pages;
public partial class SettingsPage : UserControl
{
    public SettingsPage() { InitializeComponent(); Loaded += (_, _) => { if (DataContext is MainViewModel vm) { EnvironmentHost.Content = new EnvironmentCheckPanel(vm); vm.ValidateSettingsInputs = () => !HasValidationError(this); } }; }
    private void BrowseOutput_Click(object sender, RoutedEventArgs e) { var d = new Microsoft.Win32.OpenFolderDialog(); if (d.ShowDialog() == true && DataContext is MainViewModel vm) { vm.SettingsDraft.ExportDirectory = d.FolderName; GetBindingExpression(DataContextProperty)?.UpdateTarget(); DataContext = null; DataContext = vm; } }
    private void OpenLogs_Click(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel vm) vm.OpenFolder(vm.LogDirectory); }
    private void PreviewNaming_Click(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel vm) { try { NamingPreview.Text = new OutputPathResolver(vm.SettingsDraft).RenderFileName("总图.dwg", vm.CurrentTargetLang, DateTime.Now); } catch { NamingPreview.Text = "命名规则无效。"; } } }
    private void Save_Click(object sender, RoutedEventArgs e) { if (HasValidationError(this)) { if (DataContext is MainViewModel vm) vm.SettingsFeedback = "请修正标红的输入项后保存。"; return; } if (DataContext is MainViewModel model) model.SaveSettingsPage(); }
    private static bool HasValidationError(DependencyObject root) { if (Validation.GetHasError(root)) return true; for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++) if (HasValidationError(System.Windows.Media.VisualTreeHelper.GetChild(root, i))) return true; return false; }
}