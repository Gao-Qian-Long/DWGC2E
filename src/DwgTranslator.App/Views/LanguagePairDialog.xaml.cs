using DwgTranslator.Core.Models;
using System.Windows;
using System.Windows.Controls;

namespace DwgTranslator.App.Views;

/// <summary>
/// Chooses the translation direction. The pair used to be a single ZH↔EN toggle, so this dialog is
/// also what makes the other languages reachable: the two pickers list the full catalog and the
/// preview shows the exact direction that will be applied.
/// </summary>
public partial class LanguagePairDialog : Window
{
    private readonly IReadOnlyList<TranslationLanguage> _languages = TranslationLanguages.All;
    private bool _initializing = true;

    /// <summary>Selected source language code; only meaningful when the dialog returned true.</summary>
    public string SourceCode { get; private set; } = "ZH";

    /// <summary>Selected target language code; only meaningful when the dialog returned true.</summary>
    public string TargetCode { get; private set; } = "EN";

    public LanguagePairDialog(string sourceCode, string targetCode)
    {
        InitializeComponent();

        SourceCode = TranslationLanguages.Normalize(sourceCode);
        TargetCode = TranslationLanguages.Normalize(targetCode);

        SourceCombo.ItemsSource = _languages;
        TargetCombo.ItemsSource = _languages;
        SourceCombo.SelectedValue = SourceCode;
        TargetCombo.SelectedValue = TargetCode;

        BuildPresets();
        _initializing = false;
        UpdatePreview();
    }

    private void BuildPresets()
    {
        var presets = new (string Source, string Target, string Label)[]
        {
            ("ZH", "EN", "中文 → English"),
            ("EN", "ZH", "English → 中文"),
            ("ZH", "JA", "中文 → 日本語"),
            ("ZH", "KO", "中文 → 한국어"),
            ("ZH", "RU", "中文 → Русский"),
            ("ZH", "DE", "中文 → Deutsch"),
            ("ZH", "ES", "中文 → Español"),
            ("ZH", "VI", "中文 → Tiếng Việt")
        };

        foreach (var (source, target, label) in presets)
        {
            var button = new Button
            {
                Content = label,
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(12, 6, 12, 6),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            // Reuse the toolbar button look so presets match the rest of the application.
            if (TryFindResource("ToolButton") is Style toolButton) button.Style = toolButton;
            var capturedSource = source;
            var capturedTarget = target;
            button.Click += (_, _) =>
            {
                SourceCombo.SelectedValue = capturedSource;
                TargetCombo.SelectedValue = capturedTarget;
            };
            PresetPanel.Children.Add(button);
        }
    }

    private void PairSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePreview();

    private void Swap_Click(object sender, RoutedEventArgs e)
    {
        var source = SourceCombo.SelectedValue as string;
        var target = TargetCombo.SelectedValue as string;
        SourceCombo.SelectedValue = target;
        TargetCombo.SelectedValue = source;
    }

    private void UpdatePreview()
    {
        if (_initializing) return;

        var source = SourceCombo.SelectedValue as string ?? SourceCode;
        var target = TargetCombo.SelectedValue as string ?? TargetCode;
        var from = TranslationLanguages.Find(source);
        var to = TranslationLanguages.Find(target);

        // Native name for the source (that is what the drawing is written in) and the English name
        // for the target, which keeps the line short enough for the card.
        PreviewText.Text = $"{from?.NativeName ?? source}  →  {to?.EnglishName ?? target}";

        var same = string.Equals(source, target, StringComparison.OrdinalIgnoreCase);
        ApplyButton.IsEnabled = !same;
        PreviewBorder.Background = same
            ? (System.Windows.Media.Brush)FindResource("Brush.DangerLight")
            : (System.Windows.Media.Brush)FindResource("Brush.PrimaryLight");
        if (same) PreviewText.Text = "原文语言与目标语言相同，请选择两种不同的语言。";
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var source = SourceCombo.SelectedValue as string;
        var target = TargetCombo.SelectedValue as string;
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target) ||
            string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            return;

        SourceCode = source;
        TargetCode = target;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
