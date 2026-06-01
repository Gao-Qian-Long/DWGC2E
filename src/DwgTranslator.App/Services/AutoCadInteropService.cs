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
/// Extracted from MainViewModel to improve separation of concerns.
/// </summary>
public class AutoCadInteropService : IAutoCadInteropService
{
    private static readonly string[] AcadProgIDs =
    {
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
    };

    public bool IsAutoCADAvailable(AppConfig config)
    {
        // 1. Check configured path
        var configuredPath = config.AutoCadInstallPath;
        if (!string.IsNullOrEmpty(configuredPath) && AutoCadDetector.IsValidAutoCadPath(configuredPath))
        {
            return IsAutoCADRunning();
        }

        // 2. Auto-detect from registry
        var detection = AutoCadDetector.DetectInstallation();
        if (detection.Found)
        {
            // Auto-save detected path for future use
            config.AutoCadInstallPath = detection.InstallPath;
            return IsAutoCADRunning();
        }

        // 3. Fallback: try COM ProgID directly
        return IsAutoCADRunning();
    }

    /// <summary>
    /// Execute writeback of translated entities via AutoCAD COM interop.
    /// Heavy operations (file I/O, COM interop) run on the calling thread;
    /// the caller is responsible for offloading to a background thread via Task.Run.
    /// </summary>
    public async Task<DwgWriteResult> WritebackViaAutoCadAsync(
        string sourceFilePath,
        string outputFilePath,
        List<TextEntity> entities,
        bool cnToEn,
        AppConfig config,
        IProgress<string>? progress = null)
    {
        var result = new DwgWriteResult();
        string? initialTempPath = null;

        try
        {
            progress?.Report(Strings.Get("ProgressAutoCadPreparing"));

            // Serialize a config JSON that contains source/output paths + entities
            initialTempPath = Path.Combine(Path.GetTempPath(), $"dwgtranslate_{Guid.NewGuid():N}.json");
            var configObj = new
            {
                SourceDwgPath = sourceFilePath,
                OutputDwgPath = outputFilePath,
                Entities = entities,
                CnToEn = cnToEn
            };
            var json = System.Text.Json.JsonSerializer.Serialize(configObj, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = false,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });
            File.WriteAllText(initialTempPath, json);

            progress?.Report(Strings.Get("ProgressAutoCadConnecting"));

            // Try multiple ProgIDs to connect to AutoCAD
            Type? acadType = null;
            string? triedProgID = null;
            Exception? lastException = null;
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
                catch (Exception ex)
                {
                    lastException = ex;
                }
            }

            if (acadType == null)
            {
                result.Errors.Add(Strings.Get("AutoCadConnectFailed"));
                return result;
            }

            dynamic acad = Activator.CreateInstance(acadType)!;
            acad.Visible = true;

            // Detect AutoCAD version from ProgID for compatibility check
            string acadVersion = triedProgID ?? "unknown";
            Log.Information("Connected to AutoCAD via ProgID: {ProgID}", acadVersion);

            var doc = acad.ActiveDocument;
            if (doc == null)
            {
                result.Errors.Add(Strings.Get("AutoCadNoActiveDoc"));
                return result;
            }

            // Locate the Cad plugin DLL
            string? cadDllPath = ResolveCadPluginPath(config);

            if (string.IsNullOrEmpty(cadDllPath) || !File.Exists(cadDllPath))
            {
                result.Errors.Add(Strings.Get("AutoCadPluginNotFound"));
                return result;
            }

            Log.Information("Using Cad plugin: {Path}", cadDllPath);

            progress?.Report(Strings.Get("ProgressAutoCadPreparingFiles"));

            // Step 1: Write config JSON to a fixed known path
            var configDir = Path.Combine(Path.GetTempPath(), "DwgTranslator");
            Directory.CreateDirectory(configDir);
            var fixedConfigPath = Path.Combine(configDir, "writeback_config.json");
            var doneSignalPath = Path.Combine(configDir, "writeback_done.txt");

            // Clean up previous signal files
            try { if (File.Exists(doneSignalPath)) File.Delete(doneSignalPath); } catch { }

            // Write config to fixed path (WritebackCommand reads from here)
            File.WriteAllText(fixedConfigPath, json);

            // Clean up the initial random-name temp file — the fixed path is now the canonical one
            try { if (initialTempPath != null && File.Exists(initialTempPath)) File.Delete(initialTempPath); } catch { }
            initialTempPath = null;

