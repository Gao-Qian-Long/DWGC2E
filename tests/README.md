# 测试与探针索引

按"会不会在发版时拦住你"分三类。发版门禁由 `tools/Publish-Desktop.ps1` 串行调用，任一条红就不会替换 `release`。

## 一、发版门禁（自动跑，改完代码必须过）

| 项目 | 覆盖 |
|---|---|
| `DwgTranslator.Core.Tests/` | 内核单测（约 578 项）：API 契约、翻译管线、任务状态机、项目库、校对持久化、设置迁移 |
| `DwgTranslator.App.UiSmoke/` | WPF 冒烟：真实渲染 + 几何断言 + 截图矩阵（含预算/布局/支付窗/任务抽屉/流程状态） |
| `BuildPipeline/` | 交付管线门禁：安装器与事务、品牌与图标、间距令牌、UI 绑定完整性、包内容与载荷、发布锁、壳层图标刷新、版本化路径 |
| `ArchitectureAudit/` | 候选产物架构审计：Core/Cad 不得反向依赖界面程序集，且不含 WPF 类型引用 |
| `CadIntegration/Test-CadPluginInstaller.fsx` | CAD 插件部署回归（写 acaddoc.lsp 托管块、按清单安装/卸载） |
| `TaskStoreCrashProbe/` | 任务库进程中断恢复（模拟写入中途被杀） |
| `DwgTranslator.LayoutRegression/` | 图纸排版回归（字高/宽度收敛） |

## 二、手工/隔离验证（不拦发布，按需运行）

`BuildPipeline/` 里另有一批**不被流水线调用**的检查，用于隔离验证或诊断，脚本头部都写了用法：

- 结构与配置：`Test-ProjectStructure.ps1`、`Test-GitIgnore.ps1`、`Test-SourceConfiguration.ps1`、`Test-InventoryPaths.ps1`、`Test-ArchitectureAudit.ps1`
- 发布与清理：`Test-DesktopPublishLock.ps1`、`Test-CleanPackageInput.ps1`、`Test-BrandPublishGate.ps1`、`Test-ReleaseManifest.ps1`、`Test-CadPayloadManifest.ps1`、`Test-CadPayloadVerifier.ps1`
- 安装器补充：`Test-PortableShortcuts.ps1`、`Test-PortableShortcutUninstall.ps1`、`Test-DesktopShortcutUninstall.ps1`、`Test-InstallerPathSafety.ps1`
- 品牌与取证：`Test-BrandIcons.cjs`（需要 Playwright）、`Test-CaptureLaunchSafety.ps1`

注：其中多数用**合成夹具**驱动被测脚本，不碰真实用户数据、真实安装目录或线上服务。

## 三、真实环境探针（需要宿主/网络，手工跑）

| 项目 | 用途 |
|---|---|
| `CadRuntimeProbe/` | 在真实 CAD 宿主里跑通"读取→翻译→写回→比对"，含进程中断与崩溃恢复边界 |
| `ProofreadingRestartProbe/` | 校对记录在重启后的恢复行为 |
| `Integration/` | 网页端与跨端浏览器集成（Playwright + 本地 Worker fixture），覆盖会话生命周期、后台管理与安全检查 |

## 跑法

```powershell
# 全部门禁（等价于发版前校验；正常交付请直接用 publish.bat）
dotnet test tests/DwgTranslator.Core.Tests -c Release

# 单个门禁脚本
powershell -NoProfile -ExecutionPolicy Bypass -File tests/BuildPipeline/Test-Installer.ps1 -ResultDir artifacts/<任务目录>/installer

# UI 冒烟（需要前台窗口；截图输出到 DWGC2E_UI_SMOKE_OUTPUT 指定的目录）
dotnet run --project tests/DwgTranslator.App.UiSmoke -c Release
```

约定：证据写进 `artifacts/<按任务命名的目录>/`，不要散落在 `artifacts/` 根下；`-ResultDir`/`-EvidenceDir` 之类的参数都要求**全新目录**（已存在会被拒绝，避免覆盖上一次结论）。
