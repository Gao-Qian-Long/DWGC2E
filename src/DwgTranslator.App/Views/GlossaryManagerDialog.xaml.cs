using DwgTranslator.Core.Models;
using DwgTranslator.Core.Resources;
using DwgTranslator.Core.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using System.Windows.Data;

namespace DwgTranslator.App.Views;

/// <summary>
/// Dialog for viewing and editing glossary entries.
/// Extracted from MainViewModel.OpenGlossaryManager() to separate UI from logic.
/// </summary>
public partial class GlossaryManagerDialog : Window
{
    private readonly IGlossaryService _glossaryService;
    private readonly ObservableCollection<GlossaryEntry> _editableEntries;
    private readonly string _titleSuffix;
    private bool _isSaving;
    private string _selectedFolder = "全部术语";
    private ICollectionView? _view;
    private readonly HashSet<string> _folders = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxEntries = 1000;

    /// <summary>
    /// The saved entries if the user clicked Save; null if cancelled.
    /// </summary>
    public List<GlossaryEntry>? SavedEntries { get; private set; }

    public GlossaryManagerDialog(
        IGlossaryService glossaryService,
        IEnumerable<GlossaryEntry> currentEntries,
        string titleSuffix)
    {
        InitializeComponent();

        _glossaryService = glossaryService;
        _titleSuffix = titleSuffix;
        TitleText.Text = Strings.Get("GlossaryManagerTitleSuffix", titleSuffix);

        // Clone entries for editing so changes are not applied until Save
        _editableEntries = new ObservableCollection<GlossaryEntry>();
        foreach (var e in currentEntries)
        {
            _editableEntries.Add(new GlossaryEntry
            {
                Source = e.Source,
                Target = e.Target,
                Category = e.Category
            });
        }

        _view = CollectionViewSource.GetDefaultView(_editableEntries);
        _view.Filter = FilterEntry;
        GlossaryGrid.ItemsSource = _view;
        RefreshFolders();
    }

    private bool FilterEntry(object obj) => _selectedFolder == "全部术语" || (obj is GlossaryEntry e && string.Equals(string.IsNullOrWhiteSpace(e.Folder) ? "未分类" : e.Folder, _selectedFolder, StringComparison.OrdinalIgnoreCase));

    private void RefreshFolders()
    {
        FolderList.Items.Clear();
        FolderList.Items.Add("全部术语");
        foreach (var folder in _folders.Concat(_editableEntries.Select(x => x.Folder)).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x)) FolderList.Items.Add(folder);
        FolderList.SelectedItem = _selectedFolder;
    }

    private void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FolderList.SelectedItem is string folder) { _selectedFolder = folder; _view?.Refresh(); }
    }

    private void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptForFolderName();
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        if (_folders.Contains(name)) { MessageBox.Show("该文件夹已存在。", "提示", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        _folders.Add(name);
        _selectedFolder = name; RefreshFolders(); _view?.Refresh(); AddRow_Click(sender, e);
    }

    private static string? PromptForFolderName()
    {
        var box = new Window { Title = "新建术语文件夹", Width = 360, Height = 150, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, Owner = Application.Current.MainWindow };
        var panel = new StackPanel { Margin = new Thickness(16) }; panel.Children.Add(new TextBlock { Text = "文件夹名称", Margin = new Thickness(0,0,0,6) });
        var input = new TextBox { Height = 28 }; panel.Children.Add(input); var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,10,0,0) };
        string? result = null; var ok = new Button { Content = "创建", Width = 72, IsDefault = true, Margin = new Thickness(0,0,8,0) }; ok.Click += (_,__) => { result = input.Text; box.DialogResult = true; }; var cancel = new Button { Content = "取消", Width = 72, IsCancel = true }; buttons.Children.Add(ok); buttons.Children.Add(cancel); panel.Children.Add(buttons); box.Content = panel; box.ShowDialog(); return result;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_isSaving) return;
        GlossaryGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        GlossaryGrid.CommitEdit(DataGridEditingUnit.Row, true);
        _isSaving = true;
        try
        {
            var validCount = _editableEntries.Count(x => !string.IsNullOrWhiteSpace(x.Source) && !string.IsNullOrWhiteSpace(x.Target));
            if (validCount > MaxEntries)
            {
                MessageBox.Show($"术语条目最多 {MaxEntries} 条，请删除多余条目后再保存。", "数量限制", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var list = _editableEntries
                .Where(x => !string.IsNullOrWhiteSpace(x.Source) && !string.IsNullOrWhiteSpace(x.Target))
                .Select(x => new GlossaryEntry { Source = x.Source.Trim(), Target = x.Target.Trim(), Category = x.Category?.Trim() ?? string.Empty, Folder = x.Folder?.Trim() ?? string.Empty })
                .GroupBy(x => x.Source, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First()).ToList();
            var targetPath = Path.Combine(App.AppDataDir, "glossaries", "mechanical_zh_en.json");
            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            await File.WriteAllTextAsync(targetPath, json);
            await _glossaryService.LoadGlossaryAsync(targetPath);
            SavedEntries = list;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save glossary: {ex}");
            MessageBox.Show(Strings.Get("GlossarySaveFailed"), Strings.Get("MsgTitleError"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _isSaving = false; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_isSaving) return;
        GlossaryGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        GlossaryGrid.CommitEdit(DataGridEditingUnit.Row, true);
        DialogResult = false;
    }
    /// <summary>
    /// Add a new empty glossary entry row to the editable grid.
    /// </summary>
    private void AddRow_Click(object sender, RoutedEventArgs e)
    {
        _editableEntries.Add(new GlossaryEntry
        {
            Source = string.Empty,
            Target = string.Empty,
            Category = string.Empty
        });

        // Scroll to the new row and begin editing
        var idx = _editableEntries.Count - 1;
        GlossaryGrid.SelectedIndex = idx;
        GlossaryGrid.ScrollIntoView(GlossaryGrid.SelectedItem);
        GlossaryGrid.Focus();
        if (idx >= 0)
            GlossaryGrid.CurrentCell = new DataGridCellInfo(
                GlossaryGrid.Items[idx], GlossaryGrid.Columns[0]);
        GlossaryGrid.BeginEdit();
    }

    /// <summary>
    /// Delete selected glossary entry rows with confirmation.
    /// </summary>
    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = GlossaryGrid.SelectedItems.Cast<GlossaryEntry>().ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("请先选择要删除的术语条目。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"确定要删除选中的 {selected.Count} 条术语吗？",
            Strings.Get("MsgTitleConfirm"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        foreach (var item in selected.ToList())
            _editableEntries.Remove(item);
    }
}






