using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DwgTranslator.Core.Models;

namespace DwgTranslator.Core.Infrastructure.Payments;

/// <summary>
/// Owns the existing per-account purchase recovery file and exclusive window-lifetime lock.
/// It does not create orders, choose payment channels, poll the server or change membership.
/// </summary>
public sealed class PendingPurchaseStore : IDisposable
{
    private readonly FileStream accountLock;
    private bool disposed;

    public string RecordPath { get; }

    public PendingPurchaseStore(string userId, string? dataRoot = null)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("A purchase recovery store requires an account.", nameof(userId));
        var folder = Path.Combine(dataRoot ?? Environment.GetEnvironmentVariable("DWGC2E_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DwgTranslator"), "payments");
        Directory.CreateDirectory(folder);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userId)));
        RecordPath = Path.Combine(folder, key + ".json");
        accountLock = new FileStream(RecordPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public PendingPurchase? Read()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!File.Exists(RecordPath)) return null;
        PendingPurchase? pending;
        try { pending = JsonSerializer.Deserialize<PendingPurchase>(File.ReadAllText(RecordPath)); }
        catch (JsonException ex)
        {
            throw new InvalidDataException("未完成订单记录损坏，已保留原文件。请联系支持核查，不要重复购买。", ex);
        }
        if (pending == null || string.IsNullOrWhiteSpace(pending.Key)
            || !Regex.IsMatch(pending.Key, "^[a-zA-Z0-9_-]{16,80}$")
            || !new[] { "alipay", "wxpay" }.Contains(pending.Channel)
            || string.IsNullOrWhiteSpace(pending.PlanId))
            throw new InvalidDataException("未完成订单记录异常，请联系支持核查，不要重复购买。");
        return pending;
    }

    public void Save(PendingPurchase? pending)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (pending == null)
        {
            if (File.Exists(RecordPath)) File.Delete(RecordPath);
            return;
        }
        var temp = RecordPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(pending));
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            if (File.Exists(RecordPath)) File.Replace(temp, RecordPath, null);
            else File.Move(temp, RecordPath);
        }
        catch (IOException ex)
        {
            throw new InvalidDataException("未能保存购买恢复记录，本次操作未确认。请检查磁盘空间、目录权限或文件占用后重试；不要重复购买。", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidDataException("无法写入购买恢复记录，请检查目录权限后重试；不要重复购买。", ex);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        accountLock.Dispose();
        disposed = true;
    }
}
