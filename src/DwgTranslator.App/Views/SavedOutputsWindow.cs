using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
namespace DwgTranslator.App.Views;
public sealed class SavedOutputsWindow : Window
{
    public SavedOutputsWindow(string[] paths)
    {
        Title = "翻译结果已保存";
        // §弹窗排版：高度随结果条数收敛（此前固定 420，只有 1 份结果时列表下方留大片空白，与云端术语窗同一处理）。
        // 非客户区≈40、固定 chrome≈140（标题 43 + 按钮行 48 + 外边距 48）、每行≈44。
        Width = 640; Height = Math.Clamp(40 + 140 + paths.Length * 44, 300, 420); MinWidth = 460; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        // §自适应（2026-09-25）：MinWidth=460 / 固定 640 宽在窄屏上会被工作区截住并压坏内容，
        // 收敛尺寸上下限，溢出交给下方 ListBox 自己的横向/纵向滚动承担。
        Controls.DialogShell.Constrain(this);
        Background = (Brush)Application.Current.FindResource("Brush.Surface");
        var panel = new DockPanel { Margin = new Thickness(24) };
        var heading = new TextBlock { Text = $"已保存 {paths.Length} 份图纸", FontSize = 20, Margin = new Thickness(0, 0, 0, 16) };
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(actions, Dock.Bottom); panel.Children.Add(actions);
        // §弹窗排版：路径此前被列表项硬截断（右端连省略号都没有，文件名正好被切掉）。
        // 改为两行：文件名（完整可见，本列表最需要的信息）+ 所在目录（超宽时 CharacterEllipsis），整行悬停给出完整路径。
        var list = new ListBox { ItemsSource = paths.Select(p => new FileInfo(p)).ToArray(), SelectedIndex = 0, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        list.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(FileInfo.Name)));
        name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        name.SetValue(TextBlock.ForegroundProperty, (Brush)Application.Current.FindResource("Brush.TextPrimary"));
        var folder = new FrameworkElementFactory(typeof(TextBlock));
        folder.SetBinding(TextBlock.TextProperty, new Binding(nameof(FileInfo.DirectoryName)));
        folder.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        folder.SetValue(TextBlock.MarginProperty, new Thickness(0, 2, 0, 0));
        folder.SetValue(TextBlock.ForegroundProperty, (Brush)Application.Current.FindResource("Brush.TextSecondary"));
        var item = new FrameworkElementFactory(typeof(StackPanel));
        item.SetValue(FrameworkElement.MarginProperty, new Thickness(2));
        item.AppendChild(name); item.AppendChild(folder);
        list.ItemTemplate = new DataTemplate { VisualTree = item };
        var row = new Style(typeof(ListBoxItem));
        row.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(nameof(FileInfo.FullName))));
        list.ItemContainerStyle = row;
        panel.Children.Add(list);
        void Add(string label, Action action)
        {
            var button = new Button { Content = label, Margin = new Thickness(4), Style = (Style)Application.Current.FindResource("Button.Secondary") };
            button.Click += (_, _) => {
                try { action(); }
                catch (Exception ex) { Services.ToastService.Error("操作未完成：" + ex.Message); }
            };
            actions.Children.Add(button);
        }
        string Selected() => (list.SelectedItem as FileInfo)?.FullName ?? paths[0];
        Add("打开文件", () => {
            var path = Selected();
            if (!File.Exists(path)) throw new IOException("文件已移动或删除。");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        });
        Add("打开文件夹", () => Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + Selected() + "\"") { UseShellExecute = true }));
        Add("复制路径", () => { Clipboard.SetText(Selected()); Services.ToastService.Success("已复制文件路径。"); });
        Add("关闭", Close);
        Content = panel;
    }
}
