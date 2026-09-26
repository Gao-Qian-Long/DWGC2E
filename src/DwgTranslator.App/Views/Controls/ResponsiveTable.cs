using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace DwgTranslator.App.Views.Controls;

/// <summary>
/// 表格"窄档收列"行为（§自适应 2026-09-25 用户选择：次要列合并进一个单元格，信息不丢但更紧凑）。
///
/// 为什么是"隐藏次要列 + 显示合并列"而不是改列宽：DataGrid 在可用宽小于固定列合计时会**按比例压缩
/// 所有列**（不是裁切），于是「操作」列里的按钮会被压掉——这是用户报的"内容消失"的直接成因。
/// 压缩是 DataGrid 的默认行为，无法用列宽表达"宁可少显示一列，也不要压窄任何一列"，所以只能在
/// 档位翻转时把次要列整列收起，让合并列用两行承载同样的信息。
///
/// 声明式用法（XAML）：
///   &lt;DataGrid controls:ResponsiveTable.FullColumnsAbove="{DynamicResource Size.BreakpointTaskTable}"
///             controls:ResponsiveTable.CompactColumns="阶段,进度,最近更新"
///             controls:ResponsiveTable.MergedColumn="阶段与进度"&gt;
/// 合并列本身写在 XAML 里（默认 Visibility="Collapsed"），内容是一格多行的模板；
/// 行为切换列的 Visibility；可选的 FillColumnsBelow/FillColumns 在窄页宽时把空白尾列
/// 的空间分配给实际数据列，恢复宽档时还原声明的列宽，不碰数据与绑定。
/// 判定用表格**自身** Math.Round(ActualWidth)，档位不变时直接返回（不自激、无定时器）。
/// </summary>
public static class ResponsiveTable
{
    private static readonly ConditionalWeakTable<DataGrid, Dictionary<DataGridColumn, DataGridLength>> OriginalWidths = new();

    /// <summary>完整列所需的表格可用宽；达到它保持全部列，低于它收起次要列并显示合并列。</summary>
    public static readonly DependencyProperty FullColumnsAboveProperty = DependencyProperty.RegisterAttached(
        "FullColumnsAbove", typeof(double), typeof(ResponsiveTable), new PropertyMetadata(double.NaN, OnThresholdChanged));

    public static double GetFullColumnsAbove(DependencyObject element) => (double)element.GetValue(FullColumnsAboveProperty);
    public static void SetFullColumnsAbove(DependencyObject element, double value) => element.SetValue(FullColumnsAboveProperty, value);

    /// <summary>窄档要隐藏的列标题，逗号分隔（与 XAML 里 Header 的文本逐字一致）。</summary>
    public static readonly DependencyProperty CompactColumnsProperty = DependencyProperty.RegisterAttached(
        "CompactColumns", typeof(string), typeof(ResponsiveTable), new PropertyMetadata(string.Empty, OnThresholdChanged));

    public static string GetCompactColumns(DependencyObject element) => (string)element.GetValue(CompactColumnsProperty);
    public static void SetCompactColumns(DependencyObject element, string value) => element.SetValue(CompactColumnsProperty, value);

    /// <summary>窄档才显示的合并列标题（该列在 XAML 里默认 Collapsed）。</summary>
    public static readonly DependencyProperty MergedColumnProperty = DependencyProperty.RegisterAttached(
        "MergedColumn", typeof(string), typeof(ResponsiveTable), new PropertyMetadata(string.Empty, OnThresholdChanged));

    public static string GetMergedColumn(DependencyObject element) => (string)element.GetValue(MergedColumnProperty);
    public static void SetMergedColumn(DependencyObject element, string value) => element.SetValue(MergedColumnProperty, value);

    // At the minimum window size, give spare width to readable data columns instead of
    // an empty trailing star column. The merge threshold remains independent: the queue
    // keeps its complete column set at 1024 DIP while still filling its available width.
    public static readonly DependencyProperty FillColumnsBelowProperty = DependencyProperty.RegisterAttached(
        "FillColumnsBelow", typeof(double), typeof(ResponsiveTable), new PropertyMetadata(double.NaN, OnThresholdChanged));
    public static double GetFillColumnsBelow(DependencyObject element) => (double)element.GetValue(FillColumnsBelowProperty);
    public static void SetFillColumnsBelow(DependencyObject element, double value) => element.SetValue(FillColumnsBelowProperty, value);

