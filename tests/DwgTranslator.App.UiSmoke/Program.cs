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
public sealed partial class SmokeApp : App
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
    private static async Task WaitUntil(Func<bool> completed, string description)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!completed())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException(description);
            await Task.Delay(25);
        }
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }
    private static T FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var found = FindVisual<T>(VisualTreeHelper.GetChild(parent, i));
            if (found != null) return found;
        }
        return null!;
    }
    private static int CountVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = parent is T ? 1 : 0;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) count += CountVisual<T>(VisualTreeHelper.GetChild(parent, i));
        return count;
    }
    private static void Capture(FrameworkElement window, string name)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var output = Path.GetFullPath(Environment.GetEnvironmentVariable("DWGC2E_UI_SMOKE_OUTPUT") ?? "artifacts/ui-smoke"); Directory.CreateDirectory(output);
        using var stream = File.Create(Path.Combine(output, name + ".png")); encoder.Save(stream);
    }
    private static IEnumerable<T> FindVisuals<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisuals<T>(child)) yield return descendant;
        }
    }
    private async Task VerifyWorkspaceUi(MainWindow window, MainViewModel vm)
    {
        vm.CurrentPage = MainViewModel.PageTranslate;
        var translate = FindVisual<DwgTranslator.App.Views.Pages.TranslatePage>(window);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var pinButton = (System.Windows.Controls.Button)window.FindName("TopmostButton");
        var pinViewport = (FrameworkElement)pinButton.Content;
        var pin = FindVisual<System.Windows.Shapes.Path>(pinViewport);
        Check(pinButton.Padding == new Thickness(0), "topmost icon has no text-button padding");
        Console.WriteLine($"PIN_LAYOUT viewport={pinViewport.ActualWidth}x{pinViewport.ActualHeight} path={pin.ActualWidth}x{pin.ActualHeight}");
        Check(Math.Abs(pinViewport.ActualWidth - 16) <= 1 / VisualTreeHelper.GetDpi(pinViewport).DpiScaleX && Math.Abs(pinViewport.ActualHeight - 18) <= 1 / VisualTreeHelper.GetDpi(pinViewport).DpiScaleY && pin.ActualWidth >= 14 && pin.ActualHeight >= 16, "topmost icon retains full dimensions");
        Check(pinButton.FocusVisualStyle != null, "topmost retains keyboard focus cue");
        Check(!vm.IsTopmost && !window.Topmost, "topmost starts off");
        pinButton.Command.Execute(null);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(vm.IsTopmost && window.Topmost, "topmost command pins actual window");
        Check((string)pinButton.ToolTip == "取消固定", "pinned tooltip offers unpin");
        Check(((SolidColorBrush)pin.Stroke).Color == ((SolidColorBrush)window.FindResource("Brush.Primary")).Color, "pinned icon is accent color");
        pinButton.Focus();
        window.UpdateLayout();
        var pinSurface = (System.Windows.Controls.Border)pinButton.Template.FindName("IconSurface", pinButton);
        Check(pinSurface.BorderThickness == new Thickness(0), "focused pin has no persistent orange border");
        Check(Math.Abs(pinViewport.ActualWidth - 16) <= 1 / VisualTreeHelper.GetDpi(pinViewport).DpiScaleX && Math.Abs(pinViewport.ActualHeight - 18) <= 1 / VisualTreeHelper.GetDpi(pinViewport).DpiScaleY && pin.ActualWidth >= 14 && pin.ActualHeight >= 16, "focus does not squeeze pin");
        Capture(window, "titlebar-pinned");
        pinButton.Command.Execute(null);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(!vm.IsTopmost && !window.Topmost, "topmost command unpins actual window");
        Check((string)pinButton.ToolTip == "固定窗口", "unpinned tooltip offers pin");
        Check(((SolidColorBrush)pin.Stroke).Color == ((SolidColorBrush)window.FindResource("Brush.TextMuted")).Color, "unpinned icon is muted");
        System.Windows.Input.Keyboard.ClearFocus();
        Capture(window, "titlebar-unpinned");
        Check(!vm.HasDrawingFiles, "empty workspace fixture");
        Check(!((System.Windows.Controls.Button)translate.FindName("ExportSelectionButton")).IsEnabled, "empty or untranslated selection cannot export");
        var center = (FrameworkElement)translate.FindName("DropZoneCenter");
        var drop = (FrameworkElement)translate.FindName("EmptyDropZone");
        var centerOrigin = center.TranslatePoint(new Point(0, 0), drop);
        Check(Math.Abs(centerOrigin.X + center.ActualWidth / 2 - drop.ActualWidth / 2) <= 1 && Math.Abs(centerOrigin.Y + center.ActualHeight / 2 - drop.ActualHeight / 2) <= 1, "drop content geometrically centered");
        Check(System.Windows.Controls.Grid.GetRow((FrameworkElement)translate.FindName("TranslationSettingsSection")) == 1 && System.Windows.Controls.Grid.GetRow((FrameworkElement)translate.FindName("OutputSection")) == 2, "translation and output have separate grid rows");
        Check(((FrameworkElement)translate.FindName("EmptyDropZone")).IsVisible, "empty queue has large import entry");
        Check(!((FrameworkElement)translate.FindName("QueueWorkspace")).IsVisible, "empty queue does not show redundant table");
        Check(!((System.Windows.Controls.Expander)translate.FindName("WorkspaceLog")).IsExpanded, "log is subordinate by default");
        var editEntity = new DwgTranslator.Core.Models.TextEntity { Handle = "proof-test", PlainText = "原文", TranslatedText = "original" };
        vm.Entities.Add(editEntity);
        vm.TrackProofreadingEdit(editEntity); editEntity.TranslatedText = "changed";
        Check(vm.HasUnsavedProofreading, "proofreading dirty state");
        vm.DiscardProofreadingCommand.Execute(null);
        Check(editEntity.TranslatedText == "original" && !vm.HasUnsavedProofreading, "proofreading discard restores original");
        vm.Entities.Remove(editEntity);
        await VerifyProofreadingPersistenceAsync(vm);
        await VerifyProofreadingAccountCancelAsync(vm);
        Check(!FindVisual<System.Windows.Controls.DataGrid>(translate).HasItems, "empty drawing table");
        Check(((FrameworkElement)translate.FindName("EmptyDropZone")).ActualHeight <= 360, "empty drop zone capped at 360 DIP");
        Check(!vm.HasFailedDrawingTasks,"retry disabled without failed tasks");
        Capture(window, "workspace-empty");
        var viewport = (System.Windows.Controls.ScrollViewer)window.FindName("PageViewport");
        var pageHost = (FrameworkElement)window.FindName("PageHost");
        Console.WriteLine($"PAGE_LAYOUT actual={viewport.ActualWidth}x{viewport.ActualHeight} viewport={viewport.ViewportWidth}x{viewport.ViewportHeight} extent={viewport.ExtentWidth}x{viewport.ExtentHeight} host={pageHost.Width}x{pageHost.Height}");
        var states = new[] { DwgTranslator.Core.Tasks.TranslationTaskStatus.Pending, DwgTranslator.Core.Tasks.TranslationTaskStatus.Translating, DwgTranslator.Core.Tasks.TranslationTaskStatus.Completed, DwgTranslator.Core.Tasks.TranslationTaskStatus.Failed, DwgTranslator.Core.Tasks.TranslationTaskStatus.Paused };
        foreach (var state in states)
        {
            var path = Path.Combine(AppDataDir, "验收图纸-" + state + ".dwg");
            var row = new DrawingFileItem(path);
            row.AttachTask(new DwgTranslator.Core.Tasks.TranslationTask(path) { Status = state, TextCount = 128, Progress = state == DwgTranslator.Core.Tasks.TranslationTaskStatus.Completed ? 100 : 30, Error = state == DwgTranslator.Core.Tasks.TranslationTaskStatus.Failed ? "图纸无法读取，请检查文件后重试。" : null });
            vm.DrawingFiles.Add(row);
        }
        vm.HasDrawingFiles = true;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(!((FrameworkElement)translate.FindName("EmptyDropZone")).IsVisible, "import releases drop zone space");
        Check(((FrameworkElement)translate.FindName("DrawingQueue")).ActualHeight >= 120, "queue remains bounded and scrollable in compact workspace");
        var log = (System.Windows.Controls.Expander)translate.FindName("WorkspaceLog");
        log.IsExpanded = true;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(Math.Abs(((FrameworkElement)log.Content).ActualHeight - 170) <= 1 / VisualTreeHelper.GetDpi(log).DpiScaleY, "expanded log keeps 170 DIP height");
        Capture(window, "workspace-expanded-log");
        log.IsExpanded = false;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Capture(window, "workspace-five-drawings");
        Check(!vm.DrawingFiles[2].HasOutput, "completed task without output has no open action");
        vm.ClearDrawingOutputsCommand.Execute(null);
        Check(vm.DrawingFiles.All(x => !x.IsIncludedForExport), "clear output selection");
        vm.SelectAllDrawingOutputsCommand.Execute(null);
        Check(vm.DrawingFiles.All(x => x.IsIncludedForExport), "select all outputs");
        var output = Path.Combine(AppDataDir, "test-output.dwg"); File.WriteAllText(output, "controlled fixture, not a real drawing");
        vm.DrawingFiles[2].Task!.OutputPath = output;
        Check(vm.DrawingFiles[2].HasOutput, "existing output enables result action");
        File.Delete(output);
        Check(!vm.DrawingFiles[2].HasOutput, "removed output disables result action");
        var count = vm.BatchView.Cast<object>().Count(); Check(count == 5, "batch view contains all tasks");
        vm.BatchStatusFilter = 4; Check(vm.BatchView.Cast<object>().Count() == 1, "failure filter");
        vm.BatchStatusFilter = 1; Check(vm.BatchView.Cast<object>().Count() == 1, "running filter");
        vm.BatchStatusFilter = 0; vm.BatchSearch = "Paused"; Check(vm.BatchView.Cast<object>().Count() == 1, "search preserves paused state");
        vm.BatchSearch = ""; vm.BatchDateFilter = 1; Check(vm.BatchView.Cast<object>().Count() == 5, "today filter"); vm.BatchDateFilter = 0;
        vm.CurrentPage = MainViewModel.PageBatch; vm.SelectedBatchTask = vm.DrawingFiles[3];
        Check(vm.IsTaskDetailOpen, "selection opens task detail");
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); Capture(window, "task-error-detail");
        vm.OpenTaskProofreadingCommand.Execute(null); Check(vm.IsProofreading && vm.SelectedDrawingFile == vm.DrawingFiles[3], "detail opens selected drawing proofreading");
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); Capture(window, "task-proofreading");
        vm.BackToTaskListCommand.Execute(null); Check(!vm.IsProofreading && vm.SelectedBatchTask == vm.DrawingFiles[3], "back preserves selected task");
        vm.CloseTaskDetailCommand.Execute(null);
        vm.ShowTaskDetailCommand.Execute(vm.DrawingFiles[3]);
        Check(vm.IsTaskDetailOpen, "same task detail can reopen");
        vm.CloseTaskDetailCommand.Execute(null);
        for (var i = 0; i < 600; i++) vm.DrawingFiles.Add(new DrawingFileItem(Path.Combine(AppDataDir, "长文件名-" + new string('图', 80) + i + ".dwg")));
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var batchPage = FindVisual<DwgTranslator.App.Views.Pages.BatchTasksPage>(window);
        var taskTable = (System.Windows.Controls.DataGrid)batchPage.FindName("TaskTable");
        Check(taskTable.Items.Count == 605, "large task queue retains every row");
        Check(CountVisual<System.Windows.Controls.DataGridRow>(taskTable) < 80, "large queue uses bounded row virtualization");
        Capture(window, "task-large-queue");
        vm.DrawingFiles.Clear(); vm.HasDrawingFiles = false;
        Check(!vm.IsTaskDetailOpen && vm.SelectedBatchTask == null, "workspace reset clears drawer selection");
        vm.SelectedDrawingFile = null;
        vm.CurrentPage = MainViewModel.PageSettings; vm.SettingsSection = 5;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); Capture(window, "settings-about");
        vm.SettingsSection = 0;
        Console.WriteLine("PHYSICAL_DPI_NOT_VALIDATED: matrix uses WPF effective dimensions; current monitor scale " + VisualTreeHelper.GetDpi(window).DpiScaleX);
    }
    private async Task VerifyBilling(MainWindow owner)
    {
        var display=new DwgTranslator.App.Converters.BillingPresentationConverter();
        Check((string)display.Convert(19,typeof(string),"Amount",System.Globalization.CultureInfo.InvariantCulture)=="¥0.19","billing cents formatted as yuan");
        Check((string)display.Convert("confirming",typeof(string),"Status",System.Globalization.CultureInfo.InvariantCulture)=="确认中","billing confirming localized");
        Check((string)display.Convert("paid",typeof(string),"Status",System.Globalization.CultureInfo.InvariantCulture)=="已支付","billing paid localized without claiming entitlement delivery");
        var fake=new FakeBilling();var valid=true;BillingEntitlements? applied=null;
        var folder=Path.Combine(Environment.GetEnvironmentVariable("DWGC2E_DATA_DIR")!,"payments");Directory.CreateDirectory(folder);
        var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("billing-ui-user")));
        File.WriteAllText(Path.Combine(folder,hash+".json"),"{\"PlanId\":\"p\",\"Channel\":\"alipay\",\"Key\":\"intent-000000000001\"}");
        BillingWindow Create()=>new(fake,"local@example.test",()=>valid,s=>applied=s){Owner=owner,ShowActivated=false,ShowInTaskbar=false};
        var w=Create();w.Show();await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
        var buy=(System.Windows.Controls.Button)w.FindName("Buy");Check(buy.IsEnabled,"billing initialized with persisted intent");buy.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
        Check(fake.Creates==1 && fake.LastKey=="intent-000000000001","billing resumes original idempotency key");
        Check(!((System.Windows.Controls.RadioButton)w.FindName("WechatPay")).IsEnabled,"unconnected WeChat payment cannot be selected");
        Check(((System.Windows.Controls.TextBlock)w.FindName("QrStatus")).Text.Contains("请使用支付宝扫码"),"QR identifies actual Alipay order channel");
        Check(((System.Windows.Controls.Image)w.FindName("Qr")).Source!=null,"native billing QR bitmap generated");Capture(w,"billing-native-qr");
        fake.Order.Channel="wxpay";
        await (Task)typeof(BillingWindow).GetMethod("RefreshAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(w,null)!;
        Check(((System.Windows.Controls.Image)w.FindName("Qr")).Source==null,"unsupported order channel never displays an Alipay QR");
        Check(fake.Creates==1,"viewing unsupported order never creates replacement payment");
        fake.Order.Channel="alipay";
        w.Close();fake.PaymentsEnabled=false;w=Create();w.Show();await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);Check(fake.Creates==1,"billing restart restores without checkout");
        Check(((System.Windows.Controls.TextBlock)w.FindName("PurchaseAvailability")).Text.Contains("新购买暂未开放"),"paused service remains visible after existing order refresh");
        Check(!((FrameworkElement)w.FindName("PaymentMethods")).IsEnabled,"paused service disables payment methods");
        Check(((System.Windows.Controls.ComboBox)w.FindName("Plans")).Visibility==Visibility.Collapsed,"no empty plan selector while paused");
        Check(((System.Windows.Controls.Image)w.FindName("Qr")).Source!=null,"paused new purchases do not hide valid existing QR");
        fake.Order.ExpiresAt=DateTimeOffset.UtcNow.AddSeconds(-1);fake.Order.AllowedActions.Pay=false;
        await (Task)typeof(BillingWindow).GetMethod("RefreshAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(w,null)!;
        Check(((System.Windows.Controls.Image)w.FindName("Qr")).Source==null,"expired QR hidden while order remains queryable");
        Check(((System.Windows.Controls.TextBlock)w.FindName("QrStatus")).Text.Contains("展示期限已结束"),"expired QR has explicit reason instead of blank space");Capture(w,"billing-paused-expired");
        fake.Order.Status="paid";fake.FailSnapshot=true;
        await (Task)typeof(BillingWindow).GetMethod("RefreshAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(w,null)!;
        Check(((System.Windows.Controls.TextBlock)w.FindName("Message")).Text.Contains("支付成功"),"paid is preserved when entitlement synchronization fails");
        fake.FailSnapshot=false;await (Task)typeof(BillingWindow).GetMethod("RefreshAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(w,null)!;
        Check(applied?.Subscription.PlanName=="pro","native payment refreshes membership snapshot");Capture(w,"billing-native-paid");
        Check(!File.Exists(Path.Combine(folder,hash+".json")),"paid intent cleared only for matching order");
        valid=false;await (Task)typeof(BillingWindow).GetMethod("RefreshAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(w,null)!;Check(!w.IsVisible,"account switch closes payment window");
    }
    private async Task Verify(MainWindow window)
    {
        try
        {
            ((FrameworkElement)window.Content).Width = double.NaN;
            ((FrameworkElement)window.Content).Height = double.NaN;
            window.Width = 1366; window.Height = 768;
            await Task.Delay(500);
            var vm = (MainViewModel)window.DataContext;
            VerifyEmbeddedCadPlugin();
            VerifyPluginSourceConsistency(vm);
            await VerifyTaskRecoveryNoticeAsync(window, vm);
            VerifyRecoveryDecision(vm);
            Check(!vm.IsAccountLoggedIn, "startup does not trust unverified saved token while offline");
            Check(SettingsStore.Read(Path.Combine(AppDataDir, "settings.json")).ApiMode == "worker", "startup migrates legacy direct config");
            api.FailBilling = true;
            vm.LoginName = "simulated-account";
            await VerifyAccountSaveFailureAsync(vm, logout: false);
            vm.CurrentPage = MainViewModel.PageAccount;
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            var loginPage = FindVisual<DwgTranslator.App.Views.Pages.AccountPage>(window);
            Check(loginPage != null && loginPage.IsVisible, "login regression uses the visible account page");
            var passwordBox = (System.Windows.Controls.PasswordBox)loginPage.FindName("PasswordInput");
            var loginButton = (System.Windows.Controls.Button)loginPage.FindName("LoginButton");
            api.Offline=false; api.FailLogin=true; passwordBox.Password="not-a-real-password";
            loginButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            await WaitUntil(()=>!vm.IsLoggingIn,"failed login finishes");
            Check(api.LoginCalls==1&&!vm.IsAccountLoggedIn,"saved unverified session permits password authentication");
            Check(passwordBox.Password=="not-a-real-password","failed login preserves password for retry");
            api.FailLogin=false;
            loginButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            loginButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            await WaitUntil(()=>!vm.IsLoggingIn,"successful login finishes");
            Check(api.LoginCalls==2,"duplicate login click does not submit twice");
            Check(vm.IsAccountLoggedIn&&passwordBox.Password.Length==0,"actual password box login succeeds and then clears password");
            Check(vm.AccountFeedback.Contains("会员与额度暂未同步"),"billing outage does not prevent authenticated login");
            await VerifyAccountSaveFailureAsync(vm, logout: true);
            await VerifyLogoutRecoveryAsync(vm);
            api.BillingAuthExpired=true; await vm.RefreshAccountCommand.ExecuteAsync(null);
            Check(!vm.IsAccountLoggedIn && vm.AccountState == AccountSessionState.Expired,"billing authentication errors still expire the session");
            api.BillingAuthExpired=false;
            await vm.LogoutAccountCommand.ExecuteAsync(null); api.FailBilling=false;
            api.Offline = false;
            foreach (var page in new[] { MainViewModel.PageTranslate, MainViewModel.PageBatch, MainViewModel.PageGlossary, MainViewModel.PageAccount, MainViewModel.PageSettings })
            { vm.CurrentPage = page; await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); Check(vm.CurrentPage == page, "navigate " + page); Capture(window, page); }
            await VerifyCloudGlossaryAsync(window, vm);
            vm.CurrentPage = MainViewModel.PageSettings;
            await Task.Delay(4200);
            for (var section = 0; section < 6; section++) { vm.SettingsSection = section; await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); Capture(window, "settings-" + section); var settingsPage=FindVisual<DwgTranslator.App.Views.Pages.SettingsPage>(window); Check(((FrameworkElement)settingsPage.FindName("SettingsSaveBar")).IsVisible == (section!=5), "save bar only on editable settings " + section); if(section==1) Check(FindVisuals<System.Windows.Controls.TextBox>(settingsPage).Where(t=>t.IsVisible).All(t=>t.ActualWidth<=160),"compact numeric settings inputs"); }
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
            vm.CurrentPage = MainViewModel.PageAccount; await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); Capture(window, "account-simulated-login");
            var quota = (System.Windows.Controls.ProgressBar)loginPage.FindName("QuotaProgress");
            var usageBefore = vm.OnlineUsage;
            foreach (var used in new[] { 0L, 50L, 100L })
            {
                vm.OnlineUsage = new UsageInfo { MonthlyQuota = 100, Used = used };
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                quota.UpdateLayout();
                var track = (FrameworkElement)quota.Template.FindName("PART_Track", quota);
                var indicator = (FrameworkElement)quota.Template.FindName("PART_Indicator", quota);
                Check(track.ActualWidth > 100 && Math.Abs(indicator.ActualWidth - track.ActualWidth * (100 - used) / 100) < 2, "quota bar reflects remaining " + (100 - used));
            }
            vm.OnlineUsage = null;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(!quota.IsVisible, "unknown quota has no fabricated meter");
            vm.OnlineUsage = usageBefore;
            api.Offline = true; await vm.RefreshAccountCommand.ExecuteAsync(null); Check(vm.AccountFeedback.Contains("连接"), "offline feedback"); api.Offline = false;
            await VerifyExpiredProofreadingAsync(vm);
            api.Expired = true; await vm.RefreshAccountCommand.ExecuteAsync(null); Check(!vm.IsAccountLoggedIn, "expired session clears login"); api.Expired = false;
            vm.CurrentPage = MainViewModel.PageGlossary; vm.AddTermCommand.Execute(null); vm.SelectedTerm!.Source = "示例"; vm.SelectedTerm.Target = "Example"; Capture(window, "term-editor"); Check(vm.SaveTermEditor(), "save term");
            await VerifyGlossaryWorkspace(window, vm);
            var count = vm.TermDraft.Count; vm.LoadTermEditor(); Check(vm.TermDraft.Count == count, "term count after reload");
            vm.FinishTermEditCommand.Execute(null);
            vm.SelectedTerm = null;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var glossaryPage = FindVisual<DwgTranslator.App.Views.Pages.GlossaryPage>(window);
            var syncMenu = (System.Windows.Controls.MenuItem)glossaryPage.FindName("CloudSyncMenu");
            Check(Math.Abs(syncMenu.ActualHeight - (double)window.FindResource("Size.Button")) <= 1, "cloud sync matches toolbar button height");
            Check(syncMenu.Template.FindName("DropdownSurface", syncMenu) is System.Windows.Controls.Border, "cloud sync replaces native blue menu chrome");
            Check(ReferenceEquals(((System.Windows.Controls.MenuItem)syncMenu.Items[0]).Command, vm.MergeTermsCommand) && ReferenceEquals(((System.Windows.Controls.MenuItem)syncMenu.Items[1]).Command, vm.UploadTermsCommand) && ReferenceEquals(((System.Windows.Controls.MenuItem)syncMenu.Items[2]).Command, vm.DownloadTermsCommand), "cloud sync recommends merge and keeps explicit replace/download commands");
            syncMenu.IsSubmenuOpen = true;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var syncPopup = (System.Windows.Controls.Primitives.Popup)syncMenu.Template.FindName("PART_Popup", syncMenu);
            Check(syncPopup.IsOpen, "cloud sync dropdown opens");
            var syncSurface = (System.Windows.Controls.Border)syncMenu.Template.FindName("DropdownSurface", syncMenu);
            Check(Equals(syncSurface.Background, window.FindResource("Brush.PrimaryLight")), "cloud sync open state uses warm accent");
            foreach (System.Windows.Controls.MenuItem item in syncMenu.Items)
            {
                item.ApplyTemplate();
                Check(item.Template.FindName("MenuSurface", item) is System.Windows.Controls.Border, "cloud sync command replaces native submenu chrome");
            }
            Capture(window, "glossary-sync-trigger-open");
            Capture((FrameworkElement)syncPopup.Child, "glossary-sync-popup");
            syncMenu.IsSubmenuOpen = false;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(!syncPopup.IsOpen, "cloud sync dropdown closes without executing commands");
            vm.OpenTermDrawer(vm.TermDraft.First(t => t.SourceKind == DwgTranslator.Core.Models.GlossarySource.User));
            await Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
            var editorDrawer = (FrameworkElement)glossaryPage.FindName("TermEditorDrawer");
            var sourceInput = FindVisuals<System.Windows.Controls.TextBox>(editorDrawer).First();
            sourceInput.Text += "-编辑验收";
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(vm.HasUnsavedTerms && vm.IsTermDrawerOpen, "typing immediately updates term dirty label");
            Check(FindVisuals<System.Windows.Controls.Button>(glossaryPage).First(b => Equals(b.Content, "保存到本机")).IsEnabled, "term save enabled after typing");
            vm.DiscardTermsCommand.Execute(null);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(!vm.HasUnsavedTerms && !FindVisuals<System.Windows.Controls.Button>(glossaryPage).First(b => Equals(b.Content, "保存到本机")).IsEnabled, "term discard clears dirty state and disables save");
            await VerifyCloudGlossaryAsync(window, vm);
            await Task.Delay(4200);
            await VerifyAnnouncementCaptionAsync(window, vm);
            await VerifyUpdatesAsync(window, vm);
            await VerifyWorkspaceUi(window, vm);
            foreach (var layout in new[] { (1366d,768d,1d), (1920d,1080d,1d), (1920d,1080d,1.25d), (2560d,1440d,1.5d) })
                foreach (var page in new[] { "translate", "batch", "glossary", "account", "settings" })
                {
                    window.Width = layout.Item1 / layout.Item3; window.Height = layout.Item2 / layout.Item3;
                    vm.CurrentPage = page;
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    // The current monitor may clamp native window size. Arrange the real page tree
                    // at the requested DIP size; this is not a physical-DPI acceptance test.
                    var content = (FrameworkElement)window.Content;
                    var size = new Size(layout.Item1 / layout.Item3, layout.Item2 / layout.Item3);
                    content.Width = size.Width; content.Height = size.Height;
                    content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
                    Check(Math.Abs(content.ActualWidth - size.Width) < 2, "effective viewport width " + page);
                    var headers = FindVisuals<DwgTranslator.App.Views.Controls.PageHeader>(window).Where(x => x.IsVisible).ToList();
                    Check(headers.Count == 1 && Math.Abs(headers[0].ActualHeight - 86) < 1, "one shared 86 DIP header " + page);
                    Check(Math.Abs(headers[0].TranslatePoint(new Point(), content).X - 252) < 3, "shared 28 DIP page inset " + page);
                    var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var image = File.Create(Path.Combine(Path.GetFullPath(Environment.GetEnvironmentVariable("DWGC2E_UI_SMOKE_OUTPUT") ?? "artifacts/ui-smoke"), $"matrix-{page}-{layout.Item1}-{layout.Item3}.png"));
                    encoder.Save(image);
                    if (page == "settings")
                    {
                        var settingsPage = FindVisual<DwgTranslator.App.Views.Pages.SettingsPage>(window);
                        for (var section = 0; section < 6; section++)
                        {
                            vm.SettingsSection = section;
                            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                            content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
                            var form = (FrameworkElement)settingsPage.FindName("SettingsContent");
                            var footer = (FrameworkElement)settingsPage.FindName("SettingsSaveBar");
                            Check(form.ActualWidth <= (section == 5 ? 820 : 740) && form.ActualWidth > 400, "settings form bounded " + section);
                            Check(footer.IsVisible == (section != 5), "settings footer visibility " + section);
                            if (section != 5)
                            {
                                Check(Math.Abs(form.ActualWidth - footer.ActualWidth) < 2, "settings footer aligns with form " + section);
                                Check(footer.TranslatePoint(new Point(0, footer.ActualHeight), content).Y <= size.Height - 30, "settings footer stays reachable " + section);
                            }
                            Capture(window, $"matrix-settings-section-{section}-{layout.Item1}-{layout.Item3}");
                        }
                    }
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
            var failureDraft = Path.Combine(AppDataDir, "must-survive-write-failure");
            vm.SettingsDraft.ExportDirectory = failureDraft;
            using (var locked = new FileStream(Path.Combine(AppDataDir, "settings.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(!vm.SaveSettingsPage() && vm.SettingsDraft.ExportDirectory == failureDraft, "write failure preserves draft");
            vm.DiscardSettingsChangesCommand.Execute(null);
            await VerifyBilling(window);
            await VerifyBillingPersistence(window);
            await VerifyBillingCatalog(window);
            Console.WriteLine("UI_SMOKE=PASS (controlled API; rendered screenshots, not physical DPI validation)");
            window.Close(); Shutdown(0);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); Shutdown(1); }
    }
}
public sealed class FakeApi : IApiClient, IBillingClient, IAccountSessionClient
{
    public Task<BillingEntitlements> GetBillingEntitlementsAsync(CancellationToken ct=default)=>BillingAuthExpired?Task.FromException<BillingEntitlements>(new ApiAuthenticationException("token_expired")):FailBilling?Task.FromException<BillingEntitlements>(new BillingException("offline","controlled billing outage")):Task.FromResult(new BillingEntitlements{UserId="simulated-account",Subscription=new(){PlanName="pro"},Usage=new(){MonthlyQuota=1000000}});
    public Task<BillingPlans> GetBillingPlansAsync(CancellationToken ct=default)=>throw new NotSupportedException();
    public Task<BillingOrders> GetBillingOrdersAsync(string? before=null,CancellationToken ct=default)=>throw new NotSupportedException();
    public Task<BillingOrder> GetBillingOrderAsync(string no,CancellationToken ct=default)=>throw new NotSupportedException();
    public Task<BillingOrder> ConfirmBillingOrderAsync(string no,CancellationToken ct=default)=>throw new NotSupportedException();
    public Task<BillingOrder> CheckoutAsync(string plan,string channel,string key,CancellationToken ct=default)=>throw new NotSupportedException();
    public Func<bool>? OnLogout; public int LogoutCalls;
    public Task<bool> LogoutAsync(CancellationToken cancellationToken = default) { LogoutCalls++; return Task.FromResult(OnLogout?.Invoke() ?? true); }
    public bool Configured = true; public bool IsConfigured => Configured; public string ModeName => "worker";
    public bool FailLogin, Offline, Expired, FailBilling, BillingAuthExpired; public int TranslationCalls, LoginCalls;
    public Task<LoginResult> LoginAsync(string account, string password, CancellationToken cancellationToken = default) { LoginCalls++; return Task.FromResult(new LoginResult { Success=!FailLogin, Token=FailLogin?null:"simulation-only", ExpiresAt=DateTime.UtcNow.AddHours(1) }); }
    public Task<ProfileInfo?> GetProfileAsync(CancellationToken cancellationToken = default) { if (Expired) throw new ApiAuthenticationException("token_expired"); if (Offline) throw new IOException("controlled offline"); return Task.FromResult<ProfileInfo?>(new() { DisplayName="模拟验收账号", Email="qa@example.invalid" }); }
    public Task<SubscriptionInfo?> GetSubscriptionAsync(CancellationToken cancellationToken = default) => Task.FromResult<SubscriptionInfo?>(new() { PlanName="模拟测试套餐" });
    public Task<UsageInfo?> GetUsageAsync(CancellationToken cancellationToken = default) => Task.FromResult<UsageInfo?>(new() { MonthlyQuota=10000, Used=10 });
    public Task<IReadOnlyList<DeviceInfo>?> GetDevicesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DeviceInfo>?>(new[] { new DeviceInfo { DeviceName="模拟设备", DeviceId="test", IsCurrent=true } });
    public Task<TranslationBatchResult> TranslateAsync(TranslationBatchRequest request, CancellationToken cancellationToken = default) { TranslationCalls++; throw new InvalidOperationException("Paid operation forbidden in UI smoke"); }
    public Task<DeviceBindResult> BindDeviceAsync(string deviceId, string deviceName, CancellationToken cancellationToken = default) => Task.FromResult(new DeviceBindResult { Success=true });
    public Task<bool> RevokeDeviceAsync(string deviceId, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public VersionInfo? UpdateResponse = new() { LatestVersion="2.1.0-test" };
    public bool FailUpdate; public int UpdateCalls;
    public Task<VersionInfo?> CheckVersionAsync(string currentVersion, CancellationToken cancellationToken = default)
    { UpdateCalls++; return FailUpdate ? Task.FromException<VersionInfo?>(new IOException("controlled update outage")) : Task.FromResult(UpdateResponse); }
    public Task<IReadOnlyList<CloudGlossaryEntry>?> GetGlossaryAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CloudGlossaryEntry>?>(Array.Empty<CloudGlossaryEntry>());
    public Task<bool> PutGlossaryAsync(IReadOnlyList<CloudGlossaryEntry> entries, CancellationToken cancellationToken = default) => Task.FromResult(true);
}

public sealed class FakeBilling : IBillingClient
{
 public Action? OnCheckout;public string UserId="billing-ui-user";
 public int Creates;public string? LastKey;public bool FailSnapshot;public bool PaymentsEnabled=true;
 public BillingPlans? Catalog; public bool FailPlans;
 public BillingOrder Order=new(){OrderNo="DW"+new string('a',32),PlanId="p",PlanName="隔离验收套餐（不可付款）",Status="pending",CreateState="ready",PayableCents=19,Channel="alipay",QrCode="LOCAL ACCEPTANCE ONLY - NOT A PAYMENT",ExpiresAt=DateTimeOffset.UtcNow.AddMinutes(10),AllowedActions=new(){Pay=true,Confirm=true}};
 public Task<BillingPlans> GetBillingPlansAsync(CancellationToken ct=default)=>FailPlans?Task.FromException<BillingPlans>(new BillingException("invalid_response","套餐响应不完整，请刷新套餐后重试。")):Task.FromResult(Catalog??new BillingPlans{PaymentsEnabled=PaymentsEnabled,Plans=PaymentsEnabled?[new(){Id="p",Name="隔离验收套餐",PriceCents=19,DurationDays=7}]:[]});
 public Task<BillingOrders> GetBillingOrdersAsync(string? before=null,CancellationToken ct=default)=>Task.FromResult(new BillingOrders{Orders=Creates>0?[Order]:[]});
 public Task<BillingOrder> GetBillingOrderAsync(string no,CancellationToken ct=default)=>Task.FromResult(Order);
 public Task<BillingOrder> CheckoutAsync(string p,string channel,string key,CancellationToken ct=default){Creates++;LastKey=key;OnCheckout?.Invoke();return Task.FromResult(Order);}
 public Task<BillingOrder> ConfirmBillingOrderAsync(string no,CancellationToken ct=default)=>Task.FromResult(Order);
 public Task<BillingEntitlements> GetBillingEntitlementsAsync(CancellationToken ct=default)=>FailSnapshot?Task.FromException<BillingEntitlements>(new BillingException("offline","模拟断网")):Task.FromResult(new BillingEntitlements{UserId=UserId,Subscription=new(){PlanName=Order.Status=="paid"?"pro":"free"},Usage=new(){MonthlyQuota=1000000,Used=321}});
}
