using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DwgTranslator.Core.Infrastructure.Payments;
using DwgTranslator.Core.Models;

namespace DwgTranslator.Core.Tests;

public sealed class PendingPurchaseStoreTests : IDisposable
{
    private readonly string directory;
    private readonly string parent;

    public PendingPurchaseStoreTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "DwgTranslator.sln"))) root = root.Parent;
        if (root == null) throw new InvalidOperationException("Cannot locate isolated test workspace.");
        parent = Path.Combine(root.FullName, "artifacts", "installer-safety-20260916-resumed", "billing-storage", "tests");
        directory = Path.Combine(parent, Guid.NewGuid().ToString("N"));
    }

    private static PendingPurchase Intent(string channel = "alipay") => new()
    {
        PlanId = "fixture-month", Channel = channel, Key = "0123456789abcdef0123456789abcdef"
    };

    [Fact]
    public void ExistingPascalCaseFileAndAccountHashRemainCompatible()
    {
        using var store = new PendingPurchaseStore("fixture-account", directory);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("fixture-account")));
        Assert.Equal(Path.Combine(directory, "payments", hash + ".json"), store.RecordPath);
        const string json = "{\"PlanId\":\"old-plan\",\"Channel\":\"wxpay\",\"Key\":\"0123456789abcdef\",\"OrderNo\":\"original-order\"}";
        File.WriteAllText(store.RecordPath, json);
        var read = store.Read()!;
        Assert.Equal("old-plan", read.PlanId);
        Assert.Equal("wxpay", read.Channel);
        Assert.Equal("original-order", read.OrderNo);
        Assert.Equal(json, File.ReadAllText(store.RecordPath));
    }

    [Theory]
    [InlineData("alipay")]
    [InlineData("wxpay")]
    public void CreateReplaceAndClearPreserveIdempotencyKey(string channel)
    {
        using var store = new PendingPurchaseStore("fixture-account", directory);
        Assert.Null(store.Read());
        var intent = Intent(channel);
        store.Save(intent);
        Assert.Equal(JsonSerializer.Serialize(intent), File.ReadAllText(store.RecordPath));
        intent.OrderNo = "fixture-order";
        store.Save(intent);
        Assert.Equal(intent.Key, store.Read()!.Key);
        Assert.Equal("fixture-order", store.Read()!.OrderNo);
        Assert.Equal(channel, store.Read()!.Channel);
        store.Save(null);
        Assert.Null(store.Read());
        store.Save(null);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store.RecordPath)!, "*.tmp"));
    }

    [Fact]
    public void SameAccountIsExclusiveAndOtherAccountsRemainIndependent()
    {
        using var first = new PendingPurchaseStore("first", directory);
        Assert.Throws<IOException>(() => new PendingPurchaseStore("first", directory));
        using var other = new PendingPurchaseStore("other", directory);
        Assert.NotEqual(first.RecordPath, other.RecordPath);
        first.Save(Intent());
        Assert.Null(other.Read());
        first.Dispose();
        using var reopened = new PendingPurchaseStore("first", directory);
        Assert.Equal(Intent().Key, reopened.Read()!.Key);
        Assert.Throws<ObjectDisposedException>(() => first.Read());
        Assert.Throws<ObjectDisposedException>(() => first.Save(Intent()));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"PlanId\":\"p\",\"Channel\":\"unknown\",\"Key\":\"0123456789abcdef\"}")]
    [InlineData("{\"PlanId\":\"p\",\"Channel\":\"alipay\",\"Key\":\"short\"}")]
    [InlineData("{\"PlanId\":\"\",\"Channel\":\"alipay\",\"Key\":\"0123456789abcdef\"}")]
    public void InvalidRecoveryIsRetainedAndKeepsExclusiveOwnership(string json)
    {
        using var store = new PendingPurchaseStore("fixture-account", directory);
        File.WriteAllText(store.RecordPath, json);
        Assert.Throws<InvalidDataException>(() => store.Read());
        Assert.Equal(json, File.ReadAllText(store.RecordPath));
        Assert.Throws<IOException>(() => new PendingPurchaseStore("fixture-account", directory));
    }

    [Fact]
    public void FailedReplacementPreservesOriginalAndCleansTemporaryFile()
    {
        using var store = new PendingPurchaseStore("fixture-account", directory);
        var intent = Intent();
        store.Save(intent);
        var original = File.ReadAllBytes(store.RecordPath);
        using (var busy = new FileStream(store.RecordPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            intent.OrderNo = "new-order";
            Assert.Throws<InvalidDataException>(() => store.Save(intent));
            Assert.Equal(original, File.ReadAllBytes(store.RecordPath));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store.RecordPath)!, "*.tmp"));
            Assert.Throws<IOException>(() => store.Save(null));
            Assert.True(File.Exists(store.RecordPath));
        }
        store.Save(intent);
        Assert.Equal("new-order", store.Read()!.OrderNo);
    }

    public void Dispose()
    {
        var full = Path.GetFullPath(directory);
        if (Path.GetDirectoryName(full) != parent || !Guid.TryParseExact(Path.GetFileName(full), "N", out _))
            throw new InvalidOperationException("Unsafe test fixture cleanup.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
