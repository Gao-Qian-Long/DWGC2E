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
/// Orchestrates: COM connection → config serialization → LISP script → signal polling.
/// </summary>
public class AutoCadInteropService : IAutoCadInteropService, IDisposable
{
    private static readonly string[] AcadProgIDs =
    [
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
            return IsAutoCADRunning();

        var detection = AutoCadDetector.DetectInstallation();
        if (detection.Found)
        {
            config.AutoCadInstallPath = detection.InstallPath;
            return IsAutoCADRunning();
        }

        return IsAutoCADRunning();
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
        bool cnToEn,
        AppConfig config,
        IProgress<string>? progress = null)
    {
        var result = new CadWriteResult();
        string? tempConfigPath = null;
        var localComObjects = new List<object?>();

        try
        {
            // Step 1: Serialize config with session ID for race-condition safety
            var sessionId = Guid.NewGuid().ToString("N")[..12];
            var configJson = SerializeConfig(sourceFilePath, outputFilePath, entities, cnToEn, sessionId);
            tempConfigPath = Path.Combine(Path.GetTempPath(), $"dwgtranslate_{Guid.NewGuid():N}.json");
            File.WriteAllText(tempConfigPath, configJson);

            progress?.Report(Strings.Get("ProgressAutoCadPreparing"));

            // Step 2: Connect to AutoCAD via COM
            progress?.Report(Strings.Get("ProgressAutoCadConnecting"));
            dynamic? acad = ConnectToAutoCad(localComObjects, result);
            if (acad is null) return result;

            var doc = acad.ActiveDocument;
            localComObjects.Add(doc);
            if (doc == null)
            {
                result.Errors.Add(Strings.Get("AutoCadNoActiveDoc"));
                return result;
            }

            // Step 3: Locate the Cad plugin DLL
            string? cadDllPath = ResolveCadPluginPath(config);
            if (string.IsNullOrEmpty(cadDllPath) || !File.Exists(cadDllPath))
            {
                result.Errors.Add(Strings.Get("AutoCadPluginNotFound"));
                return result;
            }
            Log.Information("Using Cad plugin: {Path}", cadDllPath);

            // Step 4: Write config + LISP script to AutoCAD temp directory
            progress?.Report(Strings.Get("ProgressAutoCadPreparingFiles"));
            var (fixedConfigPath, doneSignalPath, lspPath) = PrepareAutoCadFiles(configJson, cadDllPath);

            // Step 5: Add trusted path (best-effort)
            try { AddTrustedPath(acad, cadDllPath); }
            catch (Exception ex) { Log.Warning(ex, "Failed to add trusted path"); }

            // Step 6: Execute LISP via SendCommand
            progress?.Report(Strings.Get("ProgressAutoCadSendingCommand"));
            var lispLspPath = lspPath.Replace("\\", "\\\\");
            doc.SendCommand($"(load \"{lispLspPath}\") ");

            // Step 7: Poll for completion signal
            progress?.Report(Strings.Get("ProgressAutoCadWaiting"));
            await WaitForCompletion(doneSignalPath, outputFilePath, sessionId, entities.Count, fixedConfigPath, result);

            if (result.SuccessCount > 0)
                progress?.Report(Strings.Get("ProgressAutoCadCompleted"));

            // Cleanup signal file
            try { if (File.Exists(doneSignalPath)) File.Delete(doneSignalPath); } catch { }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "AutoCAD COM writeback failed");
            result.Errors.Add(Strings.Get("AutoCadWritebackError", ex.Message));
        }
        finally
        {
            ReleaseComObjects(localComObjects);
            try { if (tempConfigPath != null && File.Exists(tempConfigPath)) File.Delete(tempConfigPath); } catch { }
        }

