using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DwgTranslator.App.ViewModels;
using DwgTranslator.App.Views;
using DwgTranslator.App.Views.Controls;

namespace UiSmoke;
public sealed partial class SmokeApp
{
    private async Task VerifySidebarToastStabilityAsync(MainWindow window, MainViewModel vm)
    {
        var originalPage = vm.CurrentPage;
        var width = window.Width; var height = window.Height;
        var toast = (ToastHost)window.FindName("Toasts");
        var originalPause = toast.ShouldPause;
        var account = (FrameworkElement)window.FindName("AccountEntryBorder");
        var accountButton = (FrameworkElement)window.FindName("AccountMenuButton");
        var brand = (FrameworkElement)window.FindName("SidebarBrand");
        var root = (FrameworkElement)window.Content;
        Rect Bounds(FrameworkElement item) => new(item.TranslatePoint(new Point(), root), item.RenderSize);
        void Stable(Rect expected, FrameworkElement item, string label)
        {
            var actual = Bounds(item);
            Check(Math.Abs(actual.X - expected.X) < .5 && Math.Abs(actual.Y - expected.Y) < .5
                && Math.Abs(actual.Width - expected.Width) < .5 && Math.Abs(actual.Height - expected.Height) < .5,
                $"sidebar stays fixed {label}: expected={expected}, actual={actual}");
        }
        try
        {
            vm.CurrentPage = "translate";
            foreach (var size in new[] { new Size(800,600), new Size(1366,768), new Size(1366,960) })
            {
                window.Width = size.Width; window.Height = size.Height;
                toast.ShouldPause = () => true;
                await Task.Delay(300);
                await Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
                var entryBounds = Bounds(account); var buttonBounds = Bounds(accountButton); var brandBounds = Bounds(brand);
                Capture(window, $"sidebar-toast-before-{size.Width}-{size.Height}");
                toast.ShouldPause = () => false;
                toast.Show("布局回归测试：操作已完成", Brushes.Brown);
                await WaitForStableAsync(() => toast.ActualHeight, "toast height " + size);
                Check(toast.Visibility == Visibility.Visible && toast.ActualHeight > 0, "actual main toast occupies its notification row");
                Stable(entryBounds, account, "one message " + size);
                Stable(buttonBounds, accountButton, "avatar button " + size);
                toast.Show("布局回归测试：这是一条较长的信息提示，用于检查换行和多条消息出现时左下角账户栏是否保持原位置。", Brushes.Brown);
                toast.Show("布局回归测试：第三条提示", Brushes.Brown);
                await WaitForStableAsync(() => toast.ActualHeight, "stacked toast height " + size);
                Stable(entryBounds, account, "stacked messages " + size);
                Stable(brandBounds, brand, "brand " + size);
                Check(account.TranslatePoint(new Point(0,account.ActualHeight),root).Y <= root.ActualHeight - 30, "account remains above fixed status bar");
                Capture(window, $"sidebar-toast-visible-{size.Width}-{size.Height}");
                toast.ShouldPause = () => true;
                await Task.Delay(300);
                await Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
                Check(toast.Visibility == Visibility.Collapsed, "notification row collapses while paused");
                Stable(entryBounds, account, "collapsed messages " + size);
                toast.ShouldPause = () => false;
                // Each toast lives four seconds, but MaximumVisible is 1, so the three queued
                // messages expire strictly one after another: 3 x 4s = 12s, plus up to 250ms of
                // Refresh latency each and the layout work between them. The previous 15s budget
                // left no margin, and this assertion failed inside Publish-Desktop.ps1 twice
                // while passing standalone, because the pipeline reaches this point only after a
                // long regression chain. 30s is timeout headroom only: what is asserted here, and
                // every product behaviour behind it, is unchanged.
                for (var tick=0; tick<300 && (toast.VisibleCount > 0 || toast.PendingCount > 0); tick++) await Task.Delay(100);
                await Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
                Check(toast.VisibleCount == 0 && toast.PendingCount == 0, "notification stack naturally expires");
                Stable(entryBounds, account, "expired messages " + size);
                Capture(window, $"sidebar-toast-after-{size.Width}-{size.Height}");
            }
        }
        finally
        {
            toast.ShouldPause = originalPause;
            window.Width = width; window.Height = height; vm.CurrentPage = originalPage;
        }
    }
}
