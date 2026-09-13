using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DwgTranslator.App;
using DwgTranslator.App.ViewModels;
using DwgTranslator.App.Views;
using DwgTranslator.Core.Api;
using DwgTranslator.Core.Services;
using Microsoft.Extensions.DependencyInjection;
namespace UiSmoke;
public static class Program
{
    [STAThread] public static int Main()
    {
        Environment.SetEnvironmentVariable("DWGC2E_DATA_DIR", Path.Combine(Path.GetTempPath(), "dwgc2e-ui-" + Guid.NewGuid().ToString("N")));
        SettingsStore.Update(Path.Combine(Environment.GetEnvironmentVariable("DWGC2E_DATA_DIR")!, "settings.json"), c => { c.ApiMode = "direct"; c.ApiBaseUrl = ""; c.AuthTokenEncrypted = DwgTranslator.Core.Models.AppConfig.EncryptApiKey("saved-simulation-token"); });
        var app = new SmokeApp();
        app.Resources = new ResourceDictionary();
        foreach (var name in new[] { "ColorTokens", "Icons", "Metrics", "MainWindowStyles" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/DwgTranslator;component/Themes/" + name + ".xaml") });
        return app.Run();
    }
}
public sealed class SmokeApp : App
{
    private readonly FakeApi api = new() { Offline = true };
    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
        base.OnStartup(e);
        Resources = new ResourceDictionary();
        foreach (var name in new[] { "ColorTokens", "Icons", "Metrics", "MainWindowStyles" })
            Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/DwgTranslator;component/Themes/" + name + ".xaml") });
        var services = new ServiceCollection();
        typeof(App).GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { services });
        services.AddSingleton<IApiClient>(api);
        typeof(App).GetProperty(nameof(Services))!.SetValue(null, services.BuildServiceProvider());
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        Dispatcher.BeginInvoke(new Action(async () => await Verify(window)), DispatcherPriority.ApplicationIdle);
        } catch (Exception ex) { Console.Error.WriteLine(ex); Shutdown(1); }
    }
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }
    private static void Capture(Window window, string name)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var output = Path.GetFullPath("artifacts/ui-smoke"); Directory.CreateDirectory(output);
        using var stream = File.Create(Path.Combine(output, name + ".png")); encoder.Save(stream);
    }
    private async Task Verify(MainWindow window)
    {
        try
        {
            window.Width = 1366; window.Height = 768;
            await Task.Delay(500);
            var vm = (MainViewModel)window.DataContext;
            Check(!vm.IsAccountLoggedIn, "startup does not trust unverified saved token while offline");
            Check(SettingsStore.Read(Path.Combine(AppDataDir, "settings.json")).ApiMode == "worker", "startup migrates legacy direct config");
            vm.LogoutAccountCommand.Execute(null);
            api.Offline = false;
            foreach (var page in new[] { MainViewModel.PageTranslate, MainViewModel.PageBatch, MainViewModel.PageGlossary, MainViewModel.PageAccount, MainViewModel.PageSettings })
            { vm.CurrentPage = page; await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); Check(vm.CurrentPage == page, "navigate " + page); Capture(window, page); }
            for (var section = 0; section < 5; section++) { vm.SettingsSection = section; await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); Capture(window, "settings-" + section); }
            vm.SettingsDraft.ExportDirectory = Path.Combine(AppDataDir, "output-test");
            Check(vm.HasUnsavedSettings, "settings dirty"); Check(vm.SaveSettingsPage(), "settings save");
            Check(SettingsStore.Read(Path.Combine(AppDataDir, "settings.json")).ExportDirectory.EndsWith("output-test"), "settings persisted");
            vm.CurrentPage = MainViewModel.PageTranslate;
            Check(!vm.RequireAccount() && vm.CurrentPage == MainViewModel.PageAccount, "cloud gate navigates without executing");
            vm.LoginName = "simulated-account";
            api.Configured = false; await vm.SubmitLoginAsync("not-a-real-password");
            Check(vm.AccountState == AccountSessionState.ConfigurationError && !vm.IsAccountLoggedIn, "invalid deployment configuration feedback");
            api.Configured = true;
            api.FailLogin = true; await vm.SubmitLoginAsync("not-a-real-password"); Check(!vm.IsAccountLoggedIn, "failed login");
            api.FailLogin = false; await vm.SubmitLoginAsync("not-a-real-password"); Check(vm.IsAccountLoggedIn, "successful controlled login");
            Check(vm.CurrentPage == MainViewModel.PageTranslate, "login returns without starting translation");
            Check(!vm.IsProcessing && api.TranslationCalls == 0, "no quota-consuming action");
            vm.CurrentPage = MainViewModel.PageAccount; Capture(window, "account-simulated-login");
            api.Offline = true; await vm.RefreshAccountCommand.ExecuteAsync(null); Check(vm.AccountFeedback.Contains("连接"), "offline feedback"); api.Offline = false;
            api.Expired = true; await vm.RefreshAccountCommand.ExecuteAsync(null); Check(!vm.IsAccountLoggedIn, "expired session clears login"); api.Expired = false;
            vm.CurrentPage = MainViewModel.PageGlossary; vm.AddTermCommand.Execute(null); vm.SelectedTerm!.Source = "示例"; vm.SelectedTerm.Target = "Example"; Capture(window, "term-editor"); Check(vm.SaveTermEditor(), "save term");
            var count = vm.TermDraft.Count; vm.LoadTermEditor(); Check(vm.TermDraft.Count == count, "term count after reload");
            foreach (var size in new[] { (1366d,768d), (1920d,1080d) })
                foreach (var scale in new[] { 1d, 1.25d, 1.5d, 2d })
                {
                    window.Width = size.Item1 / scale; window.Height = size.Item2 / scale;
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Capture(window, "effective-layout-" + size.Item1 + "-" + scale);
                }
            window.Width = 1366; window.Height = 768;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (_, _) => { var dialog = Windows.OfType<PromptDialog>().FirstOrDefault(); if (dialog != null) { Capture(dialog, "confirmation"); timer.Stop(); dialog.Close(); } };
            timer.Start();
            Check(PromptDialog.Show("用于验收的未保存提示，不会修改数据。", "未保存的设置", MessageBoxButton.YesNoCancel) == MessageBoxResult.Cancel, "dialog close means cancel");
            vm.CurrentPage = MainViewModel.PageSettings;
            var original = vm.SettingsDraft.ExportDirectory;
            vm.SettingsDraft.ExportDirectory = "discard-test";
            vm.DiscardSettingsChangesCommand.Execute(null);
            Check(vm.SettingsDraft.ExportDirectory == original, "discard settings");
            vm.SettingsDraft.ExportDirectory = "must-survive-write-failure";
            using (var locked = new FileStream(Path.Combine(AppDataDir, "settings.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(!vm.SaveSettingsPage() && vm.SettingsDraft.ExportDirectory == "must-survive-write-failure", "write failure preserves draft");
            vm.DiscardSettingsChangesCommand.Execute(null);
            Console.WriteLine("UI_SMOKE=PASS (controlled API; rendered screenshots, not physical DPI validation)");
            window.Close(); Shutdown(0);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); Shutdown(1); }
    }
}
public sealed class FakeApi : IApiClient
{
    public bool Configured = true; public bool IsConfigured => Configured; public string ModeName => "worker";
    public bool FailLogin, Offline, Expired; public int TranslationCalls;
    public Task<LoginResult> LoginAsync(string account, string password, CancellationToken cancellationToken = default) => Task.FromResult(new LoginResult { Success=!FailLogin, Token=FailLogin?null:"simulation-only", ExpiresAt=DateTime.UtcNow.AddHours(1) });
    public Task<ProfileInfo?> GetProfileAsync(CancellationToken cancellationToken = default) { if (Expired) throw new ApiAuthenticationException("token_expired"); if (Offline) throw new IOException("controlled offline"); return Task.FromResult<ProfileInfo?>(new() { DisplayName="模拟验收账号", Email="qa@example.invalid" }); }
    public Task<SubscriptionInfo?> GetSubscriptionAsync(CancellationToken cancellationToken = default) => Task.FromResult<SubscriptionInfo?>(new() { PlanName="模拟测试套餐" });
    public Task<UsageInfo?> GetUsageAsync(CancellationToken cancellationToken = default) => Task.FromResult<UsageInfo?>(new() { MonthlyQuota=10000, Used=10 });
    public Task<IReadOnlyList<DeviceInfo>?> GetDevicesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DeviceInfo>?>(new[] { new DeviceInfo { DeviceName="模拟设备", DeviceId="test", IsCurrent=true } });
    public Task<TranslationBatchResult> TranslateAsync(TranslationBatchRequest request, CancellationToken cancellationToken = default) { TranslationCalls++; throw new InvalidOperationException("Paid operation forbidden in UI smoke"); }
    public Task<DeviceBindResult> BindDeviceAsync(string deviceId, string deviceName, CancellationToken cancellationToken = default) => Task.FromResult(new DeviceBindResult { Success=true });
    public Task<bool> RevokeDeviceAsync(string deviceId, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task<VersionInfo?> CheckVersionAsync(string currentVersion, CancellationToken cancellationToken = default) => Task.FromResult<VersionInfo?>(new() { LatestVersion="2.1.0-test" });
    public Task<IReadOnlyList<CloudGlossaryEntry>?> GetGlossaryAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CloudGlossaryEntry>?>(Array.Empty<CloudGlossaryEntry>());
    public Task<bool> PutGlossaryAsync(IReadOnlyList<CloudGlossaryEntry> entries, CancellationToken cancellationToken = default) => Task.FromResult(true);
}
