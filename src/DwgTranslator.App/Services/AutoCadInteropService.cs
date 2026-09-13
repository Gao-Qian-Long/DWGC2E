using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Resources;
using DwgTranslator.Core.Services;
using Serilog;

namespace DwgTranslator.App.Services;

/// <summary>
/// Handles AutoCAD COM interop for precise DWG writeback.
/// Orchestrates: COM connection -> config serialization -> LISP script -> signal polling.
/// </summary>
public class AutoCadInteropService : IAutoCadInteropService, IDisposable
{
    private static readonly string[] AcadProgIDs =
    [
        "GstarCAD.Application",
        "GstarCAD.Application.24",
        "Gcad.Application",
        "Gcad.Application.24",
        "AutoCAD.Application",
        "AutoCAD.Application.25",      // 2026
        "AutoCAD.Application.24.3",    // 2025
        "AutoCAD.Application.24.2",    // 2024
        "AutoCAD.Application.24.1",    // 2023
        "AutoCAD.Application.24",      // 2022
        "AutoCAD.Application.23",      // 2021
        "AutoCAD.Application.22",      // 2020
        "AutoCADLT.Application",
        "AutoCADLT.Application.25",
        "AutoCADLT.Application.24.3",
        "AutoCADLT.Application.24.2",
        "AutoCADLT.Application.24.1",
        "AutoCADLT.Application.24",
    ];

