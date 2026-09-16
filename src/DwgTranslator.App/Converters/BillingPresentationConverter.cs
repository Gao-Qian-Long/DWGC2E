using System.Globalization;
using System.Windows.Data;
namespace DwgTranslator.App.Converters;
/// <summary>Display formatting only; original order objects and payment state stay unchanged.</summary>
public sealed class BillingPresentationConverter : IValueConverter
{
 public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => parameter?.ToString() switch {
 "Amount" => value is int cents ? $"¥{cents / 100m:0.00}" : "—",
 "Order" => value is string no && no.Length > 20 ? no[..8] + "…" + no[^5..] : value,
 _ => value?.ToString()?.ToLowerInvariant() switch { "confirming" => "确认中", "paid" => "已支付", "fulfilled" or "credited" => "已到账", "closed" or "cancelled" => "已关闭", "failed" or "create_failed" => "支付失败", "pending" or "created" or "awaiting_payment" => "待支付", "creating" => "创建中", "expired" => "已过期", "refunded" => "已退款", _ => "待确认" }
 };
 public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
