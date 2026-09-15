using System.Security.Cryptography;
using System.Text;
namespace DwgTranslator.Core.Services;
/// <summary>Local data ownership boundary; never reuse legacy unowned files automatically.</summary>
public static class AccountWorkspace
{
 public static string DirectoryFor(string root,string? accountId)
 {
  var key=string.IsNullOrWhiteSpace(accountId)?"guest":Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountId))).ToLowerInvariant();
  return Path.Combine(Path.GetFullPath(root),"accounts",key);
 }
}
