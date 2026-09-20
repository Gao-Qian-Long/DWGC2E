using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
namespace DwgTranslator.Core.Services;
/// <summary>Local data ownership boundary; never reuse legacy unowned files automatically.</summary>
public static class AccountWorkspace
{
 /// <summary>
 /// <para>
 /// 解析本账号的默认导出目录。解析顺序：账号自己保存过的绝对目录（旧默认值除外）
 /// → 旧版遗留的绝对目录（仅当它不是旧默认值）→ <b>安装目录下的 exports</b>（可写时）
 /// → <paramref name="sourceDirectory"/> 旁边的 <paramref name="defaultSubfolderName"/> 子目录 → 空串。
 /// </para>
 /// <para>
 /// 2026-10-02 用户决定：默认输出 = &lt;安装目录&gt;\exports。改的是代码默认值 + 旧值迁移，
 /// 发布新版后立即生效，不需要重装；用户显式选过的目录（<c>AccountOutputDirectories</c>
 /// 里存的非旧默认值）始终优先，不会被覆盖。安装区不可写（如 Program Files 无权限）时
 /// 退回"源图纸旁边"，导出入口再兜底引导用户选一次。
 /// </para>
 /// <para>
 /// 历史包袱：早期版本先后把 %LocalAppData%\DWGC2E\exports（产品数据区）和
 /// %AppData%\DwgTranslator\exports（旧漫游目录）当默认输出并持久化进了配置，
 /// 用户看到的是"译文写进了 AppData"。这些旧默认值一律视为未被选择过，不算用户的选择。
 /// </para>
 /// </summary>
 /// <param name="sourceDirectory">首张待导出图纸所在目录；有它才有"落在源图纸旁边"这个兜底默认。</param>
 /// <param name="defaultSubfolderName">源目录下的默认子目录名；为空则直接用源目录本身。</param>
 /// <param name="installDirectory">软件安装目录（AppDomain.BaseDirectory）；为空则跳过安装区默认。</param>
 public static string OutputDirectoryFor(
     Models.AppConfig config,
     string root,
     string? sourceDirectory = null,
     string? defaultSubfolderName = null,
     string? installDirectory = null)
 {
  var key = string.IsNullOrWhiteSpace(config.ActiveAccountId) ? "guest" : config.ActiveAccountId;
  if (config.AccountOutputDirectories != null && config.AccountOutputDirectories.TryGetValue(key, out var saved)
      && !string.IsNullOrWhiteSpace(saved) && Path.IsPathFullyQualified(saved)
      && !IsLegacyDefault(saved!, root)) return saved;
  // A legacy absolute directory belongs only to the account saved alongside it — unless it is one of
  // the old product defaults this method exists to stop using.
  if ((config.AccountOutputDirectories == null || config.AccountOutputDirectories.Count == 0)
      && Path.IsPathFullyQualified(config.ExportDirectory)
      && !IsLegacyDefault(config.ExportDirectory!, root)) return config.ExportDirectory;
  var installExports = TryInstallExports(installDirectory);
  if (installExports != null) return installExports;
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
 /// <summary>
 /// 旧默认输出（不算用户的选择）：产品数据目录下的 root\exports，
 /// 或旧漫游数据目录下的 %AppData%\DwgTranslator\exports。
 /// </summary>
 private static bool IsLegacyDefault(string candidate, string root)
     => IsProductAreaDefault(candidate, root) || IsRoamingLegacyDefault(candidate);
 /// <summary>是否为产品数据目录下的旧默认输出（root\exports）。</summary>
 private static bool IsProductAreaDefault(string candidate, string root)
 {
  try
  {
   if (string.IsNullOrWhiteSpace(root)) return false;
   return SamePath(candidate, Path.Combine(Path.GetFullPath(root), "exports"));
  }
  catch (ArgumentException) { return false; }
  catch (NotSupportedException) { return false; }
 }
 /// <summary>是否为旧漫游数据目录下的默认输出（%AppData%\DwgTranslator\exports）。</summary>
 private static bool IsRoamingLegacyDefault(string candidate)
 {
  try
  {
   var roaming = Path.Combine(
       Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DwgTranslator", "exports");
   return SamePath(candidate, roaming);
  }
  catch (ArgumentException) { return false; }
  catch (NotSupportedException) { return false; }
 }
 private static bool SamePath(string left, string right)
 {
  try
  {
   return string.Equals(
       Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
       Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
       StringComparison.OrdinalIgnoreCase);
  }
  catch (ArgumentException) { return false; }
  catch (NotSupportedException) { return false; }
 }
 // 安装区 exports 的可写探测结果按目录缓存：解析在启动/翻页/导出等多处调用，不重复做 IO 探测。
 private static readonly ConcurrentDictionary<string, string?> InstallExportsProbeCache = new(StringComparer.OrdinalIgnoreCase);
 /// <summary>&lt;安装目录&gt;\exports；目录不可写（如 Program Files 无权限）时返回 null，由调用方退回下一级默认。</summary>
 private static string? TryInstallExports(string? installDirectory)
 {
  if (string.IsNullOrWhiteSpace(installDirectory)) return null;
  return InstallExportsProbeCache.GetOrAdd(installDirectory!, dir =>
  {
   try
   {
    var exports = Path.Combine(Path.GetFullPath(dir), "exports");
    Directory.CreateDirectory(exports);
    using var probe = new FileStream(Path.Combine(exports, ".write-test-" + Guid.NewGuid().ToString("N")),
        FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
    return exports;
   }
   catch (Exception) { return null; }
  });
 }
 public static string DirectoryFor(string root,string? accountId)
 {
  var key=string.IsNullOrWhiteSpace(accountId)?"guest":Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountId))).ToLowerInvariant();
  return Path.Combine(Path.GetFullPath(root),"accounts",key);
 }
}
