using System.Windows;
using System.Windows.Controls;

namespace DwgTranslator.App.Views.Controls;

/// <summary>Two child form row: 220 DIP label, flexible editor, stacked in compact windows.</summary>
public sealed class SettingsRow : Grid
{
    public SettingsRow()
    {
        Margin = new Thickness(0, 0, 0, 12);
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
            // §L14 标签槽可能是 StackPanel（标题+说明文字）而不只是 TextBlock——
            // 之前只给 TextBlock 加右边距，StackPanel 组会把 220 DIP 标签列填满、
            // 说明文字顶到右侧开关上（用户报告「文字干涉，显示不清晰」）。
            if (Children[i] is not FrameworkElement slot) continue;
            if (slot is TextBlock label) label.TextWrapping = TextWrapping.Wrap;
            if (compact || i == 0) slot.Margin = new Thickness(0, 0, 12, compact ? 8 : 0);
        }
    }
}
