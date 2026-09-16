using DwgTranslator.Core.Api;
using DwgTranslator.Core.Infrastructure.Payments;
using DwgTranslator.Core.Models;

namespace DwgTranslator.Core.Application.Payments;

/// <summary>
/// Preserves the checkout recovery ordering independently of the window.
/// The caller owns confirmation, account lifetime, serialization of actions and the store lock.
/// This service never creates a new intent/key, changes channels, polls or applies membership.
/// </summary>
public sealed class PurchaseCheckoutService(IBillingClient client, PendingPurchaseStore store)
{
    public void SaveIntent(PendingPurchase? pending) => store.Save(pending);

    public async Task<BillingOrder?> CheckoutAsync(PendingPurchase pending,
        Func<bool> sessionIsCurrent, CancellationToken cancellationToken = default)
    {
        // Every retry must persist the SAME intent before contacting checkout.
        store.Save(pending);
        var order = await client.CheckoutAsync(pending.PlanId, pending.Channel, pending.Key, cancellationToken);
        // The window's existing validity guard also closes an obsolete account window.
        if (!sessionIsCurrent()) return null;
        pending.OrderNo = order.OrderNo;
        store.Save(pending);
        return order;
    }

    public PendingPurchase? ClearPaidIntent(PendingPurchase? pending, BillingOrder order)
    {
        if (order.Status != "paid" || pending?.OrderNo != order.OrderNo) return pending;
        // Publish the cleared in-memory state only after durable deletion succeeds.
        store.Save(null);
        return null;
    }
}
