using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using DwgTranslator.App.ViewModels;
using DwgTranslator.App.Views;
using DwgTranslator.Core.Api;
using DwgTranslator.Core.Models;
namespace UiSmoke;
public sealed partial class SmokeApp
{
    private async Task VerifyUpdatesAsync(MainWindow window, MainViewModel vm)
    {
        var config = (AppConfig)typeof(MainViewModel).GetField("_config", BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(vm)!;
        var auto = config.AutoCheckUpdate;
        var previous = api.UpdateResponse;
        try
        {
            config.AutoCheckUpdate = false;
            vm.CurrentPage = MainViewModel.PageAccount;
            await NavIdleAsync();
            var accountPage = FindVisual<DwgTranslator.App.Views.Pages.AccountPage>(window);
            var recoveryButton = (Button)accountPage.FindName("SavedSessionRecoveryButton");
            Check(vm.HasSavedAccountSession || !recoveryButton.IsVisible, "signed-out account does not expose a dead session recovery action");
            vm.CurrentPage = MainViewModel.PageSettings; vm.SettingsSection = 5;
            await NavIdleAsync();
            var settingsPage = FindVisual<DwgTranslator.App.Views.Pages.SettingsPage>(window);
            // §L21 原「关于页解释独立更新源」断言随内部机制说明文字一起移除：用户要求设置页不展示开发者向小字。
            Check(!vm.AppReleaseVersionText.Contains('+') && !vm.AppBuildText.Contains(vm.AppVersionText), "about page separates release version from build identity");
            Check(settingsPage.FindName("AboutUpdatePanel") is FrameworkElement, "about page has integrated update status panel");
            var updateButton = (Button)settingsPage.FindName("AboutUpdateButton");
            Check(Equals(updateButton.Content, "检查更新") && updateButton.IsEnabled, "about update button has visible idle text");
            Check(updateButton.Command != null, "关于页 检查更新 按钮必须绑定到命令");
            var updateDetailsButton = FindVisuals<Button>(settingsPage).SingleOrDefault(b => Equals(b.Content, "查看更新详情"));
            Check(updateDetailsButton is { Command: not null }, "关于页 查看更新详情 按钮必须绑定到命令");
            Check(!window.InputBindings.OfType<System.Windows.Input.KeyBinding>().Any(k => k.Command == vm.ImportDwgCommand || k.Command == vm.ExportDwgCommand || k.Command == vm.TranslateCommand), "global business shortcuts are routed by page instead of direct key bindings");
            await vm.HandleSaveShortcutAsync();
            Check(!vm.IsProcessing, "save shortcut on about page does not export drawings");
            await vm.HandleRunShortcutAsync();
            Check(!vm.IsProcessing, "run shortcut on about page does not start translation");
            vm.OpenHelpCommand.Execute(null);
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var help = Application.Current.Windows.OfType<HelpWindow>().Single();
            Check(help.IsVisible && vm.IsHelpExpanded, "help entry opens a dedicated help window with visible feedback");
            help.Close();
            vm.NavigateToCommand.Execute("glossary"); Check(vm.CurrentPage == MainViewModel.PageGlossary, "about glossary button navigates");
            vm.NavigateToCommand.Execute("output-settings"); Check(vm.CurrentPage == MainViewModel.PageSettings && vm.SettingsSection == 2, "about output button navigates to output settings");
            vm.CurrentPage = MainViewModel.PageSettings; vm.SettingsSection = 5;
            vm.NavigateToCommand.Execute("account"); Check(vm.CurrentPage == MainViewModel.PageAccount, "about membership button navigates");
            vm.CurrentPage = MainViewModel.PageSettings; vm.SettingsSection = 5;
            await vm.CheckServiceCommand.ExecuteAsync(null);
            Check(vm.ServiceFeedback.Contains("服务连接正常") && !vm.IsCheckingService, "service check reports completion beside button");
            var calls = api.UpdateCalls;
            await vm.CheckUpdatesAsync(false);
            Check(api.UpdateCalls == calls, "disabled automatic updates do not request network");
            api.UpdateResponse = new VersionInfo { LatestVersion = "2.2.0", ReleaseNotes = "改进公告与更新体验。\n这是隔离测试，不会发布下载。", DownloadUrl = "file:///C:/unsafe.exe" };
            await vm.CheckUpdatesAsync(true);
            Check(vm.HasAvailableUpdate && vm.AboutFeedback.Contains("建议更新") && !vm.SettingsFeedback.Contains("建议更新"), "manual update checks work with auto-check off");
            Check(vm.UpdateState == UpdateCheckState.UpdateAvailable && vm.UpdateCheckSucceeded && !vm.UpdateCheckFailed && vm.UpdateStatusText.Contains("新版本"), "available update has explicit integrated state");
            var dialog = Application.Current.Windows.OfType<Window>().Single(w => w.Title == "软件更新");
            Check(FindVisuals<Button>(dialog).Single(b => Equals(b.Content,"下载并准备更新")).IsEnabled == false, "unsigned update install is disabled");
            Capture(dialog,"update-recommendation"); dialog.Close();
            Check(vm.HasAvailableUpdate, "dismissed recommendation retains update entry");
            window.Width = 600; window.UpdateLayout();
            var banner = (FrameworkElement)window.FindName("SiteAnnouncement");
            var update = FindVisuals<Button>(window).Single(b => Equals(b.Content,"建议更新"));
            Check(banner.TranslatePoint(new Point(banner.ActualWidth,0),window).X <= update.TranslatePoint(new Point(0,0),window).X, "ticker and update entry do not overlap at minimum width");
            Capture(window,"announcement-update-minimum-width");
            window.Width = 1366;
            config.AutoCheckUpdate = true;
            api.FailUpdate = true; await vm.CheckUpdatesAsync(true);
            Check(vm.HasAvailableUpdate && vm.AboutFeedback.Contains("无法检查") && !vm.SettingsFeedback.Contains("无法检查"), "transient failure preserves known update and reports manual error");
            Check(vm.UpdateState == UpdateCheckState.Failed && vm.UpdateCheckFailed && vm.UpdateStatusText.Contains("无法连接"), "update outage has compact failed state");
            api.FailUpdate = false;
            api.UpdateResponse = new VersionInfo { LatestVersion = "2.1.1" }; await vm.CheckUpdatesAsync(true);
            Check(!vm.HasAvailableUpdate && vm.AboutFeedback.Contains("最新") && !vm.SettingsFeedback.Contains("最新"), "equal version clears stale update entry");
            Check(vm.UpdateState == UpdateCheckState.UpToDate && vm.UpdateCheckSucceeded && !vm.UpdateCheckFailed && vm.UpdateStatusText.Contains("最新版本"), "latest version has explicit success state");
            api.UpdateResponse = new VersionInfo { LatestVersion = "invalid" }; await vm.CheckUpdatesAsync(true);
            Check(!vm.HasAvailableUpdate && vm.AboutFeedback.Contains("有效") && !vm.SettingsFeedback.Contains("有效"), "malformed version is not announced as latest");
            api.UpdateResponse = new VersionInfo { LatestVersion = "2.3.0" }; await vm.CheckUpdatesAsync(false);
            Check(vm.HasAvailableUpdate && !Application.Current.Windows.OfType<Window>().Any(w=>w.Title=="软件更新"), "automatic update recommends without interrupting with dialog");
            api.UpdateResponse = new VersionInfo { LatestVersion = "2.0.0" }; await vm.CheckUpdatesAsync(false);
            Check(!vm.HasAvailableUpdate, "older server version does not recommend downgrade");
        }
        finally { config.AutoCheckUpdate = auto; api.UpdateResponse = previous; api.FailUpdate = false; }
    }
}
