using System.Security.Cryptography;
using System.Text;

namespace DwgTranslator.Core.Services;

/// <summary>What the environment check found for one CAD installation.</summary>
public sealed record CadPluginStatus(
    string CadInstallPath,
    string CadProductName,
    string Executable,
    string SupportPath,
    string PluginDirectory,
    bool PluginFilesPresent,
    bool PluginUpToDate,
    bool AutoLoadConfigured,
    string InstalledPluginVersion,
    string Detail)
{
    /// <summary>True when the CAD host can already run the writeback plugin without a repair.</summary>
    public bool Ready => PluginFilesPresent && PluginUpToDate && AutoLoadConfigured;
}

/// <summary>Outcome of an install, repair or uninstall request.</summary>
public sealed record CadPluginActionResult(bool Success, string Summary, IReadOnlyList<string> Steps, string? Error)
{
    public static CadPluginActionResult Fail(string error) => new(false, error, [], error);
}

/// <summary>
/// Installs the out-of-process writeback plugin into a CAD installation so the user can run it from
/// inside the CAD, not only from the desktop application.
///
/// The desktop app always loads the plugin for its own batch runs (NETLOAD with a generated script),
/// so a drawing can be translated without this step. Installing it makes the plugin a first-class
/// command in the CAD itself, which is what a delivered build is expected to offer, and it is the
/// only part of the "environment" that has to be set up on a fresh machine: the application itself
/// is published self-contained, so no .NET runtime has to be present.
///
/// Everything here is written to be reversible and idempotent: the plugin lives in its own
/// sub-folder, the auto-load entry sits between explicit markers inside acaddoc.lsp, and an existing
/// acaddoc.lsp is backed up before the first modification and never deleted on uninstall unless this
/// installer created it and only our block was in it.
/// </summary>
public static class CadPluginInstaller
{
    public const string PluginFileName = "DwgTranslator.Cad.dll";
    public const string CoreFileName = "DwgTranslator.Core.dll";
    public const string PluginFolderName = "DwgTranslator";
    public const string AutoLoadFileName = "acaddoc.lsp";
    public const string BeginMarker = ";;; ==== DWG Translator plugin : begin (managed block, do not edit) ====";
    public const string EndMarker = ";;; ==== DWG Translator plugin : end ====";

    private static readonly string[] ManagedFileNames = [PluginFileName, CoreFileName];

    /// <summary>Support folder the CAD host searches, preferring the spelling it actually ships.</summary>
    public static string ResolveSupportPath(string cadInstallPath)
    {
        var upper = Path.Combine(cadInstallPath, "Support");
        if (Directory.Exists(upper)) return upper;
        var lower = Path.Combine(cadInstallPath, "support");
        if (Directory.Exists(lower)) return lower;
        return upper;
    }

    public static string ResolvePluginDirectory(string cadInstallPath) =>
        Path.Combine(ResolveSupportPath(cadInstallPath), PluginFolderName);

    public static string ResolveAutoLoadPath(string cadInstallPath) =>
        Path.Combine(ResolveSupportPath(cadInstallPath), AutoLoadFileName);

    /// <summary>Human readable CAD product name for the given install folder.</summary>
    public static string DescribeProduct(string cadInstallPath)
    {
        if (File.Exists(Path.Combine(cadInstallPath, "gcad.exe"))) return "GstarCAD / 浩辰CAD";
        if (File.Exists(Path.Combine(cadInstallPath, "acad.exe"))) return "AutoCAD";
        return "CAD";
    }

    private static string ResolveExecutable(string cadInstallPath)
    {
        var gcad = Path.Combine(cadInstallPath, "gcad.exe");
        if (File.Exists(gcad)) return gcad;
        var acad = Path.Combine(cadInstallPath, "acad.exe");
        return File.Exists(acad) ? acad : string.Empty;
    }

