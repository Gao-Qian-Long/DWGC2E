using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Threading;
using DwgTranslator.App.ViewModels;
using DwgTranslator.App.Views;
using DwgTranslator.App.Views.Pages;
using DwgTranslator.Core.Api;

namespace UiSmoke;
public sealed partial class SmokeApp
{
    private async Task VerifyMembershipLayoutAsync(MainWindow window, MainViewModel vm, AccountPage page)
    {
        var subscription = vm.OnlineSubscription;
        var usage = vm.OnlineUsage;
        var details = (Grid)page.FindName("MembershipDetails");
        var devices = (Border)page.FindName("DevicesCard");
        var plans = (Border)page.FindName("PlansCard");
        var account = (Button)window.FindName("AccountMenuButton");
        try
        {
            foreach (var tier in new[] { "free", "pro", "max", "go" })
            {
                vm.OnlineSubscription = new SubscriptionInfo { PlanName = tier, ExpiresAt = tier == "free" ? null : DateTime.UtcNow.AddDays(30) };
                vm.OnlineUsage = new UsageInfo { MonthlyQuota = 1008000000, Used = 3791 };
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(window.FindName("AccountAvatarCrown") == null, "no crown for tier " + tier);
                Check(account.ActualHeight == 60, "account entry has stable hit target " + tier);
                Check(((Button)page.FindName("MembershipPlansButton")).IsEnabled, "membership catalog entry enabled " + tier);
                Check(vm.OnlineQuotaText.Contains("1,007,996,209"), "quota text follows refreshed usage " + tier);
                Capture(window, "membership-redesign-" + tier);
            }
            var accountScroll = (ScrollViewer)page.FindName("AccountScroll");
            // §D11 用户要求「不要这个加载条」：AccountRefreshIndicator 整条已从 AccountPage.xaml 删除，
            // 因此这里不能再 FindName 取它（会拿到 null 并 NRE），改为断言该元素确实不存在。
            var refreshIndicator = page.FindName("AccountRefreshIndicator");
            var refreshButton = FindVisuals<Button>(page).Single(b => Equals(b.Content, "刷新权益"));
            await Dispatcher.InvokeAsync(() => { page.UpdateLayout(); accountScroll.UpdateLayout(); }, DispatcherPriority.ApplicationIdle);
            var content = (FrameworkElement)accountScroll.Content;
            var contentHeight = content.ActualHeight;
            vm.IsAccountRefreshing = true;
            await Dispatcher.InvokeAsync(() => { page.UpdateLayout(); accountScroll.UpdateLayout(); }, DispatcherPriority.ApplicationIdle);
            Check(refreshIndicator == null, "account refresh strip is removed from the page entirely");
            // 负向有效性护栏：证明上面的 null 是"元素不存在"而不是"FindName 名字查不动"。
            // 同一次 FindName 调用对仍然存在的同级元素必须返回非 null —— 否则该断言恒真、无意义。
            Check(page.FindName("AccountScroll") != null && page.FindName("QuotaRingChart") != null,
                "name lookup still resolves surviving account elements (removal check is not vacuous)");
            Check(Math.Abs(content.ActualHeight - contentHeight) < 0.5, "account refresh does not change content layout height");
            Check(Equals(refreshButton.Content, "同步中…") && !refreshButton.IsEnabled, "account refresh button exposes busy state");
            // §D11 负向证据：用户报的是「刷新时页面顶端那条琥珀加载条」。只有把刷新态渲染出来截图，
            // 「没有这条」才是有意义的读数（不刷新时它本来就不显示，扫不到等于没验证）。
            // 该截图供像素复核：期望整幅图内不存在横跨内容视口宽度的 #D97728 实心带。
            Check(vm.IsAccountRefreshing, "capture is taken while the busy state is genuinely active (negative evidence is not vacuous)");
            Capture(window, "account-refreshing-no-strip");
            vm.IsAccountRefreshing = false;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

            var feedback = vm.AccountFeedback;
            typeof(MainViewModel).GetField("_lastMembershipRefreshUtc", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(vm, DateTime.MinValue);
            await vm.RefreshMembershipOnActivationAsync();
            await Dispatcher.InvokeAsync(() => { page.UpdateLayout(); accountScroll.UpdateLayout(); }, DispatcherPriority.ApplicationIdle);
            Check(!vm.IsAccountRefreshing && page.FindName("AccountRefreshIndicator") == null, "automatic membership refresh stays silent without a busy strip to show");
            Check(vm.AccountFeedback == feedback, "automatic membership refresh does not replace user-facing account feedback");

            details.Width = 700;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            // 会员卡的堆叠判定读的是 AccountScroll.ViewportWidth，**与 details.Width 无关**，所以"窄视口"
            // 必须真的构造出来。§自适应（2026-09-25）把窗口下限放宽到 1024 之后，这一档在正常显示器上
            // 就能真的拖出来（图标栏 64 + 页边距 32 ⇒ 视口 = 1024 - 96 = 928 < 1100），所以直接改窗口宽度：
            // 这比"按请求尺寸强制布局"更贴近用户实际看到的状态，也不受强制布局的事件时序影响
            // （后者曾让这条断言变成"看布局历史"的脆弱检查）。
            var savedWindowWidth = window.Width;
            var savedWindowHeight = window.Height;
            FitNativeWindow(window, new Size(1024, 720));
            await WaitForStableAsync(() => window.ActualWidth, "membership compact window 1024x720");
            await NavIdleAsync();
            page.UpdateLayout(); accountScroll.UpdateLayout();
            Console.WriteLine($"INFO membership stack probe: window={window.ActualWidth:F1} min={window.MinWidth:F1} page={page.ActualWidth:F1} viewport={accountScroll.ViewportWidth:F1} details={details.ActualWidth:F1} row={Grid.GetRow(devices)} span={Grid.GetColumnSpan(devices)} compact={DwgTranslator.App.Views.Controls.ResponsiveLayout.GetIsCompact(window)}");
            Check(Grid.GetRow(devices) == 1 && Grid.GetColumnSpan(devices) == 2,
                $"compact membership stacks device section (viewport={accountScroll.ViewportWidth:F1}, row={Grid.GetRow(devices)}, span={Grid.GetColumnSpan(devices)})");
            Check(plans.Margin.Right == 0, "compact membership has no leftover column gutter");
            Capture(window, "membership-redesign-compact");
            FitNativeWindow(window, new Size(savedWindowWidth, savedWindowHeight));
            await WaitForStableAsync(() => window.ActualWidth, "membership wide window restored");
            details.Width = double.NaN;
            await NavIdleAsync();
            page.UpdateLayout(); accountScroll.UpdateLayout();
            Check(details.ActualWidth < 760 || Grid.GetColumn(devices) == 1, "wide membership uses two organized columns");

            vm.CurrentPage = MainViewModel.PageSettings; vm.SettingsSection = 5;
            await NavIdleAsync();
            var settings = FindVisual<SettingsPage>(window)!;
            var scroll = (ScrollViewer)settings.FindName("SettingsScroll");
            var originalOffset = scroll.VerticalOffset;
            vm.OpenHelpCommand.Execute(null);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var help = Application.Current.Windows.OfType<HelpWindow>().Single();
            Check(help.IsVisible && FindVisual<HelpPanel>(help) != null, "help opens outside the settings scroll surface");
            Check(Math.Abs(scroll.VerticalOffset - originalOffset) < 1, "opening help preserves about-page scroll position");
            Capture(help, "help-window");
            help.Close();
            vm.SettingsSection = 2;
            await NavIdleAsync();
            Check(scroll.VerticalOffset == 0, "switch settings resets previous scroll");
            vm.SettingsSection = 5;
            await NavIdleAsync();            Capture(window, "about-redesign");
        }
        finally
        {
            details.Width = double.NaN;
            vm.OnlineSubscription = subscription; vm.OnlineUsage = usage;
            vm.CurrentPage = MainViewModel.PageAccount;
            await NavIdleAsync();
        }
    }
}


