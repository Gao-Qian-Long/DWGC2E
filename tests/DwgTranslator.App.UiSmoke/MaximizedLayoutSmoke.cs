using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using DwgTranslator.App.ViewModels;
using DwgTranslator.App.Views;
using DwgTranslator.App.Views.Controls;
using DwgTranslator.App.Views.Pages;
using DwgTranslator.Core.Api;

namespace UiSmoke;

/// <summary>
/// W3 最大化（全屏）布局插桩。
///
/// 为什么必须新增：全仓此前没有任何测试进入过最大化态。grep WindowState 全仓只命中
/// src\DwgTranslator.App\ViewModels\MainViewModel.SettingsPage.cs:25-26（帮助窗口的最小化还原）
/// 与 ViewModels\MainWindow.xaml.cs 之外的零处；grep WM_GETMINMAXINFO / HwndSource /
/// MonitorFromWindow / SourceInitialized 在 src\ 下**零命中**。所以 Program.cs 的 185 张截图矩阵
/// 与全部断言都是"窗口模式"几何（:456 设 1366x768、:701-704 四档矩阵、:756-757 设 1280x720，
/// RefinementSmoke.cs 设 1120x640 / 1280x720 / 1366x768），而用户报的 D1（顶栏上下不居中）、
/// D5（被任务栏盖住）、D12（会员中心不居中）**只在最大化态出现** —— 这是守护缺口：
/// 改动前后既有断言都"全绿"，红不了。
///
/// 标尺说明：窗口几何读数取 Visual.PointToScreen（**物理像素**）与 GetMonitorInfo 的 rcWork
/// （同为物理像素），测的是真实屏幕关系。这与 Program.cs:390 的
/// PHYSICAL_DPI_NOT_VALIDATED（"矩阵截图不代表物理 DPI"）不冲突：本用例不验证 DPI 缩放，
/// 只验证最大化窗口相对显示器工作区的位置。D12 的布局读数按 Program.cs:711-714 既有的
/// "在真实页树上按请求 DIP 尺寸 Measure/Arrange" 手法取，同样不涉及物理 DPI。
///
/// 结构：先测量、三组读数全部打印，最后才断言。修复前跑一次即可拿到完整负向有效性证据。
/// </summary>
public sealed partial class SmokeApp
{
    // ── Win32：取显示器工作区（本测试工程未引用 WinForms，走 P/Invoke）─────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInfo
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
    }

    private const uint MonitorDefaultToNearest = 0x00000002;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref NativeMonitorInfo info);

    /// <summary>顶栏行高：MainWindow.xaml:60 的第一个 RowDefinition Height="44"。</summary>
    private const double TitleBarRowHeight = 44;

    /// <summary>底部状态栏高度：MainWindow.xaml:62 的第三个 RowDefinition Height="30"。</summary>
    private const double StatusBarRowHeight = 30;

    private async Task VerifyMaximizedLayoutAsync(MainWindow window, MainViewModel vm)
    {
        var savedPage = vm.CurrentPage;
        var savedUsage = vm.OnlineUsage;
        var savedSubscription = vm.OnlineSubscription;
        var content = (FrameworkElement)window.Content;
        var savedContentWidth = content.Width;
        var savedContentHeight = content.Height;
        var savedWindowWidth = window.Width;
        var savedWindowHeight = window.Height;
        try
        {
            // ── fixture ────────────────────────────────────────────────────────────────
            // 顶栏 ShellQuotaChip（MainWindow.xaml:85）只在 IsAccountLoggedIn=true 且 OnlineUsage!=null
            // 时可见（:93-100 的两个 DataTrigger）；本用例插在 Program.cs 收尾处，那时 :672 的
            // api.Expired 已经触发过一次登出。这里按 :618-623 的既有受控登录路径重新登录
            // （FakeApi 离线、不消耗额度：TranslateAsync 一律抛，"Paid operation forbidden in UI smoke"）。
            // 登录会改变 vm 的会话状态，所以本用例**必须**排在 :787-789 的 VerifyBilling* 之后：
            // 那里是最后一批依赖"已登出"状态的既有断言。插在它们之前会让下游断言观察到不同的会话，
            // 属对既有断言的破坏。放在收尾处后，窗口随即在 :791 关闭，没有下游观察者。
            api.Configured = true; api.Offline = false; api.FailLogin = false; api.Expired = false;
            if (!vm.IsAccountLoggedIn)
            {
                vm.LoginName = "simulated-account";
                await vm.SubmitLoginAsync("not-a-real-password");
            }
            Check(vm.IsAccountLoggedIn, "maximized layout fixture signs in through the controlled API");
            vm.OnlineUsage = new UsageInfo { MonthlyQuota = 1008000000, Used = 3791 };
            vm.CurrentPage = MainViewModel.PageAccount;
            await NavIdleAsync();
            // 放开尺寸：:701-713 的矩阵循环把内容尺寸钉成最后一档的 DIP 值且不会自行复位。
            content.Width = double.NaN; content.Height = double.NaN;

            // ── 进入最大化 ─────────────────────────────────────────────────────────────
            var stateBefore = window.WindowState;
            window.WindowState = WindowState.Maximized;
            await WaitForStableAsync(() => window.ActualWidth, "maximized window width");
            await WaitForStableAsync(() => window.ActualHeight, "maximized window height");
            await NavIdleAsync();
            Check(window.WindowState == WindowState.Maximized,
                $"window reaches the maximized state (state={window.WindowState}, before={stateBefore})");

            var handle = new WindowInteropHelper(window).Handle;
            var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
            var info = new NativeMonitorInfo { cbSize = Marshal.SizeOf<NativeMonitorInfo>() };
            var resolved = GetMonitorInfo(monitor, ref info);
            Console.WriteLine($"MAXIMIZED_MONITOR resolved={resolved} hwnd=0x{handle.ToInt64():X} rcMonitor=({info.rcMonitor.Left},{info.rcMonitor.Top})-({info.rcMonitor.Right},{info.rcMonitor.Bottom}) rcWork=({info.rcWork.Left},{info.rcWork.Top})-({info.rcWork.Right},{info.rcWork.Bottom})");
            Check(resolved, "maximized window resolves its monitor for work-area comparison");

            // ── 第一组：窗口客户区四条边 vs 显示器工作区（物理像素）────────────────────
            var topLeft = window.PointToScreen(new Point(0, 0));
            var bottomRight = window.PointToScreen(new Point(window.ActualWidth, window.ActualHeight));
            var clientLeft = topLeft.X;
            var clientTop = topLeft.Y;
            var clientRight = bottomRight.X;
            var clientBottom = bottomRight.Y;
            Console.WriteLine($"MAXIMIZED_CLIENT dip={window.ActualWidth:F1}x{window.ActualHeight:F1} px=({clientLeft:F1},{clientTop:F1})-({clientRight:F1},{clientBottom:F1})");
            Console.WriteLine($"MAXIMIZED_EDGES rcWorkTop={info.rcWork.Top} rcWorkBottom={info.rcWork.Bottom} rcWorkLeft={info.rcWork.Left} rcWorkRight={info.rcWork.Right} clientTop={clientTop:F1} clientBottom={clientBottom:F1} clientLeft={clientLeft:F1} clientRight={clientRight:F1} topVsWorkTop={clientTop - info.rcWork.Top:F1} bottomVsWorkBottom={clientBottom - info.rcWork.Bottom:F1} leftVsWorkLeft={clientLeft - info.rcWork.Left:F1} rightVsWorkRight={clientRight - info.rcWork.Right:F1}");
            var root = (Grid)window.FindName("WindowRoot");
            Console.WriteLine($"MAXIMIZED_MINCONSTRAINT windowMin={window.MinWidth:F1}x{window.MinHeight:F1} windowMax={window.MaxWidth:F1}x{window.MaxHeight:F1} actualDip={window.ActualWidth:F1}x{window.ActualHeight:F1} clientRect={(window.Content as FrameworkElement)?.ActualWidth:F1}x{(window.Content as FrameworkElement)?.ActualHeight:F1}");

            // ── 第二组 + 第三组（D1）：顶栏行与三个元素中心 vs 行中心的偏差 ─────────────
            var topBar = root.Children.OfType<Border>().First(b => Grid.GetRow(b) == 0 && Grid.GetColumnSpan(b) == 2);
            var topBarRect = topBar.TransformToAncestor(window).TransformBounds(new Rect(0, 0, topBar.ActualWidth, topBar.ActualHeight));
            var rowCenterY = topBarRect.Top + topBarRect.Height / 2;
            var rowTopScreen = window.PointToScreen(new Point(topBarRect.Left, topBarRect.Top)).Y;
            var rowBottomScreen = window.PointToScreen(new Point(topBarRect.Left, topBarRect.Bottom)).Y;
            Console.WriteLine($"MAXIMIZED_TOPBARROW dip={topBar.ActualWidth:F1}x{topBar.ActualHeight:F1} rowTopDip={topBarRect.Top:F1} rowCenterDip={rowCenterY:F1} screenTop={rowTopScreen:F1} screenBottom={rowBottomScreen:F1} rowBottomVsWorkBottom={rowBottomScreen - info.rcWork.Bottom:F1} rowTopVsWorkTop={rowTopScreen - info.rcWork.Top:F1}");

            var banner = (AnnouncementBanner)window.FindName("SiteAnnouncement");
            var quotaChip = (Border)window.FindName("ShellQuotaChip");
            var titleBarControl = FindVisual<TitleBarControl>(window);
            var topBarElements = new (string Name, FrameworkElement Element)[]
            {
                ("AnnouncementBanner", banner),
                ("ShellQuotaChip", quotaChip),
                ("TitleBarControl", titleBarControl),
            };
            var topBarDeviations = new List<(string Name, double Deviation, bool Visible, double Height)>();
            foreach (var (name, element) in topBarElements)
            {
                var rect = element.TransformToAncestor(window).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                var center = rect.Top + rect.Height / 2;
                var deviation = Math.Abs(center - rowCenterY);
                topBarDeviations.Add((name, deviation, element.IsVisible, element.ActualHeight));
                Console.WriteLine($"MAXIMIZED_TOPBAR {name} visible={element.IsVisible} h={element.ActualHeight:F1} topDip={rect.Top:F1} centerDip={center:F1} rowCenterDip={rowCenterY:F1} deviation={deviation:F2}");
            }

            // 底部状态栏（MainWindow.xaml:170 Grid.Row=2 Height=30）是否完整可见 —— D5 的直接观测量。
            var statusBar = root.Children.OfType<Border>().First(b => Grid.GetRow(b) == 2 && Grid.GetColumnSpan(b) == 2);
            var statusRect = statusBar.TransformToAncestor(window).TransformBounds(new Rect(0, 0, statusBar.ActualWidth, statusBar.ActualHeight));
            var statusBottomScreen = window.PointToScreen(new Point(statusRect.Left, statusRect.Bottom)).Y;
            Console.WriteLine($"MAXIMIZED_STATUSBAR dip={statusBar.ActualWidth:F1}x{statusBar.ActualHeight:F1} bottomDip={statusRect.Bottom:F1} bottomScreen={statusBottomScreen:F1} bottomVsWorkBottom={statusBottomScreen - info.rcWork.Bottom:F1} coveredByTaskbarPx={Math.Max(0, statusBottomScreen - info.rcWork.Bottom):F1}");

            Capture(window, "maximized-titlebar");
            Capture(window, "maximized-account");

            // ── 第四组（D12）：会员中心内容块中心 vs 滚动视口中心的偏差 ─────────────────
            // 只在真实页树上按请求 DIP 尺寸强制布局（Program.cs:711-714 的既有手法），逐档取读数。
            // 为什么必须逐档：AccountPage.xaml:99 是 MaxWidth="1200" + HorizontalAlignment="Left"，
            // 视口宽 <= 1200 时 Width=ViewportWidth 撑满、居与否无从观测（读数恒为 0，属同义反复）；
            // 只有视口宽 > 1200 时 MaxWidth 才生效、HorizontalAlignment 才决定落位。
            var accountPage = FindVisual<AccountPage>(window);
            Check(accountPage != null, "maximized layout exposes the account page for centring measurement");
            foreach (var width in new[] { 1366d, 1600d, 1920d, 2560d })
                await MeasureAccountCentringAtAsync(window, accountPage!, width, 900d);

            // ── 断言（读数已全部落 stdout；修复前应在这几行变红）────────────────────────
            // D5：最大化客户区底边不得越过工作区底边（否则底部 30 DIP 状态栏被 Windows 任务栏盖住）。
            Check(clientBottom <= info.rcWork.Bottom + 0.5,
                $"maximized window bottom stays inside the monitor work area (clientBottom={clientBottom:F1}, rcWork.Bottom={info.rcWork.Bottom}, overflow={clientBottom - info.rcWork.Bottom:F1}px)");
            Check(statusBottomScreen <= info.rcWork.Bottom + 0.5,
                $"maximized status bar is not covered by the taskbar (statusBottom={statusBottomScreen:F1}, rcWork.Bottom={info.rcWork.Bottom}, covered={Math.Max(0, statusBottomScreen - info.rcWork.Bottom):F1}px)");
            // D1：最大化客户区顶边不得越过工作区顶边（否则 44 DIP 顶栏上半截被推到屏幕外，看起来"上下不居中"）。
            Check(clientTop >= info.rcWork.Top - 0.5,
                $"maximized window top stays inside the monitor work area (clientTop={clientTop:F1}, rcWork.Top={info.rcWork.Top}, overflow={info.rcWork.Top - clientTop:F1}px)");
            Check(clientLeft >= info.rcWork.Left - 0.5 && clientRight <= info.rcWork.Right + 0.5,
                $"maximized window spans the monitor work area horizontally (client={clientLeft:F1}..{clientRight:F1}, rcWork={info.rcWork.Left}..{info.rcWork.Right})");
            // D1（结构面）：顶栏行高仍是 44 DIP 且整行完整落在工作区内。
            Check(Math.Abs(topBar.ActualHeight - TitleBarRowHeight) < 1,
                $"maximized title bar row keeps its 44 DIP height (actual={topBar.ActualHeight:F1})");
            Check(rowTopScreen >= info.rcWork.Top - 0.5 && rowBottomScreen <= info.rcWork.Bottom + 0.5,
                $"maximized title bar row is fully on screen (screen {rowTopScreen:F1}..{rowBottomScreen:F1}, rcWork {info.rcWork.Top}..{info.rcWork.Bottom})");
            Check(Math.Abs(statusBar.ActualHeight - StatusBarRowHeight) < 1,
                $"maximized status bar keeps its 30 DIP height (actual={statusBar.ActualHeight:F1})");
            // D1（元素面）：顶栏三个元素中心 y 与顶栏行中心 y 偏差 <= 1.0 px。
            foreach (var (name, deviation, visible, height) in topBarDeviations)
                Check(visible && height > 0 && deviation <= 1.0,
                    $"maximized title bar centres {name} in the 44 DIP row (deviation={deviation:F2}, visible={visible}, h={height:F1})");
        }
        finally
        {
            if (window.WindowState != WindowState.Normal)
            {
                window.WindowState = WindowState.Normal;
                await NavIdleAsync();
            }
            window.Width = savedWindowWidth; window.Height = savedWindowHeight;
            content.Width = savedContentWidth; content.Height = savedContentHeight;
            await WaitForStableAsync(() => window.ActualWidth, "restored window width");
            vm.CurrentPage = savedPage;
            vm.OnlineUsage = savedUsage;
            vm.OnlineSubscription = savedSubscription;
            await NavIdleAsync();
        }
    }

    /// <summary>
    /// 在真实页树上按请求的 DIP 尺寸强制布局，打印并断言会员中心内容块的水平居中情况。
    /// 与 Program.cs:711-714 同一手法（矩阵循环也用 content.Width/Measure/Arrange 表达"请求尺寸"）。
    /// </summary>
    private async Task MeasureAccountCentringAtAsync(MainWindow window, AccountPage page, double width, double height)
    {
        var content = (FrameworkElement)window.Content;
        var size = new Size(width, height);
        for (var pass = 0; pass < 2; pass++)
        {
            content.Width = size.Width; content.Height = size.Height;
            content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
            await NavIdleAsync();
        }
        var scroll = (ScrollViewer)page.FindName("AccountScroll");
        var block = (FrameworkElement)scroll.Content;
        scroll.UpdateLayout(); block.UpdateLayout();
        var rect = block.TransformToAncestor(scroll).TransformBounds(new Rect(0, 0, block.ActualWidth, block.ActualHeight));
        var viewportCenterX = scroll.ActualWidth / 2;
        var blockCenterX = rect.Left + rect.Width / 2;
        var offsetX = Math.Abs(blockCenterX - viewportCenterX);
        var rightGap = scroll.ActualWidth - rect.Right;
        Console.WriteLine($"MAXIMIZED_ACCOUNT canvas={width:F0}x{height:F0} scrollViewport={scroll.ActualWidth:F1}x{scroll.ActualHeight:F1} block={block.ActualWidth:F1}x{block.ActualHeight:F1} leftGap={rect.Left:F1} rightGap={rightGap:F1} blockCenterX={blockCenterX:F1} viewportCenterX={viewportCenterX:F1} offsetX={offsetX:F1}");
        content.Width = double.NaN; content.Height = double.NaN;
        await NavIdleAsync();
        Check(offsetX <= 1.0,
            $"account content is horizontally centred at {width:F0} DIP (offsetX={offsetX:F1}, leftGap={rect.Left:F1}, rightGap={rightGap:F1})");
    }
}
