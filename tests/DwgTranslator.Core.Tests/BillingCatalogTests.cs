using System.Net;
using System.Text;
using DwgTranslator.Core.Api;
namespace DwgTranslator.Core.Tests;
public class BillingCatalogTests
{
 private sealed class Handler(string body):HttpMessageHandler {
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct) {
   Assert.Equal(HttpMethod.Get,r.Method); Assert.Equal("/v1/billing/plans",r.RequestUri!.AbsolutePath);
   return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/json")});
  }
 }
 [Fact] public async Task SharedCatalogAllowsFreeWithoutBlockingPaidPlans() {
  using var http=new HttpClient(new Handler("""{"plans":[{"id":"free","name":"Free","price_cents":0,"duration_days":30,"enabled":1},{"id":"pro","name":"Pro","price_cents":8,"duration_days":30,"enabled":1},{"id":"max","name":"Max","price_cents":3,"duration_days":30,"enabled":1},{"id":"go","name":"Go","price_cents":70,"duration_days":30,"enabled":1},{"id":"disabled","name":"Disabled","price_cents":100,"duration_days":30,"enabled":0}],"paymentsEnabled":true}"""));
  var client=new WorkerApiClient(http,"https://test.invalid",()=>"a","d","h");
  var catalog=await client.GetBillingPlansAsync();
  Assert.Equal(5,catalog.Plans.Count); Assert.True(catalog.PaymentsEnabled);
  Assert.Equal(new[]{"pro","max","go"},catalog.Plans.Where(p=>p.IsPurchasable).Select(p=>p.Id));
  Assert.Equal(new[]{8,3,70},catalog.Plans.Where(p=>p.IsPurchasable).Select(p=>p.PriceCents));
 }
 [Theory]
 [InlineData(-1,30)] [InlineData(10,0)]
 public async Task InvalidCatalogStillRejected(int price,int days) {
  using var http=new HttpClient(new Handler($"{{\"plans\":[{{\"id\":\"p\",\"name\":\"P\",\"price_cents\":{price},\"duration_days\":{days}}}],\"paymentsEnabled\":true}}"));
  var client=new WorkerApiClient(http,"https://test.invalid",()=>"a","d","h");
  var error=await Assert.ThrowsAsync<BillingException>(()=>client.GetBillingPlansAsync());
  Assert.Equal("invalid_response",error.Code); Assert.Contains("套餐",error.Message); Assert.DoesNotContain("支付响应",error.Message);
 }
 [Fact] public async Task FreeOnlyCatalogRemainsReadableWhilePurchasesPaused() {
  using var http=new HttpClient(new Handler("""{"plans":[{"id":"free","name":"Free","price_cents":0,"duration_days":30}],"paymentsEnabled":false}"""));
  var client=new WorkerApiClient(http,"https://test.invalid",()=>"a","d","h");
  var catalog=await client.GetBillingPlansAsync(); Assert.False(catalog.PaymentsEnabled); Assert.Single(catalog.Plans); Assert.False(catalog.Plans[0].IsPurchasable);
 }
}