    /// <summary>
    /// Reports whether the plugin is installed for the given CAD and whether it matches the copy
    /// shipped with this build. Comparing content hashes is what makes "repair" meaningful: a user
    /// who updated the application but kept an older installed plugin must be told, not silently
    /// left running the old one.
    /// </summary>
    public static CadPluginStatus Inspect(string cadInstallPath, string pluginSourceDirectory)
    {
        var productName = DescribeProduct(cadInstallPath);
        var support = ResolveSupportPath(cadInstallPath);
        var pluginDir = ResolvePluginDirectory(cadInstallPath);
        var autoLoadPath = ResolveAutoLoadPath(cadInstallPath);

        var installedPlugin = Path.Combine(pluginDir, PluginFileName);
        var filesPresent = File.Exists(installedPlugin);
        var upToDate = false;
        var version = filesPresent ? DescribeFileVersion(installedPlugin) : "-";

        var sourcePlugin = Path.Combine(pluginSourceDirectory, PluginFileName);
        if (filesPresent && File.Exists(sourcePlugin))
        {
            try { upToDate = HashFile(installedPlugin) == HashFile(sourcePlugin); }
            catch { upToDate = false; }
        }

        var autoLoadConfigured = false;
        try
        {
            if (File.Exists(autoLoadPath))
            {
                var text = File.ReadAllText(autoLoadPath);
                autoLoadConfigured = text.Contains(BeginMarker, StringComparison.Ordinal);
            }
        }
        catch { autoLoadConfigured = false; }

        var detail = (filesPresent, upToDate, autoLoadConfigured) switch
        {
            (false, _, _) => "尚未安装到 CAD（桌面程序仍可正常翻译图纸）",
            (true, false, _) => "已安装，但版本与当前程序不一致，建议修复",
            (true, true, false) => "文件已就位，但缺少自动加载配置，建议修复",
            _ => "已安装且与当前程序一致"
        };

        return new CadPluginStatus(cadInstallPath, productName, ResolveExecutable(cadInstallPath),
            support, pluginDir, filesPresent, upToDate, autoLoadConfigured, version, detail);
    }

    /// <summary>
    /// Copies the plugin into the CAD support folder and wires the auto-load entry.
    /// Runs as install, repair and update: the desired end state is identical.
    /// </summary>
    public static CadPluginActionResult Install(string cadInstallPath, string pluginSourceDirectory)
    {
        var steps = new List<string>();
        try
        {
            if (string.IsNullOrWhiteSpace(cadInstallPath) || !AutoCadDetector.IsValidAutoCadPath(cadInstallPath))
                return CadPluginActionResult.Fail("未找到有效的 CAD 安装目录，请先在设置中选择 CAD 安装路径。");

            var sourcePlugin = Path.Combine(pluginSourceDirectory, PluginFileName);
            if (!File.Exists(sourcePlugin))
                return CadPluginActionResult.Fail($"安装包内缺少插件文件：{sourcePlugin}");

            var support = ResolveSupportPath(cadInstallPath);
            if (!Directory.Exists(support))
                return CadPluginActionResult.Fail($"CAD 支持目录不存在：{support}");

            var pluginDir = ResolvePluginDirectory(cadInstallPath);
            Directory.CreateDirectory(pluginDir);
            steps.Add($"创建插件目录 {pluginDir}");

            foreach (var name in ManagedFileNames)
            {
                var source = Path.Combine(pluginSourceDirectory, name);
                if (!File.Exists(source)) continue;
                File.Copy(source, Path.Combine(pluginDir, name), overwrite: true);
                steps.Add($"复制 {name}（{new FileInfo(source).Length / 1024} KB）");
            }

            var autoLoadPath = ResolveAutoLoadPath(cadInstallPath);
            var block = BuildAutoLoadBlock(pluginDir);
            var existing = File.Exists(autoLoadPath) ? File.ReadAllText(autoLoadPath) : string.Empty;

            if (existing.Contains(BeginMarker, StringComparison.Ordinal))
            {
                var replaced = ReplaceBlock(existing, block);
                File.WriteAllText(autoLoadPath, replaced, new UTF8Encoding(false));
                steps.Add($"更新自动加载配置 {autoLoadPath}");
            }
            else
            {
                if (existing.Length > 0)
                {
                    var backup = autoLoadPath + ".dwgtranslator-backup";
                    if (!File.Exists(backup))
                    {
                        File.Copy(autoLoadPath, backup, overwrite: true);
                        steps.Add($"已备份原有 {AutoLoadFileName} → {Path.GetFileName(backup)}");
                    }
                }
                var combined = existing.TrimEnd() + Environment.NewLine + Environment.NewLine + block;
                File.WriteAllText(autoLoadPath, combined, new UTF8Encoding(false));
                steps.Add($"写入自动加载配置 {autoLoadPath}");
            }

            var status = Inspect(cadInstallPath, pluginSourceDirectory);
            var summary = status.Ready
                ? $"已在 {status.CadProductName} 中安装插件，重新打开 CAD 后可使用 DwgTranslateWrite 命令。"
                : "插件文件已写入，但自检未通过，请查看下方步骤。";
            return new CadPluginActionResult(status.Ready, summary, steps, status.Ready ? null : status.Detail);
        }
        catch (Exception ex)
        {
            return new CadPluginActionResult(false, "安装插件失败：" + ex.Message, steps, ex.ToString());
        }
    }

