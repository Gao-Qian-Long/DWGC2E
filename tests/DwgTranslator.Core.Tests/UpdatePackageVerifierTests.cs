using System.Security.Cryptography;
using System.Text;
using DwgTranslator.Core.Services;
using Xunit;

namespace DwgTranslator.Core.Tests;

/// <summary>
/// 更新包应用前复验（签名 + 密钥标识）和更新脚本自身的完整性。
/// 这些是"APP 退出后到真正替换文件之间"唯一还信任得住的防线，必须有测试兜住。
/// </summary>
public sealed class UpdatePackageVerifierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dwgc2e-verify-" + Guid.NewGuid().ToString("N"));

    public UpdatePackageVerifierTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string WriteFile(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void PinnedKeyIdIsDerivedFromThePinnedPublicKey()
    {
        // 密钥标识不能是不可验证的不透明常量：必须能由固定公钥复算出来。
        var derived = UpdatePackageVerifier.ComputeKeyIdFromPem(UpdateTrust.PublicKeyPem);
        Assert.Equal(UpdateTrust.SigningKeyId, derived);
        Assert.Equal(UpdateTrust.SigningKeyId, UpdatePackageVerifier.ExpectedKeyId);
        Assert.StartsWith("rsa-sha256-", derived, StringComparison.Ordinal);
        Assert.Equal(64, derived["rsa-sha256-".Length..].Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("rsa-sha256-0000000000000000000000000000000000000000000000000000000000000000")]
    public void MissingOrMismatchedKeyIdIsNotTrusted(string? advertised)
    {
        Assert.False(UpdatePackageVerifier.IsTrustedKeyId(advertised));
    }

    [Fact]
    public void MatchingKeyIdIsTrustedCaseInsensitively()
    {
        Assert.True(UpdatePackageVerifier.IsTrustedKeyId(UpdateTrust.SigningKeyId));
        Assert.True(UpdatePackageVerifier.IsTrustedKeyId(UpdateTrust.SigningKeyId.ToUpperInvariant()));
    }

    [Fact]
    public void SignatureVerifiesForAnUntamperedPackage()
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportSubjectPublicKeyInfoPem();
        var bytes = Encoding.UTF8.GetBytes("package-payload");
        var path = WriteFile("good.zip", bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var signature = Convert.ToBase64String(rsa.SignHash(Convert.FromHexString(hash), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        var actual = UpdatePackageVerifier.VerifyPackage(path, hash, signature, pem);
        Assert.Equal(hash, actual);
    }

    [Fact]
    public void TamperedPackageIsRejectedEvenWhenTheHashMatchesTheSignature()
    {
        // 攻击面：替换 ZIP 的通时也把清单里的 sha256 一起换掉。纯哈希比对挡不住，
        // 只有固定公钥复验签名才能发现。
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportSubjectPublicKeyInfoPem();
        var original = Encoding.UTF8.GetBytes("original-payload");
        var originalHash = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();
        var signature = Convert.ToBase64String(rsa.SignHash(Convert.FromHexString(originalHash), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        var tampered = Encoding.UTF8.GetBytes("attacker-payload");
        var path = WriteFile("tampered.zip", tampered);
        var tamperedHash = Convert.ToHexString(SHA256.HashData(tampered)).ToLowerInvariant();

        // 哈希"对得上"被篡改后的文件，但签名对不上。
        var error = Assert.Throws<CryptographicException>(
            () => UpdatePackageVerifier.VerifyPackage(path, tamperedHash, signature, pem));
        Assert.Contains("签名", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HashMismatchIsRejectedBeforeSignatureCheck()
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportSubjectPublicKeyInfoPem();
        var path = WriteFile("hashed.zip", Encoding.UTF8.GetBytes("payload"));
        var signature = Convert.ToBase64String(new byte[256]);

        var error = Assert.Throws<CryptographicException>(
            () => UpdatePackageVerifier.VerifyPackage(path, new string('a', 64), signature, pem));
        Assert.Contains("SHA-256", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "signature")]
    [InlineData("not-hex-at-all", "signature")]
    [InlineData("ABCDEF", "signature")]
    public void MalformedHashOrSignatureIsRejected(string hash, string signature)
    {
        using var rsa = RSA.Create(2048);
        Assert.ThrowsAny<CryptographicException>(
            () => UpdatePackageVerifier.VerifySignature(hash, signature, rsa.ExportSubjectPublicKeyInfoPem()));
    }

    [Fact]
    public void MissingPackageFileIsReportedAsMissing()
    {
        Assert.Throws<FileNotFoundException>(
            () => UpdatePackageVerifier.VerifyPackage(Path.Combine(_root, "absent.zip"), new string('a', 64), "sig", UpdateTrust.PublicKeyPem));
    }

    [Fact]
    public void ApplyScriptDocumentsItsHashOnlyContract()
    {
        var work = Path.Combine(_root, "script");
        var path = UpdateApplyScript.Write(work);
        var text = File.ReadAllText(path);

        // 这个脚本跑在 Windows PowerShell 5.1 (.NET Framework) 上，无法加载 net8.0 的 Core，
        // 所以它只能复验哈希、无法复验 RSA 签名。契约必须写明这一点，避免以后误以为它验证了签名。
        Assert.Contains("Windows PowerShell 5.1", text, StringComparison.Ordinal);
        Assert.Contains("DwgTranslator.Updater", text, StringComparison.Ordinal);
        Assert.DoesNotContain("VerifyHash", text, StringComparison.Ordinal);
        Assert.Contains("APP 退出后更新包 SHA-256 二次校验失败", text, StringComparison.Ordinal);
    }
}
