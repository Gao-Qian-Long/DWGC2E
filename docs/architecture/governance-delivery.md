# DWGC2E 工程结构治理交付报告

> 历史报告：当前版本、57项文件去向、最新验证与未完成项以 [当前交付报告](governance-current-delivery.md) 为准。下文旧数字不代表当前状态。

日期：2026-09-16。状态：主体结构整理与安装工具交付已落地；用户将测试验收留待后续。仍有实现受阻项，不宣称全部目标完成。

## 1. 已发现并处理的问题

- 原Core/Services混放模型接口、翻译业务、文件系统、CAD部署与布局实现，已按职责移动，保持程序集与namespace兼容。
- 默认词库、提示词、图标分散，已集中到assets；发布复制契约保留，不要求运行环境使用源码结构。
- 正式布局、CAD及构建回归脚本已归入tests；旧工具位置需要兼容时保留轻量转发入口。
- CAD插件源码与桌面桥接独立；修正浩辰实际自动加载入口为gcad.lsp，不删除旧用户启动脚本。
- 安装目录默认D:/DWGC2E，D盘不可用才回退C:/DWGC2E；升级路径与数据目录不强迁移。
- 便携卸载不再笼统清空目录：按安装清单和哈希移除未修改程序文件，保留用户及未知内容，不强杀进程。
- 当前只读快照8691个文件：6586项构建/依赖目录内容、1729项产物/证据，其余为源码、测试、资源等。这些数字不是可删除文件数量；不能把验收证据当垃圾。
- 早期11001项盘点的原证据目录现已缺失；已补当前清单，不冒充原历史快照。

## 2. 实际结构（省略生成目录与本地状态）

```text
DWGC2E/
├─ src/
│  ├─ DwgTranslator.App/
│  │  ├─ Views/、ViewModels/、Converters/、Themes/
│  │  ├─ CadIntegration/        桌面端CAD桥接
│  │  ├─ Services/              现有UI支持服务
│  │  └─ Embedded/              插件内嵌资源
│  ├─ DwgTranslator.Core/
│  │  ├─ Models/、Interfaces/、Compatibility/
│  │  ├─ Application/           翻译、词库、文件用例
│  │  ├─ Infrastructure/        文件、配置、账号、API等
│  │  ├─ CadIntegration/        检测与部署
│  │  ├─ Drawing/Layout/        保持现有回写布局算法
│  │  └─ Api/、Tasks/、Translation/、Logging/、Resources/
│  └─ DwgTranslator.Cad/        真正加载到CAD中的插件
├─ tests/                      Core、UI、布局、CAD、安装与集成回归
├─ assets/                     icons、glossaries、prompts
├─ tools/                      构建、发布、校验、只读盘点、历史授权工具
├─ installer/                  安装源码与用户说明
├─ docs/architecture/          架构、治理及验收交接
├─ cf-worker/                  独立云端工程，不并入桌面重构
├─ artifacts/                  构建候选、安装包、回滚及证据
├─ release/                    用户指定的唯一正式本地启动目录
├─ Directory.Build.props
├─ DwgTranslator.sln
├─ settings.json.example
├─ README.md、cleanup-report.md
└─ .gitignore、publish.bat等工程入口
```

保留现有工程数量与名称，不强建Application/Infrastructure独立项目，不建立空samples/scripts目录。根本地settings.json仍保留待确认。release是用户明确要求的例外，不再新建dist。

## 3. 引用与依赖调整

- App图标使用assets/icons源文件，通过Link=icon.ico保留资源URI。
- 词库与提示词从assets源目录复制到既有运行路径；默认配置只取settings.json.example。
- Core的net48显式Compile列表同步到Infrastructure/Files和Drawing/Layout，避免CAD编译面漏文件。
- 5条显式ProjectReference实际目标存在；App另由MSBuild构建Cad。静态目标存在不是完整动态依赖证明。
- 本机SDK覆盖文件Directory.Build.props.user条件导入且忽略提交。
- namespace、程序集产品标识保持稳定；安装目录名称DWGC2E不擅自迁移%APPDATA%/DwgTranslator。
- UI/Core边界及遗留职责债务见overview.md；本轮不重写支付窗口、TaskManager和回写算法。

