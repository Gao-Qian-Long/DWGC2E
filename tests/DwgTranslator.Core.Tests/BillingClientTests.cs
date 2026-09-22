using System.Net;
using System.Text;
using DwgTranslator.Core.Api;
namespace DwgTranslator.Core.Tests;
public class BillingClientTests
{
 private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)=>send(r,ct); }
 private const string ValidOrder="""{"orderNo":"DW123","planId":"plan","planName":"Pro","status":"pending","channel":"alipay","payableCents":19,"expiresAt":"2026-09-16T00:00:00Z","qrCode":"test-qr","allowedActions":{"pay":true,"confirm":true},"pollAfterMs":3000}""";
 private static HttpResponseMessage Json(string body,HttpStatusCode status=HttpStatusCode.OK)=>new(status){Content=new StringContent(body,Encoding.UTF8,"application/json")};
 [Fact] public async Task CheckoutPreservesAccountAndIdempotencyOnEveryRetry(){
  var calls=0;using var http=new HttpClient(new Handler(async(r,ct)=>{calls++;Assert.Equal("Bearer account-a",r.Headers.Authorization!.ToString());Assert.Equal("intent-000000000001",r.Headers.GetValues("Idempotency-Key").Single());Assert.Equal("/v1/billing/checkout",r.RequestUri!.AbsolutePath);Assert.Contains("planId",await r.Content!.ReadAsStringAsync(ct));return Json(ValidOrder);}));
  IBillingClient c=new WorkerApiClient(http,"https://test.invalid",()=>"account-a","device","host");for(var i=0;i<2;i++){var o=await c.CheckoutAsync("plan","alipay","intent-000000000001");Assert.Equal(19,o.PayableCents);Assert.True(o.AllowedActions.Pay);}Assert.Equal(2,calls);
 }
 [Fact] public async Task SnapshotParsesPriceIndependentSnakeCaseEntitlements(){using var http=new HttpClient(new Handler((r,ct)=>Task.FromResult(Json("""{"userId":"u","subscription":{"plan_name":"pro","expires_at":"2026-10-01T00:00:00Z"},"usage":{"monthly_quota":1000000,"used":321}}"""))));var c=new WorkerApiClient(http,"https://test.invalid",()=>"a","d","h");var s=await c.GetBillingEntitlementsAsync();Assert.Equal("u",s.UserId);Assert.Equal("pro",s.Subscription.PlanName);Assert.Equal(1000000,s.Usage.MonthlyQuota);Assert.Equal(321,s.Usage.Used);}
 [Fact] public async Task AccountSwitchRejectsOldPaymentResponse(){var token="a";using var http=new HttpClient(new Handler((r,ct)=>{token="b";return Task.FromResult(Json("""{"orderNo":"old"}"""));}));var c=new WorkerApiClient(http,"https://test.invalid",()=>token,"d","h");await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>c.GetBillingOrderAsync("old"));}
 [Fact] public async Task PaymentAuthExpiryIsExplicit(){using var http=new HttpClient(new Handler((r,ct)=>Task.FromResult(Json("""{"error_code":"session_expired"}""",HttpStatusCode.Unauthorized))));var c=new WorkerApiClient(http,"https://test.invalid",()=>"a","d","h");await Assert.ThrowsAsync<ApiAuthenticationException>(()=>c.GetBillingOrdersAsync());}
 [Fact] public async Task ConfirmationNeverSendsPaidClaim(){using var http=new HttpClient(new Handler(async(r,ct)=>{Assert.EndsWith("/confirm",r.RequestUri!.AbsolutePath);Assert.Equal("{}",await r.Content!.ReadAsStringAsync(ct));return Json(ValidOrder);}));var c=new WorkerApiClient(http,"https://test.invalid",()=>"a","d","h");Assert.Equal("pending",(await c.ConfirmBillingOrderAsync("DW123")).Status);}
 [Theory]
 [InlineData("{}")]
 [InlineData("null")]
 [InlineData("{\"orderNo\":\"DW123\",\"status\":\"paid\"}")]
 [InlineData("{\"orders\":null}")]
 public async Task IncompleteSuccessfulOrdersAreRejected(string body){using var http=new HttpClient(new Handler((r,ct)=>Task.FromResult(Json(body))));var c=new WorkerApiClient(http,"https://test.invalid",()=>"a","d","h");var ex=await Assert.ThrowsAsync<BillingException>(()=>c.CheckoutAsync("plan","alipay","original-intent"));Assert.Equal("invalid_response",ex.Code);}
 [Theory]
 [InlineData("{\"userId\":\"u\"}")]
 [InlineData("{\"userId\":\"u\",\"subscription\":null,\"usage\":null}")]
 [InlineData("{\"userId\":\"u\",\"subscription\":{\"plan_name\":\"pro\"},\"usage\":{\"used\":0}}")]
 public async Task IncompleteSnapshotNeverDefaultsToZeroOrFree(string body){using var http=new HttpClient(new Handler((r,ct)=>Task.FromResult(Json(body))));var c=new WorkerApiClient(http,"https://test.invalid",()=>"a","d","h");Assert.Equal("invalid_response",(await Assert.ThrowsAsync<BillingException>(()=>c.GetBillingEntitlementsAsync())).Code);}
 [Theory]
 [InlineData("payableCents", "0")]
 [InlineData("status", "\"unexpected\"")]
 [InlineData("allowedActions", "null")]
 public async Task InvalidOrderValuesCannotBeDisplayed(string field,string value){var json=System.Text.Json.Nodes.JsonNode.Parse(ValidOrder)!;json[field]=System.Text.Json.Nodes.JsonNode.Parse(value);using var http=new HttpClient(new Handler((r,ct)=>Task.FromResult(Json(json.ToJsonString()))));var c=new WorkerApiClient(http,"https://test.invalid",()=>"a","d","h");await Assert.ThrowsAsync<BillingException>(()=>c.GetBillingOrderAsync("DW123"));}
 [Fact] public async Task InvalidCheckoutRetryRetainsOriginalKey(){var calls=0;using var http=new HttpClient(new Handler((r,ct)=>{Assert.Equal("original-intent",r.Headers.GetValues("Idempotency-Key").Single());return Task.FromResult(Json(++calls==1?"{}":ValidOrder));}));var c=new WorkerApiClient(http,"https://test.invalid",()=>"a","d","h");await Assert.ThrowsAsync<BillingException>(()=>c.CheckoutAsync("plan","alipay","original-intent"));Assert.Equal("DW123",(await c.CheckoutAsync("plan","alipay","original-intent")).OrderNo);Assert.Equal(2,calls);}
 [Fact] public async Task CheckoutConflictCarriesTheStillOpenOrderToTheWindow(){
  // 409 payment_order_pending 的响应体带着仍未完成的订单；窗口必须能拿到它去恢复二维码，
  // 否则用户只能看到一句失败提示，再也无法为这笔订单付款（网页端是直接接管该订单的）。
  var body="{\"error_code\":\"payment_order_pending\",\"message\":\"已有未确认的购买订单\",\"order\":"+ValidOrder+"}";
  using var http=new HttpClient(new Handler((r,ct)=>Task.FromResult(Json(body,HttpStatusCode.Conflict))));
  var c=new WorkerApiClient(http,"https://test.invalid",()=>"a","d","h");
  var ex=await Assert.ThrowsAsync<BillingException>(()=>c.CheckoutAsync("plan","alipay","original-intent"));
  Assert.Equal("payment_order_pending",ex.Code);Assert.NotNull(ex.Order);Assert.Equal("DW123",ex.Order!.OrderNo);Assert.Equal("pending",ex.Order.Status);
 }
 [Theory]
 [InlineData("{\"error_code\":\"idempotency_conflict\",\"message\":\"重复\"}")]
 [InlineData("{\"error_code\":\"payment_order_pending\",\"message\":\"重复\"}")]
 [InlineData("{\"error_code\":\"payment_order_pending\",\"order\":{\"orderNo\":\"DW9\"}}")]
 public async Task ConflictsWithoutAUsableOrderNeverInventOne(string body){
  using var http=new HttpClient(new Handler((r,ct)=>Task.FromResult(Json(body,HttpStatusCode.Conflict))));
  var c=new WorkerApiClient(http,"https://test.invalid",()=>"a","d","h");
  var ex=await Assert.ThrowsAsync<BillingException>(()=>c.CheckoutAsync("plan","alipay","original-intent"));
  Assert.Null(ex.Order);
 }
 [Fact] public async Task DifferentOrderCannotReplaceRequestedOrder(){using var http=new HttpClient(new Handler((r,ct)=>Task.FromResult(Json(ValidOrder))));var c=new WorkerApiClient(http,"https://test.invalid",()=>"a","d","h");await Assert.ThrowsAsync<BillingException>(()=>c.GetBillingOrderAsync("different-order"));await Assert.ThrowsAsync<BillingException>(()=>c.CheckoutAsync("different-plan","alipay","original-intent"));await Assert.ThrowsAsync<BillingException>(()=>c.CheckoutAsync("plan","wxpay","original-intent"));}
 [Theory]
 [InlineData("cancelled")]
 [InlineData("refunded")]
 [InlineData("expired")]
 [InlineData("failed")]
 [InlineData("closed")]
 [InlineData("paid")]
 public async Task NonPayableOrderStatesRemainReadableInDetailAndMixedHistory(string status)
 {
  var order=System.Text.Json.Nodes.JsonNode.Parse(ValidOrder)!;
  order["status"]=status;
  order["allowedActions"]!["pay"]=false;
  order["allowedActions"]!["confirm"]=status=="expired";
  var body=order.ToJsonString();
  using var http=new HttpClient(new Handler((r,ct)=>Task.FromResult(Json(
   r.RequestUri!.AbsolutePath.EndsWith("/orders")?"{\"orders\":["+ValidOrder+","+body+"]}":body))));
  var client=new WorkerApiClient(http,"https://test.invalid",()=>"a","d","h");
  var detail=await client.GetBillingOrderAsync("DW123");
  Assert.Equal(status,detail.Status);
  Assert.False(detail.AllowedActions.Pay);
  var history=await client.GetBillingOrdersAsync();
  Assert.Equal(2,history.Orders.Count);
  Assert.Equal("pending",history.Orders[0].Status);
  Assert.Equal(status,history.Orders[1].Status);
  Assert.False(history.Orders[1].AllowedActions.Pay);
 }
 [Theory]
 [InlineData("cancelled")]
 [InlineData("refunded")]
 [InlineData("expired")]
 [InlineData("failed")]
 [InlineData("closed")]
 [InlineData("paid")]
 public async Task NonPendingOrderWithPaymentPermissionIsRejected(string status)
 {
  var order=System.Text.Json.Nodes.JsonNode.Parse(ValidOrder)!;
  order["status"]=status;
  using var http=new HttpClient(new Handler((r,ct)=>Task.FromResult(Json(order.ToJsonString()))));
  var client=new WorkerApiClient(http,"https://test.invalid",()=>"a","d","h");
  var error=await Assert.ThrowsAsync<BillingException>(()=>client.GetBillingOrderAsync("DW123"));
  Assert.Equal("invalid_response",error.Code);
 }
}
