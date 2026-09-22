using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DwgTranslator.Core.Api;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Infrastructure.Payments;
using DwgTranslator.Core.Application.Payments;
using QRCoder;
using Serilog;
namespace DwgTranslator.App.Views;
public partial class BillingWindow : Window
{
 private readonly IBillingClient _client;
 private readonly Func<bool> _valid;
 private readonly Action<BillingEntitlements> _apply;
 private readonly CancellationTokenSource _life = new();
 private readonly DispatcherTimer _timer = new(){Interval=TimeSpan.FromSeconds(3)};
 private readonly DispatcherTimer _expiryTimer = new(){Interval=TimeSpan.FromSeconds(1)};
 private readonly List<BillingOrder> _orders = new();
 private PendingPurchaseStore? _pendingStore;
 private PurchaseCheckoutService? _checkout;
 private string? _cursor;
 private PendingPurchase? _pending;
 private BillingOrder? _current;
 private bool _busy, _enabled, _initialized;
 private int _failures, _selection;
 public BillingWindow(IBillingClient client,string account,Func<bool> valid,Action<BillingEntitlements> apply)
 {
  InitializeComponent();_client=client;_valid=valid;_apply=apply;Account.Text="购买账号："+account;
  Loaded+=async(_,_)=>await InitializeAsync();
  Closed+=(_,_)=>{_expiryTimer.Stop();_timer.Stop();_life.Cancel();_life.Dispose();_pendingStore?.Dispose();};
  Activated+=async(_,_)=>{if(_initialized)await RefreshAsync();};
  Deactivated+=(_,_)=>_timer.Stop();
  _expiryTimer.Tick+=(_,_)=>{if(_current is {Status:"pending"} o && o.ExpiresAt<=DateTimeOffset.UtcNow){Qr.Source=null;Qr.Visibility=Visibility.Collapsed;QrStatus.Text="该订单二维码展示期限已结束，不再提供扫码入口。请刷新原订单确认，已付款请勿再次支付。";}};_expiryTimer.Start();
  _timer.Tick+=async(_,_)=>{_timer.Stop();await RefreshAsync();};
 }
 private bool Valid(){if(_life.IsCancellationRequested)return false;if(_valid())return true;Close();return false;}
 private async Task InitializeAsync()
 {
  await Run(async()=>{
   var snapshot=await _client.GetBillingEntitlementsAsync(_life.Token);if(!Valid())return;
   if(string.IsNullOrWhiteSpace(snapshot.UserId))throw new InvalidOperationException("无法确定账号，请重新登录。");
   Apply(snapshot);
   _pendingStore=new PendingPurchaseStore(snapshot.UserId);
   _checkout=new PurchaseCheckoutService(_client,_pendingStore);
   _pending=_pendingStore.Read() ?? _pending;
   await LoadPlans();if(!Valid())return;
   await LoadOrders(false);if(!Valid())return;
   _initialized=true;
   if(_pending?.OrderNo is string no){_current=await _client.GetBillingOrderAsync(no,_life.Token);if(!Valid())return;await Display(_current);}
   else if(_pending!=null)Message.Text="检测到未确认的下单，点击购买将复用原标识确认，不会新建第二笔订单。";
   _initialized=true;
   if(_current==null && _pending==null)Message.Text="";
  });
 }
 private void CopyOrder_Click(object sender, RoutedEventArgs e) { if(sender is Button { Tag: string no }) { try { Clipboard.SetText(no); Message.Text="订单号已复制。"; } catch { Message.Text="复制失败，请稍后重试。"; } } }
 /// <summary>
 /// 从历史列表移除一条未付款记录，与网页端 hideOrder 同一契约：只删本账号的显示记录，
 /// 不取消平台订单，所以提示必须写明旧二维码不可再支付。
 /// </summary>
 private async void HideOrder_Click(object sender,RoutedEventArgs e)
 {
  // 自动刷新（_timer，每 3~30 秒一次）与"刷新订单与会员"都会占用 _busy。旧实现在这里直接
  // return，于是只要恰好在刷新，点"删除"就毫无反应——正是"点了删除列表项没变"的来源。
  // 先停掉自动刷新，等当前操作收尾（最多 3 秒）；确实拿不到就给出可见原因。
  _timer.Stop();
  var waited=0;
  while(_busy&&waited<3000){await Task.Delay(100);waited+=100;}
  if(_busy)
  {
   Message.Text="上一个操作仍在进行，请稍候再删除。";
   if(Valid()&&IsActive)_timer.Start();
   return;
  }
  if(sender is not Button button||button.Tag is not string no||string.IsNullOrEmpty(no))
  {
   Message.Text="无法确定要删除的订单编号，请点击“刷新订单与会员”后重试。";
   Log.Warning("删除订单记录被跳过：按钮没有携带订单编号");
   return;
  }
  if(MessageBox.Show(this,
   $"仅从列表移除这笔记录，不会取消支付平台订单。\n\n订单号：{no}\n\n请确认不会再支付该订单的旧二维码。是否继续？",
   "删除订单记录",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
  await Run(async()=>{
   var hidden=await _client.HideBillingOrderAsync(no,_life.Token);
   if(!Valid())return;
   if(!hidden)throw new BillingException("invalid_response","服务端未确认删除，请刷新订单列表后重试。");
   if(_current?.OrderNo==no)
   {
    _current=null;++_selection;Qr.Source=null;Qr.Visibility=Visibility.Collapsed;
    CurrentOrderCard.Visibility=Visibility.Collapsed;
    OrderDetails.Text="";QrStatus.Text="尚未选择订单。可从历史订单查看付款状态。";
   }
   await LoadOrders(false);
   Message.Text=$"已从列表移除 {no}；平台订单未取消，请勿再支付旧二维码。";
  });
 }
 private async Task LoadPlans(){
  try {
   var plans=await _client.GetBillingPlansAsync(_life.Token);if(!Valid())return;
   // The shared catalog includes Free and disabled tiers; neither is a checkout choice.
   var purchasable=plans.Plans.Where(p=>p.IsPurchasable).ToList();
   var selected=(Plans.SelectedItem as BillingPlan)?.Id;Plans.ItemsSource=purchasable;
   Plans.SelectedItem=purchasable.FirstOrDefault(p=>p.Id==selected)??purchasable.FirstOrDefault();
   _enabled=plans.PaymentsEnabled;PaymentMethods.IsEnabled=_enabled&&purchasable.Count>0;Plans.Visibility=purchasable.Count==0?Visibility.Collapsed:Visibility.Visible;Plans.IsEnabled=_enabled;
   PurchaseAvailability.Text=!_enabled?"新购买暂未开放\n暂时无法创建新的支付订单。已有订单仍可查询，已付款订单继续确认到账。":purchasable.Count==0?"当前暂无可购买套餐；已有订单仍可查询。":"购买服务可用：选择套餐后获取二维码。价格与权益以服务器为准。";
  } catch {
   _enabled=false;PaymentMethods.IsEnabled=false;Plans.IsEnabled=false;
   PurchaseAvailability.Text="套餐加载失败，请点击“刷新订单与会员”重试。已有订单请继续核对，勿重复付款。";
   throw;
  }
 }
 private void Save()
 {
  if(_pendingStore==null)throw new InvalidOperationException("购买记录尚未就绪。");
  _checkout!.SaveIntent(_pending);
 }
 private void Apply(BillingEntitlements s){if(!Valid())return;_apply(s);Entitlements.Text=$"当前会员：{s.Subscription.PlanName} · 到期：{s.Subscription.ExpiresAt?.ToLocalTime():yyyy-MM-dd HH:mm} · 已用 {s.Usage.Used:N0} / {s.Usage.MonthlyQuota:N0}";}
 private async Task Run(Func<Task> action)
 {
  if(_busy||!Valid())return;_busy=true;Buy.IsEnabled=false;
  try{await action();_failures=0;}
  catch(OperationCanceledException){if(!_life.IsCancellationRequested)Close();}
  catch(ApiAuthenticationException){Message.Text="登录已过期，请关闭购买窗口并重新登录。";_life.Cancel();_timer.Stop();Qr.Source=null;Qr.Visibility=Visibility.Collapsed;}
  catch(Exception ex){if(Valid()){_failures=Math.Min(_failures+1,4);if(!_initialized)PurchaseAvailability.Text="购买信息加载失败，请点击“刷新订单与会员”重试。";Message.Text=(!_initialized&&_pending==null?"购买信息加载失败。 ":_current?.Status=="paid"?"支付成功，会员同步失败，请勿再次付款。":"操作未确认，请勿重复付款。 ")+ (ex is BillingException||ex is InvalidDataException?ex.Message:"请检查网络，或确认没有在其他 APP 窗口购买。");}}
  finally{_busy=false;Buy.IsEnabled=_initialized&&((_enabled&&Plans.SelectedItem is BillingPlan)||_pending is {OrderNo:null})&&!_life.IsCancellationRequested;Buy.Content=_pending is {OrderNo:null}?"确认原购买（不重复建单）":"确认套餐并获取二维码";if(Valid()&&IsActive&&_initialized){_timer.Interval=TimeSpan.FromSeconds(Math.Min(30,3*Math.Pow(2,_failures)));_timer.Start();}}
 }
 private async void Buy_Click(object sender,RoutedEventArgs e)=>await Run(async()=>{
  if(!_initialized)return;
  // An unresolved intent is reconciled first: the user must either see that order's QR again or
  // explicitly abandon it, exactly as the website does. Without this the window stays parked on an
  // order whose display window lapsed, and no new purchase can ever be started.
  if(_pending?.OrderNo is string openNo)
  {
   if(!await ReconcileOpenIntentAsync(openNo))return;
   if(!Valid())return;
  }
  if(_pending==null){if(!_enabled||Plans.SelectedItem is not BillingPlan plan)return;const string channel="alipay";
   if(MessageBox.Show(this,$"账号：{Account.Text}\n{plan.Label}\n确认购买？","核对购买",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
   _pending=new PendingPurchase{PlanId=plan.Id,Channel=channel,Key=Guid.NewGuid().ToString("N")};Save();
  }
  // Never convert an unresolved WeChat intent into a different payment channel.
  if(_pending.Channel!="alipay"){Qr.Source=null;Qr.Visibility=Visibility.Collapsed;Message.Text="微信支付暂未开通。原购买记录已保留，请查询已有订单或联系支持确认；不会自动改用支付宝或重复下单。";return;}
  BillingOrder? o;
  try{o=await _checkout!.CheckoutAsync(_pending,Valid,_life.Token);}
  catch(BillingException ex) when (ex.Order!=null&&Valid())
  {
   // The server refused a second order because this one is still open; adopt it instead of
   // stopping at the error text, which is what the website does with the same 409.
   _pending.OrderNo=ex.Order!.OrderNo;Save();
   _current=ex.Order;++_selection;await Display(_current);
   Message.Text="已有未完成的购买，已恢复原订单；未创建新订单，请核对原套餐与金额。";
   await LoadOrders(false);
   return;
  }
  if(o==null)return;
  _current=o;++_selection;await Display(o);await LoadOrders(false);
 });
 /// <summary>
 /// Returns true when the caller may continue towards a (new) order, false when the open intent was
 /// shown to the user instead. Branch order mirrors the website: terminal states are dropped, a
 /// payable or still-confirming order is displayed as-is, and only a lapsed order asks the user
 /// whether to abandon it. The platform order is never cancelled from here.
 /// </summary>
 private async Task<bool> ReconcileOpenIntentAsync(string orderNo)
 {
  var open=await _client.GetBillingOrderAsync(orderNo,_life.Token);if(!Valid())return false;
  if(open.Status is "paid" or "failed" or "cancelled" or "refunded")
  {
   _pending=null;Save();
   return true;
  }
  var sameIntent=_pending!.PlanId==open.PlanId&&_pending.Channel==open.Channel;
  if(sameIntent&&open.AllowedActions.Pay&&open.ExpiresAt>DateTimeOffset.UtcNow)
  {
   _current=open;++_selection;await Display(open);
   Message.Text="已显示原订单的二维码，请勿重复付款。";
   return false;
  }
  if(open.Status=="pending"&&(open.ExpiresAt>DateTimeOffset.UtcNow||open.CreateState is "creating" or "unknown"))
  {
   _current=open;++_selection;await Display(open);
   Message.Text="原购买仍待确认，已保留原订单；请先核实，不会因重试或切换套餐创建新订单。";
   return false;
  }
  var answer=MessageBox.Show(this,
   $"原订单二维码展示期限已结束，但不代表平台订单已取消。\n\n原订单：{open.PlanName} · ¥{open.PayableCents/100m:0.00}\n订单号：{open.OrderNo}\n\n请先确认没有付款，且不会再支付旧二维码。\n是否放弃原订单并继续购买？",
   "核对原订单",MessageBoxButton.YesNo,MessageBoxImage.Warning);
  if(answer!=MessageBoxResult.Yes)
  {
   _current=open;++_selection;await Display(open);
   Message.Text="已保留原订单；若已付款请点击“刷新订单与会员”确认到账。";
   return false;
  }
  // Abandoning the intent does not cancel the platform order; it stops this client from reusing
  // its idempotency key, which is exactly what the website does when the user confirms.
  _pending=null;Save();
  return true;
 }
 private async Task Display(BillingOrder o)
 {
  if(!Valid())return;var index=_orders.FindIndex(item=>item.OrderNo==o.OrderNo);if(index>=0){_orders[index]=o;Orders.ItemsSource=_orders.ToArray();}Qr.Source=null;Qr.Visibility=Visibility.Collapsed;
  // 当前订单卡是这一屏的主角：金额单独成行，订单号降为辅助行。
  CurrentOrderCard.Visibility=Visibility.Visible;
  OrderPlanName.Text=$"{o.PlanName} · {(o.Channel=="alipay"?"支付宝":o.Channel=="wxpay"?"微信支付（暂未开通）":"未知支付方式")}";
  OrderAmount.Text=$"¥{o.PayableCents/100m:0.00}";
  OrderDetails.Text=$"订单号：{o.OrderNo}";
  CopyOrderNo.Tag=o.OrderNo;
  // 卡片内的删除入口与表格里的删除按钮同一判定：只有未付款记录可移除。
  HideCurrentOrder.Tag=o.OrderNo;
  HideCurrentOrder.Visibility=o.CanHide?Visibility.Visible:Visibility.Collapsed;
  if(o.Status=="paid"){
   QrStatus.Text="该订单已付款，无需再次扫码。";Message.Text="支付成功，正在同步会员。";
   _pending=_checkout!.ClearPaidIntent(_pending,o);
   var snapshot=await _client.GetBillingEntitlementsAsync(_life.Token);if(!Valid())return;Apply(snapshot);Message.Text="支付成功，会员与额度已同步。";return;
  }
  if(o.Channel!="alipay"){QrStatus.Text="该订单不是支付宝订单，当前通道暂不可扫码。请刷新原订单确认状态，已付款请勿重复支付。";Message.Text="当前仅开放支付宝扫码；原订单查询与到账确认不受影响。";return;}
  QrStatus.Text=o.ExpiresAt<=DateTimeOffset.UtcNow?"该订单二维码展示期限已结束，不再提供扫码入口。请刷新原订单确认，已付款请勿再次支付。":!o.AllowedActions.Pay?"服务端尚未允许该订单扫码，正在确认原订单状态；请勿重复下单。":"正在加载支付二维码…";
  Message.Text=o.AllowedActions.Pay?"请核对金额后扫码，付款结果由服务器确认。":"订单正在确认，请勿重复付款。";
  // 显示的订单与上方所选套餐不一致时必须说明原因，否则看起来像界面串了数据。
  if(o.Status!="paid"&&(Plans.SelectedItem as BillingPlan)?.Id is string chosen&&chosen!=o.PlanId)
   Message.Text+=$" 当前显示的是原订单（{o.PlanName}），与上方所选套餐不同；切换套餐不会取消原订单，如需按新套餐购买请点击“确认套餐并获取二维码”。";
  if(o.AllowedActions.Pay&&o.ExpiresAt>DateTimeOffset.UtcNow&&!string.IsNullOrEmpty(o.QrCode)){
   using var generator=new QRCodeGenerator();using var data=generator.CreateQrCode(o.QrCode,QRCodeGenerator.ECCLevel.M);using var png=new PngByteQRCode(data);using var stream=new MemoryStream(png.GetGraphic(8));
   var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.StreamSource=stream;image.EndInit();image.Freeze();Qr.Source=image;Qr.Visibility=Visibility.Visible;QrStatus.Text="当前订单为支付宝支付，请使用支付宝扫码；二维码有效至 "+o.ExpiresAt.ToLocalTime().ToString("HH:mm:ss")+"。";
  }else if(o.AllowedActions.Pay&&o.ExpiresAt>DateTimeOffset.UtcNow&&Uri.TryCreate(o.QrCodeImageUrl,UriKind.Absolute,out var imageUrl)&&imageUrl.Scheme=="https"&&string.IsNullOrEmpty(imageUrl.UserInfo)){
   using var handler=new System.Net.Http.HttpClientHandler{AllowAutoRedirect=false};using var http=new System.Net.Http.HttpClient(handler){Timeout=TimeSpan.FromSeconds(10)};
   using var response=await http.GetAsync(imageUrl,System.Net.Http.HttpCompletionOption.ResponseHeadersRead,_life.Token);response.EnsureSuccessStatusCode();
   if(response.Content.Headers.ContentLength>1048576)throw new InvalidDataException("二维码图片过大。");
   using var timeout=CancellationTokenSource.CreateLinkedTokenSource(_life.Token);timeout.CancelAfter(TimeSpan.FromSeconds(10));
   using var input=await response.Content.ReadAsStreamAsync(timeout.Token);using var output=new MemoryStream();var buffer=new byte[8192];int count;
   while((count=await input.ReadAsync(buffer,timeout.Token))>0){if(output.Length+count>1048576)throw new InvalidDataException("二维码图片过大。");output.Write(buffer,0,count);}
   if(!Valid()||_current?.OrderNo!=o.OrderNo||o.ExpiresAt<=DateTimeOffset.UtcNow)return;output.Position=0;
   var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.StreamSource=output;image.DecodePixelWidth=480;image.EndInit();image.Freeze();Qr.Source=image;Qr.Visibility=Visibility.Visible;QrStatus.Text="当前订单为支付宝支付，请使用支付宝扫码；二维码有效至 "+o.ExpiresAt.ToLocalTime().ToString("HH:mm:ss")+"。";
  }else if(o.AllowedActions.Pay)Message.Text="平台没有返回可生成二维码的内容，暂不可扫码。请联系支持核查原订单，不要重复下单。";
 }
 private async Task RefreshAsync()=>await Run(async()=>{
  if(!_initialized)return;
  var version=_selection;var no=_current?.OrderNo;
  if(no!=null){var o=await _client.GetBillingOrderAsync(no,_life.Token);if(!Valid()||version!=_selection)return;_current=o;await Display(o);if(o.Status!="paid")Apply(await _client.GetBillingEntitlementsAsync(_life.Token));}
  else Apply(await _client.GetBillingEntitlementsAsync(_life.Token));
 });
 private async void Refresh_Click(object sender,RoutedEventArgs e){if(!_initialized){_pendingStore?.Dispose();_pendingStore=null;await InitializeAsync();return;}await Run(async()=>{
  if(!_initialized)return;await LoadPlans();if(!Valid())return;var version=_selection;var no=_current?.OrderNo;
  if(no!=null){var o=await _client.ConfirmBillingOrderAsync(no,_life.Token);if(!Valid()||version!=_selection)return;_current=o;await Display(o);}
  Apply(await _client.GetBillingEntitlementsAsync(_life.Token));await LoadOrders(false);
 });}
 private async Task LoadOrders(bool append){var d=await _client.GetBillingOrdersAsync(append?_cursor:null,_life.Token);if(!Valid())return;if(!append)_orders.Clear();_orders.AddRange(d.Orders);_cursor=d.NextCursor;Orders.ItemsSource=null;Orders.ItemsSource=_orders.ToArray();EmptyOrders.Visibility=_orders.Count==0?Visibility.Visible:Visibility.Collapsed;Orders.Visibility=_orders.Count==0?Visibility.Collapsed:Visibility.Visible;More.IsEnabled=_cursor!=null;}
 private async void More_Click(object sender,RoutedEventArgs e)=>await Run(()=>LoadOrders(true));
 private async void Orders_SelectionChanged(object sender,SelectionChangedEventArgs e){if(_busy||Orders.SelectedItem is not BillingOrder selected)return;++_selection;await Run(async()=>{_current=await _client.GetBillingOrderAsync(selected.OrderNo,_life.Token);if(Valid())await Display(_current);});}
}