## 4. 删除与保留

已记录的重复图标清理：原src/DwgTranslator.App/icon.ico与集中图标一致，项目改为链接集中资源。56项搬迁详见下表，不把移动误计为删除业务模块。

本次收尾没有删除未知文件、DLL或其他任务产物。LicenseGenerator仍有历史授权语义；根settings.json为零字节但归属不明；IDE/Cloudflare状态、付款及CAD证据保留。安装故障回滚与本任务过期夹具清理操作此前被执行策略拒绝，未绕过重试；它们仍未完成。

## 5. 已有Build及验收结果（非本轮新运行）

- 最后正式APP交付：2.1.1+ui.20260916-095905.a3f6f1f，已安装至release。
- 该发布日志包含Restore、Build、Publish成功及插件/包验证；Core164/164、布局21/21、CAD16/16、UI_SMOKE通过（受控接口）。Clean和较早阶段记录见governance-final-validation.md，不虚构本轮完整重跑。
- 后续便携安装/卸载49项通过；Inno46项通过（两次2.1.1实际构建间升级，不等于跨语义版本迁移）。
- 新便携包位于artifacts/install-default-20260916-082921/remaining-acceptance/delivery-final/DwgTranslator-2.1.1-win-x64.zip。修订安装说明，不改变程序；与当前release EXE一致。
- 真实启动、页面和购买窗已有受控UI记录；真实登录、会员、云翻译、APP→CAD→输出、更新全链路不因此记为通过。用户决定后续自行验收，见manual-acceptance.md。

## 6. Git审阅与后续工作

当前工作区混有UI/后端其他任务变更，未自动暂存或提交，不整体格式化。全树diff检查仍报告其他改动中的5处空白问题（后端index.ts、BatchTasksPage、SettingsPage、UiSmoke），没有为目录治理重写这些文件。工程引用差异已复核；所有新目录和旧路径删除必须成对审阅与提交，不能只提交删除。

真正剩余实现项：安装故障完整回滚（受阻）、快捷方式自动安全清理（当前明确手动）、产物有界清理（受阻）。真实验收单列，不要求现在执行。后续仅按真实职责提取UI业务，先保护既有行为，不引入新架构框架。

## 7. 文件移动逐项当前核对

下表从cleanup-report记录提取；56个新位置全部存在，55个旧位置已不存在，1个旧工具位置保留兼容转发。存在性核对不是对历史所有内容修改的背书。