    private static readonly System.Text.Json.JsonSerializerOptions ConfigJsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
    };

    private readonly List<object?> _comObjects = [];
    private bool _disposed;

    public bool IsAutoCADAvailable(AppConfig config)
    {
        var configuredPath = config.AutoCadInstallPath;
        if (!string.IsNullOrEmpty(configuredPath) && AutoCadDetector.IsValidAutoCadPath(configuredPath))
            return true;

        var detection = AutoCadDetector.DetectInstallation();
        if (detection.Found)
        {
            config.AutoCadInstallPath = detection.InstallPath;
            return true;
        }

        if (IsAutoCADRunning()) return true;
        try
        {
            return AcadProgIDs.Any(id => Type.GetTypeFromProgID(id, throwOnError: false) != null);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Execute writeback of translated entities via AutoCAD COM interop.
    /// Heavy operations run on the calling thread; the caller is responsible
    /// for offloading to a background thread via Task.Run.
    /// </summary>
    public async Task<CadWriteResult> WritebackViaAutoCadAsync(
        string sourceFilePath,
        string outputFilePath,
        List<TextEntity> entities,
        bool targetIsCjk,
        AppConfig config,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new CadWriteResult();
        string? sessionWorkDir = null;
        var localComObjects = new List<object?>();
        bool dispatched = false, completionConfirmed = false;
        string? leasePath = null;
        var sessionId = Guid.NewGuid().ToString("N")[..12];

        try
        {
            // Step 1: Serialize config with session ID for race-condition safety
            sessionWorkDir = Path.Combine(Path.GetTempPath(), "DwgTranslator", sessionId);
            Directory.CreateDirectory(sessionWorkDir);
            var doneSignalPath = Path.Combine(sessionWorkDir, "writeback_done.txt");
            var candidateLease = Path.GetFullPath(outputFilePath) + ".dwgc2e.pending";
            Directory.CreateDirectory(Path.GetDirectoryName(candidateLease)!);
            if (File.Exists(candidateLease))
            {
                // A late completion can release an old lease; absence of a signal is not success.
                var previous = System.Text.Json.JsonSerializer.Deserialize<PendingCadSession>(File.ReadAllText(candidateLease));
                var sessionRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DwgTranslator")) + Path.DirectorySeparatorChar;
                if (previous == null || !Path.GetFullPath(previous.DonePath).StartsWith(sessionRoot, StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(previous.DonePath))
                    throw new IOException("该输出仍有未确认的 CAD 写回会话；请先在 CAD 中确认旧命令已结束。记录：" + candidateLease);
                var oldResult = new CadWriteResult();
                if (ProcessDoneSignal(previous.DonePath, previous.SessionId, entities.Count, oldResult) == DoneSignalOutcome.Stale)
                    throw new IOException("CAD 完成信号与未决会话不匹配。");
                File.Delete(candidateLease);
            }
            using (var lease = new FileStream(candidateLease, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                System.Text.Json.JsonSerializer.Serialize(lease, new PendingCadSession { SessionId = sessionId, DonePath = doneSignalPath });
            leasePath = candidateLease;
            if (config.BackupSourceBeforeWrite && !File.Exists(sourceFilePath + ".bak"))
                File.Copy(sourceFilePath, sourceFilePath + ".bak", false);
            var configJson = SerializeConfig(
                sourceFilePath, outputFilePath, entities, targetIsCjk, sessionId, doneSignalPath, config.DuplicatePolicy == "overwrite");

            progress?.Report(Strings.Get("ProgressAutoCadPreparing"));

            // Locate and package files before COM connection. GstarCAD installations
            // commonly expose NETLOAD but do not register an automation ProgID, so a
            // command-line batch fallback must use the same session payload.
            string? cadDllPath = ResolveCadPluginPath(config);
            if (string.IsNullOrEmpty(cadDllPath) || !File.Exists(cadDllPath))
            {
                result.Errors.Add(Strings.Get("AutoCadPluginNotFound"));
                return result;
            }
            var pluginDirectory = Path.GetDirectoryName(cadDllPath) ?? string.Empty;
            if (!CadPluginInstaller.IsPackageCompatible(config.AutoCadInstallPath, pluginDirectory))
            {
                result.Errors.Add($"CAD 插件平台不匹配：{CadPluginInstaller.DescribeProduct(config.AutoCadInstallPath)}");
                return result;
            }
            Log.Information("Using Cad plugin: {Path}", cadDllPath);

            progress?.Report(Strings.Get("ProgressAutoCadPreparingFiles"));
            var (configPath, lspPath) = PrepareAutoCadFiles(
                configJson, cadDllPath, sessionWorkDir);

            // Step 2: Connect to AutoCAD via COM (prefer running instance)
            progress?.Report(Strings.Get("ProgressAutoCadConnecting"));
            dynamic? acad = ConnectToAutoCad(localComObjects, result, reportFailure: false);
            if (acad is null)
            {
                Log.Information("CAD COM server is unavailable; using batch NETLOAD fallback");
                dispatched = true;
                await RunCadBatchWritebackAsync(
                    config.AutoCadInstallPath, cadDllPath, configPath, sessionWorkDir,
                    doneSignalPath, sessionId, entities.Count, result, outputFilePath,
                    progress, cancellationToken);
                return result;
            }

            var doc = acad.ActiveDocument;
            localComObjects.Add(doc);
            if (doc == null)
            {
                result.Errors.Add(Strings.Get("AutoCadNoActiveDoc"));
                return result;
            }

            // Step 5: Add trusted path (best-effort)
            try { AddTrustedPath(acad, cadDllPath); }
            catch (Exception ex) { Log.Warning(ex, "Failed to add trusted path"); }

            // Step 6: Execute LISP via SendCommand
            progress?.Report(Strings.Get("ProgressAutoCadSendingCommand"));
            var lispLspPath = lspPath.Replace("\\", "\\\\");
            var outputBaseline = TryGetLastWriteTimeUtc(outputFilePath);
            dispatched = true;
            doc.SendCommand($"(load \"{lispLspPath}\") ");

            // Step 7: Poll for completion signal
            progress?.Report(Strings.Get("ProgressAutoCadWaiting"));
            await WaitForCompletion(
                doneSignalPath, sessionId, entities.Count, configPath, result, outputBaseline, outputFilePath,
                cancellationToken);

            if (result.SuccessCount > 0)
                progress?.Report(Strings.Get("ProgressAutoCadCompleted"));

            completionConfirmed = IsConfirmedSignal(doneSignalPath, sessionId);
        }
        catch (OperationCanceledException)
        {
            Log.Information("CAD writeback cancelled by user");
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "AutoCAD COM writeback failed");
            result.Errors.Add(Strings.Get("AutoCadWritebackError", ex.Message));
        }
        finally
        {
            ReleaseComObjects(localComObjects);
            if (!completionConfirmed && sessionWorkDir != null)
                completionConfirmed = IsConfirmedSignal(Path.Combine(sessionWorkDir, "writeback_done.txt"), sessionId);
            if (!dispatched || completionConfirmed)
            {
                if (leasePath != null) { try { File.Delete(leasePath); } catch (Exception ex) { Log.Warning(ex,"Could not remove completed CAD lease"); } }
                if (!string.IsNullOrEmpty(sessionWorkDir)) await TryDeleteSessionDirectoryAsync(sessionWorkDir);
            }
            else Log.Warning("CAD session is still unconfirmed; retained {Session} and output lease {Lease}",sessionWorkDir,leasePath);
        }

        return result;
    }

    private sealed class PendingCadSession
    {
        public string SessionId { get; set; } = "";
        public string DonePath { get; set; } = "";
    }

    // ========== Step methods ==========

    private static string SerializeConfig(string source, string output, List<TextEntity> entities, bool targetIsCjk,
        string sessionId, string doneSignalPath, bool overwriteExisting)
    {
        var configObj = new
        {
            OverwriteExisting = overwriteExisting,
            SourceDwgPath = source,
            OutputDwgPath = output,
            Entities = entities,
            targetIsCjk = targetIsCjk,
            // Legacy field name: an installed plugin older than the language-pair support reads
            // Legacy plugins understand cnToEn (true means a Latin/English target).
            cnToEn = !targetIsCjk,
            SessionId = sessionId,
            DoneSignalPath = doneSignalPath
        };
        return System.Text.Json.JsonSerializer.Serialize(configObj, ConfigJsonOptions);
    }

    // .NET 8 does not ship Marshal.GetActiveObject; use OLE automation export.
    [DllImport("oleaut32.dll", PreserveSig = false)]
    private static extern void GetActiveObject(
        ref Guid rclsid,
        IntPtr pvReserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

    private static object? TryGetActiveObject(string progId)
    {
        try
        {
            var clsid = Type.GetTypeFromProgID(progId, throwOnError: false);
            if (clsid == null) return null;
            var guid = clsid.GUID;
            GetActiveObject(ref guid, IntPtr.Zero, out var obj);
            return obj;
        }
        catch (COMException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Prefer attaching to a running AutoCAD instance; only create a new one if none is running.
    /// Creating a new instance while AutoCAD is already open often targets a blank Drawing1.dwg.
    /// </summary>
    private static dynamic? ConnectToAutoCad(
        List<object?> comObjects, CadWriteResult result, bool reportFailure = true)
    {
        // 1) Try GetActiveObject against known ProgIDs (running instance)
        foreach (var progId in AcadProgIDs)
        {
            try
            {
                var running = TryGetActiveObject(progId);
                if (running != null)
                {
                    comObjects.Add(running);
                    try { ((dynamic)running).Visible = true; } catch { }
                    Log.Information("Attached to running AutoCAD via ProgID: {ProgID}", progId);
                    return running;
                }
            }
            catch
            {
                // Try next ProgID
            }
        }

        // 2) No running instance - create a new one
        Type? acadType = null;
        string? triedProgID = null;

        foreach (var progId in AcadProgIDs)
        {
            try
            {
                triedProgID = progId;
                acadType = Type.GetTypeFromProgID(progId, false);
                if (acadType != null)
                {
                    Log.Information("Found AutoCAD COM ProgID (will create): {ProgID}", progId);
                    break;
                }
            }
            catch { /* Try next ProgID */ }
        }

        if (acadType == null)
        {
            if (reportFailure)
                result.Errors.Add(Strings.Get("AutoCadConnectFailed"));
            return null;
        }

        dynamic acad = Activator.CreateInstance(acadType)!;
        comObjects.Add(acad);
        acad.Visible = true;

        Log.Information("Created new AutoCAD instance via ProgID: {ProgID}", triedProgID ?? "unknown");
        return acad;
    }

    private static (string configPath, string lspPath) PrepareAutoCadFiles(
        string configJson, string cadDllPath, string sessionWorkDir)
    {
        Directory.CreateDirectory(sessionWorkDir);

        var configPath = Path.Combine(sessionWorkDir, "writeback_config.json");
        File.WriteAllText(configPath, configJson);
        Log.Information("Config written to session path: {Path}", configPath);

        var lspPath = Path.Combine(sessionWorkDir, "dwgtranslate_exec.lsp");
        var lispDllPath = cadDllPath.Replace("\\", "\\\\");
        var lispConfigPath = configPath.Replace("\\", "\\\\");
        var lspContent = $@"(princ ""
DwgTranslator: loading plugin..."")
(command ""_.NETLOAD"" ""{lispDllPath}"" )
(princ ""
DwgTranslator: executing writeback..."")
(command ""_.DwgTranslateWrite"" ""{lispConfigPath}"" )
(princ ""
DwgTranslator: done."")
(princ)
";
        File.WriteAllText(lspPath, lspContent);
        Log.Information("Created session LISP file: {Path}", lspPath);

        return (configPath, lspPath);
    }

    private static async Task RunCadBatchWritebackAsync(
        string installPath,
        string cadDllPath,
        string configPath,
        string sessionWorkDir,
        string doneSignalPath,
        string sessionId,
        int entityCount,
        CadWriteResult result,
        string outputFilePath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var exePath = ResolveCadExecutable(installPath);
        if (exePath == null)
        {
            result.Errors.Add($"CAD executable not found under: {installPath}");
            return;
        }

        var scriptPath = Path.Combine(sessionWorkDir, "dwgtranslate_batch.scr");
        File.WriteAllLines(scriptPath,
        [
            "_.NETLOAD",
            cadDllPath.Replace('\\', '/'),
            "_.DwgTranslateWrite",
            configPath.Replace('\\', '/'),
            "_.QUIT",
            "_N"
        ], System.Text.Encoding.Default);

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("/nologo");
        startInfo.ArgumentList.Add("/b");
        startInfo.ArgumentList.Add(scriptPath);

        progress?.Report("正在通过CAD批处理执行在线回写…");
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                result.Errors.Add("CAD batch process failed to start.");
                return;
            }

            var outputBaseline = TryGetLastWriteTimeUtc(outputFilePath);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var completionTask = WaitForCompletion(
                doneSignalPath, sessionId, entityCount, configPath, result,
                outputBaseline, outputFilePath, linkedCts.Token);
            var exitTask = process.WaitForExitAsync(cancellationToken);

            var first = await Task.WhenAny(completionTask, exitTask);
            if (first == exitTask && !File.Exists(doneSignalPath))
            {
                await Task.Delay(500, cancellationToken);
                if (!File.Exists(doneSignalPath))
                {
                    linkedCts.Cancel();
                    try { await completionTask; } catch (OperationCanceledException) { }
                    result.Errors.Add($"CAD exited before writeback completed (exit code {process.ExitCode}).");
                    return;
                }
            }

            await completionTask;

            if (!process.HasExited)
            {
                using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await process.WaitForExitAsync(exitCts.Token); }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                }
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
        }
    }

    private static string? ResolveCadExecutable(string installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath)) return null;
        foreach (var name in new[] { "gcad.exe", "acad.exe" })
        {
            var path = Path.Combine(installPath, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static async Task WaitForCompletion(
        string doneSignalPath, string sessionId, int entityCount, string configPath,
        CadWriteResult result, DateTime? outputBaseline, string outputFilePath,
        CancellationToken cancellationToken)
    {
        Log.Information("Waiting for WritebackCommand to complete...");
        // Large DWGs need extra time for host startup, drawing load, save and signal
        // flush. Keep the small-job timeout at two minutes and scale up to ten.
        int maxWaitSeconds = Math.Clamp(120 + entityCount / 15, 120, 600);
        int waited = 0;
        bool outputChanged = false;

        while (waited < maxWaitSeconds)
        {
            await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
            waited += 2;

            if (File.Exists(doneSignalPath))
            {
                var outcome = ProcessDoneSignal(doneSignalPath, sessionId, entityCount, result);
                if (outcome == DoneSignalOutcome.Stale)
                {
                    try { File.Delete(doneSignalPath); } catch { }
                    Log.Warning("Deleted stale session signal; continuing to wait for {SessionId}", sessionId);
                    continue;
                }
                return;
            }

            // An output timestamp is diagnostic evidence only. Never convert it into
            // a success count without a matching completion signal from the plugin.
            if (!outputChanged && HasOutputChanged(outputFilePath, outputBaseline))
            {
                outputChanged = true;
                Log.Information("Output file changed while waiting; still awaiting session signal {SessionId}", sessionId);
            }
        }

        if (result.SuccessCount == 0 && result.Errors.Count == 0)
        {
            Log.Warning("WritebackCommand timed out after {Sec}s (outputChanged={OutputChanged})",
                maxWaitSeconds, outputChanged);
            result.Errors.Add(Strings.Get("AutoCadTimeout", configPath));
        }
    }

    private static async Task TryDeleteSessionDirectoryAsync(string sessionWorkDir)
    {
        var sessionRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DwgTranslator"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullSessionPath = Path.GetFullPath(sessionWorkDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!fullSessionPath.StartsWith(sessionRoot, StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning("Skipped cleanup outside the CAD session root: {Dir}", sessionWorkDir);
            return;
        }

        for (int attempt = 1; attempt <= 6; attempt++)
        {
            try
            {
                if (Directory.Exists(sessionWorkDir))
                    Directory.Delete(sessionWorkDir, recursive: true);
                return;
            }
            catch (Exception cleanupEx) when (attempt < 6 &&
                (cleanupEx is IOException || cleanupEx is UnauthorizedAccessException))
            {
                await Task.Delay(500 * attempt).ConfigureAwait(false);
            }
            catch (Exception cleanupEx)
            {
                Log.Debug(cleanupEx, "Failed to clean CAD session directory {Dir}", sessionWorkDir);
                return;
            }
        }
    }

    private static bool IsConfirmedSignal(string path, string sessionId)
    {
        try
        {
            var parts = File.ReadAllText(path).Trim().Split('|');
            return parts.Length >= 3 && parts[1] == sessionId &&
                (parts[0] == "failed" || ((parts[0] == "success" || parts[0] == "partial") && parts.Length >= 5)) &&
                DateTime.TryParse(parts.Length >= 5 ? parts[4] : parts[parts.Length - 1], out _);
        }
        catch { return false; }
    }

    private enum DoneSignalOutcome
    {
        Processed,
        Stale
    }

    /// <summary>
    /// Parses writeback_done.txt. Expected formats:
    ///   success|sessionId|successCount|failCount|timestamp|detail
    ///   partial|sessionId|successCount|failCount|timestamp|detail
    ///   success|sessionId|timestamp              (legacy)
    ///   failed|sessionId|message|timestamp
    /// </summary>
    private static DoneSignalOutcome ProcessDoneSignal(string doneSignalPath, string sessionId, int entityCount, CadWriteResult result)
    {
        Log.Information("WritebackCommand completed (done signal detected)");
        try
        {
            if (!IsConfirmedSignal(doneSignalPath, sessionId)) return DoneSignalOutcome.Stale;
            var doneContent = File.ReadAllText(doneSignalPath).Trim();
            var parts = doneContent.Split('|');
            var status = parts.Length > 0 ? parts[0] : string.Empty;
            bool isSuccess = status.Equals("success", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("partial", StringComparison.OrdinalIgnoreCase);

            // Validate SessionId to prevent cross-session confusion
            if (!string.IsNullOrEmpty(sessionId))
            {
                var doneSessionId = parts.Length >= 2 ? parts[1] : "";
                if (!string.Equals(doneSessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warning("Done signal SessionId mismatch: expected={Expected}, got={Actual}",
                        sessionId, doneSessionId);
                    return DoneSignalOutcome.Stale;
                }
            }

            if (!isSuccess)
            {
                result.SuccessCount = 0;
                result.FailCount = entityCount;
                result.Errors.Add(Strings.Get("AutoCadWritebackFailed", doneContent));
            }
            else
            {
                // Prefer real counts when CAD plugin reports them:
                // success|sessionId|successCount|failCount|timestamp
                if (parts.Length >= 4 &&
                    int.TryParse(parts[2], out var successCount) &&
                    int.TryParse(parts[3], out var failCount) &&
                    successCount >= 0 && failCount >= 0 &&
                    (long)successCount + failCount == entityCount)
                {
                    result.SuccessCount = successCount;
                    result.FailCount = failCount;
                    if (failCount > 0)
                    {
                        var detail = parts.Length >= 6 && !string.IsNullOrWhiteSpace(parts[5])
                            ? parts[5]
                            : $"{failCount} entities kept their original text";
                        result.Errors.Add(detail);
                    }
                }
                else
                {
                    result.SuccessCount = 0;
                    result.FailCount = entityCount;
                    result.Errors.Add(Strings.Get("AutoCadSignalReadError"));
                }
            }
        }
        catch
        {
            result.SuccessCount = 0;
            result.Errors.Add(Strings.Get("AutoCadSignalReadError"));
        }
        return DoneSignalOutcome.Processed;
    }

    private static DateTime? TryGetLastWriteTimeUtc(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null; }
        catch { return null; }
    }

    private static bool HasOutputChanged(string outputFilePath, DateTime? baselineUtc)
    {
        try
        {
            if (!File.Exists(outputFilePath)) return false;
            var fi = new FileInfo(outputFilePath);
            if (fi.Length <= 1000) return false;
            return !baselineUtc.HasValue || fi.LastWriteTimeUtc > baselineUtc.Value;
        }
        catch { return false; }
    }

    // ========== Helpers ==========

    private static bool IsAutoCADRunning()
    {
        try
        {
            if (Process.GetProcessesByName("acad").Length > 0) return true;
            if (Process.GetProcessesByName("acadlt").Length > 0) return true;
            if (Process.GetProcessesByName("gcad").Length > 0) return true;
        }
        catch { }
        return false;
    }

    private static void AddTrustedPath(dynamic acad, string dllPath)
    {
        var comRefs = new List<object?>();
        try
        {
            var dllDir = Path.GetDirectoryName(dllPath);
            if (string.IsNullOrEmpty(dllDir)) return;

            if (!dllDir.EndsWith('\\')) dllDir += "\\";

            dynamic prefs = acad.Preferences;
            comRefs.Add(prefs);
            dynamic files = prefs.Files;
            comRefs.Add(files);
            string? currentTrusted = files.TrustedPath;

            if (currentTrusted != null && currentTrusted.Contains(dllDir, StringComparison.OrdinalIgnoreCase))
            {
                Log.Information("Trusted path already contains: {Dir}", dllDir);
                return;
            }

            string newTrusted = string.IsNullOrEmpty(currentTrusted)
                ? dllDir
                : currentTrusted + ";" + dllDir;

            files.TrustedPath = newTrusted;
            Log.Information("Added trusted path: {Dir}", dllDir);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to add trusted path");
        }
        finally
        {
            ReleaseComObjects(comRefs);
        }
    }

    private static string? ResolveCadPluginPath(AppConfig config)
    {
        string cadDllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CadPlugin", "DwgTranslator.Cad.dll");
        if (File.Exists(cadDllPath))
        {
            // App and bundled plugin are released together. A persisted path from
            // an older installation must not silently load the old writer.
            config.CadPluginPath = cadDllPath;
            return cadDllPath;
        }

        cadDllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DwgTranslator.Cad.dll");
        if (File.Exists(cadDllPath)) return cadDllPath;

        if (!string.IsNullOrEmpty(config.CadPluginPath) && File.Exists(config.CadPluginPath))
            return config.CadPluginPath;

        var detected = AutoCadDetector.FindCadPlugin();
        if (detected != null) return detected;

        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        string srcDir = Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", ".."));
        foreach (var configuration in new[] { "Release", "Debug" })
        {
            foreach (var framework in new[] { "net48", "net8.0" })
            {
                var path = Path.Combine(srcDir, "DwgTranslator.Cad", "bin", configuration, framework, "DwgTranslator.Cad.dll");
                if (File.Exists(path)) return path;
            }
        }

        return null;
    }

    private static void ReleaseComObjects(List<object?> objs)
    {
        for (int i = objs.Count - 1; i >= 0; i--)
        {
            var obj = objs[i];
            if (obj != null && Marshal.IsComObject(obj))
            {
                try { Marshal.ReleaseComObject(obj); } catch { }
            }
        }
    }

    #region IDisposable

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            ReleaseComObjects(_comObjects);
            _comObjects.Clear();
        }

        _disposed = true;
    }

    #endregion
}