            Log.Information("Config written to fixed path: {Path}", fixedConfigPath);

            // Step 2: Add DLL directory to AutoCAD Trusted Paths
            try
            {
                AddTrustedPath(acad, cadDllPath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to add trusted path (security dialog may still appear)");
            }

            // Step 3: Create a LISP file that loads the DLL and runs the writeback command
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

            progress?.Report(Strings.Get("ProgressAutoCadSendingCommand"));

            // Step 4: Load and execute the LISP file via SendCommand
            var lispLspPath = lspPath.Replace("\\", "\\\\");
            doc.SendCommand($"(load \"{lispLspPath}\") ");

            progress?.Report(Strings.Get("ProgressAutoCadWaiting"));

            // Step 5: Wait for the command to complete (monitor the done signal file)
            Log.Information("Waiting for WritebackCommand to complete...");
            int maxWaitSeconds = 120;
            int waited = 0;
            while (waited < maxWaitSeconds)
            {
                await Task.Delay(2000).ConfigureAwait(false);
                waited += 2;

                if (File.Exists(doneSignalPath))
                {
                    Log.Information("WritebackCommand completed (done signal detected)");
                    try
                    {
                        var doneContent = File.ReadAllText(doneSignalPath);
                        if (!doneContent.StartsWith("success|", StringComparison.OrdinalIgnoreCase))
                        {
                            result.SuccessCount = 0;
                            result.Errors.Add(Strings.Get("AutoCadWritebackFailed", doneContent));
                        }
                        else
                        {
                            result.SuccessCount = entities.Count;
                        }
                    }
                    catch
                    {
                        result.SuccessCount = 0;
                        result.Errors.Add(Strings.Get("AutoCadSignalReadError"));
                    }
                    break;
                }

                // Also check if the output DWG file has been created recently
                if (File.Exists(outputFilePath))
                {
                    try
                    {
                        var fi = new FileInfo(outputFilePath);
                        if (fi.Length > 1000 && fi.LastWriteTime > DateTime.Now.AddSeconds(-10))
                        {
                            Log.Information("Output DWG detected, writeback likely complete");
                            result.SuccessCount = entities.Count;
                            break;
                        }
                    }
                    catch { }
                }
            }

            if (result.SuccessCount == 0 && waited >= maxWaitSeconds)
            {
                Log.Warning("WritebackCommand timed out after {Sec}s", maxWaitSeconds);
                result.Errors.Add(Strings.Get("AutoCadTimeout", fixedConfigPath));
            }

            if (result.SuccessCount > 0)
            {
                progress?.Report(Strings.Get("ProgressAutoCadCompleted"));
            }

            // Cleanup signal file
            try { if (File.Exists(doneSignalPath)) File.Delete(doneSignalPath); } catch { }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "AutoCAD COM writeback failed");
            result.Errors.Add(Strings.Get("AutoCadWritebackError", ex.Message));

            // Clean up temp file on error
            try { if (initialTempPath != null && File.Exists(initialTempPath)) File.Delete(initialTempPath); } catch { }
        }

        return result;
    }

    #region Private Helpers

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
        try
        {
            var dllDir = Path.GetDirectoryName(dllPath);
            if (string.IsNullOrEmpty(dllDir)) return;

            if (!dllDir.EndsWith("\\")) dllDir += "\\";

            dynamic prefs = acad.Preferences;
            dynamic files = prefs.Files;
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
    }

    private static string? ResolveCadPluginPath(AppConfig config)
    {
        // 1. Use configured path
        if (!string.IsNullOrEmpty(config.CadPluginPath) && File.Exists(config.CadPluginPath))
            return config.CadPluginPath;

        // 2. Auto-detect using AutoCadDetector
        var detected = AutoCadDetector.FindCadPlugin();
        if (detected != null)
            return detected;

        // 3. Fallback: look in exe directory and solution output
        string cadDllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DwgTranslator.Cad.dll");
        if (File.Exists(cadDllPath))
            return cadDllPath;

        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        string srcDir = Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", ".."));
        foreach (var configuration in new[] { "Release", "Debug" })
        {
            var path = Path.Combine(srcDir, "DwgTranslator.Cad", "bin", configuration, "net8.0", "DwgTranslator.Cad.dll");
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    #endregion
}