| 旧路径 | 新路径 | 当前状态 |
|---|---|---|
| `src/DwgTranslator.Core/Services/IAutoCadInteropService.cs` | `src/DwgTranslator.Core/Interfaces/IAutoCadInteropService.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/IDeepSeekClient.cs` | `src/DwgTranslator.Core/Interfaces/IDeepSeekClient.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/IDwgReaderService.cs` | `src/DwgTranslator.Core/Interfaces/IDwgReaderService.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/IDwgWriterService.cs` | `src/DwgTranslator.Core/Interfaces/IDwgWriterService.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/IDxfReaderService.cs` | `src/DwgTranslator.Core/Interfaces/IDxfReaderService.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/IDxfWriterService.cs` | `src/DwgTranslator.Core/Interfaces/IDxfWriterService.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/IExcelService.cs` | `src/DwgTranslator.Core/Interfaces/IExcelService.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/IGlossaryService.cs` | `src/DwgTranslator.Core/Interfaces/IGlossaryService.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/ILicenseService.cs` | `src/DwgTranslator.Core/Interfaces/ILicenseService.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/ILocalizationService.cs` | `src/DwgTranslator.Core/Interfaces/ILocalizationService.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/ITranslationService.cs` | `src/DwgTranslator.Core/Interfaces/ITranslationService.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/AutoCadDetector.cs` | `src/DwgTranslator.Core/CadIntegration/Detection/AutoCadDetector.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/CadPluginInstaller.cs` | `src/DwgTranslator.Core/CadIntegration/Deployment/CadPluginInstaller.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/BatchExportPlanner.cs` | `src/DwgTranslator.Core/Application/Files/BatchExportPlanner.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/GlossaryConflictDetector.cs` | `src/DwgTranslator.Core/Application/Glossary/GlossaryConflictDetector.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/GlossaryService.cs` | `src/DwgTranslator.Core/Application/Glossary/GlossaryService.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/SettingsBackedDeepSeekClient.cs` | `src/DwgTranslator.Core/Application/Translation/SettingsBackedDeepSeekClient.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/TranslationFilter.cs` | `src/DwgTranslator.Core/Application/Translation/TranslationFilter.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/TranslationPrompt.cs` | `src/DwgTranslator.Core/Application/Translation/TranslationPrompt.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/TranslationQualityValidator.cs` | `src/DwgTranslator.Core/Application/Translation/TranslationQualityValidator.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/TranslationService.cs` | `src/DwgTranslator.Core/Application/Translation/TranslationService.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/WorkerTranslationService.cs` | `src/DwgTranslator.Core/Application/Translation/WorkerTranslationService.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/AccountWorkspace.cs` | `src/DwgTranslator.Core/Infrastructure/Accounts/AccountWorkspace.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/InstallationIdentityStore.cs` | `src/DwgTranslator.Core/Infrastructure/Accounts/InstallationIdentityStore.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/MachineIdentifier.cs` | `src/DwgTranslator.Core/Infrastructure/Accounts/MachineIdentifier.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/LicenseService.cs` | `src/DwgTranslator.Core/Infrastructure/Accounts/LicenseService.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/SettingsStore.cs` | `src/DwgTranslator.Core/Infrastructure/Configuration/SettingsStore.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/LocalizationService.cs` | `src/DwgTranslator.Core/Infrastructure/Localization/LocalizationService.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/DeepSeekClient.cs` | `src/DwgTranslator.Core/Infrastructure/Api/DeepSeekClient.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/DeepSeekClientFactory.cs` | `src/DwgTranslator.Core/Infrastructure/Api/DeepSeekClientFactory.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/DwgReaderService.cs` | `src/DwgTranslator.Core/Infrastructure/Files/DwgReaderService.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/DwgWriterService.cs` | `src/DwgTranslator.Core/Infrastructure/Files/DwgWriterService.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/ExcelService.cs` | `src/DwgTranslator.Core/Infrastructure/Files/ExcelService.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/OutputPathResolver.cs` | `src/DwgTranslator.Core/Infrastructure/Files/OutputPathResolver.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/SafeFileCommit.cs` | `src/DwgTranslator.Core/Infrastructure/Files/SafeFileCommit.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/CadGeometryHelper.cs` | `src/DwgTranslator.Core/Drawing/Layout/CadGeometryHelper.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/CadLabelCompactor.cs` | `src/DwgTranslator.Core/Drawing/Layout/CadLabelCompactor.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/DwgBoundsEstimator.cs` | `src/DwgTranslator.Core/Drawing/Layout/DwgBoundsEstimator.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/DwgCollisionDetector.cs` | `src/DwgTranslator.Core/Drawing/Layout/DwgCollisionDetector.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/DwgFontManager.cs` | `src/DwgTranslator.Core/Drawing/Layout/DwgFontManager.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/DwgFrameDetector.cs` | `src/DwgTranslator.Core/Drawing/Layout/DwgFrameDetector.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/DwgTextReplacer.cs` | `src/DwgTranslator.Core/Drawing/Layout/DwgTextReplacer.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/DwgTextScaler.cs` | `src/DwgTranslator.Core/Drawing/Layout/DwgTextScaler.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/FontMapper.cs` | `src/DwgTranslator.Core/Drawing/Layout/FontMapper.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/TextEnvelopeGeometry.cs` | `src/DwgTranslator.Core/Drawing/Layout/TextEnvelopeGeometry.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/TextWidthEstimator.cs` | `src/DwgTranslator.Core/Drawing/Layout/TextWidthEstimator.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.Core/Services/VerticalTextLayout.cs` | `src/DwgTranslator.Core/Drawing/Layout/VerticalTextLayout.cs` | 新位置存在，旧位置不存在 |
| `src/DwgTranslator.App/Services/AutoCadInteropService.cs` | `src/DwgTranslator.App/CadIntegration/AutoCadInteropService.cs` | 新位置存在，旧位置不存在 |
| `glossaries/mechanical_zh_en.json` | `assets/glossaries/mechanical_zh_en.json` | 新位置存在，旧位置不存在 |
| `prompts/deepl_context.txt` | `assets/prompts/deepl_context.txt` | 新位置存在，旧位置不存在 |
| `assets/icon.ico` | `assets/icons/icon.ico` | 新位置存在，旧位置不存在 |
| `tools/LayoutRegression/Program.cs` | `tests/DwgTranslator.LayoutRegression/Program.cs` | 新位置存在，旧位置不存在 |
| `tools/LayoutRegression/LayoutRegression.csproj` | `tests/DwgTranslator.LayoutRegression/LayoutRegression.csproj` | 新位置存在，旧位置不存在 |
| `tools/tests/Test-CadPluginInstaller.fsx` | `tests/CadIntegration/Test-CadPluginInstaller.fsx` | 新位置存在，旧位置不存在 |
| `CODE_REVIEW.md` | `docs/architecture/legacy-code-review.md` | 新位置存在，旧位置不存在 |
| `tools/Test-DesktopArtifactCleanup.ps1` | `tests/BuildPipeline/Test-DesktopArtifactCleanup.ps1` | 目标存在；旧入口兼容保留 |

