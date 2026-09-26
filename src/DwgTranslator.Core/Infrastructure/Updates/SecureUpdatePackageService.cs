using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using DwgTranslator.Core.Api;

namespace DwgTranslator.Core.Services;

public sealed class UpdatePackageFile
{
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

public sealed class UpdatePackageManifest
{
    public int SchemaVersion { get; set; } = 2;
    public string Version { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = "QLCAD.exe";
    public string ExecutableSha256 { get; set; } = string.Empty;
    public List<UpdatePackageFile> Files { get; set; } = new();
    public List<string> ObsoletePaths { get; set; } = new();
}

public sealed class StagedUpdate
{
    public string Version { get; init; } = string.Empty;
    public string PackagePath { get; init; } = string.Empty;
    public string PackageSha256 { get; init; } = string.Empty;
    public string PackageSignature { get; init; } = string.Empty;
    public string PackageType { get; init; } = "zip";
    public string StagingDirectory { get; init; } = string.Empty;
    public string ExecutablePath { get; init; } = string.Empty;
}

/// <summary>Downloads and validates signed update packages without touching the running installation.</summary>
public sealed class SecureUpdatePackageService
{
    private const long MaximumPackageBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumExtractedBytes = 4L * 1024 * 1024 * 1024;
    private const string ManifestName = "update-manifest.json";
    private readonly HttpClient _http;
    private readonly string _publicKeyPem;

    public SecureUpdatePackageService(HttpClient http, string publicKeyPem)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _publicKeyPem = publicKeyPem ?? string.Empty;
    }

    public static bool HasInstallMetadata(VersionInfo info) =>
        (info.PackageType?.Equals("zip", StringComparison.OrdinalIgnoreCase) == true ||
         info.PackageType?.Equals("setup-exe", StringComparison.OrdinalIgnoreCase) == true) &&
        info.PackageSize is > 0 and <= MaximumPackageBytes &&
        IsSha256(info.PackageSha256) && !string.IsNullOrWhiteSpace(info.PackageSignature) &&
        Uri.TryCreate(info.DownloadUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    public async Task<StagedUpdate> DownloadAndStageAsync(VersionInfo info, string updateRoot,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!HasInstallMetadata(info)) throw new InvalidOperationException("更新缺少受信任安装包类型、大小、SHA-256、签名或 HTTPS 下载地址，只能显示更新信息。 ");
        if (string.IsNullOrWhiteSpace(_publicKeyPem)) throw new InvalidOperationException("APP 未配置更新签名公钥。");
        var root = Path.GetFullPath(updateRoot);
        Directory.CreateDirectory(root);
        var work = Path.Combine(root, SanitizeVersion(info.LatestVersion) + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var packageType = info.PackageType!.Trim().ToLowerInvariant();
        var package = Path.Combine(work, packageType == "setup-exe" ? "package.exe" : "package.zip");
        var staging = Path.Combine(work, "staging");
        try
        {
            using var response = await _http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long advertised && advertised != info.PackageSize)
                throw new InvalidDataException("更新包服务器大小与清单不一致。");
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(package, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, true))
            {
                var buffer = new byte[1024 * 128];
                long total = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    total += read;
                    if (total > info.PackageSize || total > MaximumPackageBytes) throw new InvalidDataException("更新包超过清单声明大小。");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report(total * 100d / info.PackageSize!.Value);
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (total != info.PackageSize) throw new InvalidDataException("更新包下载不完整。");
            }

            var hash = await HashFileAsync(package, cancellationToken).ConfigureAwait(false);
            if (!hash.Equals(info.PackageSha256, StringComparison.OrdinalIgnoreCase)) throw new CryptographicException("更新包 SHA-256 校验失败。");
            VerifySignature(hash, info.PackageSignature!);

            if (packageType == "setup-exe")
            {
                return new StagedUpdate
                {
                    Version = info.LatestVersion,
                    PackagePath = package,
                    PackageSha256 = hash,
                    PackageSignature = info.PackageSignature!,
                    PackageType = packageType,
                    ExecutablePath = package
                };
            }

            Directory.CreateDirectory(staging);
            ExtractSafely(package, staging);
            var manifestPath = Path.Combine(staging, ManifestName);
            if (!File.Exists(manifestPath)) throw new InvalidDataException($"更新包缺少 {ManifestName}。");
            var manifest = JsonSerializer.Deserialize<UpdatePackageManifest>(await File.ReadAllTextAsync(manifestPath, cancellationToken), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("更新清单为空。");
            await ValidateManifestAsync(staging, manifest, info.LatestVersion, cancellationToken).ConfigureAwait(false);
            var executable = SafeChildPath(staging, manifest.ExecutablePath);

            return new StagedUpdate
            {
                Version = info.LatestVersion,
                PackagePath = package,
                PackageSha256 = hash,
                PackageSignature = info.PackageSignature!,
                PackageType = packageType,
                StagingDirectory = staging,
                ExecutablePath = executable
            };
        }
        catch
        {
            try { Directory.Delete(work, true); } catch { }
            throw;
        }
    }

