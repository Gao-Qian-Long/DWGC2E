using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;

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
        TitleText.Text = $"术语库管理 — {titleSuffix}";

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
            MessageBox.Show($"保存术语库失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
