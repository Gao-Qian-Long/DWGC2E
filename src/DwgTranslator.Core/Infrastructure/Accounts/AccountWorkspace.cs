using System.Security.Cryptography;
using System.Text;
namespace DwgTranslator.Core.Services;
/// <summary>Local data ownership boundary; never reuse legacy unowned files automatically.</summary>
public static class AccountWorkspace
{
 public static string OutputDirectoryFor(Models.AppConfig config, string root)
 {
  var key = string.IsNullOrWhiteSpace(config.ActiveAccountId) ? "guest" : config.ActiveAccountId;
  if (config.AccountOutputDirectories != null && config.AccountOutputDirectories.TryGetValue(key, out var saved)
      && !string.IsNullOrWhiteSpace(saved) && Path.IsPathFullyQualified(saved)) return saved;
  // A legacy absolute directory belongs only to the account saved alongside it.
  if ((config.AccountOutputDirectories == null || config.AccountOutputDirectories.Count == 0)
      && Path.IsPathFullyQualified(config.ExportDirectory)) return config.ExportDirectory;
  // 未明确选择时保持为空；翻译/校对可继续，导出入口负责引导用户设置。
  return string.Empty;
 }
 public static string DirectoryFor(string root,string? accountId)
 {
  var key=string.IsNullOrWhiteSpace(accountId)?"guest":Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountId))).ToLowerInvariant();
  return Path.Combine(Path.GetFullPath(root),"accounts",key);
 }
}
