// CAD 插件安装/修复/卸载的回归测试（沙箱运行，不会触碰真实的 CAD 安装）。
//
// 运行：
//   dotnet fsi tools\tests\Test-CadPluginInstaller.fsx
//
// 前置：先构建 Core（dotnet build src\DwgTranslator.Core -c Debug --no-restore），
// 以及 release\CadPlugin 下存在插件文件。
// 逐字字符串：F# 会把普通字符串里的 \b、\n 当转义序列，路径用 @"..." 才不会被打断。
#r @"..\..\src\DwgTranslator.App\bin\Debug\net8.0-windows\DwgTranslator.Core.dll"
open System
open System.IO
open DwgTranslator.Core.Services

let root = Path.Combine(Path.GetTempPath(), "cadtest", "fakecad")
let pluginSource = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "release", "CadPlugin"))
let support = Path.Combine(root, "Support")
let autoLoad = Path.Combine(support, "acaddoc.lsp")
let userLine = "(setq my-user-setting 42)  ;; 用户自己的配置"

let reset () =
    if Directory.Exists root then Directory.Delete(root, true)
    Directory.CreateDirectory support |> ignore
    File.WriteAllText(Path.Combine(root, "gcad.exe"), "")
    File.WriteAllText(autoLoad, userLine + Environment.NewLine)

let show (label: string) =
    let s = CadPluginInstaller.Inspect(root, pluginSource)
    printfn "  [%s] 产品=%s 文件在场=%b 版本一致=%b 自动加载=%b Ready=%b"
        label s.CadProductName s.PluginFilesPresent s.PluginUpToDate s.AutoLoadConfigured s.Ready
    printfn "        说明: %s" s.Detail

printfn "=== 场景 1：CAD 已存在用户的 acaddoc.lsp ==="
reset ()
show "初始"
let r1 = CadPluginInstaller.Install(root, pluginSource)
printfn "  安装结果 success=%b : %s" r1.Success r1.Summary
r1.Steps |> Seq.iter (fun s -> printfn "    · %s" s)
show "安装后"
let textAfterInstall = File.ReadAllText autoLoad
printfn "  用户原有配置仍在: %b" (textAfterInstall.Contains(userLine))
printfn "  标记块存在: %b" (textAfterInstall.Contains(CadPluginInstaller.BeginMarker))
printfn "  备份文件存在: %b" (File.Exists(autoLoad + ".dwgtranslator-backup"))

printfn ""
printfn "=== 场景 2：重复安装必须幂等 ==="
let r2 = CadPluginInstaller.Install(root, pluginSource)
show "重装后"
let textAfterSecond = File.ReadAllText autoLoad
let blocks = textAfterSecond.Split(CadPluginInstaller.BeginMarker).Length - 1
printfn "  标记块数量（应为 1）: %d" blocks
printfn "  文件长度 第一次=%d 第二次=%d" textAfterInstall.Length textAfterSecond.Length

printfn ""
printfn "=== 场景 3：版本更新（模拟发布新插件）==="
File.AppendAllText(Path.Combine(root, "Support", "DwgTranslator", "DwgTranslator.Cad.dll"), "tampered")
show "被改坏后"
let r3 = CadPluginInstaller.Install(root, pluginSource)
show "修复后"
printfn "  修复结果 success=%b" r3.Success

printfn ""
printfn "=== 场景 4：卸载 ==="
let r4 = CadPluginInstaller.Uninstall(root)
printfn "  卸载结果 success=%b : %s" r4.Success r4.Summary
r4.Steps |> Seq.iter (fun s -> printfn "    · %s" s)
let textAfterUninstall = File.ReadAllText autoLoad
printfn "  acaddoc.lsp 仍存在: %b  用户配置保留: %b  标记块已移除: %b" (File.Exists autoLoad) (textAfterUninstall.Contains(userLine)) (not (textAfterUninstall.Contains(CadPluginInstaller.BeginMarker)))
printfn "  插件文件已删除: %b" (not (File.Exists(Path.Combine(root, "Support", "DwgTranslator", "DwgTranslator.Cad.dll"))))

printfn ""
printfn "=== 场景 5：全新机器（没有 acaddoc.lsp）==="
if Directory.Exists root then Directory.Delete(root, true)
Directory.CreateDirectory support |> ignore
File.WriteAllText(Path.Combine(root, "gcad.exe"), "")
CadPluginInstaller.Install(root, pluginSource) |> ignore
printfn "  安装后创建了 acaddoc.lsp: %b" (File.Exists autoLoad)
CadPluginInstaller.Uninstall(root) |> ignore
printfn "  卸载后删除自建 acaddoc.lsp: %b" (not (File.Exists autoLoad))

printfn ""
printfn "=== 场景 6：目标不是 CAD 目录时拒绝安装 ==="
let bogus = Path.Combine(Path.GetTempPath(), "cadtest", "notacad")
Directory.CreateDirectory bogus |> ignore
let r6 = CadPluginInstaller.Install(bogus, pluginSource)
printfn "  结果 success=%b （应为 false）: %s" r6.Success r6.Summary
