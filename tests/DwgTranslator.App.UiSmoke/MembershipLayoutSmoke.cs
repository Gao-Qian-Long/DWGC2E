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
            var refreshIndicator = (Border)page.FindName("AccountRefreshIndicator");
            var refreshButton = FindVisuals<Button>(page).Single(b => Equals(b.Content, "刷新权益"));
            await Dispatcher.InvokeAsync(() => { page.UpdateLayout(); accountScroll.UpdateLayout(); }, DispatcherPriority.ApplicationIdle);
            var content = (FrameworkElement)accountScroll.Content;
            var contentHeight = content.ActualHeight;
            vm.IsAccountRefreshing = true;
            await Dispatcher.InvokeAsync(() => { page.UpdateLayout(); accountScroll.UpdateLayout(); }, DispatcherPriority.ApplicationIdle);
            Check(refreshIndicator.IsVisible, "account refresh indicator overlays without disappearing");
            Check(Math.Abs(content.ActualHeight - contentHeight) < 0.5, "account refresh does not change content layout height");
            Check(Equals(refreshButton.Content, "同步中…") && !refreshButton.IsEnabled, "account refresh button exposes busy state");
            vm.IsAccountRefreshing = false;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

            var feedback = vm.AccountFeedback;
            typeof(MainViewModel).GetField("_lastMembershipRefreshUtc", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(vm, DateTime.MinValue);
            await vm.RefreshMembershipOnActivationAsync();
            await Dispatcher.InvokeAsync(() => { page.UpdateLayout(); accountScroll.UpdateLayout(); }, DispatcherPriority.ApplicationIdle);
            Check(!vm.IsAccountRefreshing && !refreshIndicator.IsVisible, "automatic membership refresh stays silent without showing the busy strip");
            Check(vm.AccountFeedback == feedback, "automatic membership refresh does not replace user-facing account feedback");

            details.Width = 700;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(Grid.GetRow(devices) == 1 && Grid.GetColumnSpan(devices) == 2, "compact membership stacks device section");
            Check(plans.Margin.Right == 0, "compact membership has no leftover column gutter");
            Capture(window, "membership-redesign-compact");
            details.Width = double.NaN;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
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