    /// <summary>Removes the plugin files and the managed auto-load block.</summary>
    public static CadPluginActionResult Uninstall(string cadInstallPath)
    {
        var steps = new List<string>();
        try
        {
            if (string.IsNullOrWhiteSpace(cadInstallPath))
                return CadPluginActionResult.Fail("未指定 CAD 安装目录。");

            var autoLoadPath = ResolveAutoLoadPath(cadInstallPath);
            var createdByUs = false;
            if (File.Exists(autoLoadPath))
            {
                var text = File.ReadAllText(autoLoadPath);
                if (text.Contains(BeginMarker, StringComparison.Ordinal))
                {
                    var withoutBlock = RemoveBlock(text);
                    createdByUs = withoutBlock.Trim().Length == 0 &&
                                  text.Contains("managed block, do not edit", StringComparison.Ordinal) &&
                                  !File.Exists(autoLoadPath + ".dwgtranslator-backup");
                    if (createdByUs)
                    {
                        File.Delete(autoLoadPath);
                        steps.Add($"删除 {AutoLoadFileName}（由本程序创建）");
                    }
                    else
                    {
                        File.WriteAllText(autoLoadPath, withoutBlock, new UTF8Encoding(false));
                        steps.Add($"从 {AutoLoadFileName} 中移除自动加载配置");
                    }
                }
            }

            var pluginDir = ResolvePluginDirectory(cadInstallPath);
            if (Directory.Exists(pluginDir))
            {
                // Only remove the files this installer owns, never the folder wholesale.
                foreach (var name in ManagedFileNames)
                {
                    var file = Path.Combine(pluginDir, name);
                    if (File.Exists(file)) { File.Delete(file); steps.Add($"删除 {name}"); }
                }
                if (!Directory.EnumerateFileSystemEntries(pluginDir).Any())
                {
                    Directory.Delete(pluginDir);
                    steps.Add($"删除插件目录 {pluginDir}");
                }
            }

            return new CadPluginActionResult(true, "已从 CAD 中卸载插件（桌面程序不受影响）。", steps, null);
        }
        catch (Exception ex)
        {
            return new CadPluginActionResult(false, "卸载插件失败：" + ex.Message, steps, ex.ToString());
        }
    }

    private static string BuildAutoLoadBlock(string pluginDirectory)
    {
        var dll = Path.Combine(pluginDirectory, PluginFileName).Replace('\\', '/');
        var sb = new StringBuilder();
        sb.AppendLine(BeginMarker);
        sb.AppendLine(";; Installed by DWG Translator. Loads the writeback plugin into every drawing session");
        sb.AppendLine(";; so DwgTranslateWrite is available inside the CAD. Reinstalling replaces this block.");
        sb.AppendLine("(if (and (null dwgtranslator:loaded) (findfile \"" + dll + "\"))");
        sb.AppendLine("  (progn");
        sb.AppendLine("    (command \"_.NETLOAD\" \"" + dll + "\")");
        sb.AppendLine("    (setq dwgtranslator:loaded T)))");
        sb.AppendLine(EndMarker);
        return sb.ToString();
    }

    private static string ReplaceBlock(string text, string block)
    {
        var start = text.IndexOf(BeginMarker, StringComparison.Ordinal);
        var end = text.IndexOf(EndMarker, StringComparison.Ordinal);
        if (start < 0 || end < start) return text.TrimEnd() + Environment.NewLine + block;
        end += EndMarker.Length;
        return text[..start] + block.TrimEnd() + text[end..];
    }

    private static string RemoveBlock(string text)
    {
        var start = text.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (start < 0) return text;
        var end = text.IndexOf(EndMarker, StringComparison.Ordinal);
        if (end < start) return text;
        end += EndMarker.Length;
        return text.Remove(start, end - start).TrimEnd() + Environment.NewLine;
    }

    private static string DescribeFileVersion(string path)
    {
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            var version = string.IsNullOrWhiteSpace(info.FileVersion) ? null : info.FileVersion;
            return version ?? new FileInfo(path).LastWriteTime.ToString("yyyy-MM-dd HH:mm");
        }
        catch { return "-"; }
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
