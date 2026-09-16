// CAD 插件安装/修复/卸载回归测试（仅使用临时目录）。
// 运行：dotnet fsi tests\CadIntegration\Test-CadPluginInstaller.fsx
#r @"..\..\src\DwgTranslator.Core\bin\Release\net8.0\DwgTranslator.Core.dll"
open System
open System.IO
open DwgTranslator.Core.Services

let sandbox = Path.Combine(Path.GetTempPath(), "dwgtranslator-installer-tests", Guid.NewGuid().ToString("N"))
let root = Path.Combine(sandbox, "fakecad")
let pluginSource = Path.Combine(sandbox, "package")
let support = Path.Combine(root, "Support")
let autoLoad = Path.Combine(support, "gcad.lsp")
let userLine = "(setq my-user-setting 42)  ;; 用户自己的配置"
let failures = ResizeArray<string>()
let mutable checks = 0
let check label condition =
    checks <- checks + 1
    printfn "%s=%b" label condition
    if not condition then failures.Add(label)

let preparePackage platform includeCore =
    Directory.CreateDirectory(pluginSource) |> ignore
    File.WriteAllText(Path.Combine(pluginSource, CadPluginInstaller.PluginFileName), "plugin-v1")
    if includeCore then File.WriteAllText(Path.Combine(pluginSource, CadPluginInstaller.CoreFileName), "core-v1")
    elif File.Exists(Path.Combine(pluginSource, CadPluginInstaller.CoreFileName)) then
        File.Delete(Path.Combine(pluginSource, CadPluginInstaller.CoreFileName))
    File.WriteAllText(Path.Combine(pluginSource, CadPluginInstaller.PlatformFileName), platform)

let deleteFixture path =
    let full = Path.GetFullPath(path)
    let allowed = Path.GetFullPath(sandbox)
    let parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "dwgtranslator-installer-tests")) + string Path.DirectorySeparatorChar
    if not (allowed.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) ||
       not (full = allowed || full.StartsWith(allowed + string Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) then
        failwith "Refusing to delete outside this test sandbox"
    if Directory.Exists(full) then Directory.Delete(full, true)

let reset () =
    deleteFixture root
    Directory.CreateDirectory support |> ignore
    File.WriteAllText(Path.Combine(root, "gcad.exe"), "")
    File.WriteAllText(autoLoad, userLine + Environment.NewLine)

try
    preparePackage "GstarCAD" true
    reset ()
    check "GSTARCAD_USES_GCAD_STARTUP" (CadPluginInstaller.ResolveAutoLoadPath(root) = autoLoad)
    let autocad = Path.Combine(sandbox, "autocad")
    Directory.CreateDirectory(autocad) |> ignore
    File.WriteAllText(Path.Combine(autocad, "acad.exe"), "fixture-not-executed")
    check "AUTOCAD_KEEPS_ACADDOC" (Path.GetFileName(CadPluginInstaller.ResolveAutoLoadPath(autocad)) = "acaddoc.lsp")
    let legacyPath = Path.Combine(support, "acaddoc.lsp")
    File.WriteAllText(legacyPath, "legacy-user-content")
    let r1 = CadPluginInstaller.Install(root, pluginSource)
    check "LEGACY_ACADDOC_UNTOUCHED" (File.ReadAllText(legacyPath) = "legacy-user-content")
    let s1 = CadPluginInstaller.Inspect(root, pluginSource)
    let first = File.ReadAllText(autoLoad)
    check "INSTALL_SUCCESS" (r1.Success && s1.Ready)
    check "USER_LSP_PRESERVED" (first.Contains(userLine))
    check "BACKUP_CREATED" (File.Exists(autoLoad + ".dwgtranslator-backup"))

    let r2 = CadPluginInstaller.Install(root, pluginSource)
    let second = File.ReadAllText(autoLoad)
    check "IDEMPOTENT" (r2.Success && second.Split(CadPluginInstaller.BeginMarker).Length - 1 = 1)

    File.AppendAllText(Path.Combine(support, "DwgTranslator", CadPluginInstaller.PluginFileName), "tampered")
    check "TAMPER_DETECTED" (not (CadPluginInstaller.Inspect(root, pluginSource).Ready))
    check "REPAIR_SUCCESS" ((CadPluginInstaller.Install(root, pluginSource)).Success)

    preparePackage "AutoCAD" true
    check "WRONG_PLATFORM_REJECTED" (not (CadPluginInstaller.Install(root, pluginSource)).Success)
    preparePackage "GstarCAD" false
    check "MISSING_CORE_REJECTED" (not (CadPluginInstaller.Install(root, pluginSource)).Success)
    preparePackage "GstarCAD" true

    let userFile = Path.Combine(support, "DwgTranslator", "user-note.txt")
    File.WriteAllText(userFile, "user-owned-content")
    let originalBackup = File.ReadAllText(autoLoad + ".dwgtranslator-backup")
    check "FIRST_BACKUP_PRESERVED" (originalBackup = userLine + Environment.NewLine)
    let r4 = CadPluginInstaller.Uninstall(root)
    let afterUninstall = File.ReadAllText(autoLoad)
    check "UNINSTALL_SUCCESS" r4.Success
    check "UNINSTALL_PRESERVES_USER_LSP" (afterUninstall.Contains(userLine) && not (afterUninstall.Contains(CadPluginInstaller.BeginMarker)))

    check "UNINSTALL_PRESERVES_UNMANAGED_FILE" (File.Exists(userFile) && File.ReadAllText(userFile) = "user-owned-content")
    let bogus = Path.Combine(sandbox, "notacad")
    Directory.CreateDirectory bogus |> ignore
    check "INVALID_TARGET_REJECTED" (not (CadPluginInstaller.Install(bogus, pluginSource)).Success)

    if failures.Count > 0 then
        eprintfn "FAILED=%s" (String.Join(",", failures))
        exit 1
    printfn "INSTALLER_CHECKS=%d/%d" checks checks
finally
    deleteFixture sandbox
