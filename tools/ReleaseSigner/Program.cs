using System.Security.Cryptography;
using System.Text.Json;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: ReleaseSigner <package-path> <private-key-pem>");
    return 2;
}

var packagePath = Path.GetFullPath(args[0]);
var keyPath = Path.GetFullPath(args[1]);
if (!File.Exists(packagePath) || !File.Exists(keyPath))
{
    Console.Error.WriteLine("Package or private key file does not exist.");
    return 3;
}

try
{
    var hash = await SHA256.HashDataAsync(File.OpenRead(packagePath));
    using var rsa = RSA.Create();
    rsa.ImportFromPem(await File.ReadAllTextAsync(keyPath));
    var signature = rsa.SignHash(hash, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    var publicKey = rsa.ExportSubjectPublicKeyInfo();
    var keyHash = SHA256.HashData(publicKey);
    var result = new
    {
        package_sha256 = Convert.ToHexString(hash).ToLowerInvariant(),
        package_signature = Convert.ToBase64String(signature),
        signing_key_id = "rsa-sha256-" + Convert.ToHexString(keyHash).ToLowerInvariant(),
        package_size = new FileInfo(packagePath).Length
    };
    Console.WriteLine(JsonSerializer.Serialize(result));
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("Signing failed: " + ex.Message);
    return 4;
}