## 源码后续补充：快捷方式
安全定向清理源码已实现，尚未验收及重新打包，替代第6节对此项的‘未实施’描述。当前已验证交付包保持原样；详细边界见governance-current-status.md。


## 追加：Git文件去向核查（2026-09-16）

正式清单为 `docs/architecture/project-moves.json`，固定记录原始Git基线；重复检查入口 `tools/Verify-ProjectMoves.ps1`。本次57条记录全部通过：56条既有搬迁/兼容记录加1条重复图标合并。当前所有Git已跟踪删除均有去向，没有遗漏。

- 18条搬迁与基线字节相同；34条只存在当前Git `core.autocrlf=true` 对应的换行差异，内容经Git clean过滤一致。未为了审查而全项目改换行。
- 3条不是纯移动：CadPluginInstaller已有浩辰gcad.lsp加载修正；SettingsStore已有跨端API地址迁移；CAD安装测试已有路径修正和场景增强。清单分别记录原因与审查时Git内容哈希；后续内容变化会使检查失败，需重新审查，不得盲目刷新哈希。
- tools/Test-DesktopArtifactCleanup.ps1是尚未进入原基线的新兼容入口，验证其指向tests中的正式测试，不声称可与原Git内容比较。
- src/DwgTranslator.App/icon.ico与assets/icons/icon.ico逐字节一致，删除属于资源去重，不是资源丢失。
- 只改隔离的检查清单做了3项拒绝验证：目标缺失、审查后内容变化、遗漏已跟踪删除，均正确失败。没有为了测试改动正式源码。

证据：`artifacts/installer-safety-20260916-resumed/git-review/verified-moves.json` 及3份负向结果。此检查只证明登记文件去向、内容边界及当前删除覆盖，不替代所有新增/修改文件的语义审查、构建或真实业务验收；未编译APP、未更新release、未提交Git。