        return result;
    }

    // ───────────────────── Step methods ─────────────────────

    private static string SerializeConfig(string source, string output, List<TextEntity> entities, bool cnToEn, string sessionId)
    {
        var configObj = new
        {
            SourceDwgPath = source,
            OutputDwgPath = output,
            Entities = entities,
            CnToEn = cnToEn,
            SessionId = sessionId
        };
        return System.Text.Json.JsonSerializer.Serialize(configObj, ConfigJsonOptions);
    }

    private static dynamic? ConnectToAutoCad(List<object?> comObjects, CadWriteResult result)
    {
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
                    Log.Information("Found AutoCAD COM ProgID: {ProgID}", progId);
                    break;
                }
            }
            catch { /* Try next ProgID */ }
        }

        if (acadType == null)
        {
            result.Errors.Add(Strings.Get("AutoCadConnectFailed"));
            return null;
        }

        dynamic acad = Activator.CreateInstance(acadType)!;
        comObjects.Add(acad);
        acad.Visible = true;

        Log.Information("Connected to AutoCAD via ProgID: {ProgID}", triedProgID ?? "unknown");
        return acad;
    }

    private static (string configPath, string signalPath, string lspPath) PrepareAutoCadFiles(string configJson, string cadDllPath)
    {
        var configDir = Path.Combine(Path.GetTempPath(), "DwgTranslator");
        Directory.CreateDirectory(configDir);

        var fixedConfigPath = Path.Combine(configDir, "writeback_config.json");
        var doneSignalPath = Path.Combine(configDir, "writeback_done.txt");

        // Clean up previous signal files
        try { if (File.Exists(doneSignalPath)) File.Delete(doneSignalPath); } catch { }

        File.WriteAllText(fixedConfigPath, configJson);
        Log.Information("Config written to fixed path: {Path}", fixedConfigPath);

        // Create LISP file
        var lspPath = Path.Combine(configDir, "dwgtranslate_exec.lsp");
        var lispDllPath = cadDllPath.Replace("\\", "\\\\");
        var lspContent = $@"(princ ""\nDwgTranslator: loading plugin..."")
(command ""_.NETLOAD"" ""{lispDllPath}"" )
(princ ""\nDwgTranslator: executing writeback..."")
(command ""_.DwgTranslateWrite"" )
(princ ""\nDwgTranslator: done."")
(princ)
";
        File.WriteAllText(lspPath, lspContent);
        Log.Information("Created LISP file: {Path}", lspPath);

        return (fixedConfigPath, doneSignalPath, lspPath);
    }

    private static async Task WaitForCompletion(string doneSignalPath, string outputFilePath,
        string sessionId, int entityCount, string configPath, CadWriteResult result)
    {
        Log.Information("Waiting for WritebackCommand to complete...");
        int maxWaitSeconds = 120;
        int waited = 0;

        while (waited < maxWaitSeconds)
        {
            await Task.Delay(2000).ConfigureAwait(false);
            waited += 2;

            if (File.Exists(doneSignalPath))
            {
                if (ProcessDoneSignal(doneSignalPath, sessionId, entityCount, result))
                {
                    waited -= 2; // Stale signal — continue waiting
                    continue;
                }
                return; // Valid signal processed
            }

            // Also check if the output DWG file has been created recently
            if (TryDetectOutputFile(outputFilePath, entityCount, result))
                return;
        }

        if (result.SuccessCount == 0)
        {
            Log.Warning("WritebackCommand timed out after {Sec}s", maxWaitSeconds);
            result.Errors.Add(Strings.Get("AutoCadTimeout", configPath));
        }
    }

    /// <returns>true if the signal was stale and caller should continue waiting</returns>
    private static bool ProcessDoneSignal(string doneSignalPath, string sessionId, int entityCount, CadWriteResult result)
    {
        Log.Information("WritebackCommand completed (done signal detected)");
        try
        {
            var doneContent = File.ReadAllText(doneSignalPath);
            var parts = doneContent.Split('|');
            bool isSuccess = parts.Length > 0 &&
                parts[0].StartsWith("success", StringComparison.OrdinalIgnoreCase);

            // Validate SessionId to prevent cross-session confusion
            if (parts.Length >= 2 && !string.IsNullOrEmpty(sessionId))
            {
                var doneSessionId = parts[1];
                if (!string.Equals(doneSessionId, sessionId, StringComparison.OrdinalIgnoreCase)
                    && doneSessionId.Length == 12)
                {
                    Log.Warning("Done signal SessionId mismatch: expected={Expected}, got={Actual}",
                        sessionId, doneSessionId);
                    return true; // Stale — continue waiting
                }
            }

            if (!isSuccess)
            {
                result.SuccessCount = 0;
                result.Errors.Add(Strings.Get("AutoCadWritebackFailed", doneContent));
            }
            else
            {
                result.SuccessCount = entityCount;
            }
        }
        catch
        {
            result.SuccessCount = 0;
            result.Errors.Add(Strings.Get("AutoCadSignalReadError"));
        }
        return false;
    }

    private static bool TryDetectOutputFile(string outputFilePath, int entityCount, CadWriteResult result)
    {
        if (!File.Exists(outputFilePath)) return false;
        try
        {
            var fi = new FileInfo(outputFilePath);
            if (fi.Length > 1000 && fi.LastWriteTime > DateTime.Now.AddSeconds(-10))
            {
                Log.Information("Output DWG detected, writeback likely complete");
                result.SuccessCount = entityCount;
                return true;
            }
        }
        catch { }
        return false;
    }

    // ───────────────────── Helpers ─────────────────────

    private static bool IsAutoCADRunning()
    {
        try
        {
            if (Process.GetProcessesByName("acad").Length > 0) return true;
            if (Process.GetProcessesByName("acadlt").Length > 0) return true;
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
        if (!string.IsNullOrEmpty(config.CadPluginPath) && File.Exists(config.CadPluginPath))
            return config.CadPluginPath;

        var detected = AutoCadDetector.FindCadPlugin();
        if (detected != null) return detected;

        string cadDllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DwgTranslator.Cad.dll");
        if (File.Exists(cadDllPath)) return cadDllPath;

        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        string srcDir = Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", ".."));
        foreach (var configuration in new[] { "Release", "Debug" })
        {
            var path = Path.Combine(srcDir, "DwgTranslator.Cad", "bin", configuration, "net8.0", "DwgTranslator.Cad.dll");
            if (File.Exists(path)) return path;
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
