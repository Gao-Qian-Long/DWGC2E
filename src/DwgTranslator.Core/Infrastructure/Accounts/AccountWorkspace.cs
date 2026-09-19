using System.Security.Cryptography;
using System.Text;
namespace DwgTranslator.Core.Services;
/// <summary>Local data ownership boundary; never reuse legacy unowned files automatically.</summary>
public static class AccountWorkspace
{
 /// <summary>
 /// <para>
 /// 解析本账号的默认导出目录。解析顺序：账号自己保存过的绝对目录 → 旧版遗留的绝对目录（仅当它不是产品默认值）
 /// → <paramref name="sourceDirectory"/> 旁边的 <paramref name="defaultSubfolderName"/> 子目录 → 空串。
 /// </para>
 /// <para>
 /// 这里的默认值**永不属于 <paramref name="root"/>**（即 %LocalAppData%\DWGC2E）：早期版本在用户数据目录下
 /// 建了一个 exports 并把它当默认输出，用户看到的是"译文写进了 AppData"。任何落在产品目录里的旧值一律视为
 /// 未被选择过的默认值，而不是用户的选择——只有 <c>AccountOutputDirectories</c> 里存的才是用户显式选过的。
 /// </para>
 /// </summary>
 /// <param name="sourceDirectory">首张待导出图纸所在目录；有它才有"落在源图纸旁边"这个默认。</param>
 /// <param name="defaultSubfolderName">源目录下的默认子目录名；为空则直接用源目录本身。</param>
 public static string OutputDirectoryFor(
     Models.AppConfig config,
     string root,
     string? sourceDirectory = null,
     string? defaultSubfolderName = null)
 {
  var key = string.IsNullOrWhiteSpace(config.ActiveAccountId) ? "guest" : config.ActiveAccountId;
  if (config.AccountOutputDirectories != null && config.AccountOutputDirectories.TryGetValue(key, out var saved)
      && !string.IsNullOrWhiteSpace(saved) && Path.IsPathFullyQualified(saved)) return saved;
  // A legacy absolute directory belongs only to the account saved alongside it — unless it is the
  // product-area default this method exists to stop using.
  if ((config.AccountOutputDirectories == null || config.AccountOutputDirectories.Count == 0)
      && Path.IsPathFullyQualified(config.ExportDirectory)
      && !IsProductAreaDefault(config.ExportDirectory!, root)) return config.ExportDirectory;
  return DefaultBesideSource(sourceDirectory, defaultSubfolderName);
 }
 /// <summary>源图纸旁边的默认输出目录；没有源目录可依据时返回空串（保持"由导出入口引导"的旧行为）。</summary>
 public static string DefaultBesideSource(string? sourceDirectory, string? defaultSubfolderName)
 {
  if (string.IsNullOrWhiteSpace(sourceDirectory)) return string.Empty;
  string parent;
  try { parent = Path.GetFullPath(sourceDirectory); }
  catch (ArgumentException) { return string.Empty; }
  catch (NotSupportedException) { return string.Empty; }
  if (string.IsNullOrWhiteSpace(parent)) return string.Empty;
  return string.IsNullOrWhiteSpace(defaultSubfolderName)
      ? parent
      : Path.Combine(parent, defaultSubfolderName!);
 }
 /// <summary>是否为产品数据目录下的旧默认输出（root\exports）；这个值不算用户的选择。</summary>
 private static bool IsProductAreaDefault(string candidate, string root)
 {
  try
  {
   if (string.IsNullOrWhiteSpace(root)) return false;
   var productArea = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
   var value = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
   return string.Equals(value, Path.Combine(productArea, "exports"), StringComparison.OrdinalIgnoreCase);
  }
  catch (ArgumentException) { return false; }
  catch (NotSupportedException) { return false; }
 }
 public static string DirectoryFor(string root,string? accountId)
 {
  var key=string.IsNullOrWhiteSpace(accountId)?"guest":Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountId))).ToLowerInvariant();
  return Path.Combine(Path.GetFullPath(root),"accounts",key);
 }
}
