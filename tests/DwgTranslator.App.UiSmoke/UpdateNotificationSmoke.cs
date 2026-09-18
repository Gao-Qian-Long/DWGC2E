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
            var calls = api.UpdateCalls;
            await vm.CheckUpdatesAsync(false);
            Check(api.UpdateCalls == calls, "disabled automatic updates do not request network");
            api.UpdateResponse = new VersionInfo { LatestVersion = "2.2.0", ReleaseNotes = "改进公告与更新体验。\n这是隔离测试，不会发布下载。", DownloadUrl = "file:///C:/unsafe.exe" };
            await vm.CheckUpdatesAsync(true);
            Check(vm.HasAvailableUpdate && vm.SettingsFeedback.Contains("建议更新"), "manual update checks work with auto-check off");
            var dialog = Application.Current.Windows.OfType<Window>().Single(w => w.Title == "软件更新");
            Check(FindVisuals<Button>(dialog).Single(b => Equals(b.Content,"前往下载")).IsEnabled == false, "unsafe update download is disabled");
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
            Check(vm.HasAvailableUpdate && vm.SettingsFeedback.Contains("无法检查"), "transient failure preserves known update and reports manual error");
            api.FailUpdate = false;
            api.UpdateResponse = new VersionInfo { LatestVersion = "2.1.1" }; await vm.CheckUpdatesAsync(true);
            Check(!vm.HasAvailableUpdate && vm.SettingsFeedback.Contains("最新"), "equal version clears stale update entry");
            api.UpdateResponse = new VersionInfo { LatestVersion = "invalid" }; await vm.CheckUpdatesAsync(true);
            Check(!vm.HasAvailableUpdate && vm.SettingsFeedback.Contains("有效"), "malformed version is not announced as latest");
            api.UpdateResponse = new VersionInfo { LatestVersion = "2.3.0" }; await vm.CheckUpdatesAsync(false);
            Check(vm.HasAvailableUpdate && !Application.Current.Windows.OfType<Window>().Any(w=>w.Title=="软件更新"), "automatic update recommends without interrupting with dialog");
            api.UpdateResponse = new VersionInfo { LatestVersion = "2.0.0" }; await vm.CheckUpdatesAsync(false);
            Check(!vm.HasAvailableUpdate, "older server version does not recommend downgrade");
        }
        finally { config.AutoCheckUpdate = auto; api.UpdateResponse = previous; api.FailUpdate = false; }
    }
}
