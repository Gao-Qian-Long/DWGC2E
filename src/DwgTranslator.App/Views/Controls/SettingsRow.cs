using System.Windows;
using System.Windows.Controls;

namespace DwgTranslator.App.Views.Controls;

/// <summary>Two child form row: 220 DIP label, flexible editor, stacked in compact windows.</summary>
public sealed class SettingsRow : Grid
{
    public SettingsRow()
    {
        Margin = new Thickness(0, 0, 0, 16);
        Loaded += (_, _) => Reflow();
        SizeChanged += (_, _) => Reflow();
    }
    private void Reflow()
    {
        var compact = ResponsiveLayout.GetIsCompact(this);
        ColumnDefinitions.Clear(); RowDefinitions.Clear();
        ColumnDefinitions.Add(new ColumnDefinition { Width = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(220) });
        if (!compact) ColumnDefinitions.Add(new ColumnDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (compact) RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < Children.Count; i++)
        {
            SetColumn(Children[i], compact ? 0 : Math.Min(i, 1));
            SetRow(Children[i], compact ? Math.Min(i, 1) : 0);
            if (Children[i] is TextBlock label) { label.TextWrapping = TextWrapping.Wrap; label.Margin = new Thickness(0, 0, 12, compact ? 8 : 0); }
        }
    }
}
