using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
namespace DwgTranslator.Core.Api;
public interface IBillingClient
{
 Task<BillingPlans> GetBillingPlansAsync(CancellationToken ct = default);
 Task<BillingOrders> GetBillingOrdersAsync(string? before = null, CancellationToken ct = default);
 Task<BillingOrder> GetBillingOrderAsync(string no, CancellationToken ct = default);
 Task<BillingOrder> CheckoutAsync(string planId, string channel, string key, CancellationToken ct = default);
 Task<BillingOrder> ConfirmBillingOrderAsync(string no, CancellationToken ct = default);
 Task<BillingEntitlements> GetBillingEntitlementsAsync(CancellationToken ct = default);
}
public sealed class BillingPlan
{
 [JsonRequired] public string Id {get;set;} = "";
 [JsonRequired] public string Name {get;set;} = "";
 [JsonRequired, JsonPropertyName("price_cents")] public int PriceCents {get;set;}
 [JsonRequired, JsonPropertyName("duration_days")] public int DurationDays {get;set;}
 public int Enabled {get;set;} = 1;
 public bool IsPurchasable => Enabled == 1 && PriceCents > 0;
 public string Label => $"{Name} · ¥{PriceCents/100m:0.00} · {DurationDays} 天";
}
public sealed class BillingPlans { [JsonRequired] public List<BillingPlan> Plans {get;set;} = new(); [JsonRequired] public bool PaymentsEnabled {get;set;} public string Message {get;set;} = ""; }
public sealed class BillingOrders { [JsonRequired] public List<BillingOrder> Orders {get;set;} = new(); public string? NextCursor {get;set;} }
public sealed class BillingActions { [JsonRequired] public bool Pay {get;set;} [JsonRequired] public bool Confirm {get;set;} }
public sealed class BillingOrder
{
 [JsonRequired] public string OrderNo {get;set;} = "";
 [JsonRequired] public string PlanId {get;set;} = "";
 [JsonRequired] public string PlanName {get;set;} = "";
 [JsonRequired] public string Status {get;set;} = "";
 public string CreateState {get;set;} = "";
 public string DisplayState {get;set;} = "";
 [JsonRequired] public string Channel {get;set;} = "";
 [JsonRequired] public int PayableCents {get;set;}
 [JsonRequired] public DateTimeOffset ExpiresAt {get;set;}
 public string? QrCode {get;set;}
 public string? QrCodeImageUrl {get;set;}
 [JsonRequired] public BillingActions AllowedActions {get;set;} = new();
 public int PollAfterMs {get;set;}
 public string Label => $"{PlanName} · ¥{PayableCents/100m:0.00} · {(Status=="paid"?"已付款":"待确认")} · {OrderNo}";
}
public sealed class BillingSubscription
{
 [JsonRequired, JsonPropertyName("plan_name")] public string PlanName {get;set;} = "free";
 [JsonPropertyName("starts_at")] public DateTime? StartsAt {get;set;}
 [JsonPropertyName("expires_at")] public DateTime? ExpiresAt {get;set;}
}
public sealed class BillingUsage
{
 [JsonRequired, JsonPropertyName("monthly_quota")] public long MonthlyQuota {get;set;}
 [JsonRequired] public long Used {get;set;}
 [JsonPropertyName("reset_at")] public DateTime? ResetAt {get;set;}
}
public sealed class BillingEntitlements
{
 [JsonRequired] public string UserId {get;set;} = "";
 [JsonRequired] public BillingSubscription Subscription {get;set;} = new();
 [JsonRequired] public BillingUsage Usage {get;set;} = new();
}
public sealed class BillingException : Exception
{
    public string Code { get; }
    /// <summary>
    /// 409 payment_order_pending 会带回仍未完成的原订单。网页端直接接管该订单继续展示二维码，
    /// 桌面端也必须拿得到它，否则用户只能停在错误提示上，再也看不到那笔订单的二维码。
    /// </summary>
    public BillingOrder? Order { get; init; }
    public BillingException(string code,string message):base(message){Code=code;}
}
