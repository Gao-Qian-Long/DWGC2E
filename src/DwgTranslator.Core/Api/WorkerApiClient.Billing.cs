using System;
using System.Net.Http;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace DwgTranslator.Core.Api;
public sealed partial class WorkerApiClient : IBillingClient
{
 public Task<BillingPlans> GetBillingPlansAsync(CancellationToken ct=default)=>BillingRequest<BillingPlans>("/v1/billing/plans",ct);
 public Task<BillingOrders> GetBillingOrdersAsync(string? before=null,CancellationToken ct=default)=>BillingRequest<BillingOrders>("/v1/billing/orders"+(before==null?"":"?before="+Uri.EscapeDataString(before)),ct);
 public Task<BillingOrder> GetBillingOrderAsync(string no,CancellationToken ct=default)=>BillingRequest<BillingOrder>("/v1/billing/orders/"+Uri.EscapeDataString(no),ct,expectedOrderNo:no);
 public Task<BillingOrder> ConfirmBillingOrderAsync(string no,CancellationToken ct=default)=>BillingRequest<BillingOrder>("/v1/billing/orders/"+Uri.EscapeDataString(no)+"/confirm",ct,new {},expectedOrderNo:no);
 public Task<BillingEntitlements> GetBillingEntitlementsAsync(CancellationToken ct=default)=>BillingRequest<BillingEntitlements>("/v1/billing/entitlements",ct);
 public Task<BillingOrder> CheckoutAsync(string planId,string channel,string key,CancellationToken ct=default)=>CheckoutRequest(planId,channel,key,ct);
 /// <summary>
 /// Checkout is the one billing call whose 409 is not an error to show: the body carries the order
 /// that is still open. The web client adopts it and keeps showing its QR; the desktop must expose
 /// the same order, otherwise the window only reports a failure and the user is stranded.
 /// </summary>
 private async Task<BillingOrder> CheckoutRequest(string planId,string channel,string key,CancellationToken ct)
 {
  if(!IsConfigured)throw new BillingException("unconfigured","账户服务未配置，无法购买。");
  var outcome=await SendAsync(HttpMethod.Post,Url("/v1/billing/checkout"),JsonContent(new {planId,channel}),DefaultTimeout,ct,idempotencyKey:key).ConfigureAwait(false);
  if(!outcome.IsSuccess)
  {
   ThrowIfAuthenticationFailure(outcome);
   var (code,message)=ReadError(outcome);
   var carried=ReadErrorOrder(outcome);
   if(code=="payment_order_pending"&&carried!=null&&ValidBillingResponse(carried))
    throw new BillingException(code,message??"已有未确认的购买订单，已保留原订单。"){Order=carried};
   throw new BillingException(code,message??"账户服务暂不可用；下单重试将复用原订单标识。");
  }
  var result=Deserialize<BillingOrder>(outcome.Body);
  if(result==null||!ValidBillingResponse(result))throw new BillingException("invalid_response","支付响应不完整，请刷新原订单确认，请勿重复付款。");
  if(result.PlanId!=planId||result.Channel!=channel)throw new BillingException("invalid_response","订单信息与本次请求不符，请刷新原订单确认，请勿重复付款。");
  return result;
 }
 /// <summary>Reads the optional order attached to a failed response; absent for every other error.</summary>
 private static BillingOrder? ReadErrorOrder(HttpOutcome outcome)
 {
  if(string.IsNullOrWhiteSpace(outcome.Body))return null;
  try{return Deserialize<WireErrorBody>(outcome.Body)?.Order;}catch{return null;}
 }
 private async Task<T> BillingRequest<T>(string path,CancellationToken ct,object? body=null,string? key=null,string? expectedOrderNo=null,string? expectedPlanId=null,string? expectedChannel=null) where T:class
 {
  if(!IsConfigured)throw new BillingException("unconfigured","账户服务未配置，无法购买。");
  var outcome=await SendAsync(body==null?HttpMethod.Get:HttpMethod.Post,Url(path),body==null?null:JsonContent(body),DefaultTimeout,ct,idempotencyKey:key).ConfigureAwait(false);
  if(!outcome.IsSuccess){ThrowIfAuthenticationFailure(outcome);var (code,message)=ReadError(outcome);throw new BillingException(code,message??"账户服务暂不可用；下单重试将复用原订单标识。");}
  var result=Deserialize<T>(outcome.Body);
  if(result==null||!ValidBillingResponse(result))throw new BillingException("invalid_response",typeof(T)==typeof(BillingPlans)?"套餐响应不完整，请刷新套餐后重试。":"支付响应不完整，请刷新原订单确认，请勿重复付款。");
  if(result is BillingOrder order&&((expectedOrderNo!=null&&order.OrderNo!=expectedOrderNo)||(expectedPlanId!=null&&order.PlanId!=expectedPlanId)||(expectedChannel!=null&&order.Channel!=expectedChannel)))
   throw new BillingException("invalid_response","订单信息与本次请求不符，请刷新原订单确认，请勿重复付款。");
  return result;
 }
 private static bool ValidBillingResponse(object value)=>value switch
 {
  BillingOrder o=>!string.IsNullOrWhiteSpace(o.OrderNo)&&!string.IsNullOrWhiteSpace(o.PlanId)&&!string.IsNullOrWhiteSpace(o.PlanName)
   &&o.Status is "pending" or "paid" or "expired" or "failed" or "closed" or "cancelled" or "refunded"
   &&o.Channel is "alipay" or "wxpay" &&o.PayableCents>0&&o.ExpiresAt!=default&&o.AllowedActions!=null
   &&(!o.AllowedActions.Pay||(o.Status=="pending"&&(!string.IsNullOrWhiteSpace(o.QrCode)||!string.IsNullOrWhiteSpace(o.QrCodeImageUrl)))),
  BillingPlans p=>p.Plans!=null&&p.Plans.All(x=>x!=null&&!string.IsNullOrWhiteSpace(x.Id)&&!string.IsNullOrWhiteSpace(x.Name)&&x.PriceCents>=0&&x.DurationDays>0),
  BillingOrders o=>o.Orders!=null&&o.Orders.All(x=>x!=null&&ValidBillingResponse(x)),
  BillingEntitlements e=>!string.IsNullOrWhiteSpace(e.UserId)&&e.Subscription!=null&&!string.IsNullOrWhiteSpace(e.Subscription.PlanName)
   &&e.Usage!=null&&e.Usage.MonthlyQuota>=0&&e.Usage.Used>=0,
  _=>false
 };
}
