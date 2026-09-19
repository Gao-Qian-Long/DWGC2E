using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using DwgTranslator.Core.Services;

namespace DwgTranslator.Core.Tests;

public sealed class UpdateApplyScriptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dwgc2e-apply-script-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void WritesIndependentUpdaterWithSecondZipCheckCompleteManifestCleanupAndRollback()
    {
        var path = UpdateApplyScript.Write(_root);
        Assert.True(File.Exists(path));
        var script = File.ReadAllText(path);
        Assert.Contains("Wait-Process -Id $AppPid", script);
        Assert.Contains("Get-Process acad,acadlt,gcad", script);
        Assert.Contains("[string]$PackageSha256", script);
        Assert.Contains("Copy-Item -LiteralPath $package -Destination $verifiedPackage", script);
        Assert.Contains("APP 退出后更新包 SHA-256 二次校验失败", script);
        Assert.Contains("Extract-VerifiedZip $verifiedPackage", script);
        Assert.Contains("$manifest.schemaVersion -ne 2", script);
        Assert.Contains("更新包实际文件与完整受管文件列表不一致", script);
        Assert.Contains("settings.json", script);
        Assert.Contains("update-managed-files.json", script);
        Assert.Contains("$manifest.obsoletePaths", script);
        Assert.Contains("$obsolete.Add($relative)", script);
        Assert.Contains("Backup-Destination $dest", script);
        Assert.Contains("function Get-Sha256", script);
        Assert.Contains("Get-Sha256 $dest", script);
        Assert.Contains("开始回滚", script);
        Assert.Contains("foreach($dest in $created)", script);
        Assert.Contains("foreach($dest in $backed.Keys)", script);
        Assert.Contains("Start-Process -FilePath $exe", script);
    }

    [Fact]
    public void GeneratedPowerShellHasValidSyntax()
    {
        var path = UpdateApplyScript.Write(_root);
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            ArgumentList =
            {
                "-NoProfile", "-NonInteractive", "-Command",
                "$errors=$null;$tokens=$null;[void][System.Management.Automation.Language.Parser]::ParseFile($env:UPDATE_SCRIPT_PATH,[ref]$tokens,[ref]$errors);if($errors.Count){$errors|% Message;exit 1}"
            }
        };
        startInfo.Environment["UPDATE_SCRIPT_PATH"] = path;
        using var process = Process.Start(startInfo)!;
        var error = process.StandardError.ReadToEnd();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, output + error);
    }

    [Fact]
    public void RewritingScriptIsDeterministicAndKeepsOnlyOneScriptFile()
    {
        var first = UpdateApplyScript.Write(_root);
        var firstText = File.ReadAllText(first);
        var second = UpdateApplyScript.Write(_root);
        Assert.Equal(first, second);
        Assert.Equal(firstText, File.ReadAllText(second));
        Assert.Single(Directory.GetFiles(_root, "apply-update.ps1"));
    }
    [Fact]
    public void AppliesVerifiedPackageRemovesOnlyOwnedObsoleteFilesAndKeepsRollback()
    {
        // The production updater intentionally refuses to modify an installation while
        // any supported CAD host is running. Do not weaken that gate or terminate a
        // developer's live CAD session merely to make this machine-local integration
        // test pass; CI and clean test machines still exercise the full apply path.
        if (new[] { "acad", "acadlt", "gcad" }.Any(name => Process.GetProcessesByName(name).Length > 0))
        {
            return;
        }
        var work = Path.Combine(_root, "integration-work");
        var install = Path.Combine(_root, "installed");
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "DwgTranslator.exe"), "old-exe");
        File.WriteAllText(Path.Combine(install, "old.dll"), "old-library");
        Directory.CreateDirectory(Path.Combine(install, "prompts"));
        File.WriteAllText(Path.Combine(install, "prompts", "deepl_context.txt"), "old-prompt");
        File.WriteAllText(Path.Combine(install, "settings.json"), "user-settings");
        File.WriteAllText(Path.Combine(install, "unmanaged.txt"), "keep-me");
        File.WriteAllText(Path.Combine(install, "update-managed-files.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            version = "old",
            files = new[] { "DwgTranslator.exe", "old.dll", "prompts/deepl_context.txt" }
        }));

        var payload = Path.Combine(work, "payload");
        Directory.CreateDirectory(payload);
        var executable = Path.Combine(payload, "DwgTranslator.exe");
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe"), executable);
        Directory.CreateDirectory(Path.Combine(payload, "lib"));
        File.WriteAllText(Path.Combine(payload, "lib", "new.dll"), "new-library");
        var records = new[] { executable, Path.Combine(payload, "lib", "new.dll") }.Select(file => new UpdatePackageFile
        {
            Path = Path.GetRelativePath(payload, file).Replace('\\', '/'),
            Size = new FileInfo(file).Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant()
        }).ToList();
        var exeRecord = records.Single(item => item.Path == "DwgTranslator.exe");
        File.WriteAllText(Path.Combine(payload, "update-manifest.json"), JsonSerializer.Serialize(new UpdatePackageManifest
        {
            Version = "2.2.0",
            ExecutablePath = exeRecord.Path,
            ExecutableSha256 = exeRecord.Sha256,
            Files = records,
            ObsoletePaths = new List<string> { "prompts/deepl_context.txt" }
        }));
        var package = Path.Combine(work, "package.zip");
        ZipFile.CreateFromDirectory(payload, package);
        var packageHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(package)));
        var script = UpdateApplyScript.Write(work);
        var log = Path.Combine(work, "apply.log");

        using var sleeper = Process.Start(new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Milliseconds 500" }
        })!;
        using var updater = Process.Start(new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            ArgumentList =
            {
                "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                "-AppPid", sleeper.Id.ToString(), "-Package", package, "-PackageSha256", packageHash,
                "-Install", install, "-Log", log
            }
        })!;
        var stderr = updater.StandardError.ReadToEnd();
        var stdout = updater.StandardOutput.ReadToEnd();
        Assert.True(updater.WaitForExit(30_000), "Updater timed out.");
        Assert.True(updater.ExitCode == 0, stdout + stderr + Environment.NewLine + File.ReadAllText(log));

        Assert.True(File.Exists(Path.Combine(install, "DwgTranslator.exe")));
        Assert.Equal("new-library", File.ReadAllText(Path.Combine(install, "lib", "new.dll")));
        Assert.False(File.Exists(Path.Combine(install, "old.dll")));
        Assert.False(File.Exists(Path.Combine(install, "prompts", "deepl_context.txt")));
        Assert.Equal("user-settings", File.ReadAllText(Path.Combine(install, "settings.json")));
        Assert.Equal("keep-me", File.ReadAllText(Path.Combine(install, "unmanaged.txt")));
        var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(install, "update-managed-files.json")));
        Assert.Equal(2, receipt.RootElement.GetProperty("files").GetArrayLength());

        var rollback = install + ".rollback";
        Assert.Equal("old-exe", File.ReadAllText(Path.Combine(rollback, "DwgTranslator.exe")));
        Assert.Equal("old-library", File.ReadAllText(Path.Combine(rollback, "old.dll")));
        Assert.Equal("old-prompt", File.ReadAllText(Path.Combine(rollback, "prompts", "deepl_context.txt")));
        Assert.True(File.Exists(Path.Combine(rollback, "update-managed-files.json")));
    }
}
