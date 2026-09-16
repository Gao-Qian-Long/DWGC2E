using System.Windows;
using System.Windows.Controls;
using DwgTranslator.App.ViewModels;
using DwgTranslator.Core.Services;
namespace DwgTranslator.App.Views.Pages;
public partial class SettingsPage : UserControl
{
    public SettingsPage() { InitializeComponent(); AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_,_) => Dispatcher.BeginInvoke(new Action(UpdateSaveState)))); AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent,new RoutedEventHandler((_,_)=>Dispatcher.BeginInvoke(new Action(UpdateSaveState)))); AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent,new RoutedEventHandler((_,_)=>UpdateSaveState())); AddHandler(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent,new SelectionChangedEventHandler((_,_)=>Dispatcher.BeginInvoke(new Action(UpdateSaveState)))); IsVisibleChanged += (_,_)=>UpdateSaveState(); Loaded += (_, _) => { if (DataContext is MainViewModel vm) { EnvironmentHost.Content = new EnvironmentCheckPanel(vm); vm.ValidateSettingsInputs = () => !HasValidationError(this); } }; }
    private void NamingChanged(object sender, TextChangedEventArgs e) { Dispatcher.BeginInvoke(new Action(()=> { if(NamingPreview!=null) PreviewNaming_Click(this,new RoutedEventArgs()); })); }
    private void BrowseCad_Click(object sender, RoutedEventArgs e) { var d=new Microsoft.Win32.OpenFolderDialog(); if(d.ShowDialog()==true && DataContext is MainViewModel vm) { vm.SettingsDraft.AutoCadInstallPath=d.FolderName; DataContext=null; DataContext=vm; UpdateSaveState(); } }
    private void BrowsePlugin_Click(object sender, RoutedEventArgs e) { var d=new Microsoft.Win32.OpenFileDialog { Filter="CAD 插件 (*.dll)|*.dll" }; if(d.ShowDialog()==true && DataContext is MainViewModel vm) { vm.SettingsDraft.CadPluginPath=d.FileName; DataContext=null; DataContext=vm; UpdateSaveState(); } }
    private void UpdateSaveState() { if(DataContext is MainViewModel vm && SaveSettingsButton != null) { SaveSettingsButton.IsEnabled=DiscardSettingsButton.IsEnabled=vm.HasUnsavedSettings || HasValidationError(this); } }
    private void BrowseOutput_Click(object sender, RoutedEventArgs e) { var d = new Microsoft.Win32.OpenFolderDialog(); if (d.ShowDialog() == true && DataContext is MainViewModel vm) { vm.SettingsDraft.ExportDirectory = d.FolderName; GetBindingExpression(DataContextProperty)?.UpdateTarget(); DataContext = null; DataContext = vm; UpdateSaveState(); } }
    private void OpenLogs_Click(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel vm) vm.OpenFolder(vm.LogDirectory); }
    private void PreviewNaming_Click(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel vm) { try { NamingPreview.Text = new OutputPathResolver(vm.SettingsDraft).RenderFileName("总图.dwg", vm.CurrentTargetLang, DateTime.Now); } catch { NamingPreview.Text = "命名规则无效。"; } } }
    private void Save_Click(object sender, RoutedEventArgs e) { if (HasValidationError(this)) { if (DataContext is MainViewModel vm) vm.SettingsFeedback = "请修正标红的输入项后保存。"; return; } if (DataContext is MainViewModel model) { model.SaveSettingsPage(); UpdateSaveState(); } }
    private void Discard_Click(object sender, RoutedEventArgs e) => Dispatcher.BeginInvoke(new Action(UpdateSaveState));
    private static bool HasValidationError(DependencyObject root) { if (Validation.GetHasError(root)) return true; for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++) if (HasValidationError(System.Windows.Media.VisualTreeHelper.GetChild(root, i))) return true; return false; }
}