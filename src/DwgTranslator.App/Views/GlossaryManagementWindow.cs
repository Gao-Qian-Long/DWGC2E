using System.Windows;
using System.Windows.Controls;
using DwgTranslator.App.ViewModels;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using DwgTranslator.App.Views.Controls;
namespace DwgTranslator.App.Views;
public static class GlossaryManagementWindow
{
    private static Window CreateWindow(string title, UIElement body, UIElement actions)
    {
        var window = new Window { Title = title, Content = new DialogShell(title, body, actions) { ReserveFeedbackBand = true }, Width = 480,
            SizeToContent = SizeToContent.Height, Owner = Application.Current.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.CanResize,
            MinWidth = 360, MinHeight = 280 };
        window.SetResourceReference(Window.BackgroundProperty, "Brush.Surface");
        DialogShell.Constrain(window);
        return window;
    }
    public static void ShowCategories(MainViewModel vm)
    {
        var panel = new StackPanel(); var actions = new WrapPanel { Margin = new Thickness(0,16,0,0) };
        var window = CreateWindow("分类管理", panel, actions);
        panel.Children.Add(new TextBlock { Text = "现有分类", Margin = new Thickness(0,0,0,8) });
        var list = new ComboBox { ItemsSource = vm.TermCategories.ToList(), SelectedIndex = 0 }; panel.Children.Add(list);
        panel.Children.Add(new TextBlock { Text = "新分类名称", Margin = new Thickness(0,16,0,8) });
        var name = new TextBox { MaxLength = 128, ToolTip = "输入新分类名称（最多128字）" }; panel.Children.Add(name);
        System.Windows.Automation.AutomationProperties.SetName(name, "新分类名称");
        var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,12,0,0) }; panel.Children.Add(feedback);
        foreach (var action in new[] { "新建", "重命名", "删除并迁回默认分类" })
        {
            // §弹窗排版：显式指定按钮权重——无 Style 时会落到隐式 Button.Tertiary（无底色无边框），
            // 反而比被禁用的灰底按钮更弱，主次颠倒。
            var button = new Button { Content = action, Margin = new Thickness(0,0,8,8) };
            button.SetResourceReference(FrameworkElement.StyleProperty,
                action == "新建" ? "Button.Primary" : action.StartsWith("删除") ? "Button.SecondaryDanger" : "Button.Secondary");
            actions.Children.Add(button);
            void UpdateAllowed() => button.IsEnabled = vm.CanEditWorkspace && (action == "新建" || list.SelectedItem?.ToString() is { } selected && selected != EffectiveGlossary.DefaultCategory);
            list.SelectionChanged += (_, _) => UpdateAllowed(); UpdateAllowed();
            button.Click += async (_, _) =>
            {
                var old = list.SelectedItem?.ToString(); var delete = action.StartsWith("删除");
                if (delete && PromptDialog.Show($"将 {vm.TermDraft.Count(t => t.SourceKind == GlossarySource.User && t.Category == old)} 条用户术语迁回默认分类。词条不会删除，云端不自动修改。", "删除分类", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                await vm.ChangeTermCategoryAsync(action == "新建" ? null : old, name.Text, delete);
                feedback.Text = vm.TermFeedback; list.ItemsSource = vm.TermCategories.ToList(); list.SelectedIndex = 0;
            };
        }
        window.ShowDialog();
    }
    public static (string Source, string Target)? ChooseDirection()
    {
        (string, string)? result = null; var body = new StackPanel(); var actions = new WrapPanel { Margin = new Thickness(0,16,0,0) };
        var window = CreateWindow("指定所选用户术语的适用方向", body, actions);
        var source = new ComboBox { ItemsSource = TranslationLanguages.All, DisplayMemberPath = "DisplayName", SelectedIndex = 0 };
        var target = new ComboBox { ItemsSource = TranslationLanguages.All, DisplayMemberPath = "DisplayName", SelectedIndex = 2 };
        body.Children.Add(new TextBlock { Text = "源语言", Margin = new Thickness(0,0,0,8) }); body.Children.Add(source);
        body.Children.Add(new TextBlock { Text = "目标语言", Margin = new Thickness(0,16,0,8) }); body.Children.Add(target);
        // §弹窗排版：补回「取消」——此前只有保存按钮，用户除了标题栏 × 没有退出路径（Esc 由 DialogShell.Constrain 兜底）。
        // 顺序与 LanguagePairDialog.xaml:114-121 一致：取消在左、主操作在右。
        var cancel = new Button { Content = "取消", MinWidth = 92, IsCancel = true }; cancel.SetResourceReference(FrameworkElement.StyleProperty, "DialogCancelButton"); actions.Children.Add(cancel);
        cancel.Click += (_, _) => window.Close();
        var save = new Button { Content = "保存到本机", MinWidth = 92, IsDefault = true, Margin = new Thickness(8, 0, 0, 0) }; save.SetResourceReference(FrameworkElement.StyleProperty, "DialogSaveButton"); actions.Children.Add(save);
        save.Click += (_, _) => { var s = (TranslationLanguage)source.SelectedItem; var t = (TranslationLanguage)target.SelectedItem; if (s.Code == t.Code) return; result = (s.Code, t.Code); window.DialogResult = true; };
        window.ShowDialog(); return result;
    }
}
