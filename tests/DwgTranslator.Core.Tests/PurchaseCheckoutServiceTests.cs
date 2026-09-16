using DwgTranslator.Core.Api;
using DwgTranslator.Core.Application.Payments;
using DwgTranslator.Core.Infrastructure.Payments;
using DwgTranslator.Core.Models;

namespace DwgTranslator.Core.Tests;

public sealed class PurchaseCheckoutServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dwgc2e-checkout-" + Guid.NewGuid().ToString("N"));
    private static PendingPurchase Intent() => new() { PlanId = "fixture-plan", Channel = "alipay", Key = "fixture-intent-00001" };
    private static BillingOrder Order(string status = "pending", string no = "fixture-order") => new() { OrderNo = no, Status = status };

    [Fact] public async Task PersistsBeforeCheckoutAndBeforeReturningOrder()
    {
        using var store = new PendingPurchaseStore("fixture-user", root);
        var intent = Intent();
        using var cts = new CancellationTokenSource();
        var client = new Client((plan, channel, key, ct) => {
            Assert.Equal(intent.PlanId, plan); Assert.Equal(intent.Channel, channel); Assert.Equal(intent.Key, key);
            Assert.Equal(cts.Token, ct); Assert.Equal(key, store.Read()!.Key); Assert.Null(store.Read()!.OrderNo);
            return Task.FromResult(Order());
        });
        var service = new PurchaseCheckoutService(client, store);
        var result = await service.CheckoutAsync(intent, () => true, cts.Token);
        Assert.Equal("fixture-order", result!.OrderNo);
        Assert.Equal(result.OrderNo, intent.OrderNo); Assert.Equal(result.OrderNo, store.Read()!.OrderNo);
    }

    [Fact] public async Task LockedInitialSaveMakesNoRequestAndRetainsOriginalBytes()
    {
        using var store = new PendingPurchaseStore("fixture-user", root);
        var intent = Intent(); store.Save(intent); var before = File.ReadAllBytes(store.RecordPath);
        var client = new Client((_, _, _, _) => Task.FromResult(Order()));
        using var locked = new FileStream(store.RecordPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await Assert.ThrowsAsync<InvalidDataException>(() => new PurchaseCheckoutService(client, store).CheckoutAsync(intent, () => true));
        Assert.Equal(0, client.Calls); Assert.Equal(before, File.ReadAllBytes(store.RecordPath));
    }

    [Fact] public async Task LostResponseRetriesSameDurableIntent()
    {
        using var store = new PendingPurchaseStore("fixture-user", root);
        var intent = Intent(); var calls = 0;
        var client = new Client((_, _, key, _) => { Assert.Equal(intent.Key, key); return ++calls == 1 ? Task.FromException<BillingOrder>(new IOException("fixture lost reply")) : Task.FromResult(Order()); });
        var service = new PurchaseCheckoutService(client, store);
        await Assert.ThrowsAsync<IOException>(() => service.CheckoutAsync(intent, () => true));
        var restored = store.Read()!; Assert.Null(restored.OrderNo);
        await service.CheckoutAsync(restored, () => true);
        Assert.Equal(2, calls); Assert.Equal(intent.Key, store.Read()!.Key);
    }

    [Fact] public async Task ResponseSaveFailureRetainsRecoveryKey()
    {
        using var store = new PendingPurchaseStore("fixture-user", root);
        var intent = Intent(); FileStream? responseLock = null;
        var client = new Client((_, _, _, _) => { responseLock = new FileStream(store.RecordPath, FileMode.Open, FileAccess.Read, FileShare.Read); return Task.FromResult(Order()); });
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => new PurchaseCheckoutService(client, store).CheckoutAsync(intent, () => true));
            Assert.Equal(intent.Key, store.Read()!.Key); Assert.Null(store.Read()!.OrderNo);
            Assert.Equal("fixture-order", intent.OrderNo); // Existing in-memory recovery behavior remains unchanged.
        }
        finally { responseLock?.Dispose(); }
    }

    [Fact] public async Task ObsoleteSessionDoesNotPersistResponse()
    {
        using var store = new PendingPurchaseStore("fixture-user", root);
        var intent = Intent(); var validityCalls = 0;
        var client = new Client((_, _, _, _) => Task.FromResult(Order()));
        var result = await new PurchaseCheckoutService(client, store).CheckoutAsync(intent, () => { validityCalls++; return false; });
        Assert.Null(result); Assert.Equal(1, validityCalls); Assert.Null(intent.OrderNo); Assert.Null(store.Read()!.OrderNo);
    }

    [Theory]
    [InlineData("pending", "fixture-order")]
    [InlineData("cancelled", "fixture-order")]
    [InlineData("paid", "other-order")]
    public void OnlyMatchingPaidOrderClearsIntent(string status, string no)
    {
        using var store = new PendingPurchaseStore("fixture-user", root);
        var intent = Intent(); intent.OrderNo = "fixture-order"; store.Save(intent);
        var before = File.ReadAllBytes(store.RecordPath);
        var service = new PurchaseCheckoutService(new Client((_, _, _, _) => throw new Exception("No network expected")), store);
        Assert.Same(intent, service.ClearPaidIntent(intent, Order(status, no)));
        Assert.Equal(before, File.ReadAllBytes(store.RecordPath));
    }

    [Fact] public void PaidCleanupFailureRetainsIntentAndCanRetry()
    {
        using var store = new PendingPurchaseStore("fixture-user", root);
        var intent = Intent(); intent.OrderNo = "fixture-order"; store.Save(intent);
        var service = new PurchaseCheckoutService(new Client((_, _, _, _) => throw new Exception("No network expected")), store);
        using (var locked = new FileStream(store.RecordPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Throws<IOException>(() => service.ClearPaidIntent(intent, Order("paid")));
            Assert.Equal("fixture-order", intent.OrderNo); Assert.Equal(intent.Key, store.Read()!.Key);
        }
        Assert.Null(service.ClearPaidIntent(intent, Order("paid"))); Assert.False(File.Exists(store.RecordPath));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private sealed class Client(Func<string, string, string, CancellationToken, Task<BillingOrder>> checkout) : IBillingClient
    {
        public int Calls;
        public Task<BillingOrder> CheckoutAsync(string planId, string channel, string key, CancellationToken ct = default) { Calls++; return checkout(planId, channel, key, ct); }
        public Task<BillingPlans> GetBillingPlansAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BillingOrders> GetBillingOrdersAsync(string? before = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BillingOrder> GetBillingOrderAsync(string no, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BillingOrder> ConfirmBillingOrderAsync(string no, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BillingEntitlements> GetBillingEntitlementsAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }
}
