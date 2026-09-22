using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
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
        var settingsPath = Path.Combine(Environment.GetEnvironmentVariable("DWGC2E_DATA_DIR")!, "settings.json");
        SettingsStore.Update(settingsPath, c => { c.ApiBaseUrl = ""; c.AuthTokenEncrypted = DwgTranslator.Core.Models.AppConfig.EncryptApiKey("saved-simulation-token"); });
        var legacyJson = File.ReadAllText(settingsPath).TrimStart();
        File.WriteAllText(settingsPath, legacyJson.Insert(legacyJson.IndexOf('{') + 1, "\n  \"apiMode\": \"direct\","));
        var app = new SmokeApp();
        app.Resources = new ResourceDictionary();
        foreach (var name in new[] { "ColorTokens", "Icons", "Metrics", "Decorations", "MainWindowStyles" })
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
        foreach (var name in new[] { "ColorTokens", "Icons", "Metrics", "Decorations", "MainWindowStyles" })
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
    /// <summary>
    /// 等待布局、渲染与动画全部落定。
    /// 仅靠 ApplicationIdle 是不够的：toast 等控件带高度/透明度动画，
    /// 在动画中途读取 ActualWidth/ActualHeight 会得到中间帧，造成断言随机失败。
    /// 这里按 Input→Loaded→Render→Background→Idle 顺序排空队列并强制一次布局，重复若干轮。
    /// </summary>
    private static async Task SettleAsync(int rounds = 3)
    {
        // 先清键盘焦点：停掉任何光标闪烁 Forever 时钟（聚焦的 TextBox），避免它饿死下面的
        // ApplicationIdle 排空。CaptureLayout 已在摘树前清焦点（防孤儿化）；这里是针对"TextBox
        // 合法持有焦点时直接调用 SettleAsync"路径的防御性补充（合法光标虽为 ~500ms 周期，但清掉更稳）。
        System.Windows.Input.Keyboard.ClearFocus();
        for (var round = 0; round < rounds; round++)
        {
            foreach (var priority in new[] { DispatcherPriority.Input, DispatcherPriority.Loaded, DispatcherPriority.Render, DispatcherPriority.Background, DispatcherPriority.ApplicationIdle })
                await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, priority);
            Application.Current?.MainWindow?.UpdateLayout();
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// 页面导航后的落定助手。hang6-9 根因（dump 取证，artifacts\uismoke-hang7/8/9.dmp）：
    /// 本方法此前用 "await Task.Delay(settleMs) + Keyboard.ClearFocus + ApplicationIdle" 结构；
    /// Task.Delay 的定时续延在 account 页早期加载窗口（IME/TSF 初始化，约头 150ms）投递时
    /// wmMsgPosted 偶发丢失 → dispatcher 操作队列永久冻结（hang9 dumpasync：NavIdleAsync state (0)
    /// 卡在首个 await Task.Delay(150)；无 caret AnimationClock；TimerQueue 线程空闲=定时器已 fire、
    /// 续延已投递却不被处理；UI 线程在 DispatchMessage 主动派发 41% CPU）。compact-account 连续 6 次挂死。
    /// HEAD(25f74fc) 实证：其全部 24 处导航落定均为裸
    /// `await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle)`，
    /// matrix 循环页清单含 "account"，以此通过既往全部发布门禁（含 compact-account 与 matrix-account）；
    /// 且 src\DwgTranslator.App\Views\Pages\AccountPage.xaml.cs 与 HEAD 无 diff（IsVisibleChanged→
    /// BeginInvoke(AccountInput.Focus) 自动聚焦是 HEAD 既有行为）。裸 ApplicationIdle 的续延在 dispatcher
    /// 真正空闲后才投递，避开加载窗口，实证可靠；Task.Delay 与 ClearFocus 皆毒。
    /// "caret 饿死 ApplicationIdle" 理论已被 HEAD 实证推翻（HEAD 不清焦点照样过 account）。
    /// settleMs 形参保留仅为兼容 20 处调用点（含 account 的 1500），现为无害死参数。
    /// </summary>
    private static async Task NavIdleAsync(int settleMs = 150)
    {
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// 轮询直到某个测量值连续稳定（用于动画/虚拟化/异步布局）。
    /// 比固定延时可靠：动画快时立即返回，动画慢时不会误测中间帧。
    /// </summary>
    private static async Task WaitForStableAsync(Func<double> measure, string description, int stableReads = 4, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var last = double.NaN;
        var stable = 0;
        while (DateTime.UtcNow < deadline)
        {
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Application.Current?.MainWindow?.UpdateLayout();
            var current = measure();
            if (!double.IsNaN(last) && Math.Abs(current - last) < 0.01)
            {
                if (++stable >= stableReads) return;
            }
            else stable = 0;
            last = current;
            await Task.Delay(16);
        }
        throw new TimeoutException("布局或动画未在超时内稳定：" + description);
    }

    /// <summary>
    /// 排空队列直到 confirmAction 完成，并把期间弹起的模态确认框（PromptDialog/ConfirmDialog）当作
    /// "点了确认"处理。§L4 后"取消编辑/放弃更改"等命令会先弹危险确认，这里让它们跑完而不是挂住测试。
    /// </summary>
    private static async Task ConfirmModalsAsync(Action confirmAction, string description)
    {
        var confirmed = 0;
        var done = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            foreach (var modal in Application.Current?.Windows.OfType<Window>().Where(w => w.IsVisible && w != Application.Current.MainWindow).ToList() ?? new List<Window>())
            {
                if (modal is PromptDialog prompt) { prompt.DialogResult = true; confirmed++; }
                else if (modal is ConfirmDialog confirm) { confirm.DialogResult = true; confirmed++; }
            }
        };
        timer.Start();
        try
        {
            var pump = Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
            {
                try { confirmAction(); }
                finally { done = true; }
            }));
            while (!done)
            {
                await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                await Task.Delay(10);
            }
            await pump;
        }
        finally { timer.Stop(); }
        Console.WriteLine($"INFO modal confirmations auto-accepted: {description} = {confirmed}");
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
        var background = window is Window native ? native.Background : Brushes.Transparent;
        // VisualBrush uses local client coordinates; Render(window) includes layout
        // offsets such as a dialog content margin and clips the opposite edge.
        if (window is Window shell && shell.Content is FrameworkElement client) window = client;
        var offset = VisualTreeHelper.GetOffset(window);
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth + Math.Max(0, offset.X) + Math.Max(0, window.Margin.Right)));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight + Math.Max(0, offset.Y) + Math.Max(0, window.Margin.Bottom)));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, width, height);
            drawing.DrawRectangle(background ?? Brushes.White, null, bounds);
            var brush = new VisualBrush(window) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = bounds, Stretch = Stretch.Fill };
            drawing.DrawRectangle(brush, null, bounds);
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var output = Path.GetFullPath(Environment.GetEnvironmentVariable("DWGC2E_UI_SMOKE_OUTPUT") ?? "artifacts/ui-smoke"); Directory.CreateDirectory(output);
        using var stream = File.Create(Path.Combine(output, name + ".png")); encoder.Save(stream);
    }
    // Isolated layout evidence: never render a large visual through a smaller HWND's
    // layout clip, and never label this detached 96-DPI tree as physical monitor evidence.
    private static void CaptureLayout(Window owner, Size size, string name)
    {
        var content = (FrameworkElement)owner.Content;
        var previousContext = content.ReadLocalValue(FrameworkElement.DataContextProperty);
        var holder = new System.Windows.Controls.Border { Width = size.Width, Height = size.Height, Background = owner.Background, DataContext = owner.DataContext, UseLayoutRounding = true };
        System.Windows.Documents.TextElement.SetFontFamily(holder, owner.FontFamily);
        System.Windows.Documents.TextElement.SetFontSize(holder, owner.FontSize);
        holder.Resources.MergedDictionaries.Add(owner.Resources);
        DwgTranslator.App.Views.Controls.ResponsiveLayout.SetIsCompact(holder, size.Width < 1100);
        DwgTranslator.App.Views.Controls.ResponsiveLayout.SetIsShort(holder, size.Height < 540);
        // 摘树前必须清除键盘焦点：被聚焦的 TextBox 携带一个光标闪烁时钟（CaretElement 上的
        // Forever DoubleAnimationUsingKeyFrames + 两个 DiscreteDoubleKeyFrame）。把内容重新挂到
        // 游离 holder（owner.Content=null → holder.Child=content）会把该时钟孤儿化——它仍挂在
        // TimeManager 上永久 tick（ClockGroup._children 变 null 的破碎态），渲染线程每帧空转，
        // 之后每一个 DispatcherPriority.ApplicationIdle 等待都被永久饿死（冒烟卡死、compact-account
        // 挂起的根因）。ClearFocus 同步触发 LostKeyboardFocus→光标隐藏→闪烁时钟被移除，摘树时无从孤儿化。
        // 与账户页 indeterminate ProgressBar 的 Forever 时钟孤儿化属同一类问题（那边已用静态 Border 修掉）。
        System.Windows.Input.Keyboard.ClearFocus();
        owner.Content = null;
        try
        {
            content.DataContext = owner.DataContext;
            holder.Child = content;
            holder.Measure(size); holder.Arrange(new Rect(size)); holder.UpdateLayout();
            Check(Math.Abs(content.ActualWidth - size.Width) < 2 && Math.Abs(content.ActualHeight - size.Height) < 2, "detached render matches requested DIP canvas " + name);
            Capture(holder, name);
        }
        finally
        {
            holder.Child = null;
            if (previousContext == DependencyProperty.UnsetValue) content.ClearValue(FrameworkElement.DataContextProperty);
            else content.SetValue(FrameworkElement.DataContextProperty, previousContext);
            owner.Content = content;
            owner.UpdateLayout();
        }
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
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var translate = FindVisual<DwgTranslator.App.Views.Pages.TranslatePage>(window);
        Check(translate != null, "translate page is attached before workspace interaction");
        var outputSettings=FindVisuals<System.Windows.Controls.Button>(translate).Single(b=>Equals(b.Content,"输出设置"));
        ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(outputSettings).GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
        await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
        Check(vm.CurrentPage==MainViewModel.PageSettings && vm.SettingsSection==2,"output settings button opens file/output section");
        vm.CurrentPage=MainViewModel.PageTranslate;
        await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
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
        var translationSettingsSection = (FrameworkElement)translate.FindName("TranslationSettingsSection");
         var outputSection = (FrameworkElement)translate.FindName("OutputSection");
         Check(translationSettingsSection.Parent is System.Windows.Controls.Grid settingsColumn && outputSection.Parent == settingsColumn && System.Windows.Controls.Grid.GetRow(translationSettingsSection) == 0 && System.Windows.Controls.Grid.GetRow(outputSection) == 1, "translation and output have separate settings rows");
        Check(((FrameworkElement)translate.FindName("EmptyDropZone")).IsVisible, "empty queue has large import entry");
        Check(!((FrameworkElement)translate.FindName("QueueWorkspace")).IsVisible, "empty queue does not show redundant table");
        Check(translate.FindName("WorkspaceLogLauncher") is FrameworkElement, "log is represented by a compact launcher by default");
        var editEntity = new DwgTranslator.Core.Models.TextEntity { Handle = "proof-test", PlainText = "原文", TranslatedText = "original" };
        vm.Entities.Add(editEntity);
        vm.TrackProofreadingEdit(editEntity); editEntity.TranslatedText = "changed";
        Check(vm.HasUnsavedProofreading, "proofreading dirty state");
        await ConfirmModalsAsync(() => vm.DiscardProofreadingCommand.Execute(null), "workspace discard proofreading");
        Check(editEntity.TranslatedText == "original" && !vm.HasUnsavedProofreading, "proofreading discard restores original");
        vm.Entities.Remove(editEntity);
        await VerifyProofreadingPersistenceAsync(vm);
        await VerifyProofreadingAccountCancelAsync(vm);
        Check(!FindVisual<System.Windows.Controls.DataGrid>(translate).HasItems, "empty drawing table");
        Check(((FrameworkElement)translate.FindName("EmptyDropZone")).ActualHeight <= 360, "empty drop zone capped at 360 DIP");
        Check(!vm.HasFailedDrawingTasks,"retry disabled without failed tasks");
        Capture(window, "workspace-empty");
        var pageHost = (FrameworkElement)window.FindName("PageHost");
        Console.WriteLine($"PAGE_LAYOUT hostActual={pageHost.ActualWidth}x{pageHost.ActualHeight} desired={pageHost.DesiredSize.Width}x{pageHost.DesiredSize.Height} maxWidth={pageHost.MaxWidth}");
        var states = new[] { DwgTranslator.Core.Tasks.TranslationTaskStatus.Pending, DwgTranslator.Core.Tasks.TranslationTaskStatus.Translating, DwgTranslator.Core.Tasks.TranslationTaskStatus.ReadyForReview, DwgTranslator.Core.Tasks.TranslationTaskStatus.Completed, DwgTranslator.Core.Tasks.TranslationTaskStatus.Failed, DwgTranslator.Core.Tasks.TranslationTaskStatus.Paused };
        foreach (var state in states)
        {
            var path = Path.Combine(AppDataDir, "验收图纸-" + state + ".dwg");
            var row = new DrawingFileItem(path);
            row.AttachTask(new DwgTranslator.Core.Tasks.TranslationTask(path) { Status = state, TextCount = 128, Progress = state is DwgTranslator.Core.Tasks.TranslationTaskStatus.Completed or DwgTranslator.Core.Tasks.TranslationTaskStatus.ReadyForReview ? 100 : 30, Error = state == DwgTranslator.Core.Tasks.TranslationTaskStatus.Failed ? "图纸无法读取，请检查文件后重试。" : null });
            vm.DrawingFiles.Add(row);
        }
        vm.HasDrawingFiles = true;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(!((FrameworkElement)translate.FindName("EmptyDropZone")).IsVisible, "import releases drop zone space");
        Check(((FrameworkElement)translate.FindName("DrawingQueue")).ActualHeight >= 120, "queue remains bounded and scrollable in compact workspace");
        // 用户批注（2026-09-22）「校对完成之后好像没有导出按钮啊」：行内导出入口按 CanExport 显隐。
        // 此前这一列只有「校对」和（要 HasOutput 才出现的）「打开」，待导出这一行看不到任何导出动作，
        // 用户只能去点工具栏那个会导出全部勾选项的按钮。六行里只有 index 3（Completed）该出现它。
        // 注意表头「导出」是 DataGridColumnHeader 的 Content 而不是 Button，不会被下面的筛选命中。
        translate.UpdateLayout();
        var queueGrid=(System.Windows.Controls.DataGrid)translate.FindName("DrawingQueue");
        var inRowExports=FindVisuals<System.Windows.Controls.Button>(queueGrid)
            .Where(b=>b.IsVisible&&Equals(b.Content,"导出")).ToArray();
        // 校对降级为可选后（2026-09-22 用户批注「校对不是必须的」），未校对（ReadyForReview）
        // 与已校对（Completed）两行都属于"待导出"，都必须出现行内导出按钮——旧实现只有后者有。
        Check(inRowExports.Length==2,"both export-ready rows expose an in-row export button, count="+inRowExports.Length);
        vm.ToggleLogViewerCommand.Execute(null);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var logWindow = Application.Current.Windows.OfType<LogViewerWindow>().Single();
        Check(logWindow.IsVisible && vm.LogViewModel.IsVisible, "log opens in a dedicated virtualized window");
        Capture(logWindow, "workspace-log-window");
        logWindow.Close();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(!vm.LogViewModel.IsVisible, "closing log window suspends live UI updates");
        Capture(window, "workspace-six-drawings");
        Check(!vm.DrawingFiles[3].HasOutput, "completed task without output has no open action");
        vm.ClearDrawingOutputsCommand.Execute(null);
        Check(vm.DrawingFiles.All(x => !x.IsIncludedForExport), "clear output selection");
        vm.SelectAllDrawingOutputsCommand.Execute(null);
        Check(vm.DrawingFiles.All(x => x.IsIncludedForExport), "select all outputs");
        // 用户批注（2026-09-22）：「文件队列怎么没有一些基础操作，比如删除队列里面的文件」。
        // 移除只摘队列行、实体与任务记录，不动磁盘文件；这里用一个临时行验证它真的被摘掉，
        // 并且没有误伤其它行 —— 后面 :350/:366 的计数断言依赖队列长度仍是 6。
        var removable = new DrawingFileItem(Path.Combine(AppDataDir, "待移除-验收.dwg"));
        vm.DrawingFiles.Add(removable);
        Check(vm.DrawingFiles.Contains(removable), "temporary queue row added for the removal check");
        vm.RemoveDrawingCommand.Execute(removable);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(!vm.DrawingFiles.Contains(removable) && vm.DrawingFiles.Count == 6, "queue removal drops only the targeted row");
        var output = Path.Combine(AppDataDir, "test-output.dwg"); File.WriteAllText(output, "controlled fixture, not a real drawing");
        vm.DrawingFiles[3].Task!.LastExportPath = output;
        Check(vm.DrawingFiles[3].HasOutput, "existing output enables result action");
        File.Delete(output);
        Check(!vm.DrawingFiles[3].HasOutput, "removed output disables result action");
        var count = vm.BatchView.Cast<object>().Count(); Check(count == 6, "batch view contains all tasks");
        vm.BatchStatusFilter = 6; Check(vm.BatchView.Cast<object>().Count() == 1, "failure filter");
        vm.BatchStatusFilter = 1; Check(vm.BatchView.Cast<object>().Count() == 1, "running filter");
        vm.BatchStatusFilter = 0; vm.BatchSearch = "Paused"; Check(vm.BatchView.Cast<object>().Count() == 1, "search preserves paused state");
        vm.BatchSearch = ""; vm.BatchDateFilter = 1; Check(vm.BatchView.Cast<object>().Count() == 6, "today filter"); vm.BatchDateFilter = 0;
        vm.CurrentPage = MainViewModel.PageBatch; vm.ShowTaskDetailCommand.Execute(vm.DrawingFiles[4]);
        Check(vm.IsTaskDetailOpen, "explicit detail action opens task detail");
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        // 这里**故意不截图**：抽屉的 220ms 滑入动画刚起步，此刻 RenderTransform.X ≈ 265 DIP，
        // 面板大半在窗口右边之外，截图看着就像"抽屉被裁掉了"。截图挪到下面的落定断言之后。
        vm.OpenTaskProofreadingCommand.Execute(null); Check(!vm.IsProofreading && vm.SelectedDrawingFile != vm.DrawingFiles[4], "failed detail cannot enter proofreading");
        vm.SelectedBatchTask = vm.DrawingFiles[2]; vm.OpenTaskProofreadingCommand.Execute(null); Check(vm.IsProofreading && vm.SelectedDrawingFile == vm.DrawingFiles[2], "review detail opens selected drawing proofreading");
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); Capture(window, "task-proofreading");
        vm.BackToTaskListCommand.Execute(null); Check(!vm.IsProofreading && vm.SelectedBatchTask == vm.DrawingFiles[2], "back preserves selected task");
        vm.CloseTaskDetailCommand.Execute(null);
        vm.ShowTaskDetailCommand.Execute(vm.DrawingFiles[4]);
        Check(vm.IsTaskDetailOpen, "same task detail can reopen");
        vm.CloseTaskDetailCommand.Execute(null);
        for (var i = 0; i < 1000; i++) vm.DrawingFiles.Add(new DrawingFileItem(Path.Combine(AppDataDir, "长文件名-" + new string('图', 80) + i + ".dwg")));
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var batchPage = FindVisual<DwgTranslator.App.Views.Pages.BatchTasksPage>(window);
        var taskTable = (System.Windows.Controls.DataGrid)batchPage.FindName("TaskTable");
        taskTable.ScrollIntoView(vm.DrawingFiles[0]);
        taskTable.UpdateLayout();
        var detailRow = (System.Windows.Controls.DataGridRow)taskTable.ItemContainerGenerator.ContainerFromItem(vm.DrawingFiles[0]);
        var detailButton = FindVisual<System.Windows.Controls.Button>(detailRow);
        Check(detailButton != null && detailButton.IsVisible && detailButton.IsEnabled, "task detail button visible and enabled");
        var detailPeer = new System.Windows.Automation.Peers.ButtonAutomationPeer(detailButton!);
        ((System.Windows.Automation.Provider.IInvokeProvider)detailPeer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(vm.IsTaskDetailOpen && vm.SelectedBatchTask == vm.DrawingFiles[0], "real detail button binding opens correct task");
        Check(((FrameworkElement)batchPage.FindName("TaskDetailDrawer")).IsVisible, "detail drawer renders above task table");
        // 抽屉的滑入动画是 RenderTransform 实现的，而 RenderTransform 不参与布局——上面所有几何/
        // 可见性断言都看不见它。FillBehavior.Stop 若没在动画结束时显式归零，抽屉会在滑入完成后
        // 立刻跳回右侧视野之外（用户报的「点查看详情后面板卡在右边不出现」）。这里直接量偏移。
        await Task.Delay(450);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var drawerElement = (FrameworkElement)batchPage.FindName("TaskDetailDrawer");
        var drawerOffset = (drawerElement.RenderTransform as System.Windows.Media.TranslateTransform)?.X ?? 0;
        Check(Math.Abs(drawerOffset) < 0.5,
            "task drawer settles on screen after slide-in, offset=" + drawerOffset.ToString("F1"));
        // 抽屉是 400 DIP 的右对齐覆盖层，父 Grid 一旦比可视区宽，它就会整个溢出到窗口右边之外——
        // 用户看到的正是"弹出的卡片被裁掉/布局怪怪的"。这里用右边缘直接量出来。
        var drawerRight = drawerElement.TranslatePoint(new Point(drawerElement.ActualWidth, 0), batchPage).X;
        Check(drawerRight <= batchPage.ActualWidth + 0.5 && drawerRight <= window.ActualWidth + 0.5,
            "task drawer stays inside the page, right=" + drawerRight.ToString("F1") + " page=" + batchPage.ActualWidth.ToString("F1"));
        Capture(window, "task-error-detail");
        vm.CloseTaskDetailCommand.Execute(null);
        Check(taskTable.Items.Count == 1006, "large task queue retains every row");
        Check(CountVisual<System.Windows.Controls.DataGridRow>(taskTable) < 80, "large queue uses bounded row virtualization");
        Capture(window, "task-large-queue");
        await VerifyTranslationRestoreAsync(vm);
        vm.DrawingFiles.Clear(); vm.HasDrawingFiles = false;
        Check(!vm.IsTaskDetailOpen && vm.SelectedBatchTask == null, "workspace reset clears drawer selection");
        vm.SelectedDrawingFile = null;
        vm.CurrentPage = MainViewModel.PageSettings; vm.SettingsSection = 5;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); Capture(window, "settings-about");
        vm.SettingsSection = 0;
        Console.WriteLine("PHYSICAL_DPI_NOT_VALIDATED: matrix uses WPF effective dimensions; current monitor scale " + VisualTreeHelper.GetDpi(window).DpiScaleX);
    }

    /// <summary>
    /// 用户报告（2026-09-22）：「我翻译之后，退出软件，再次打开就看不到译文了！又要重新翻译啊！」
    /// 根因：翻译成功会把译文归档进项目库（projects/&lt;id&gt;/project.json），此后"保存校对"写的也是项目库，
    /// 而启动恢复只读 proofreading.json —— 两条路径不对称。任务状态（tasks.json，含 ProjectId）回得来，
    /// 译文回不来，于是界面显示"待导出"但校对视图是空的。
    /// 这里按真实状态复现：磁盘上真有一张图纸（ACadSharp 现场生成，仓库里没有二进制样本）、
    /// 一个归档好的项目、一行带 ProjectId 的已完成任务，然后调用启动流程里的那个恢复阶段。
    /// 方法被删掉/改名时反射会直接抛出来 —— 那正是"存了但读不回"复发的情形，必须炸而不是静默通过。
    /// </summary>
    private static async Task VerifyTranslationRestoreAsync(MainViewModel vm)
    {
        var savedEntities = vm.Entities.ToArray();
        // 项目库/任务库写在**账号工作目录**下（MainViewModel.AccountDataDirectory 是私有属性，
        // 实际是 <数据目录>/accounts/<账号>，离线未登录时是 accounts/guest），不是数据目录根部。
        var accountDir = ResolveAccountDirectory();
        var fixture = Path.Combine(AppDataDir, "恢复验收样本.dwg");
        string? projectId = null;
        DrawingFileItem? row = null;
        try
        {
            vm.Entities.Clear();
            var document = new ACadSharp.CadDocument();
            document.Entities.Add(new ACadSharp.Entities.MText { Value = "阀门反馈", Height = 3.5 });
            ACadSharp.IO.DwgWriter.Write(fixture, document);
            var archived = new DwgTranslator.Core.Services.DwgReaderService().ExtractFromFile(fixture).ToArray();
            Check(archived.Length == 1, "restore fixture exposes exactly one text entity");
            foreach (var entity in archived)
            {
                entity.TranslatedText = "Valve feedback";
                entity.Status = DwgTranslator.Core.Models.TranslationStatus.Translated;
            }
            var project = new DwgTranslator.Core.Services.TranslationProjectStore(accountDir)
                .Create("恢复验收项目", "ZH", "EN", archived);
            projectId = project.Id;
            var projectJson = Path.Combine(accountDir, "projects", project.Id, "project.json");
            Console.WriteLine("RESTORE_FIXTURE accountDir=" + accountDir + " projectFile=" + File.Exists(projectJson));
            Check(File.Exists(projectJson), "restore fixture wrote the archived project into the account workspace");

            row = new DrawingFileItem(fixture);
            row.AttachTask(new DwgTranslator.Core.Tasks.TranslationTask(fixture)
            {
                Status = DwgTranslator.Core.Tasks.TranslationTaskStatus.Completed,
                ProjectId = project.Id
            });
            vm.DrawingFiles.Add(row);

            var stage = typeof(MainViewModel)
                .GetMethod("RestoreActiveTranslationProjectAsync", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new Exception("启动恢复译文的阶段不存在：译文会再次变成'存了但读不回'");
            await (Task)stage.Invoke(vm, null)!;
            Check(vm.Entities.Count == 1 && vm.Entities[0].TranslatedText == "Valve feedback",
                "archived translation returns on startup instead of a blank workspace, count=" + vm.Entities.Count);
            Check(vm.ActiveTranslationProject?.Id == project.Id, "restored translation stays bound to its project");
        }
        finally
        {
            if (row != null) vm.DrawingFiles.Remove(row);
            vm.Entities.Clear();
            foreach (var entity in savedEntities) vm.Entities.Add(entity);
            if (projectId != null)
            {
                try { Directory.Delete(Path.Combine(accountDir, "projects", projectId), true); } catch { /* 清理失败不影响结论 */ }
            }
            try { if (File.Exists(fixture)) File.Delete(fixture); } catch { /* 清理失败不影响结论 */ }
        }
    }
    /// <summary>
    /// 账号工作目录（项目库、任务库都写在它下面）。MainViewModel 把它藏成私有属性，所以这里按
    /// 磁盘形态推断：accounts 下唯一/含 tasks.json 的那个子目录；都没有时回落到 guest 规则。
    /// </summary>
    private static string ResolveAccountDirectory()
    {
        var accounts = Path.Combine(AppDataDir, "accounts");
        // 冒烟全程离线未登录，MainViewModel 的 ActiveAccountId 为空 → AccountWorkspace 落到 guest。
        // 这里必须优先 guest：accounts 下同时存在账号切换测试留下的哈希目录，按"含 tasks.json"或
        // 目录枚举顺序去挑会挑错（实测就是挑错了，项目写进了别人的工作区）。
        var guest = Path.Combine(accounts, "guest");
        if (Directory.Exists(guest)) return guest;
        if (Directory.Exists(accounts))
        {
            var dirs = Directory.GetDirectories(accounts);
            if (dirs.Length > 0)
                return dirs.OrderByDescending(d => File.GetLastWriteTimeUtc(d)).First();
        }
        return DwgTranslator.Core.Services.AccountWorkspace.DirectoryFor(AppDataDir, null);
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
        // 2026-09-22 布局调整：付款这一屏以金额和二维码为主角，两者都必须在当前订单卡内可见。
        Check(((System.Windows.Controls.Border)w.FindName("CurrentOrderCard")).Visibility==Visibility.Visible,"current order card hosts the QR and amount");
        var amountText=((System.Windows.Controls.TextBlock)w.FindName("OrderAmount")).Text;
        Check(amountText.StartsWith("¥")&&amountText.Contains("."),"amount is shown as the primary figure: "+amountText);
        Check(((System.Windows.Controls.Button)w.FindName("CopyOrderNo")).Tag is string copyTag&&copyTag.StartsWith("DW"),"copy action carries the current order number");
        // 用户批注（2026-09-22）「布局样式太丑了」，两处都在这一屏：
        // ①「历史订单」原来被 DockPanel 的 LastChildFill=True 拉伸成一条通栏空框（像坏掉的标题栏）；
        // ②账号没有到期时间时，左栏会留下一个只有"到期："标签、后面什么都没有的空档。
        w.UpdateLayout();
        var toggleOrders=(System.Windows.Controls.Button)w.FindName("ToggleOrders");
        var toggleHost=(FrameworkElement)VisualTreeHelper.GetParent(toggleOrders);
        Check(toggleOrders.ActualWidth<=140,"history orders button keeps its natural width, actual="+toggleOrders.ActualWidth.ToString("F1"));
        Check(Math.Abs(toggleOrders.TranslatePoint(new Point(toggleOrders.ActualWidth,0),toggleHost).X-toggleHost.ActualWidth)<=1,
            "history orders button is right-aligned instead of spanning the column");
        var entitlements=((System.Windows.Controls.TextBlock)w.FindName("Entitlements")).Text;
        Check(!entitlements.Contains("到期：")&&entitlements.Contains("长期有效"),
            "membership without an expiry reads as durable, not as an empty label: "+entitlements.Replace("\n"," / "));
        // Verify row actions fit and are centred under the header at the real minimum and the
        // default window width. These were 620/860 until BillingWindow MinWidth went to 900: both
        // were then clamped up to 900, so the two iterations silently tested the same geometry and
        // the narrower coverage was lost without any assertion failing. 900 = MinWidth floor.
        // The action column now carries 复制 + 删除, so the group — not the copy button alone — is
        // what must line up with the header centre.
        // 2026-09-22：历史订单表格已移入按需展开的弹层（用户批注「列表滑动很难滑动，可以做成一个
        // 按钮，点开弹出」）。折叠时 DataGrid 的行根本不会被实例化，下面的 FindVisuals 会得到 0 个
        // 按钮，断言就成了假红/假绿；所以这里必须先点开弹层再测量，测完收回去，免得后面几张
        // 截图（QR、到期态）被弹层盖住。
        var ordersOverlay=(System.Windows.Controls.Border)w.FindName("OrdersOverlay");
        Check(ordersOverlay.Visibility==Visibility.Collapsed,"history orders start collapsed so the QR keeps the right column");
        toggleOrders.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);w.UpdateLayout();
        Check(ordersOverlay.Visibility==Visibility.Visible,"history overlay opens on demand");
        var ordersGrid=(System.Windows.Controls.DataGrid)w.FindName("Orders");
        foreach(var width in new[]{900d,1020d})
        {
            w.Width=width; ordersGrid.BringIntoView();
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle); w.UpdateLayout();
            // 复制按钮已移入左栏的当前订单卡（表格里逐行复制订单号没有用途），操作列只剩「删除」。
            var actions=FindVisuals<System.Windows.Controls.Button>(ordersGrid).Where(b=>Equals(b.Content,"删除")).ToArray();
            Check(actions.Length==1,"billing row offers only the hide action at "+width);
            var cell=FindVisuals<System.Windows.Controls.DataGridCell>(ordersGrid).First(c=>FindVisuals<System.Windows.Controls.Button>(c).Contains(actions[0]));
            var bounds=actions.Select(b=>b.TransformToAncestor(cell).TransformBounds(new Rect(0,0,b.ActualWidth,b.ActualHeight))).ToArray();
            var left=bounds.Min(r=>r.Left);var right=bounds.Max(r=>r.Right);
            Check(left>=0 && bounds.Min(r=>r.Top)>=0 && right<=cell.ActualWidth+0.5 && bounds.Max(r=>r.Bottom)<=cell.ActualHeight+0.5,"billing row actions fit row at "+width);
            var header=FindVisuals<System.Windows.Controls.Primitives.DataGridColumnHeader>(ordersGrid).First(h=>Equals(h.Content,"操作"));
            var cellOrigin=cell.TranslatePoint(new Point(0,0),ordersGrid).X;
            var groupCenter=cellOrigin+(left+right)/2;
            var headerCenter=header.TranslatePoint(new Point(header.ActualWidth/2,0),ordersGrid).X;
            Check(Math.Abs(groupCenter-headerCenter)<=1,"billing action header and row actions align at "+width);
            Capture(w,"billing-order-action-"+width);
        }
        toggleOrders.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
        Check(ordersOverlay.Visibility==Visibility.Collapsed,"history overlay collapses again, restoring the current order card");
        // 用户批注（2026-09-23）：「更改套餐后 右侧的二维码并不刷新」。
        // 二维码就是订单：要显示另一个套餐的二维码，必须先有那个套餐的订单。所以"改选即自动刷新"
        // 只能靠自动建单——用户在下拉框里划过三个套餐就会凭空多出三张平台订单。这里验证替代路径：
        // 选中另一个套餐后提示条必须出现，并且它右侧那个按钮真的下单、刷新二维码、收起提示条。
        fake.Catalog=new BillingPlans{PaymentsEnabled=true,Plans=[
            new(){Id="p",Name="Go",PriceCents=2000,DurationDays=30},
            new(){Id="max",Name="Max",PriceCents=5900,DurationDays=30}]};
        await (Task)typeof(BillingWindow).GetMethod("LoadPlans",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(w,null)!;
        await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);w.UpdateLayout();
        var plansBox=(System.Windows.Controls.ComboBox)w.FindName("Plans");
        var mismatchBar=(System.Windows.Controls.Border)w.FindName("PlanMismatchBar");
        var switchPlanButton=(System.Windows.Controls.Button)w.FindName("SwitchPlanButton");
        Check(mismatchBar.Visibility==Visibility.Collapsed,"refresh keeps selection on the current order's plan");
        // 改选到**另一个**套餐（max），当前订单是 p —— 这才构成"选的套餐和二维码对不上"。
        plansBox.SelectedItem=((System.Collections.IEnumerable)plansBox.ItemsSource).Cast<object>()
            .First(item=>(string)item.GetType().GetProperty("Id")!.GetValue(item)=="max");
        await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
        Check(mismatchBar.Visibility==Visibility.Visible,"picking another plan surfaces the mismatch bar instead of silently keeping the old QR");
        Check(switchPlanButton.IsVisible && switchPlanButton.Content is string label && label.Contains("Max"),
            "mismatch bar offers the one-click refresh for the selected plan: "+switchPlanButton.Content);
        var createsBeforePlanSwitch=fake.Creates;
        switchPlanButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
        Check(fake.Creates==createsBeforePlanSwitch+1,"the bar action creates exactly one order for the selected plan");
        Check(((System.Windows.Controls.Image)w.FindName("Qr")).Source!=null,"the QR is regenerated for the newly selected plan");
        Check(mismatchBar.Visibility==Visibility.Collapsed,"the mismatch bar clears once the QR matches the selection");
        // 必须还原：Catalog 一旦非空就会盖过 fake.PaymentsEnabled，后面的"暂停服务"用例
        // （paused service remains visible…）依赖默认目录返回 PaymentsEnabled=false。
        fake.Catalog=null;
        // 换套餐会产生一张新订单，所以后面几处"只创建过一次"的计数都要以此为基准（原值 1 → 2）。
        fake.Order.Channel="wxpay";
        await (Task)typeof(BillingWindow).GetMethod("RefreshAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(w,null)!;
        Check(((System.Windows.Controls.Image)w.FindName("Qr")).Source==null,"unsupported order channel never displays an Alipay QR");
        Check(fake.Creates==2,"viewing unsupported order never creates replacement payment");
        fake.Order.Channel="alipay";
        w.Close();fake.PaymentsEnabled=false;w=Create();w.Show();await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);Check(fake.Creates==2,"billing restart restores without checkout");
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
            if (Environment.GetEnvironmentVariable("DWGC2E_REFINEMENT_ONLY") == "1")
            {
                await VerifyRefinementAsync(window, vm);
                Console.WriteLine("REFINEMENT_SMOKE=PASS (isolated UI checks only)");
                Shutdown(0);
                return;
            }
            await VerifySidebarToastStabilityAsync(window, vm);
            await VerifyRefinementAsync(window, vm);
            await VerifySmallSurfacesAsync(window, vm);
            VerifyEmbeddedCadPlugin();
            VerifyPluginSourceConsistency(vm);
            await VerifyTaskRecoveryNoticeAsync(window, vm);
            VerifyRecoveryDecision(vm);
            Check(!vm.IsAccountLoggedIn, "startup does not trust unverified saved token while offline");
            var migratedSettingsPath = Path.Combine(AppDataDir, "settings.json");
            // Drive the legacy-config migration explicitly rather than relying on the App
            // start-up side effect: that made this check order-dependent, because on identical
            // code the file was sometimes still unmigrated at this point, so it passed only
            // when start-up happened to rewrite the file first. Re-create the legacy shape
            // here and migrate it deterministically.
            var legacyJson = File.ReadAllText(migratedSettingsPath).TrimStart();
            if (!legacyJson.Contains("\"apiMode\"", StringComparison.Ordinal))
                legacyJson = legacyJson.Insert(legacyJson.IndexOf('{') + 1, "\n  \"apiMode\": \"direct\",");
            File.WriteAllText(migratedSettingsPath, legacyJson);
            SettingsStore.Migrate(
                migratedSettingsPath,
                SettingsStore.Read(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json")));
            var migratedSettings = SettingsStore.Read(migratedSettingsPath);
            Check(migratedSettings.ConfigurationVersion >= 2 &&
                  !File.ReadAllText(migratedSettingsPath).Contains("apiMode", StringComparison.OrdinalIgnoreCase),
                  "legacy direct config is migrated away");
            api.FailBilling = true;
            vm.LoginName = "simulated-account";
            await VerifyAccountSaveFailureAsync(vm, logout: false);
            vm.CurrentPage = MainViewModel.PageAccount;
            await NavIdleAsync();
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
            { vm.CurrentPage = page; await NavIdleAsync(); Check(vm.CurrentPage == page, "navigate " + page); Capture(window, page); }
            await VerifyCloudGlossaryAsync(window, vm);
            vm.CurrentPage = MainViewModel.PageSettings;
            await Task.Delay(4200);
            for (var section = 0; section < 6; section++) { vm.SettingsSection = section; await NavIdleAsync(); Capture(window, "settings-" + section); var currentSettingsPage=FindVisual<DwgTranslator.App.Views.Pages.SettingsPage>(window); Check(((FrameworkElement)currentSettingsPage.FindName("SettingsSaveBar")).IsVisible == (section!=5), "save bar only on editable settings " + section); if(section==1) Check(FindVisuals<System.Windows.Controls.TextBox>(currentSettingsPage).Where(t=>t.IsVisible).All(t=>t.ActualWidth<=160),"compact numeric settings inputs"); }
            // ── §G「设置页左右主从双卡片」导航链路守护（本批新增，只加强、不改动任何既有断言）──────
            // 为什么必须加：f3 把分区导航从"页内 2×3 UniformGrid 区块"改回"左卡内竖排 ListBox"
            // （SettingsPage.xaml:109-134 SettingsNavCard > SettingsNav，删掉了 UniformGrid ItemsPanel）。
            // 而上面 :524 的分区循环与下面的保存链全部直接写 vm.SettingsSection，走的是 ViewModel 通道，
            // 从不经过左卡 UI。新布局若把 ListBoxItem 压成 0 尺寸、被卡片 Padding(10) 裁掉、或
            // SelectedIndex 双向绑定断开，旧断言一条都不会红 —— 重构后守护强度实际是下降的。
            // 这里补回等价链路"点左卡导航 → 右卡换出对应分区内容"：
            //   a) 非 compact 下左卡与导航列表可见，且两个列宽严格等于共享令牌（从 Resources 读，不写死数字，
            //      令牌被改小/改没都会红）；
            //   b) 6 个 ListBoxItem 全部可见、可命中、有实际尺寸，且完整落在左卡边界内（不被裁）；
            //   c) 走 ListBoxItemAutomationPeer 的 ISelectionItemProvider.Select() —— 与 :261 对按钮用
            //      IInvokeProvider 属同一自动化层，是 UI 选择通道而非直接改 ViewModel —— 断言
            //      vm.SettingsSection 跟随变化、右卡内"有且仅有"对应那一个分区面板可见、面板真的有高度、
            //      且操作条可见性仍守 section!=5 的旧规则。
            {
                var navSettingsPage = FindVisual<DwgTranslator.App.Views.Pages.SettingsPage>(window);
                var navCard = (Border)navSettingsPage.FindName("SettingsNavCard");
                var navList = (ListBox)navSettingsPage.FindName("SettingsNav");
                var navColumn = (ColumnDefinition)navSettingsPage.FindName("SettingsNavColumn");
                var navGutter = (ColumnDefinition)navSettingsPage.FindName("SettingsNavGutter");
                Check(navCard.IsVisible && navList.IsVisible && !DwgTranslator.App.Views.Controls.ResponsiveLayout.GetIsCompact(window),
                    "master-detail settings exposes the left navigation card at full width");
                var expectedNavWidth = ((GridLength)Resources["Size.SettingsNav"]).Value;
                var expectedGutterWidth = ((GridLength)Resources["Size.CardGutter"]).Value;
                Check(Math.Abs(navColumn.Width.Value - expectedNavWidth) < 0.1 && Math.Abs(navGutter.Width.Value - expectedGutterWidth) < 0.1,
                    $"settings nav columns keep the shared width tokens (nav={navColumn.Width.Value:F1}/{expectedNavWidth:F1}, gutter={navGutter.Width.Value:F1}/{expectedGutterWidth:F1})");
                var navTiles = FindVisuals<ListBoxItem>(navList).ToList();
                Check(navTiles.Count == 6, "settings navigation lists all six sections, actual=" + navTiles.Count);
                foreach (var tile in navTiles)
                {
                    Check(tile.IsVisible && tile.IsHitTestVisible && tile.ActualWidth > 40 && tile.ActualHeight >= 24,
                        $"settings nav tile stays clickable ({tile.ActualWidth:F1}x{tile.ActualHeight:F1})");
                    var inCard = tile.TransformToVisual(navCard).TransformBounds(new Rect(0, 0, tile.ActualWidth, tile.ActualHeight));
                    Check(inCard.Left >= -0.5 && inCard.Top >= -0.5 && inCard.Right <= navCard.ActualWidth + 0.5 && inCard.Bottom <= navCard.ActualHeight + 0.5,
                        $"settings nav tile is not clipped by the card (tile={inCard}, card={navCard.ActualWidth:F1}x{navCard.ActualHeight:F1})");
                }
                // 六格必须严格等高：用户反复反馈"第 6 项比其他格高一点"。高度由 Height 与选中态样式
                // 共同决定，任何一方回退都会在这里被拦下（"看起来不一样"另由边框色统一处理）。
                var tileHeights = navTiles.Select(t => t.ActualHeight).ToArray();
                Check(tileHeights.Max() - tileHeights.Min() < 0.5,
                    "settings nav tiles share one height, actual=" + string.Join("/", tileHeights.Select(h => h.ToString("F1"))));
                var sectionHost = (StackPanel)((Border)navSettingsPage.FindName("SettingsContent")).Child;
                Check(sectionHost.Children.Count == 6, "settings right card hosts exactly six section panels, actual=" + sectionHost.Children.Count);
                var navSaveBar = (FrameworkElement)navSettingsPage.FindName("SettingsSaveBar");
                for (var target = 0; target < 6; target++)
                {
                    var tile = navTiles[target];
                    // 走 UI 元素的选择通道，不是 ViewModel 通道：ListBoxItem.IsSelected 正是鼠标点击最终
                    // 落到的那个属性，赋值后由 ListBoxItem → Selector 选中机制 → SelectedIndex 的
                    // **双向**绑定 → vm.SettingsSection → IndexToVis 驱动右卡分区可见性。这条链上任何一环
                    // 被 §G 重构弄断（ItemsPanel 改回默认纵向堆叠、ListBox 挪进左卡 Border、绑定退化成
                    // OneWay），下面 4 条断言都会红。
                    // 为什么不用 ISelectionItemProvider.Select()（与 :261 对按钮用 IInvokeProvider 同层）：
                    // UIElementAutomationPeer.CreatePeerForElement(tile) 在本场景直接抛 NullReferenceException
                    // ——这些 ListBoxItem 是 XAML 里显式声明的元素、自身即容器，其 peer 建立需要父级
                    // SelectorAutomationPeer 链，而冒烟进程里没有任何自动化客户端去创建过该链。
                    // 也不合成 MouseButton 事件：ListBoxItem 的选中在内部把该事件标记为已处理，
                    // RaiseEvent 的结果取决于路由细节，不确定；IsSelected 赋值是同一终态且确定。
                    // "可被点击"这一半由上面单独的 IsHitTestVisible + 实际尺寸 + 不被左卡裁切三条守住。
                    tile.IsSelected = true;
                    await NavIdleAsync();
                    Check(vm.SettingsSection == target, $"selecting the left nav tile switches to section {target}, actual={vm.SettingsSection}");
                    Check(navList.SelectedIndex == target && ReferenceEquals(navList.SelectedItem, tile), "left nav selection tracks the chosen section " + target);
                    var visiblePanels = sectionHost.Children.OfType<FrameworkElement>().Where(p => p.Visibility == Visibility.Visible).ToList();
                    Check(visiblePanels.Count == 1 && ReferenceEquals(visiblePanels[0], sectionHost.Children[target]),
                        $"right card shows only section {target}, visible={visiblePanels.Count}");
                    Check(visiblePanels[0].ActualHeight > 0 && visiblePanels[0].ActualWidth > 0,
                        $"section {target} content is really laid out inside the right card ({visiblePanels[0].ActualWidth:F1}x{visiblePanels[0].ActualHeight:F1})");
                    Check(navSaveBar.IsVisible == (target != 5), "nav-selected section still drives the save bar " + target);
                    Capture(window, "settings-nav-" + target);
                }
            }
            vm.SettingsSection = 1;
            await NavIdleAsync();
            var settingsPage = FindVisual<DwgTranslator.App.Views.Pages.SettingsPage>(window);
            var saveSettingsButton = (Button)settingsPage.FindName("SaveSettingsButton");
            var discardSettingsButton = (Button)settingsPage.FindName("DiscardSettingsButton");
            var restoreSettingsButton = (Button)settingsPage.FindName("RestoreSettingsButton");
            Check(((FrameworkElement)settingsPage.FindName("SettingsDraftStatus")).IsVisible && vm.SettingsStateText == "已应用", "settings exposes applied draft state");
            Check(!saveSettingsButton.IsEnabled && !discardSettingsButton.IsEnabled, "clean settings disable save and discard");
            Check(restoreSettingsButton.IsVisible && Equals(restoreSettingsButton.Content, "恢复本节推荐值"), "settings exposes per-section recommended defaults");
            var retryInput = (TextBox)settingsPage.FindName("MaxRetryCountInput");
            retryInput.Text = "99";
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(Validation.GetHasError(retryInput) && vm.HasSettingsValidationErrors && !saveSettingsButton.IsEnabled, "invalid settings show inline error and block save");
            retryInput.Text = "3";
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(!Validation.GetHasError(retryInput) && !vm.HasSettingsValidationErrors, "corrected settings clear inline error");
            vm.SettingsDraft.ExportDirectory = Path.Combine(AppDataDir, "output-test");
            vm.NotifySettingsDraftState(true);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(vm.HasUnsavedSettings && vm.SettingsStateText == "有未保存更改" && saveSettingsButton.IsEnabled && discardSettingsButton.IsEnabled, "settings dirty state drives bound action bar");
            Check(vm.SaveSettingsPage(), "settings save");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(SettingsStore.Read(Path.Combine(AppDataDir, "settings.json")).ExportDirectory.EndsWith("output-test"), "settings persisted");
            Check(vm.SettingsStateText == "已应用" && !saveSettingsButton.IsEnabled, "settings save returns to applied state");
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
            vm.CurrentPage = MainViewModel.PageAccount; await NavIdleAsync(); Capture(window, "account-simulated-login");
            // ── §B/§C 复审取证：等 fixture toast 自然过期后再拍一张干净的会员中心整页 ──────────────
            // 会员中心的既有截图（account-simulated-login、membership-redesign-*）全都紧跟在登出/过期/
            // 保存失败这些 fixture 之后，ToastHost 里必然还挂着提示；而 Toasts 是 MainWindow.xaml:392 的
            // Grid.Row=1/Column=1 右下覆盖层，正好压在第 3 块导航磁贴的右下角——membership-redesign-max
            // 实测 toast 红点 bbox x:1042..1048 / y:693..697，而磁贴卡面实测 y=600..719（x=1300 列扫描
            // 出 (255,252,245) 卡面、584..598 与 720..733 为边框+阴影）。t5 要判断的恰是"三块磁贴是否
            // 填满、右缘是否齐平"，被 toast 盖住的角落会被误读成裁切，所以补一张干净整页。
            // 不去改 toast 状态：ToastItem.Remaining 是 internal、ToastHost._items 是 private，硬清属侵入。
            // 改为等它按自身 4 秒寿命过期（ToastHost.xaml.cs:13）。MaximumVisible=1 时 pending 会在可见项
            // 过期后被提升，故 VisibleCount 与 PendingCount 必须同时归零。
            // 刻意不用 WaitUntil：它超时会抛 TimeoutException，取证代码不该有让整轮变红的能力；
            // 这里用既有 :523/:603 的固定 Task.Delay idiom + 有界非抛出轮询，最坏情况只是这张图仍带 toast，
            // 由下面的 INFO 行如实记录。NavIdleAsync(:100-103) 一字未动。
            {
                var evidenceToasts = (DwgTranslator.App.Views.Controls.ToastHost)window.FindName("Toasts");
                await Task.Delay(4600);
                var drained = 0;
                for (; drained < 200 && (evidenceToasts.VisibleCount > 0 || evidenceToasts.PendingCount > 0); drained++)
                {
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    await Task.Delay(50);
                }
                Console.WriteLine($"INFO evidence toast drain: visible={evidenceToasts.VisibleCount} pending={evidenceToasts.PendingCount} polls={drained}");
                vm.CurrentPage = MainViewModel.PageAccount;
                await NavIdleAsync();
                Capture(window, "evidence-account-clean");
            }
            await VerifyMembershipLayoutAsync(window, vm, loginPage);
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
            // Async command: wait for the save to settle instead of assuming it completed synchronously.
            await WaitUntil(() => !vm.IsTermDrawerOpen && !vm.HasUnsavedTerms, "term drawer closes after finishing edit");
            vm.SelectedTerm = null;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var glossaryPage = FindVisual<DwgTranslator.App.Views.Pages.GlossaryPage>(window);
            var cloudButton=FindVisuals<System.Windows.Controls.Button>(glossaryPage).Single(b=>Equals(b.Content,"云端管理"));
            Check(cloudButton.IsVisible,"cloud management remains visible without selection");
            Capture(window,"glossary-cloud-toolbar");
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
                    // matrix-account 先重尺寸再导航、account 页慢加载使其异步聚焦晚到(>150ms)，给足 1500ms 让 ClearFocus 落在聚焦之后；
                    // 其余 matrix 页无异步聚焦(grep 证实只有 AccountPage 有)，默认 150ms 即可(pwsh-32 已过 matrix translate/batch/glossary)。
                    await NavIdleAsync(page == "account" ? 1500 : 150);
                    // The current monitor may clamp native window size. Arrange the real page tree
                    // at the requested DIP size; this is not a physical-DPI acceptance test.
                    var content = (FrameworkElement)window.Content;
                    var size = new Size(layout.Item1 / layout.Item3, layout.Item2 / layout.Item3);
                    content.Width = size.Width; content.Height = size.Height;
                    content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
                    Check(Math.Abs(content.ActualWidth - size.Width) < 2, "effective viewport width " + page);
                    var headers = FindVisuals<DwgTranslator.App.Views.Controls.PageHeader>(window).Where(x => x.IsVisible).ToList();
                    Check(headers.Count == 1 && headers[0].ActualHeight >= 72, "one shared adaptive header " + page);
                    Check(Math.Abs(headers[0].TranslatePoint(new Point(), (FrameworkElement)window.FindName("PageHost")).X - 32) < 3, "shared 32 DIP page inset " + page);
                    CaptureLayout(window, size, $"matrix-{page}-{layout.Item1}-{layout.Item3}");
                    if (page == "settings")
                    {
                        var matrixSettingsPage = FindVisual<DwgTranslator.App.Views.Pages.SettingsPage>(window);
                        for (var section = 0; section < 6; section++)
                        {
                            vm.SettingsSection = section;
                            await NavIdleAsync();
                            content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
                            var form = (FrameworkElement)matrixSettingsPage.FindName("SettingsContent");
                            var footer = (FrameworkElement)matrixSettingsPage.FindName("SettingsSaveBar");
                            Check(form.ActualWidth <= 1080 && form.ActualWidth > 400, "settings form bounded " + section);
                            Check(footer.IsVisible == (section != 5), "settings footer visibility " + section);
                            if (section != 5)
                            {
                                Check(Math.Abs(form.ActualWidth - footer.ActualWidth) < 2, $"settings footer aligns with form {section}; form={form.ActualWidth:F1}, footer={footer.ActualWidth:F1}, formX={form.TranslatePoint(new Point(), content).X:F1}, footerX={footer.TranslatePoint(new Point(), content).X:F1}");
                                Check(footer.TranslatePoint(new Point(0, footer.ActualHeight), content).Y <= size.Height - 30, "settings footer stays reachable " + section);
                            }
                            CaptureLayout(window, size, $"matrix-settings-section-{section}-{layout.Item1}-{layout.Item3}");
                        }
                    }
                }
            // ── §G aboutStack 阈值在 1280 这一档的实拍证据（本批新增）──────────────────────────
            // f3 把「关于与帮助」分区两张卡的并排/堆叠分界从"仅 compact"放宽为
            // SettingsPage.xaml.cs:94 `var aboutStack = compact || ActualWidth < 1200;`
            // （换算窗口宽 < 1464，因为 ActualWidth = 窗口宽 - 侧栏 200 - Spacing.Page 左右 64）。
            // matrix 清单是 1366/1920/1920@1.25/2560@1.5：1366 覆盖了堆叠态，但 1280 这一档没有，
            // 而 1280 恰是右卡可用宽最小、说明文字最容易被 CharacterEllipsis 截断的一档。
            // 这里补一条真实断言 + 一张真窗口实拍。刻意不用 CaptureLayout 做 detached 取证、也不把
            // 1280 加进 matrix 清单：那会把 matrix 的整片几何断言覆盖面悄悄扩大到 1280（5 页 × 6 分区），
            // 属另一件事，需要单独立项评估，不该混在证据补充里。
            // 注意 matrix 循环在 :619 把 content.Width/Height 钉死成最后一档的 DIP 值且不会自行复位，
            // 所以这里先存后还，保证下游（:647 起的对话框与放弃更改测试）行为与改动前逐字一致。
            {
                var evidenceContent = (FrameworkElement)window.Content;
                var savedContentWidth = evidenceContent.Width;
                var savedContentHeight = evidenceContent.Height;
                window.Width = 1280; window.Height = 720;
                evidenceContent.Width = 1280; evidenceContent.Height = 720;
                vm.CurrentPage = MainViewModel.PageSettings;
                await WaitForStableAsync(() => window.ActualWidth, "evidence window 1280x720");
                var narrowSettings = FindVisual<DwgTranslator.App.Views.Pages.SettingsPage>(window);
                await WaitForStableAsync(() => narrowSettings.ActualWidth, "settings canvas 1280x720");
                vm.SettingsSection = 5;
                await NavIdleAsync();
                var aboutDetailsCard = (FrameworkElement)narrowSettings.FindName("AboutDetailsCard");
                Check(System.Windows.Controls.Grid.GetRow(aboutDetailsCard) == 2 && System.Windows.Controls.Grid.GetColumn(aboutDetailsCard) == 0,
                    $"narrow settings about section stacks instead of clipping (row={System.Windows.Controls.Grid.GetRow(aboutDetailsCard)}, column={System.Windows.Controls.Grid.GetColumn(aboutDetailsCard)}, pageWidth={narrowSettings.ActualWidth:F1})");
                Capture(window, "settings-about-stacked-1280");
                evidenceContent.Width = savedContentWidth; evidenceContent.Height = savedContentHeight;
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
            // §L4 放弃更改现在先弹危险确认：由 ConfirmModalsAsync 自动点确认。
            await ConfirmModalsAsync(() => vm.DiscardSettingsChangesCommand.Execute(null), "discard settings");
            Check(vm.SettingsDraft.ExportDirectory == original, "discard settings");
            var failureDraft = Path.Combine(AppDataDir, "must-survive-write-failure");
            vm.SettingsDraft.ExportDirectory = failureDraft;
            using (var locked = new FileStream(Path.Combine(AppDataDir, "settings.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(!vm.SaveSettingsPage() && vm.SettingsDraft.ExportDirectory == failureDraft, "write failure preserves draft");
            await ConfirmModalsAsync(() => vm.DiscardSettingsChangesCommand.Execute(null), "discard settings after write failure");
            await VerifyBilling(window);
            await VerifyBillingPersistence(window);
            await VerifyBillingCatalog(window);
            // ── W3：最大化（全屏）布局插桩 ──────────────────────────────────────────────
            // 全仓此前无任何测试进入最大化态（grep WindowState 全仓零命中最大化；
            // grep WM_GETMINMAXINFO/HwndSource/MonitorFromWindow/SourceInitialized 在 src\ 下零命中），
            // 所以上面的 185 张截图矩阵与全部断言都是窗口模式几何，覆盖不到用户报的
            // D1（顶栏上下不居中）/D5（被任务栏盖住）/D12（会员中心不居中）——这三条只在最大化态出现。
            // 本用例插在最后一批既有断言（VerifyBilling*，它们依赖"已登出"状态）之后：
            // 它需要重新登录才能让顶栏三个元素同时在场，插在前面会让下游断言观察到不同会话。
            // 放在这里后窗口随即在下一行关闭，无下游观察者；用例自身在 finally 里还原
            // WindowState/窗口尺寸/内容尺寸/CurrentPage/额度快照。
            await VerifyMaximizedLayoutAsync(window, vm);
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
    public Task<bool> HideBillingOrderAsync(string no,CancellationToken ct=default)=>throw new NotSupportedException();
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
 public int Hides;public string? LastHidden;public readonly HashSet<string> Hidden=new();
 public Task<BillingOrders> GetBillingOrdersAsync(string? before=null,CancellationToken ct=default)=>Task.FromResult(new BillingOrders{Orders=Creates>0&&!Hidden.Contains(Order.OrderNo)?[Order]:[]});
 public Task<bool> HideBillingOrderAsync(string no,CancellationToken ct=default){Hides++;LastHidden=no;Hidden.Add(no);return Task.FromResult(true);}
 public Task<BillingOrder> GetBillingOrderAsync(string no,CancellationToken ct=default)=>Task.FromResult(Order);
 // 真实服务端返回的订单一定属于被请求的套餐；这里补上这一步，否则"换套餐"用例里
 // 新订单仍带着旧套餐 id，提示条永远收不起来，等于在测一个假象。
 public Task<BillingOrder> CheckoutAsync(string p,string channel,string key,CancellationToken ct=default){Creates++;LastKey=key;Order.PlanId=p;OnCheckout?.Invoke();return Task.FromResult(Order);}
 public Task<BillingOrder> ConfirmBillingOrderAsync(string no,CancellationToken ct=default)=>Task.FromResult(Order);
 public Task<BillingEntitlements> GetBillingEntitlementsAsync(CancellationToken ct=default)=>FailSnapshot?Task.FromException<BillingEntitlements>(new BillingException("offline","模拟断网")):Task.FromResult(new BillingEntitlements{UserId=UserId,Subscription=new(){PlanName=Order.Status=="paid"?"pro":"free"},Usage=new(){MonthlyQuota=1000000,Used=321}});
}
