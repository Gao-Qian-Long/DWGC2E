using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace DwgTranslator.App.Views;
public sealed class SavedOutputsWindow : Window
{
    public SavedOutputsWindow(string[] paths)
    {
        Title = "翻译结果已保存";
        Width = 640; Height = 420; MinWidth = 460; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("Brush.Surface");
        var panel = new DockPanel { Margin = new Thickness(24) };
        var heading = new TextBlock { Text = $"已保存 {paths.Length} 份图纸", FontSize = 20, Margin = new Thickness(0, 0, 0, 16) };
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(actions, Dock.Bottom); panel.Children.Add(actions);
        var list = new ListBox { ItemsSource = paths, SelectedIndex = 0, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        list.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
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
        string Selected() => list.SelectedItem as string ?? paths[0];
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
