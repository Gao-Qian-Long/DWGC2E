using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using DwgTranslator.App.ViewModels;
using DwgTranslator.App.Views;
using DwgTranslator.App.Views.Controls;
using DwgTranslator.Core.Models;
namespace UiSmoke;
public sealed partial class SmokeApp
{
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr CaptionSendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    private static int HitTestCaption(Window window, Point point)
    {
        var screen = window.PointToScreen(point);
        var packed = ((int)screen.X & 0xffff) | (((int)screen.Y & 0xffff) << 16);
        return CaptionSendMessage(new WindowInteropHelper(window).Handle, 0x0084, IntPtr.Zero, new IntPtr(packed)).ToInt32();
    }
    private async Task VerifyAnnouncementCaptionAsync(MainWindow window, MainViewModel vm)
    {
        var banner = (AnnouncementBanner)window.FindName("SiteAnnouncement");
        var config = (AppConfig)typeof(MainViewModel).GetField("_config",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(vm)!;
        var oldAddress = config.ApiBaseUrl;
        using var port = new TcpListener(IPAddress.Loopback, 0); port.Start();
        var address = "http://127.0.0.1:" + ((IPEndPoint)port.LocalEndpoint).Port + "/"; port.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add(address); listener.Start();
        string body = ""; var status = 200; bool receivedCredential = false; bool pathCorrect = true;
        var server = Task.Run(async () =>
        {
            try { while (listener.IsListening)
            {
                var context = await listener.GetContextAsync();
                receivedCredential |= context.Request.Headers["Authorization"] != null;
                pathCorrect &= context.Request.Url!.AbsolutePath == "/v1/site";
                context.Response.StatusCode = status;
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { content = new { announcement = body } }));
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
            }} catch (HttpListenerException) { } catch (ObjectDisposedException) { }
        });
        try
        {
            config.ApiBaseUrl = address + "v1/";
            await banner.RefreshAsync();
            Check(banner.DisplayText == "暂无公告", "empty announcement is explicit, not a blank title bar");
            foreach (var y in new[] { 12d, 24d, 40d })
                Check(HitTestCaption(window,new Point(240,y)) == 2, "empty title bar native HTCAPTION at " + y);
            Capture(window,"announcement-empty-caption");
            body = "后台公告：这是隔离测试内容，验证公告显示、长文本滚动以及窗口拖动。" + new string('测',180);
            await banner.RefreshAsync();
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            Check(banner.DisplayText == body, "admin announcement loads through actual HTTP client");
            var entry = FindVisual<Button>(banner);
            // §L20 公告条由固定 220 改为弹性 160..320，以保证最窄窗口（§自适应后下限 1024）下与用户入口不重叠。
            Check(entry != null && entry.ActualWidth >= 160 && entry.ActualWidth <= 320,
                $"announcement has bounded ticker entry (actual={entry?.ActualWidth:F1})");
            Check(banner.IsHitTestVisible, "announcement entry accepts input");
            Check(HitTestCaption(window, banner.TranslatePoint(new Point(16,16),window)) == 1, "announcement entry is client input");
            Check(HitTestCaption(window,new Point(350,22)) == 2,"announcement content native HTCAPTION");
            var topmost = (Button)window.FindName("TopmostButton");
            Check(HitTestCaption(window,topmost.TranslatePoint(new Point(18,18),window)) == 1,"pin remains clickable HTCLIENT");
            var controls = FindVisual<TitleBarControl>(window)!;
            foreach (var name in new[]{"MinimizeButton","MaxRestoreButton","CloseButton"})
            {
                var button=(Button)controls.FindName(name);
                Check(HitTestCaption(window,button.TranslatePoint(new Point(button.ActualWidth/2,button.ActualHeight/2),window)) == 1,"window control remains clickable " + name);
            }
            Check(banner.HasUnread, "new announcement has unread dot");
            Capture(window,"announcement-visible-caption");
            banner.IsOpen = true;
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            Check(banner.IsOpen && !banner.HasUnread, "opening announcement clears dot");
            var announcementWindow = Application.Current.Windows.OfType<AnnouncementWindow>().Single();
            Capture(announcementWindow,"announcement-detail");
            var hostBounds = window.WindowState == WindowState.Maximized
                ? SystemParameters.WorkArea
                : new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight);
            var hostCenter = hostBounds.Left + hostBounds.Width / 2;
            var popupCenter = announcementWindow.Left + announcementWindow.ActualWidth / 2;
            Check(Math.Abs(popupCenter - hostCenter) <= 1.5,
                $"announcement popup is horizontally centered (popup={popupCenter:F1}, host={hostCenter:F1})");
            banner.IsOpen = false;
            await banner.RefreshAsync();
            Check(!banner.HasUnread, "same announcement stays read after refresh");
            typeof(MainViewModel).GetField("_readAnnouncement", BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(vm, null);
            Check(!vm.IsAnnouncementUnread(body), "read state survives reloading from disk");
            body = "更新后的公告内容";
            await banner.RefreshAsync();
            Check(banner.HasUnread, "changed content becomes unread");
            status = 503; await banner.RefreshAsync();
            Check(banner.DisplayText.Contains("无法加载") && !banner.DisplayText.Contains("后台公告"),"failed refresh clears stale announcement with explicit retry state");
            status = 200; body = "更新后的公告"; await banner.RefreshAsync();
            Check(banner.DisplayText == body,"announcement recovers after request failure");
            Check(pathCorrect && !receivedCredential,"public announcement URL normalized and no account token sent");
        }
        finally { config.ApiBaseUrl = oldAddress; listener.Stop(); await server; await banner.RefreshAsync(); }
    }
}
