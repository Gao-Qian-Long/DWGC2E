using DwgTranslator.Core.Models;
using DwgTranslator.Core.Resources;
using DwgTranslator.Core.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

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

        GlossaryGrid.ItemsSource = _editableEntries;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var list = _editableEntries
                .Where(x => !string.IsNullOrWhiteSpace(x.Source) && !string.IsNullOrWhiteSpace(x.Target))
                .ToList();

            // Save to AppData glossary file
            var targetPath = Path.Combine(App.AppDataDir, "glossaries", "mechanical_zh_en.json");
            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            await File.WriteAllTextAsync(targetPath, json);

            // Reload into service
            await _glossaryService.LoadGlossaryAsync(targetPath);

            SavedEntries = list;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save glossary: {ex}");
            MessageBox.Show(Strings.Get("GlossarySaveFailed"), Strings.Get("MsgTitleError"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
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



