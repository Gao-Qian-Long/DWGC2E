using System.Security.Cryptography;

namespace DwgTranslator.Core.Services;

/// <summary>
/// 更新包的应用前复验：只依赖已固定的公钥和原始 ZIP 字节，不信任任何"已解压暂存目录"。
///
/// 为什么需要它：APP 下载并校验一次之后会把 ZIP 留在磁盘上，等到独立更新器真正执行安装时，
/// 中间隔了"用户确认 + APP 退出"这段时间。此时若只比对 SHA-256，攻击者或残留进程可以同时
/// 替换 ZIP 与其哈希；只有用固定公钥复验 RSA 签名才能证明"这个包仍出自发布方"。密钥标识
/// 同样由公钥本身推导，避免把它当成不可验证的不透明常量。
/// </summary>
public static class UpdatePackageVerifier
{
    private const string KeyIdPrefix = "rsa-sha256-";

    /// <summary>由固定公钥推导的密钥标识（SubjectPublicKeyInfo 的 SHA-256）。</summary>
    public static string ComputeKeyIdFromPem(string publicKeyPem)
    {
        if (string.IsNullOrWhiteSpace(publicKeyPem)) throw new ArgumentException("更新公钥不能为空。", nameof(publicKeyPem));
        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKeyPem);
        return KeyIdPrefix + Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
    }

    /// <summary>由固定公钥推导的密钥标识，用于校验服务端下发的 signing_key_id。</summary>
    public static string ExpectedKeyId => ComputeKeyIdFromPem(UpdateTrust.PublicKeyPem);

    /// <summary>服务端声明的密钥标识是否就是本机固定公钥。缺失或不一致都判为不可信。</summary>
    public static bool IsTrustedKeyId(string? advertisedKeyId, string publicKeyPem = "")
    {
        if (string.IsNullOrWhiteSpace(advertisedKeyId)) return false;
        var pem = string.IsNullOrWhiteSpace(publicKeyPem) ? UpdateTrust.PublicKeyPem : publicKeyPem;
        return string.Equals(advertisedKeyId.Trim(), ComputeKeyIdFromPem(pem), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>计算文件 SHA-256（小写十六进制）。</summary>
    public static string ComputeFileSha256(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// 用固定公钥复验「SHA-256 十六进制 + Base64 签名」是否匹配。失败抛 <see cref="CryptographicException"/>。
    /// 签名对象是<b>哈希字节</b>（不是文件内容），与 APP 侧保持一致。
    /// </summary>
    public static void VerifySignature(string sha256Hex, string signatureBase64, string publicKeyPem)
    {
        if (sha256Hex is null || sha256Hex.Length != 64) throw new CryptographicException("更新包 SHA-256 格式无效。");
        if (string.IsNullOrWhiteSpace(signatureBase64)) throw new CryptographicException("更新包签名为空。");

        byte[] signature;
        try { signature = Convert.FromBase64String(signatureBase64); }
        catch (FormatException ex) { throw new CryptographicException("更新包签名格式无效。", ex); }

        byte[] hashBytes;
        try { hashBytes = Convert.FromHexString(sha256Hex); }
        catch (FormatException ex) { throw new CryptographicException("更新包 SHA-256 格式无效。", ex); }

        using var rsa = RSA.Create();
        try { rsa.ImportFromPem(publicKeyPem); }
        catch (Exception ex) { throw new CryptographicException("更新签名公钥不可用。", ex); }

        if (!rsa.VerifyHash(hashBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new CryptographicException("更新包签名验证失败：包内容与发布方签名不一致。");
    }

    /// <summary>
    /// 应用前对原始 ZIP 的完整复验：文件必须存在、SHA-256 必须等于预期、签名必须由固定公钥签出。
    /// 返回实际哈希，便于调用方继续核对。
    /// </summary>
    public static string VerifyPackage(string packagePath, string expectedSha256, string signatureBase64, string publicKeyPem)
    {
        if (string.IsNullOrWhiteSpace(packagePath)) throw new ArgumentException("更新包路径不能为空。", nameof(packagePath));
        if (!File.Exists(packagePath)) throw new FileNotFoundException("更新包已不存在，无法应用更新。", packagePath);
        if (expectedSha256 is null || expectedSha256.Length != 64) throw new CryptographicException("预期更新包哈希格式无效。");

        var actual = ComputeFileSha256(packagePath);
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("APP 退出后更新包 SHA-256 复核失败：文件已被改动。");

        VerifySignature(actual, signatureBase64, publicKeyPem);
        return actual;
    }
}