    private async Task ValidateManifestAsync(string staging, UpdatePackageManifest manifest, string expectedVersion, CancellationToken token)
    {
        if (manifest.SchemaVersion != 2 || !string.Equals(manifest.Version, expectedVersion, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新包版本或清单架构与服务端版本不一致。");
        if (manifest.Files == null || manifest.Files.Count == 0 || manifest.ObsoletePaths == null)
            throw new InvalidDataException("更新清单缺少完整的受管文件列表。");

        var listed = new Dictionary<string, UpdatePackageFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            var relative = NormalizeRelativePath(file.Path);
            RejectProtectedPath(relative);
            if (file.Size < 0 || !IsSha256(file.Sha256) || !listed.TryAdd(relative, file))
                throw new InvalidDataException($"更新清单文件记录无效或重复：{relative}");
            var path = SafeChildPath(staging, relative);
            if (!File.Exists(path)) throw new InvalidDataException($"更新包缺少清单文件：{relative}");
            var actualSize = new FileInfo(path).Length;
            if (actualSize != file.Size) throw new InvalidDataException($"更新文件大小不匹配：{relative}");
            var actualHash = await HashFileAsync(path, token).ConfigureAwait(false);
            if (!actualHash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException($"更新文件哈希不匹配：{relative}");
        }

        var actualFiles = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(staging, path).Replace('\\', '/'))
            .Where(path => !path.Equals(ManifestName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (actualFiles.Length != listed.Count || actualFiles.Any(path => !listed.ContainsKey(path)))
            throw new InvalidDataException("更新包实际文件与受管文件清单不完全一致。");

        var executableRelative = NormalizeRelativePath(manifest.ExecutablePath);
        if (!listed.TryGetValue(executableRelative, out var executableRecord) || !IsSha256(manifest.ExecutableSha256)
            || !manifest.ExecutableSha256.Equals(executableRecord.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新清单主程序记录无效。");

        var obsolete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in manifest.ObsoletePaths)
        {
            var relative = NormalizeRelativePath(value);
            RejectProtectedPath(relative);
            if (listed.ContainsKey(relative) || !obsolete.Add(relative))
                throw new InvalidDataException($"更新清单待删除路径无效、重复或仍由新版本管理：{relative}");
        }
    }

    private void VerifySignature(string packageHash, string signatureText)
    {
        byte[] signature;
        try { signature = Convert.FromBase64String(signatureText); }
        catch (FormatException ex) { throw new CryptographicException("更新签名格式无效。", ex); }
        using var rsa = RSA.Create();
        rsa.ImportFromPem(_publicKeyPem);
        var hashBytes = Convert.FromHexString(packageHash);
        if (!rsa.VerifyHash(hashBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new CryptographicException("更新包签名验证失败。");
    }

    internal static void ExtractSafely(string package, string staging)
    {
        var root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalExtracted = 0;
        using var archive = ZipFile.OpenRead(package);
        foreach (var entry in archive.Entries)
        {
            if ((entry.ExternalAttributes & 0xF0000000) == 0xA0000000) throw new InvalidDataException("更新包禁止包含符号链接。");
            var relative = NormalizeRelativePath(entry.FullName, allowDirectory: true);
            if (!seen.Add(relative)) throw new InvalidDataException($"更新包包含重复路径：{relative}");
            var destination = Path.GetFullPath(Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新包路径越界。");
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            totalExtracted = checked(totalExtracted + entry.Length);
            if (totalExtracted > MaximumExtractedBytes) throw new InvalidDataException("更新包解压后体积超过安全上限。");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = entry.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
            output.Flush(true);
        }
    }

    internal static string NormalizeRelativePath(string? relative, bool allowDirectory = false)
    {
        if (string.IsNullOrWhiteSpace(relative)) throw new InvalidDataException("更新清单包含空路径。");
        var normalized = relative.Replace('\\', '/').Trim();
        if (normalized.StartsWith('/') || Path.IsPathRooted(normalized)) throw new InvalidDataException("更新路径必须是相对路径。");
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => part is "." or ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("更新路径无效或越界。");
        normalized = string.Join('/', parts);
        if (!allowDirectory && normalized.EndsWith('/')) throw new InvalidDataException("更新清单只能列出文件路径。");
        return normalized;
    }

    private static void RejectProtectedPath(string relative)
    {
        var normalized = relative.Replace('\\', '/');
        var first = normalized.Split('/')[0];
        if (first.Equals("settings.json", StringComparison.OrdinalIgnoreCase)
            || first.Equals("data", StringComparison.OrdinalIgnoreCase)
            || first.Equals("projects", StringComparison.OrdinalIgnoreCase)
            || first.Equals("glossaries", StringComparison.OrdinalIgnoreCase)
            || first.Equals("update-managed-files.json", StringComparison.OrdinalIgnoreCase)
            || new[] { ".db", ".sqlite", ".sqlite3" }.Contains(Path.GetExtension(normalized), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"更新清单不得管理用户数据路径：{relative}");
    }

    private static string SafeChildPath(string root, string relative)
    {
        var normalized = NormalizeRelativePath(relative);
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新清单路径越界。");
        return full;
    }

    private static bool IsSha256(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
    private static string SanitizeVersion(string value) => string.Concat((value ?? "update").Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_'));

    private static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
    }
}
