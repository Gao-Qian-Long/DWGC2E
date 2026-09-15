using DwgTranslator.Core.Services;
namespace DwgTranslator.Core.Tests;
public sealed class InstallationIdentityStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "dwgc2e-identity-" + Guid.NewGuid().ToString("N"));
    private string IdentityPath => Path.Combine(directory, "device-id");
    [Fact] public void RestartPreservesIdentity()
    {
        var id = InstallationIdentityStore.GetOrCreate(IdentityPath);
        Assert.True(Guid.TryParse(id, out _));
        Assert.Equal(id, InstallationIdentityStore.GetOrCreate(IdentityPath));
    }
    [Fact] public async Task ConcurrentInitializationReturnsOneIdentity()
    {
        var ids = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => InstallationIdentityStore.GetOrCreate(IdentityPath))));
        Assert.Single(ids.Distinct());
        Assert.Single(Directory.GetFiles(directory));
    }
    [Fact] public void CorruptIdentityIsPreservedAndNeverReplaced()
    {
        Directory.CreateDirectory(directory); File.WriteAllText(IdentityPath, "broken");
        Assert.Throws<IOException>(() => InstallationIdentityStore.GetOrCreate(IdentityPath));
        Assert.Equal("broken", File.ReadAllText(IdentityPath));
    }
    [Fact] public void UnwritableLocationDoesNotReturnTemporaryIdentity()
    {
        Directory.CreateDirectory(directory); var blocker = Path.Combine(directory, "file"); File.WriteAllText(blocker, "preserve");
        Assert.ThrowsAny<IOException>(() => InstallationIdentityStore.GetOrCreate(Path.Combine(blocker, "device-id")));
        Assert.Equal("preserve", File.ReadAllText(blocker));
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