    // Semicolon-separated header=star-weight pairs, for example 文件名=4;文本=1.
    public static readonly DependencyProperty FillColumnsProperty = DependencyProperty.RegisterAttached(
        "FillColumns", typeof(string), typeof(ResponsiveTable), new PropertyMetadata(string.Empty, OnThresholdChanged));
    public static string GetFillColumns(DependencyObject element) => (string)element.GetValue(FillColumnsProperty);
    public static void SetFillColumns(DependencyObject element, string value) => element.SetValue(FillColumnsProperty, value);

    private static readonly DependencyProperty FullStateProperty = DependencyProperty.RegisterAttached(
        "FullState", typeof(bool?), typeof(ResponsiveTable), new PropertyMetadata(null));
    private static readonly DependencyProperty FillStateProperty = DependencyProperty.RegisterAttached(
        "FillState", typeof(bool?), typeof(ResponsiveTable), new PropertyMetadata(null));

    private static void OnThresholdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid) return;
        grid.SizeChanged -= OnGridSizeChanged;
        grid.Loaded -= OnGridLoaded;
        if (!double.IsNaN(GetFullColumnsAbove(grid)))
        {
            grid.SizeChanged += OnGridSizeChanged;
            grid.Loaded += OnGridLoaded;
        }
        Apply(grid);
    }

    private static void OnGridLoaded(object sender, RoutedEventArgs e)
    {
        var grid = (DataGrid)sender;
        grid.SetValue(FullStateProperty, null);
        grid.SetValue(FillStateProperty, null);
        Apply(grid);
    }
    private static void OnGridSizeChanged(object sender, SizeChangedEventArgs e) => Apply((DataGrid)sender);

    private static void Apply(DataGrid grid)
    {
        var threshold = GetFullColumnsAbove(grid);
        if (double.IsNaN(threshold) || threshold <= 0) return;
        var width = Math.Round(grid.ActualWidth);
        if (width <= 0 || grid.Columns.Count == 0) return;
        var full = width >= threshold;
        if (grid.GetValue(FullStateProperty) is not bool previous || previous != full)
        {
            grid.SetValue(FullStateProperty, full);
            var compact = GetCompactColumns(grid)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var column in grid.Columns)
            {
                if (column.Header is string header && compact.Contains(header, StringComparer.Ordinal))
                    column.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
            }

            var merged = GetMergedColumn(grid);
            if (!string.IsNullOrWhiteSpace(merged))
                foreach (var column in grid.Columns.Where(c => c.Header is string header && string.Equals(header, merged, StringComparison.Ordinal)))
                    column.Visibility = full ? Visibility.Collapsed : Visibility.Visible;
        }

        var fillThreshold = GetFillColumnsBelow(grid);
        var specification = GetFillColumns(grid);
        if (double.IsNaN(fillThreshold) || string.IsNullOrWhiteSpace(specification)) return;
        var fill = width < fillThreshold;
        if (grid.GetValue(FillStateProperty) is bool previousFill && previousFill == fill) return;
        grid.SetValue(FillStateProperty, fill);

        var weights = specification.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0)
            .ToDictionary(parts => parts[0], parts => double.Parse(parts[1], CultureInfo.InvariantCulture), StringComparer.Ordinal);
        var originals = OriginalWidths.GetValue(grid, _ => new Dictionary<DataGridColumn, DataGridLength>());
        foreach (var column in grid.Columns)
        {
            if (column.Header is not string header) continue;
            if (header.Length == 0)
            {
                column.Visibility = fill ? Visibility.Collapsed : Visibility.Visible;
                continue;
            }
            if (!weights.TryGetValue(header, out var weight)) continue;
            if (!originals.ContainsKey(column)) originals.Add(column, column.Width);
            column.Width = fill
                ? new DataGridLength(weight, DataGridLengthUnitType.Star)
                : originals[column];
        }
    }
}
