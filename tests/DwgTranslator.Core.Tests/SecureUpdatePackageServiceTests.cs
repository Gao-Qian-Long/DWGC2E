using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DwgTranslator.Core.Api;
using DwgTranslator.Core.Services;

namespace DwgTranslator.Core.Tests;

public sealed class SecureUpdatePackageServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dwgc2e-update-" + Guid.NewGuid().ToString("N"));
    public SecureUpdatePackageServiceTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void MissingSecurityMetadataIsInformationOnly()
    {
        Assert.False(SecureUpdatePackageService.HasInstallMetadata(new VersionInfo { LatestVersion = "2.2.0", DownloadUrl = "https://example.com/app.zip" }));
        Assert.False(SecureUpdatePackageService.HasInstallMetadata(new VersionInfo { LatestVersion = "2.2.0", DownloadUrl = "http://example.com/app.zip", PackageType = "zip", PackageSize = 10, PackageSha256 = new string('a', 64), PackageSignature = "x" }));
    }

    [Fact]
    public async Task ValidSignedSchemaTwoZipStagesCompleteVerifiedPayload()
    {
        using var rsa = RSA.Create(2048);
        var zip = CreatePackage("2.2.0", new Dictionary<string, string>
        {
            ["QLCAD.exe"] = "new-exe",
            ["lib/helper.dll"] = "helper"
        });
        var info = Sign(zip, rsa, "2.2.0");
        using var http = new HttpClient(new BytesHandler(zip));
        var staged = await new SecureUpdatePackageService(http, rsa.ExportSubjectPublicKeyInfoPem()).DownloadAndStageAsync(info, _root);

        Assert.True(File.Exists(staged.ExecutablePath));
        Assert.Equal("new-exe", File.ReadAllText(staged.ExecutablePath));
        Assert.Equal(info.PackageSha256, staged.PackageSha256);
        Assert.Equal(info.PackageSignature, staged.PackageSignature);
        Assert.Equal("helper", File.ReadAllText(Path.Combine(staged.StagingDirectory, "lib", "helper.dll")));
    }

    [Fact]
    public async Task HashAndSignatureMismatchAreRejectedAndCleaned()
    {
        using var rsa = RSA.Create(2048);
        var zip = CreatePackage("2.2.0", new Dictionary<string, string> { ["QLCAD.exe"] = "new-exe" });
        var badHash = Sign(zip, rsa, "2.2.0");
        badHash.PackageSha256 = new string('0', 64);
        using var http1 = new HttpClient(new BytesHandler(zip));
        await Assert.ThrowsAsync<CryptographicException>(() => new SecureUpdatePackageService(http1, rsa.ExportSubjectPublicKeyInfoPem()).DownloadAndStageAsync(badHash, Path.Combine(_root, "hash")));

        var badSignature = Sign(zip, rsa, "2.2.0");
        badSignature.PackageSignature = Convert.ToBase64String(new byte[256]);
        using var http2 = new HttpClient(new BytesHandler(zip));
        await Assert.ThrowsAsync<CryptographicException>(() => new SecureUpdatePackageService(http2, rsa.ExportSubjectPublicKeyInfoPem()).DownloadAndStageAsync(badSignature, Path.Combine(_root, "signature")));
    }

    [Fact]
    public async Task TruncatedDownloadAndManifestVersionMismatchAreRejected()
    {
        using var rsa = RSA.Create(2048);
        var zip = CreatePackage("2.2.0", new Dictionary<string, string> { ["QLCAD.exe"] = "new-exe" });
        var truncated = Sign(zip, rsa, "2.2.0");
        truncated.PackageSize = zip.Length + 1;
        using var http1 = new HttpClient(new BytesHandler(zip, includeLength: false));
        await Assert.ThrowsAsync<InvalidDataException>(() => new SecureUpdatePackageService(http1, rsa.ExportSubjectPublicKeyInfoPem()).DownloadAndStageAsync(truncated, Path.Combine(_root, "truncated")));

        var mismatch = Sign(zip, rsa, "2.3.0");
        using var http2 = new HttpClient(new BytesHandler(zip));
        await Assert.ThrowsAsync<InvalidDataException>(() => new SecureUpdatePackageService(http2, rsa.ExportSubjectPublicKeyInfoPem()).DownloadAndStageAsync(mismatch, Path.Combine(_root, "version")));
    }

    [Fact]
    public async Task UnlistedPayloadFileIsRejectedEvenWhenPackageSignatureIsValid()
    {
        using var rsa = RSA.Create(2048);
        var zip = CreatePackage("2.2.0", new Dictionary<string, string>
        {
            ["QLCAD.exe"] = "new-exe",
            ["hidden.bin"] = "not-listed"
        }, manifestPaths: new[] { "QLCAD.exe" });
        var info = Sign(zip, rsa, "2.2.0");
        using var http = new HttpClient(new BytesHandler(zip));

        await Assert.ThrowsAsync<InvalidDataException>(() => new SecureUpdatePackageService(http, rsa.ExportSubjectPublicKeyInfoPem()).DownloadAndStageAsync(info, Path.Combine(_root, "unlisted")));
    }

    [Fact]
    public async Task PerFileHashMismatchAndProtectedUserPathAreRejected()
    {
        using var rsa = RSA.Create(2048);
        var badFileHash = CreatePackage("2.2.0", new Dictionary<string, string> { ["QLCAD.exe"] = "new-exe" }, corruptManifestHash: true);
        var badFileInfo = Sign(badFileHash, rsa, "2.2.0");
        using var http1 = new HttpClient(new BytesHandler(badFileHash));
        await Assert.ThrowsAsync<CryptographicException>(() => new SecureUpdatePackageService(http1, rsa.ExportSubjectPublicKeyInfoPem()).DownloadAndStageAsync(badFileInfo, Path.Combine(_root, "file-hash")));

        var protectedPayload = CreatePackage("2.2.0", new Dictionary<string, string>
        {
            ["QLCAD.exe"] = "new-exe",
            ["settings.json"] = "must-not-overwrite"
        });
        var protectedInfo = Sign(protectedPayload, rsa, "2.2.0");
        using var http2 = new HttpClient(new BytesHandler(protectedPayload));
        await Assert.ThrowsAsync<InvalidDataException>(() => new SecureUpdatePackageService(http2, rsa.ExportSubjectPublicKeyInfoPem()).DownloadAndStageAsync(protectedInfo, Path.Combine(_root, "protected")));
    }

    [Fact]
    public async Task ZipTraversalIsRejected()
    {
        using var rsa = RSA.Create(2048);
        byte[] zip;
        using (var memory = new MemoryStream())
        {
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
            {
                using var writer = new StreamWriter(archive.CreateEntry("../outside.txt").Open());
                writer.Write("bad");
            }
            zip = memory.ToArray();
        }
        var info = Sign(zip, rsa, "2.2.0");
        using var http = new HttpClient(new BytesHandler(zip));
        await Assert.ThrowsAsync<InvalidDataException>(() => new SecureUpdatePackageService(http, rsa.ExportSubjectPublicKeyInfoPem()).DownloadAndStageAsync(info, Path.Combine(_root, "traversal")));
        Assert.False(File.Exists(Path.Combine(_root, "outside.txt")));
    }

    private static byte[] CreatePackage(string version, IReadOnlyDictionary<string, string> payload,
        IReadOnlyCollection<string>? manifestPaths = null, bool corruptManifestHash = false)
    {
        manifestPaths ??= payload.Keys.ToArray();
        var records = manifestPaths.Select(path =>
        {
            var bytes = Encoding.UTF8.GetBytes(payload[path]);
            return new UpdatePackageFile
            {
                Path = path,
                Size = bytes.LongLength,
                Sha256 = corruptManifestHash ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
            };
        }).ToList();
        var executable = records.Single(record => record.Path.Equals("QLCAD.exe", StringComparison.OrdinalIgnoreCase));
        var manifest = JsonSerializer.Serialize(new UpdatePackageManifest
        {
            Version = version,
            ExecutablePath = "QLCAD.exe",
            ExecutableSha256 = executable.Sha256,
            Files = records,
            ObsoletePaths = new List<string> { "prompts/deepl_context.txt" }
        });

        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            foreach (var item in payload)
            {
                using var stream = archive.CreateEntry(item.Key).Open();
                stream.Write(Encoding.UTF8.GetBytes(item.Value));
            }
            using var writer = new StreamWriter(archive.CreateEntry("update-manifest.json").Open());
            writer.Write(manifest);
        }
        return memory.ToArray();
    }

    private static VersionInfo Sign(byte[] package, RSA rsa, string version)
    {
        var hash = SHA256.HashData(package);
        return new VersionInfo
        {
            LatestVersion = version,
            DownloadUrl = "https://updates.example/app.zip",
            PackageType = "zip",
            PackageSize = package.LongLength,
            PackageSha256 = Convert.ToHexString(hash).ToLowerInvariant(),
            PackageSignature = Convert.ToBase64String(rsa.SignHash(hash, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
        };
    }

    private sealed class BytesHandler(byte[] bytes, bool includeLength = true) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(bytes);
            if (!includeLength) content.Headers.ContentLength = null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
