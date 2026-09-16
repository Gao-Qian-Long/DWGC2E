using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DwgTranslator.App.Views;

namespace UiSmoke;

public sealed partial class SmokeApp
{
    private async Task VerifyBillingPersistence(MainWindow owner)
    {
        var folder=Path.Combine(Environment.GetEnvironmentVariable("DWGC2E_DATA_DIR")!,"payments");
        Directory.CreateDirectory(folder);
        var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("billing-ui-user")));
        var path=Path.Combine(folder,hash+".json");
        const string pending="{\"PlanId\":\"p\",\"Channel\":\"alipay\",\"Key\":\"persistence-00000001\"}";
        var fake=new FakeBilling();var valid=true;
        BillingWindow Create()=>new(fake,"isolated@example.test",()=>valid,_=>{}){Owner=owner,ShowActivated=false,ShowInTaskbar=false};
        async Task Idle(BillingWindow w){await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);await WaitUntil(()=>(bool)typeof(BillingWindow).GetField("_busy",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(w)! == false,"billing action completes");}
        async Task Buy(BillingWindow w){((Button)w.FindName("Buy")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Idle(w);}
        string Message(BillingWindow w)=>((TextBlock)w.FindName("Message")).Text;
        // Malformed state must block checkout and survive closing/reopening byte-for-byte.
        foreach(var invalid in new[]{"{broken", "null", "{\"PlanId\":\"p\",\"Channel\":\"alipay\",\"Key\":null}", "{\"PlanId\":\"p\",\"Channel\":\"alipay\",\"Key\":\"bad\"}"})
        {
            File.WriteAllText(path,invalid);var w=Create();w.Show();await Idle(w);
            Check(!((Button)w.FindName("Buy")).IsEnabled && Message(w).Contains("记录"),"corrupt payment state blocks checkout with explicit feedback");
            Check(File.ReadAllText(path)==invalid && fake.Creates==0,"corrupt payment state is preserved without checkout");w.Close();
        }
        File.WriteAllText(path,pending);
        var window=Create();window.Show();await Idle(window);
        // A second purchase window must not open the same account's intent for writing.
        var duplicate=Create();duplicate.Show();await Idle(duplicate);
        Check(!((Button)duplicate.FindName("Buy")).IsEnabled,"second purchase window cannot own same pending state");duplicate.Close();
        var original=File.ReadAllBytes(path);
        using(var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            await Buy(window);await Buy(window);
            Check(fake.Creates==0,"repeated failed persistence never reaches checkout");
            Check(Message(window).Contains("保存购买恢复记录") && File.ReadAllBytes(path).SequenceEqual(original),"write failure is explicit and retains previous intent");
            Check(Directory.GetFiles(folder,hash+".json.*.tmp").Length==0,"failed payment writes clean their temporary files");
        }
        await Buy(window);Check(fake.Creates==1 && fake.LastKey=="persistence-00000001","write recovery reuses original idempotency key");window.Close();
        window=Create();window.Show();await Idle(window);
        Check(fake.Creates==1 && ((TextBlock)window.FindName("OrderDetails")).Text.Contains(fake.Order.OrderNo),"close/reopen restores known order without new checkout");window.Close();
        // Simulate a server-created order whose response could not be persisted locally.
        File.WriteAllText(path,pending);FileStream? responseLock=null;byte[]? beforeResponse=null;
        fake.OnCheckout=()=>{beforeResponse=File.ReadAllBytes(path);responseLock=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);};
        window=Create();window.Show();await Idle(window);
        try { await Buy(window);Check(fake.Creates==2 && beforeResponse!=null && File.ReadAllBytes(path).SequenceEqual(beforeResponse),"post-checkout persistence failure retains original recovery key"); }
        finally {responseLock?.Dispose();fake.OnCheckout=null;window.Close();}
        window=Create();window.Show();await Idle(window);await Buy(window);
        Check(fake.Creates==3 && fake.LastKey=="persistence-00000001","restart after response write failure reconciles with same key");window.Close();
        // Lost network reply leaves the durable intent intact for restart.
        File.WriteAllText(path,pending);fake.OnCheckout=()=>throw new IOException("isolated lost reply");
        window=Create();window.Show();await Idle(window);await Buy(window);window.Close();
        Check(JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("Key").GetString()=="persistence-00000001","lost reply preserves durable key");
        fake.OnCheckout=null;window=Create();window.Show();await Idle(window);await Buy(window);
        Check(fake.LastKey=="persistence-00000001","lost reply restart cannot invent new key");
        fake.Order.Status="paid";
        using(var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            await (Task)typeof(BillingWindow).GetMethod("RefreshAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null)!;
            Check(typeof(BillingWindow).GetField("_pending",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!=null && File.Exists(path),"failed paid-state cleanup retains in-memory and durable recovery intent");
        }
        await (Task)typeof(BillingWindow).GetMethod("RefreshAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null)!;
        Check(!File.Exists(path),"paid-state cleanup retries successfully after unlock");
        File.WriteAllText(path,pending);
        valid=false;await (Task)typeof(BillingWindow).GetMethod("RefreshAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null)!;
        Check(!window.IsVisible,"account invalidation closes recovery window");
        var prior=File.ReadAllBytes(path);valid=true;fake.UserId="billing-other-user";
        window=Create();window.Show();await Idle(window);
        var otherStore=(DwgTranslator.Core.Infrastructure.Payments.PendingPurchaseStore?)typeof(BillingWindow).GetField("_pendingStore",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window);
        Check(otherStore!=null && otherStore.RecordPath!=path,"another account has a distinct pending path");
        Check(File.ReadAllBytes(path).SequenceEqual(prior),"other account cannot modify first account pending state");window.Close();
    }
}
