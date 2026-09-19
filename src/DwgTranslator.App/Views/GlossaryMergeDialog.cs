using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DwgTranslator.Core.Api;
using DwgTranslator.Core.Services;

namespace DwgTranslator.App.Views;

/// <summary>Explicit row-level selection; never writes cloud or local data.</summary>
public sealed class GlossaryMergeDialog : UserControl
{
    private readonly TaskCompletionSource<IReadOnlyDictionary<int, GlossaryMergeChoice>?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<IReadOnlyDictionary<int, GlossaryMergeChoice>?> Result => completion.Task;
    public IReadOnlyDictionary<int, GlossaryMergeChoice>? Choices { get; private set; }
    public void Close() => completion.TrySetResult(Choices);
    public GlossaryMergeDialog(IReadOnlyList<GlossaryMergeRow> rows, Func<bool> active)
    {
        Background = (Brush)Application.Current.FindResource("Brush.Surface");
        Foreground = (Brush)Application.Current.FindResource("Brush.TextPrimary");
        // §弹窗排版：摘要 / 正文 / 操作三区改为 Grid。正文区高度按可用空间收敛——
        // 行数少时按钮紧跟卡片（不再被 Dock.Bottom 顶到底部而留出大片空白），行数多时列表在限高内滚动。
        var layout = new Grid { Margin = new Thickness(20) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        var summary = new TextBlock { Text = $"共 {rows.Count} 组 · {rows.Count(x => x.HasConflict)} 组冲突需选择\n云端词库由所有语言方向共用。确认后才保存云端；取消保留当前草稿。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
        layout.Children.Add(summary);
        var body = new Grid { VerticalAlignment = VerticalAlignment.Top };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(body, 1); layout.Children.Add(body);
        var footer = new StackPanel(); Grid.SetRow(footer, 1); body.Children.Add(footer);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) }; footer.Children.Add(error);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; footer.Children.Add(actions);
        var cancel = new Button { Content = "取消，保留草稿", Margin = new Thickness(8) };
        cancel.SetResourceReference(FrameworkElement.StyleProperty, "DialogCancelButton");
        cancel.Click += (_, _) => Close(); actions.Children.Add(cancel);
        // 主操作必须走 Button.Primary：先前无 Style 落到隐式 Button.Tertiary（无边框无底色），主次颠倒。
        var confirm = new Button { Content = "确认合并并保存云端", Margin = new Thickness(8) };
        confirm.SetResourceReference(FrameworkElement.StyleProperty, "DialogSaveButton");
        actions.Children.Add(confirm);
        var list = new StackPanel();
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 200 };
        Grid.SetRow(scroll, 0); body.Children.Add(scroll);
        var selections = new Dictionary<int, GlossaryMergeChoice>();
        for (var i = 0; i < rows.Count; i++)
        {
            var index = i; var row = rows[i];
            var box = new StackPanel { Margin = new Thickness(10) };
            box.Children.Add(new TextBlock { Text = $"{i + 1}. {(row.HasConflict ? "需要选择" : "已建议，可更改")}", FontWeight = FontWeights.SemiBold });
            var grid = new Grid { Margin = new Thickness(0, 8, 0, 8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition());
            TextBlock Text(string heading, CloudGlossaryEntry? entry) => new()
            { Text = heading + "\n" + Describe(entry), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0) };
            grid.Children.Add(Text("本机", row.Local)); var remote = Text("最新云端", row.Remote); Grid.SetColumn(remote, 1); grid.Children.Add(remote); box.Children.Add(grid);
            var choice = new ComboBox { MinWidth = 180, HorizontalAlignment = HorizontalAlignment.Left, SelectedIndex = -1 };
            choice.Items.Add("使用本机（不存在则删除）"); choice.Items.Add("使用云端（不存在则删除）");
            choice.SelectionChanged += (_, _) => { if (choice.SelectedIndex >= 0) selections[index] = (GlossaryMergeChoice)choice.SelectedIndex; };
            if (row.SuggestedChoice is { } suggested) choice.SelectedIndex = (int)suggested;
            box.Children.Add(choice);
            list.Children.Add(new Border { Child = box, BorderThickness = new Thickness(1), BorderBrush = (Brush)Application.Current.FindResource("Brush.BorderLight"), Margin = new Thickness(0, 0, 0, 8) });
        }
        confirm.Click += (_, _) =>
        {
            if (!active()) { Close(); return; }
            try { CloudGlossaryMerge.Resolve(rows, selections); Choices = new Dictionary<int, GlossaryMergeChoice>(selections); Close(); }
            catch (InvalidOperationException ex) { error.Text = ex.Message; }
        };
        // 正文区上限 = 弹窗可用高度 − 摘要行 − 操作区行；随尺寸/反馈文案变化重算，保证操作按钮永不被裁。
        void FitScrollRegion()
        {
            var available = layout.ActualHeight - layout.RowDefinitions[0].ActualHeight - body.RowDefinitions[1].ActualHeight;
            var cap = Math.Max(80, available - 2);
            if (Math.Abs(scroll.MaxHeight - cap) > 0.5) scroll.MaxHeight = cap;
        }
        layout.SizeChanged += (_, _) => FitScrollRegion();
        footer.SizeChanged += (_, _) => FitScrollRegion();
        Loaded += (_, _) => FitScrollRegion();
        Content = layout;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => { if (!active()) Close(); };
        Loaded += (_, _) => { cancel.Focus(); timer.Start(); };
        Unloaded += (_, _) => { timer.Stop(); Close(); };
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { Close(); e.Handled = true; } };
        _ = Result.ContinueWith(_ => Dispatcher.BeginInvoke(new Action(timer.Stop)));
    }

    private static string Describe(CloudGlossaryEntry? entry) => entry == null ? "不存在（删除）" :
        $"{entry.Source} → {entry.Target}\n备注：{entry.Note}\n分类：{entry.Category} · 文件夹：{entry.Folder}\n状态：{(entry.Enabled ? "启用" : "停用")}";

    public static async Task<IReadOnlyDictionary<int, GlossaryMergeChoice>?> ShowAsync(IReadOnlyList<GlossaryMergeRow> rows, Func<bool> active)
    {
        static T? Find<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent is T match) return match;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                if (Find<T>(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
            return null;
        }
        var page = Find<Pages.GlossaryPage>(Application.Current.MainWindow);
        if (page == null) return null;
        var host = (Border)page.FindName("MergeDrawer");
        var drawer = new GlossaryMergeDialog(rows, active);
        host.Child = drawer; host.Visibility = Visibility.Visible;
        try { return await drawer.Result; }
        finally { host.Child = null; host.Visibility = Visibility.Collapsed; }
    }
}
