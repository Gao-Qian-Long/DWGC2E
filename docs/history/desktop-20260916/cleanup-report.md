# 工程结构治理清单

日期：2026-09-16（本地时间）。历史清单记录当时计划；最初盘点证据目录现已缺失，无法追证完整执行顺序。当前去向校验和交付状态见 docs/architecture/governance-current-delivery.md。

## 当前盘点补充（2026-09-16，构建引用补齐）

后续有效快照为 artifacts/installer-safety-20260916-resumed/build-reference-inventory/final-current：14615文件、67显式构建声明。新增覆盖.props/.targets、Solution项目入口与显式构建顺序；测试30项通过。旧快照计数保留作历史，增长包含测试与证据，不作为垃圾删除数量。未知文件仍保留。

## 历史盘点补充（2026-09-16，20:19）

最新快照13937个文件、59项显式工程条目，见 artifacts/installer-safety-20260916-resumed/inventory-classification/current/。跳过.git数据库和两条已登记测试联接。10项后端配置/文档/SQL/验证工具已按实际用途归类KEEP；自动UNKNOWN仅根settings.json（0字节），仍禁止自动删除。

生成/依赖7053项、产物/证据6394项均不是可直接删除数量；内含当前交付、回滚及独有证据。盘点工具23项回归通过，包括未知DLL仍要求运行时审查、不导出文件内容、不改源文件。本轮未执行删除或迁移。当前39项判定见 docs/architecture/governance-completion-audit.md。

## 盘点基线
- 全盘 11001 个物理文件；含 .git、依赖、构建输出和历史截图。
- 已读取 219 个桌面源文件、工程及脚本；逐文件分类及依赖证据：`D:/DWGC2E/artifacts/project-governance-2026-09-15T16-36-25-518Z`。
- 已备份现有 tracked diff、状态和源文件摘要；不回滚其他任务的 UI、支付、后端改动。

## SAFE TO DELETE
- `src/DwgTranslator.App/icon.ico`：与 `assets/icon.ico` SHA256 完全相同（9D43AACFD54E170015A965B64715A58FB59C2BD70FFB260469DDA3CE43E1F0A1）。仅在 csproj 改为链接集中资源并保持 icon.ico 的 Pack URI 后删除重复副本。
- 标准 bin/obj：可重建；本轮仅由 dotnet clean 管理，不批量删除。
- 发布历史：仅调用既有有界清理工作流；保留运行版本、独有便携数据及回滚。

## REVIEW REQUIRED（本轮不删除）
- tools/LicenseGenerator：KEEP。当前源码仅提示离线签发已退休并返回 1，保留拒绝型兼容入口；不据此删除旧授权兼容接口。
- settings.json：根目录零字节本地文件；保留，实际发布使用 settings.json.example。
- %SystemDrive%：4个历史缓存数据库，归属及恢复价值未证实，保留。根.claude/.vscode当前不存在，不把旧清单当作当前文件状态。
- artifacts 内历史 CAD 测试、付款验证、网页及桌面截图：可能是验收证据，全部保留（发布清理器限定候选除外）。
- CAD 诊断命令、插件 DLL、宿主依赖：可能动态加载，不以 using 判定删除。
- installer 的说明及安装引导：已用 git ls-files 复核为正式受控文件（中文路径转义导致最初计数误差）；实际打包依赖，必须保留。

## KEEP
- tools/LicenseGenerator：当前源码仅提示离线签发已退休并返回1，保留拒绝型兼容入口；不据此删除旧授权接口。
- 根.wrangler/cache的3个JSON：KEEP_LOCAL_STATE，未读取内容、不调用外部账户，不纳入发布。元数据和27项忽略规则回归见 artifacts/installer-safety-20260916-resumed/root-state-review/report.md。
- 正式代码、资源、配置模板、所有正式测试、安装脚本、运行时 CAD 私有依赖。
- cf-worker 独立后端，本轮不移动、不部署、不变更账号/会员/订单/支付业务。
- release 是唯一正式本地运行目录；artifacts 是构建候选和证据目录，不新增 dist。

## 结构方案（阶段 3）
保留现有三大程序集与产品标识。Core 是现有多目标共享库，并非纯 Domain：目录内明确 Interfaces、Application、Infrastructure、Drawing、CadIntegration，保持 namespace 兼容。App 的 CAD 桥接从 Services 单独归类；插件继续在 DwgTranslator.Cad。正式布局/CAD 脚本测试进入 tests。资源集中 assets，运行时相对路径不变。构建工具仍在 tools，不为了模板新增 scripts 空目录。

## 边界与不做的事
不进行支付/登录真实交易；不自动删除未知文件；不对整个项目格式化。UI购买记录持久化已后续抽取到PendingPurchaseStore；购买窗口与ViewModel尚有业务编排：先明确边界，不能以目录整理名义重写在途业务。TaskManager（1486 行）和 CAD 写回（1055 行）不按行数机械拆分。

## 计划移动
- `src/DwgTranslator.Core/Services/IAutoCadInteropService.cs` → `src/DwgTranslator.Core/Interfaces/IAutoCadInteropService.cs`
- `src/DwgTranslator.Core/Services/IDeepSeekClient.cs` → `src/DwgTranslator.Core/Interfaces/IDeepSeekClient.cs`
- `src/DwgTranslator.Core/Services/IDwgReaderService.cs` → `src/DwgTranslator.Core/Interfaces/IDwgReaderService.cs`
- `src/DwgTranslator.Core/Services/IDwgWriterService.cs` → `src/DwgTranslator.Core/Interfaces/IDwgWriterService.cs`
- `src/DwgTranslator.Core/Services/IDxfReaderService.cs` → `src/DwgTranslator.Core/Interfaces/IDxfReaderService.cs`
- `src/DwgTranslator.Core/Services/IDxfWriterService.cs` → `src/DwgTranslator.Core/Interfaces/IDxfWriterService.cs`
- `src/DwgTranslator.Core/Services/IExcelService.cs` → `src/DwgTranslator.Core/Interfaces/IExcelService.cs`
- `src/DwgTranslator.Core/Services/IGlossaryService.cs` → `src/DwgTranslator.Core/Interfaces/IGlossaryService.cs`
- `src/DwgTranslator.Core/Services/ILicenseService.cs` → `src/DwgTranslator.Core/Interfaces/ILicenseService.cs`
- `src/DwgTranslator.Core/Services/ILocalizationService.cs` → `src/DwgTranslator.Core/Interfaces/ILocalizationService.cs`
- `src/DwgTranslator.Core/Services/ITranslationService.cs` → `src/DwgTranslator.Core/Interfaces/ITranslationService.cs`
- `src/DwgTranslator.Core/Services/AutoCadDetector.cs` → `src/DwgTranslator.Core/CadIntegration/Detection/AutoCadDetector.cs`
- `src/DwgTranslator.Core/Services/CadPluginInstaller.cs` → `src/DwgTranslator.Core/CadIntegration/Deployment/CadPluginInstaller.cs`
- `src/DwgTranslator.Core/Services/BatchExportPlanner.cs` → `src/DwgTranslator.Core/Application/Files/BatchExportPlanner.cs`
- `src/DwgTranslator.Core/Services/GlossaryConflictDetector.cs` → `src/DwgTranslator.Core/Application/Glossary/GlossaryConflictDetector.cs`
- `src/DwgTranslator.Core/Services/GlossaryService.cs` → `src/DwgTranslator.Core/Application/Glossary/GlossaryService.cs`
- `src/DwgTranslator.Core/Services/SettingsBackedDeepSeekClient.cs` → `src/DwgTranslator.Core/Application/Translation/SettingsBackedDeepSeekClient.cs`
- `src/DwgTranslator.Core/Services/TranslationFilter.cs` → `src/DwgTranslator.Core/Application/Translation/TranslationFilter.cs`
- `src/DwgTranslator.Core/Services/TranslationPrompt.cs` → `src/DwgTranslator.Core/Application/Translation/TranslationPrompt.cs`
- `src/DwgTranslator.Core/Services/TranslationQualityValidator.cs` → `src/DwgTranslator.Core/Application/Translation/TranslationQualityValidator.cs`
- `src/DwgTranslator.Core/Services/TranslationService.cs` → `src/DwgTranslator.Core/Application/Translation/TranslationService.cs`
- `src/DwgTranslator.Core/Services/WorkerTranslationService.cs` → `src/DwgTranslator.Core/Application/Translation/WorkerTranslationService.cs`
- `src/DwgTranslator.Core/Services/AccountWorkspace.cs` → `src/DwgTranslator.Core/Infrastructure/Accounts/AccountWorkspace.cs`
- `src/DwgTranslator.Core/Services/InstallationIdentityStore.cs` → `src/DwgTranslator.Core/Infrastructure/Accounts/InstallationIdentityStore.cs`
- `src/DwgTranslator.Core/Services/MachineIdentifier.cs` → `src/DwgTranslator.Core/Infrastructure/Accounts/MachineIdentifier.cs`
- `src/DwgTranslator.Core/Services/LicenseService.cs` → `src/DwgTranslator.Core/Infrastructure/Accounts/LicenseService.cs`
- `src/DwgTranslator.Core/Services/SettingsStore.cs` → `src/DwgTranslator.Core/Infrastructure/Configuration/SettingsStore.cs`
- `src/DwgTranslator.Core/Services/LocalizationService.cs` → `src/DwgTranslator.Core/Infrastructure/Localization/LocalizationService.cs`
- `src/DwgTranslator.Core/Services/DeepSeekClient.cs` → `src/DwgTranslator.Core/Infrastructure/Api/DeepSeekClient.cs`
- `src/DwgTranslator.Core/Services/DeepSeekClientFactory.cs` → `src/DwgTranslator.Core/Infrastructure/Api/DeepSeekClientFactory.cs`
- `src/DwgTranslator.Core/Services/DwgReaderService.cs` → `src/DwgTranslator.Core/Infrastructure/Files/DwgReaderService.cs`
- `src/DwgTranslator.Core/Services/DwgWriterService.cs` → `src/DwgTranslator.Core/Infrastructure/Files/DwgWriterService.cs`
- `src/DwgTranslator.Core/Services/ExcelService.cs` → `src/DwgTranslator.Core/Infrastructure/Files/ExcelService.cs`
- `src/DwgTranslator.Core/Services/OutputPathResolver.cs` → `src/DwgTranslator.Core/Infrastructure/Files/OutputPathResolver.cs`
- `src/DwgTranslator.Core/Services/SafeFileCommit.cs` → `src/DwgTranslator.Core/Infrastructure/Files/SafeFileCommit.cs`
- `src/DwgTranslator.Core/Services/CadGeometryHelper.cs` → `src/DwgTranslator.Core/Drawing/Layout/CadGeometryHelper.cs`
- `src/DwgTranslator.Core/Services/CadLabelCompactor.cs` → `src/DwgTranslator.Core/Drawing/Layout/CadLabelCompactor.cs`
- `src/DwgTranslator.Core/Services/DwgBoundsEstimator.cs` → `src/DwgTranslator.Core/Drawing/Layout/DwgBoundsEstimator.cs`
- `src/DwgTranslator.Core/Services/DwgCollisionDetector.cs` → `src/DwgTranslator.Core/Drawing/Layout/DwgCollisionDetector.cs`
- `src/DwgTranslator.Core/Services/DwgFontManager.cs` → `src/DwgTranslator.Core/Drawing/Layout/DwgFontManager.cs`
- `src/DwgTranslator.Core/Services/DwgFrameDetector.cs` → `src/DwgTranslator.Core/Drawing/Layout/DwgFrameDetector.cs`
- `src/DwgTranslator.Core/Services/DwgTextReplacer.cs` → `src/DwgTranslator.Core/Drawing/Layout/DwgTextReplacer.cs`
- `src/DwgTranslator.Core/Services/DwgTextScaler.cs` → `src/DwgTranslator.Core/Drawing/Layout/DwgTextScaler.cs`
- `src/DwgTranslator.Core/Services/FontMapper.cs` → `src/DwgTranslator.Core/Drawing/Layout/FontMapper.cs`
- `src/DwgTranslator.Core/Services/TextEnvelopeGeometry.cs` → `src/DwgTranslator.Core/Drawing/Layout/TextEnvelopeGeometry.cs`
- `src/DwgTranslator.Core/Services/TextWidthEstimator.cs` → `src/DwgTranslator.Core/Drawing/Layout/TextWidthEstimator.cs`
- `src/DwgTranslator.Core/Services/VerticalTextLayout.cs` → `src/DwgTranslator.Core/Drawing/Layout/VerticalTextLayout.cs`
- `src/DwgTranslator.App/Services/AutoCadInteropService.cs` → `src/DwgTranslator.App/CadIntegration/AutoCadInteropService.cs`
- `glossaries/mechanical_zh_en.json` → `assets/glossaries/mechanical_zh_en.json`
- `prompts/deepl_context.txt` → `assets/prompts/deepl_context.txt`
- `assets/icon.ico` → `assets/icons/icon.ico`
- `tools/LayoutRegression/Program.cs` → `tests/DwgTranslator.LayoutRegression/Program.cs`
- `tools/LayoutRegression/LayoutRegression.csproj` → `tests/DwgTranslator.LayoutRegression/LayoutRegression.csproj`
- `tools/tests/Test-CadPluginInstaller.fsx` → `tests/CadIntegration/Test-CadPluginInstaller.fsx`
- `CODE_REVIEW.md` → `docs/architecture/legacy-code-review.md`

## 验证状态
待执行。结果和实际差异将在验证后追加。

## 路径修复的限定行为变化
发现任务管理器在输出配置为空时回退进程工作目录，可能污染源码。仅该兜底改为既有 DWGC2E_DATA_DIR 或 %AppData%/DwgTranslator/exports；用户显式输出目录、正常账号目录和文件命名不变，并新增隔离回归。

- 追加移动：`tools/Test-DesktopArtifactCleanup.ps1` → `tests/BuildPipeline/Test-DesktopArtifactCleanup.ps1`，旧位置保留兼容入口。
- 可重建残留补充：`tools/LayoutRegression/bin`、`tools/LayoutRegression/obj` 为已移动项目的旧生成物，可在核验位于指定目录且无链接后删除，正式 Program.cs 和工程文件已保留到 tests。

---

# 执行结果（最终）

## 八阶段执行记录

1. 只读扫描整个工作区，保留初始 Git 状态、diff 与文件元数据。
2. 分类资源、测试、生成物、未知用途；未凭文件名删除 test 或 DLL。
3. 先写本报告的删除等级与目录方案，再移动。
4. 实际移动 **56 个文件**；未增加业务程序集。
5. 修复显式编译、集中资源、正式测试路径、发布与安装复制；命名空间及程序集名称不变。
6. Clean、Restore、Build、Publish 均通过，正常发布到 release。
7. 完成自动化与发布后启动验证；真实外部系统和 Inno 安装项列明未验证。
8. 输出本报告、开发 README、架构说明以及完整本地证据。

## 当前项目发现的问题与数量

基线扫描 11001 个物理文件（含版本库、缓存和历史交付），不是 11001 个业务文件。读入 219 个桌面源文件/工程/脚本用于引用分析，另审查 28 个配置/资源/脚本文件。二进制清单 1379 项，多数位于生成物/历史交付；不按“无 using”删除。以下分类以**移动前快照**为准：

| 分类 | 文件数 |
|---|---:|
| 本地缓存/工具状态（保留） | 13 |
| 版本库元数据 | 710 |
| 正式工程/默认配置 | 7 |
| 文档 | 27 |
| 历史产物/验收证据（保留） | 3546 |
| 历史备份（待确认） | 12 |
| 生成文件/依赖缓存 | 6394 |
| 正式资源 | 5 |
| 独立后端（本轮不改） | 55 |
| 构建/开发入口 | 2 |
| 安装工具/资源 | 4 |
| 正式发布产物（保护） | 11 |
| 未知用途 | 1 |
| 正式源代码 | 178 |
| 正式测试 | 22 |
| 开发诊断工具 | 9 |
| 构建/发布工具 | 5 |

- Core/Services 原有 47 个文件，混合 CAD、网络、文件、配置、翻译和接口，已归类。
- 正式布局回归、CAD 安装脚本和构建清理测试散在 tools，已归 tests；旧清理入口保留兼容壳。
- 图标两份内容完全相同，仅保留 assets/icons 的一份源资源。
- 安装打包遗漏 assets/default-glossaries；包校验未检测该路径，现均补齐。
- Inno 固定旧版本/过时 publish 目录；改为显式输入干净候选、从可执行文件读版本。原未定义 CopyDir 等安装后配置代码改为声明式首次安装复制，避免覆盖用户数据。此分支尚缺 Inno 编译验收。
- Directory.Build.props.user 文档有说明但实际未导入，已修正。
- 任务空输出配置使用工作目录，改为正式应用数据目录兜底。正常用户指定输出不改。
- 发布清理器在 Windows PowerShell 5.1 调用不支持的 GetRelativePath，已以边界核验后的相对路径计算修复并通过专门回归。
- TaskManager、AcadWriterEngine、WorkerApiClient 等大文件保留；有职责债务，但未为行数做高风险拆分。
- 尚未确认任何业务类为死代码，因此业务代码删除 **0 个**；没有虚构“已删废弃模块”。

## 最终目录导航

以下省略 .git、IDE 状态、bin/obj 和 artifacts 内大批历史内容；完整原始盘点另存 inventory.json。

```text
DWGC2E/
├─ src/
│  ├─ DwgTranslator.App/        WPF；Views、ViewModels、Themes、Converters、CadIntegration
│  ├─ DwgTranslator.Core/       多目标共享库（不是纯 Domain）
│  │  ├─ Models/、Interfaces/   模型与服务契约
│  │  ├─ Application/           Files、Glossary、Translation 用例服务
│  │  ├─ Infrastructure/        Accounts、Api、Configuration、Files、Localization
│  │  ├─ CadIntegration/        Detection、Deployment（桌面侧）
│  │  ├─ Drawing/Layout/        图纸布局与写回辅助
│  │  └─ Api/、Tasks/、Translation/、Logging/、Resources/、Compatibility/
│  └─ DwgTranslator.Cad/        CAD 宿主内插件；与桌面桥接分离
├─ tests/                       Core.Tests、App.UiSmoke、LayoutRegression、CadIntegration、BuildPipeline
├─ assets/                      icons、glossaries、prompts（正式源资源）
├─ tools/                       构建、发布、打包、诊断及兼容入口；LicenseGenerator 保留拒绝型兼容入口
├─ installer/                   安装引导、Inno 定义、用户安装说明
├─ docs/architecture/           当前架构与历史代码审查
├─ cf-worker/                   独立云端服务，保留自身测试与部署体系
├─ artifacts/                   忽略入库的候选包、证据、历史截图、受控回滚
├─ release/                     唯一本地可运行交付，不是源代码
├─ DwgTranslator.sln、Directory.Build.props
├─ README.md、cleanup-report.md、settings.json.example
└─ publish.bat、dev.bat、.gitignore
```

根目录仍保留受保护的本地 settings.json（零字节）、%SystemDrive% 缓存及工具状态目录。tools/LayoutRegression 中残留旧 bin/obj 可重建；尝试显式删除被执行环境策略拒绝，未绕过限制，本轮保留，不谎称工作区已完全无缓存。

## 实际移动文件

| 旧路径 | 新路径 |
|---|---|
| `src/DwgTranslator.Core/Services/IAutoCadInteropService.cs` | `src/DwgTranslator.Core/Interfaces/IAutoCadInteropService.cs` |
| `src/DwgTranslator.Core/Services/IDeepSeekClient.cs` | `src/DwgTranslator.Core/Interfaces/IDeepSeekClient.cs` |
| `src/DwgTranslator.Core/Services/IDwgReaderService.cs` | `src/DwgTranslator.Core/Interfaces/IDwgReaderService.cs` |
| `src/DwgTranslator.Core/Services/IDwgWriterService.cs` | `src/DwgTranslator.Core/Interfaces/IDwgWriterService.cs` |
| `src/DwgTranslator.Core/Services/IDxfReaderService.cs` | `src/DwgTranslator.Core/Interfaces/IDxfReaderService.cs` |
| `src/DwgTranslator.Core/Services/IDxfWriterService.cs` | `src/DwgTranslator.Core/Interfaces/IDxfWriterService.cs` |
| `src/DwgTranslator.Core/Services/IExcelService.cs` | `src/DwgTranslator.Core/Interfaces/IExcelService.cs` |
| `src/DwgTranslator.Core/Services/IGlossaryService.cs` | `src/DwgTranslator.Core/Interfaces/IGlossaryService.cs` |
| `src/DwgTranslator.Core/Services/ILicenseService.cs` | `src/DwgTranslator.Core/Interfaces/ILicenseService.cs` |
| `src/DwgTranslator.Core/Services/ILocalizationService.cs` | `src/DwgTranslator.Core/Interfaces/ILocalizationService.cs` |
| `src/DwgTranslator.Core/Services/ITranslationService.cs` | `src/DwgTranslator.Core/Interfaces/ITranslationService.cs` |
| `src/DwgTranslator.Core/Services/AutoCadDetector.cs` | `src/DwgTranslator.Core/CadIntegration/Detection/AutoCadDetector.cs` |
| `src/DwgTranslator.Core/Services/CadPluginInstaller.cs` | `src/DwgTranslator.Core/CadIntegration/Deployment/CadPluginInstaller.cs` |
| `src/DwgTranslator.Core/Services/BatchExportPlanner.cs` | `src/DwgTranslator.Core/Application/Files/BatchExportPlanner.cs` |
| `src/DwgTranslator.Core/Services/GlossaryConflictDetector.cs` | `src/DwgTranslator.Core/Application/Glossary/GlossaryConflictDetector.cs` |
| `src/DwgTranslator.Core/Services/GlossaryService.cs` | `src/DwgTranslator.Core/Application/Glossary/GlossaryService.cs` |
| `src/DwgTranslator.Core/Services/SettingsBackedDeepSeekClient.cs` | `src/DwgTranslator.Core/Application/Translation/SettingsBackedDeepSeekClient.cs` |
| `src/DwgTranslator.Core/Services/TranslationFilter.cs` | `src/DwgTranslator.Core/Application/Translation/TranslationFilter.cs` |
| `src/DwgTranslator.Core/Services/TranslationPrompt.cs` | `src/DwgTranslator.Core/Application/Translation/TranslationPrompt.cs` |
| `src/DwgTranslator.Core/Services/TranslationQualityValidator.cs` | `src/DwgTranslator.Core/Application/Translation/TranslationQualityValidator.cs` |
| `src/DwgTranslator.Core/Services/TranslationService.cs` | `src/DwgTranslator.Core/Application/Translation/TranslationService.cs` |
| `src/DwgTranslator.Core/Services/WorkerTranslationService.cs` | `src/DwgTranslator.Core/Application/Translation/WorkerTranslationService.cs` |
| `src/DwgTranslator.Core/Services/AccountWorkspace.cs` | `src/DwgTranslator.Core/Infrastructure/Accounts/AccountWorkspace.cs` |
| `src/DwgTranslator.Core/Services/InstallationIdentityStore.cs` | `src/DwgTranslator.Core/Infrastructure/Accounts/InstallationIdentityStore.cs` |
| `src/DwgTranslator.Core/Services/MachineIdentifier.cs` | `src/DwgTranslator.Core/Infrastructure/Accounts/MachineIdentifier.cs` |
| `src/DwgTranslator.Core/Services/LicenseService.cs` | `src/DwgTranslator.Core/Infrastructure/Accounts/LicenseService.cs` |
| `src/DwgTranslator.Core/Services/SettingsStore.cs` | `src/DwgTranslator.Core/Infrastructure/Configuration/SettingsStore.cs` |
| `src/DwgTranslator.Core/Services/LocalizationService.cs` | `src/DwgTranslator.Core/Infrastructure/Localization/LocalizationService.cs` |
| `src/DwgTranslator.Core/Services/DeepSeekClient.cs` | `src/DwgTranslator.Core/Infrastructure/Api/DeepSeekClient.cs` |
| `src/DwgTranslator.Core/Services/DeepSeekClientFactory.cs` | `src/DwgTranslator.Core/Infrastructure/Api/DeepSeekClientFactory.cs` |
| `src/DwgTranslator.Core/Services/DwgReaderService.cs` | `src/DwgTranslator.Core/Infrastructure/Files/DwgReaderService.cs` |
| `src/DwgTranslator.Core/Services/DwgWriterService.cs` | `src/DwgTranslator.Core/Infrastructure/Files/DwgWriterService.cs` |
| `src/DwgTranslator.Core/Services/ExcelService.cs` | `src/DwgTranslator.Core/Infrastructure/Files/ExcelService.cs` |
| `src/DwgTranslator.Core/Services/OutputPathResolver.cs` | `src/DwgTranslator.Core/Infrastructure/Files/OutputPathResolver.cs` |
| `src/DwgTranslator.Core/Services/SafeFileCommit.cs` | `src/DwgTranslator.Core/Infrastructure/Files/SafeFileCommit.cs` |
| `src/DwgTranslator.Core/Services/CadGeometryHelper.cs` | `src/DwgTranslator.Core/Drawing/Layout/CadGeometryHelper.cs` |
| `src/DwgTranslator.Core/Services/CadLabelCompactor.cs` | `src/DwgTranslator.Core/Drawing/Layout/CadLabelCompactor.cs` |
| `src/DwgTranslator.Core/Services/DwgBoundsEstimator.cs` | `src/DwgTranslator.Core/Drawing/Layout/DwgBoundsEstimator.cs` |
| `src/DwgTranslator.Core/Services/DwgCollisionDetector.cs` | `src/DwgTranslator.Core/Drawing/Layout/DwgCollisionDetector.cs` |
| `src/DwgTranslator.Core/Services/DwgFontManager.cs` | `src/DwgTranslator.Core/Drawing/Layout/DwgFontManager.cs` |
| `src/DwgTranslator.Core/Services/DwgFrameDetector.cs` | `src/DwgTranslator.Core/Drawing/Layout/DwgFrameDetector.cs` |
| `src/DwgTranslator.Core/Services/DwgTextReplacer.cs` | `src/DwgTranslator.Core/Drawing/Layout/DwgTextReplacer.cs` |
| `src/DwgTranslator.Core/Services/DwgTextScaler.cs` | `src/DwgTranslator.Core/Drawing/Layout/DwgTextScaler.cs` |
| `src/DwgTranslator.Core/Services/FontMapper.cs` | `src/DwgTranslator.Core/Drawing/Layout/FontMapper.cs` |
| `src/DwgTranslator.Core/Services/TextEnvelopeGeometry.cs` | `src/DwgTranslator.Core/Drawing/Layout/TextEnvelopeGeometry.cs` |
| `src/DwgTranslator.Core/Services/TextWidthEstimator.cs` | `src/DwgTranslator.Core/Drawing/Layout/TextWidthEstimator.cs` |
| `src/DwgTranslator.Core/Services/VerticalTextLayout.cs` | `src/DwgTranslator.Core/Drawing/Layout/VerticalTextLayout.cs` |
| `src/DwgTranslator.App/Services/AutoCadInteropService.cs` | `src/DwgTranslator.App/CadIntegration/AutoCadInteropService.cs` |
| `glossaries/mechanical_zh_en.json` | `assets/glossaries/mechanical_zh_en.json` |
| `prompts/deepl_context.txt` | `assets/prompts/deepl_context.txt` |
| `assets/icon.ico` | `assets/icons/icon.ico` |
| `tools/LayoutRegression/Program.cs` | `tests/DwgTranslator.LayoutRegression/Program.cs` |
| `tools/LayoutRegression/LayoutRegression.csproj` | `tests/DwgTranslator.LayoutRegression/LayoutRegression.csproj` |
| `tools/tests/Test-CadPluginInstaller.fsx` | `tests/CadIntegration/Test-CadPluginInstaller.fsx` |
| `CODE_REVIEW.md` | `docs/architecture/legacy-code-review.md` |
| `tools/Test-DesktopArtifactCleanup.ps1` | `tests/BuildPipeline/Test-DesktopArtifactCleanup.ps1` |

52 个源代码/测试工程/脚本移动中，50 个内容哈希完全不变；另外两个只修改测试入口/相对定位（CAD fsx、清理测试）。资源及历史文档采用原字节移动。

## 实际删除文件及理由

- src/DwgTranslator.App/icon.ico：已验证与集中图标 SHA256 相同；csproj 链接集中图标且保留 icon.ico 资源名，UI 冒烟通过；删除前已备份到证据目录。
- `artifacts/DwgTranslator-win-x64-GstarCAD-20260916-002455.zip`：由既有有界发布清理器删除；当前新版与一个回滚已保留。
- `artifacts/publish-20260916-002455`：由既有有界发布清理器删除；当前新版与一个回滚已保留。
- `artifacts/release-backup-20260916-002455`：由既有有界发布清理器删除；当前新版与一个回滚已保留。
- 有界发布清理输出回收 **223,130,372 字节**。除这些已列出项及 dotnet clean 管理的标准中间文件外，不对历史资源、未知文件、CAD DLL 进行清理。
- 旧 tools/LayoutRegression/bin、obj **未删除**。

## 保留待确认

LicenseGenerator 当前源码已确认不再签发授权，仅提示退休并返回失败；历史单独编译证据保留，本轮不运行、不删除；源码根 settings.json 不作发布输入；%SystemDrive%/IDE/云端本地状态保留；历史付款/CAD 验收/网页和桌面 review 截图不删。真实凭据未输出进报告；字段名扫描不等于完整安全审计，不能据此声称不存在任何历史密钥。

## 修改的引用与兼容性

- Core net48 的 5 条原 Services 显式 Compile 路径更新到 Infrastructure/Files 和 Drawing/Layout。
- App Resource 链接集中图标，ApplicationIcon 指向 assets；XAML Pack URI 仍为 DwgTranslator;component/icon.ico。
- 术语源与提示词源进入 assets，打包后的 assets/default-glossaries、glossaries、prompts 路径继续兼容。
- CAD DLL 仍为 release/CadPlugin/DwgTranslator.Cad.dll；私有依赖复制和平台清单保留。
- Solution 加入 UI smoke 和 LayoutRegression；测试工程相对 ProjectReference 深度未改变。
- 发布前加入布局和 CAD 安装回归；原 tools/Publish-Desktop.ps1、publish.bat、dev.bat 入口保留。
- New-ReleasePackage 不再默认旧 publish 目录，也不覆写已有打包目录；必须明确提供干净候选。
- Namespace、x:Class、反射类型名和业务 DTO 均未批量变更。UI/后端原有未提交改动无回滚、无全局格式化。

## Build / 发布 / 测试结果

| 验证 | 结果 |
|---|---|
| dotnet clean（Release） | 通过 |
| dotnet restore（Configuration=Release） | 通过 |
| Solution Release Build | 通过：0 错误，1 条原 UI smoke CS8602 警告 |
| Core net8.0 / net48 | 两个目标均编译通过 |
| App / 浩辰 CAD net48 / 正式测试工程 | 编译通过 |
| LicenseGenerator | 编译通过，未执行 |
| Core 自动化 | **125/125 通过**，含 5 个新增结构/空路径回归 |
| 图纸布局 | **21/21 通过** |
| CAD 插件部署回归 | **9/9 通过**，隔离目录，不操作真实 CAD 安装 |
| 有界清理回归 | 通过：预览、保留、独有数据、重复执行 |
| WPF UI smoke | 通过：受控接口、隔离数据、页面截图 |
| 正常 Publish / 包依赖校验 | 通过；已更新 release |
| 安装便携包生成 + 校验 | 通过，包含 assets/default-glossaries 和 CAD 依赖 |
| 已安装 exe 启动 + 独立日志 | 通过；仅关闭本轮创建的测试进程 |
| Inno Setup 安装器编译/实际安装 | **未验证：未找到 ISCC** |
| AutoCAD 平台编译/宿主 NETLOAD | **未验证；本机实际构建为 GstarCAD** |

首次执行发现系统 PATH 只找到 Runtime；使用本机已有用户目录 SDK 8.0.423 后继续，无下载。初次 Restore 默认 Debug 与 Release Build 不匹配，改为同配置恢复并重新完整 Clean/Restore/Build。首次发布因 release 正在运行被阻止；未终止用户进程，待其退出后正常重跑发布成功。保留这些失败日志以供复查。

### 本地交付

- 运行入口：D:/DWGC2E/release/DwgTranslator.exe
- 版本：2.1.1+ui.20260916-005804.a3f6f1f
- SHA256：46F1CD558F901335F9C8035E0277ACAA879FCA10EBFB4B46611A4FA2FCE45AC4
- 干净发布候选：artifacts/publish-20260916-005804
- 受控回滚：artifacts/release-backup-20260916-005804
- release 中原有 settings.json、glossaries、prompts 共 3 个文件逐一哈希验证保持不变；原无 exports/logs 文件，未虚构数据迁移。
- 正常发布包和安装便携包均为本地文件，未上传网站、未开启购买、未发生真实支付。

## 核心业务回归（区分真实与模拟）

| 项目 | 已验证范围 | 仍需真实环境验收 |
|---|---|---|
| 启动、页面切换 | WPF 实际窗口及发布 exe 启动 | 人工全部屏幕/DPI 观感 |
| 登录、会员、设备/账号边界 | 受控 API 登录、离线/过期、会员刷新和相关 Core 测试 | 真实账号、远端状态、设备服务端联调 |
| 图纸添加、任务创建、翻译 | UI 导入状态和任务管线自动化，21 项布局 | 真实云翻译配额消耗及生产 DWG 全流程 |
| 术语库 | 编辑/保存/重载；默认 JSON 路径与交付包校验 | 用户实际大型术语表体验 |
| CAD | 检测/部署隔离测试，真实编译 DLL 与依赖校验 | 宿主内 NETLOAD、自动加载、真实图纸回写 |
| 输出 | 管线测试、命名保护、空配置正式目录兜底 | 大型实图输出及用户自选目录权限 |
| 设置与日志 | UI 保存、隔离应用数据和发布 exe 写日志 | 用户特定系统环境 |
| 支付/订单窗口 | 受控套餐、二维码、恢复、暂停、过期和状态回填 | 真实下单/付款未执行 |
| 更新 | 原接口/类型/运行路径保持，编译与相关客户端回归 | 真实下载安装升级未执行 |

## Git 与后续建议

已保存本轮前 diff 与状态；全部未纳入本轮修改的已盘点桌面源文件再次哈希核对，意外修改 0。任务所改 tracked 文件在仓库现有换行规则下 diff --check 通过；全仓库仍有先前 UI 文件的空白告警，未为消除告警擅自改其他任务。新文件和重命名尚未提交；未自动 staging/commit。

后续仅建议三件事：
1. 在安装有 Inno 和真实 CAD 宿主的环境完成安装/加载/回写验收。
2. 支付功能稳定后，以测试先行方式提取 BillingWindow 的订单会话与持久化职责；不要现在重写业务。
3. 由维护者确认旧授权工具、未知配置和历史证据的保留期，再执行下一轮清理；不要一刀切删除 DLL。

## 证据索引

本地目录：D:/DWGC2E/artifacts/project-governance-2026-09-15T16-36-25-518Z

- inventory.json / classification-summary.json：逐文件分类及数量。
- source-dependencies.json / config-script-audit.json / binary-inventory.json：引用、配置与二进制盘点。
- baseline-status.txt / baseline-tracked.patch：整理前状态。
- actual-moves.json / move-content-verification.json：实际路径映射及字节保真。
- clean.log / restore.log / build.log / core-final.trx：构建与 125 项测试。
- layout-test.log / cad-install-test.log / cleanup-test.log / ui-smoke-final.log：回归。
- publish-final.log / portable-package-check.log / installed-startup.json：交付和真实启动。
- portable-data-preservation.json / removed-artifact-roots.json / unrelated-source-check.json：数据保护与删除证据。


## 第二轮续做（2026-09-16）

新增安装链保护：安装前检查必需资源与配置；拒绝目录重叠/链接和运行中目标；重装保留既有配置、词库、提示词；复制失败不再删除/改名旧文件重试。新增 13 项隔离回归全部通过，并使用真实发布包完成临时目录安装及 EXE 哈希核验。正式发布脚本已加入安装回归门禁。

本轮未改业务、未重新 Build APP，release 保持首轮已验证版本；更新的安装 ZIP 保存在 `D:/DWGC2E/artifacts/project-governance-followup-20260916-0707/package/DwgTranslator-2.1.1-win-x64.zip`。完整说明见 `docs/architecture/installer-governance-followup.md`。Inno、生产账号/支付、真实 CAD 仍未验证；复制安装非事务式更新，未宣称中途故障可自动全量回滚。

## 第三轮收尾（2026-09-16）

已修复安装默认源路径和失败退出码，安装回归为 19/19；危险自动卸载改为手动安全提示。正常发布更新 release 至 2.1.1+ui.20260916-072524.a3f6f1f，Core 125、布局 21、CAD 部署 9、UI smoke 均通过，真实包隔离安装/启动通过，3 个便携用户文件哈希保持。完整结果见 docs/architecture/governance-final-validation.md。

不能标记“全部完成”：完整安装回滚、自动卸载、Inno 编译、真实 CAD/生产账号/支付验收仍未完成；本轮扩展后台测试 192/194，通过之外两项本地 D1 fetch failed，独立重跑仍失败。跨端 24/24 中包含 H01 已知缺陷复现，不代表已修复。最终包为 artifacts/governance-final-20260916-072248/package-verified/DwgTranslator-2.1.1-win-x64.zip。

## D1 阻塞解除（2026-09-16T08:06:40.2008142+08:00）

用户重启 v2rayN 后，在没有修改 Worker 业务代码、测试断言、数据库结构或依赖的情况下：
- payment-d1.test.mjs：2/2 通过。
- npm test：194/194 通过，0 失败、0 跳过。
- npm run typecheck：通过。

此前嵌套错误为本机 loopback connect EADDRINUSE；端点占用检查发现 xray 占用大量端点。重启后的复测支持端口资源耗尽诊断；这不是通过跳过测试或放宽断言得到的绿灯。此项已关闭，不需要修改支付业务代码或部署后台。真实支付验收仍未进行。

证据目录：D:\DWGC2E\artifacts\governance-resolution-20260916-080055
文件：payment-d1-after-restart.log、backend-after-restart.log、typecheck-after-restart.log、after-restart-result.json。

## 2026-09-16 补充：Inno 隔离安装验收已通过

此前“尚未安装/卸载验收”状态由本节更新：正式与隔离分支均编译成功，隔离首次安装、同版本修复、卸载合计 40/40；便携安装链复测 19/19。最终编译日志无 Warning/Error。真实 settings.json/术语库、正式卸载注册项和 release EXE 未变化。仅测试分支安装器被执行，正式分支输出未运行且未签名。跨版本升级、事务失败回滚以及真实 CAD/账号/支付未覆盖；不能据此宣布全部治理完成。未 Build APP、替换 release 或部署后台。

复测方法与完整边界见 docs/architecture/inno-installer-acceptance.md；最终证据位于 D:\DWGC2E\artifacts\inno-acceptance-20260916-082013\final\results.json。
## 2026-09-16 默认安装目录与 CAD 续验
最终默认 D:\DWGC2E，没有 D 盘时 C:\DWGC2E；Inno 原地升级保持旧路径。便携 21/21、Inno 40/40、新包校验通过。CAD 已启动但控制接口仍异常，未完成回写验收。详见 docs/architecture/install-directory-followup.md。

## CAD 受限宿主验收更新（2026-09-16）
用户手动执行插件命令后，PING成功；合成测试文字回写6/6、内容6/6；详细审计可用范围6/6、新增文字重叠0。原边界限制测试仍为0/6，不抹去；与利用周边空间策略存在规则差异。单行最小字高3.5→1.4，视觉可读性待验收。未覆盖真实云翻译、APP全链路、自动加载或复杂生产图纸。证据：D:\DWGC2E\artifacts\install-default-20260916-082921\cad-host-20260916-085409\status.md 及 layout\audit-request.txt.report。

## CAD 视觉验收更新（2026-09-16）
14份PDF成功导出和渲染，DWG哈希未变，7组对照已检查。英文完整可见，但原图3处单行中文显示问号、2处译文字高缩至40%、倾斜多行逐词换行。视觉验收保持待确认，不因几何6/6而全部关闭。完整记录与对照：D:\DWGC2E\artifacts\install-default-20260916-082921\cad-host-20260916-085409\visual\visual-review.md、index.html。本轮未改业务或release。

## 用户决策及运行路径审计（2026-09-16）

用户明确要求保留现有回写算法，后续再考虑调整。因此此前视觉记录保留作为观察证据，排版/字高算法修改从本轮治理阻塞项中移出；这不将原始包围框0/6改记为通过，也不代表已完成复杂生产图纸验收。

本轮仅增加 RuntimeResourcePathTests 五项回归测试，未修改 APP/Core/CAD 业务源码、未构建 APP、未替换 release、未执行真实支付或生产接口请求。

- Core 全套测试：150/150通过，0失败、0跳过。新增覆盖插件优先使用 CadPlugin 子目录、兼容同目录插件、用户提示词优先、内置文件回退、缺失提示词安全兜底。插件夹具是从未加载的文本文件，不冒充宿主加载测试。
- 初次新增测试出现2项路径分隔符断言失败，已修正测试夹具为规范绝对路径；业务代码未修改。最终编译无新增平台警告。
- release 发布文件校验：RELEASE_PACKAGE=OK、CAD_PLUGIN_PACKAGE=OK。不是本轮重新发布，也不是完整启动验收。
- APP 默认数据根仍为 %APPDATA%\DwgTranslator（可由 DWGC2E_DATA_DIR 显式覆盖）；日志、设置、账号任务与缓存使用数据目录。安装目录改为 DWGC2E 不迁移既有数据目录，以免丢失用户状态。
- 术语库：账号目录优先，回退程序 assets/default-glossaries。提示词：用户数据目录优先，程序 prompts 次之，最后内置兜底。没有因源码资源集中而更改已发布相对路径。
- CAD 插件定位首先使用程序 CadPlugin，其次兼容同目录；历史开发输出回退保留，不贸然删除。已发布包依赖验证通过，但不等同于 APP→CAD 调度及自动加载全链路通过。
- 更新检查：设置页面调用版本接口，清单请求不携带 Authorization，失败可回退 Worker 版本接口。当前确认的是手动版本检查路径；未证明自动检查开关触发后台检查，也未实现或验证自动下载替换。此次不新增更新功能，不将静态检查记为联网成功。

证据：D:/DWGC2E/artifacts/install-default-20260916-082921/path-contract-audit/（core-path-final.trx、core-path-final.log、release-verification.log、release-hash.json）。

剩余验收与决策项：APP→CAD全链路和自动加载；真实账号/会员/更新联网验收（不进行真实支付）；跨版本安装及故障恢复；便携自动卸载安全实现；历史未知文件逐项确认。此前执行策略拒绝的安装事务回滚操作不绕过重试。当前不宣布全部工程治理完成。

本轮检测到 release EXE SHA256 为 9CAA33DF63353DFE6E772D9CE490899A67BCBCB9BEB6E694C9E4DFE69F3009A5，与早先080934基线记录不同。本轮未写入release，具体替换来源尚未核实；因此上述包校验仅针对当前磁盘版本，不把早先基线的安装/启动验收自动套用到当前版本，也不进行回滚覆盖。

## CAD集成审计补充（2026-09-16）
隔离安装/修复/卸载回归扩充并按实际检查数量统计：13/13通过，补充保留非托管文件与原始备份检查。真实CAD只读检查发现Support/DwgTranslator中的Cad、Core DLL与当前release不一致，平台和自动加载配置存在，但Ready=false。未向运行中的CAD替换DLL；需保存关闭CAD后更新安装副本并在新会话验收。此前手动NETLOAD成功不等于自动加载副本已更新。本轮没有修改算法、业务代码或release。证据：D:/DWGC2E/artifacts/install-default-20260916-082921/cad-integration-audit/report.md及plugin-hashes.json。

## 已关闭CAD后的插件同步（2026-09-16）
用户确认已关闭后，进程检查未发现gcad/acad。先将真实安装的三件套及acaddoc.lsp备份到cad-integration-audit/installed-plugin-before-sync，再调用现有CadPluginInstaller.Install同步release插件。安装成功、Inspect Ready=true，三件套安装后哈希全部与release一致，acaddoc.lsp文本未变化。未修改算法或任何业务源码，未重建APP、未更换release。

自动加载运行验证仍未通过：启动独立CAD批处理会话，脚本仅DWGTRANSLATORPING及退出，不显式NETLOAD、不打开用户图纸。70秒观察内未产生新ping，进程仍存活且没有可选的CAD窗口；旧08:53:38标记没有被当作新成功证据。未降低安全设置、未强杀进程。需用户可见的正常CAD新会话确认启动状态并执行DWGTRANSLATORPING，不能把Ready=true等同于宿主实际加载成功。启动/观察/正常关闭尝试记录见autoload-launch.json、autoload-result.json、test-process-close.json。

## 未知命令反馈及启动入口修复（2026-09-16）
用户在新会话输入DWGTRANSLATORPING提示未知命令，实际自动加载验收失败，不以文件就绪覆盖此结果。检查本机浩辰Support/gcad2024doc.lsp，内含LoadGcadLsp，明确通过findfile/load执行gcad.lsp；原安装器统一写acaddoc.lsp。最终改为发现gcad.exe时写gcad.lsp，AutoCAD仍用acaddoc.lsp。旧文件不自动删除。首轮gcaddoc.lsp方案已被本机可验证的gcad.lsp入口替代，不作为最终交付。

正式隔离安装测试新增平台入口及旧文件保留：16/16；Core164/164；布局21/21；便携安装21/21；UI_SMOKE=PASS（模拟API）。UI冒烟测试已有CS8602警告，未趁机修改无关UI代码。按正式Publish-Desktop工作流发布并更新D:/DWGC2E/release，最终版本2.1.1+ui.20260916-095905.a3f6f1f，SHA256=52DE397B96AE79B447ACC19B4C7BABA105CBA7F1D48E834633B057D5E5403C23。已校验安装EXE哈希、保留便携数据和有界回滚备份。未修改回写/布局算法。

真实CAD当前打开，未覆盖运行中DLL；仅将此前已校验的本程序自动加载段写入Support/gcad.lsp，保留已有内容和原acaddoc.lsp。未修改安全变量。新会话的自动加载仍待用户重启后执行DWGTRANSLATORPING确认。此前隐藏测试进程27852未强杀，仍需关注；当前用户图纸进程未关闭。

证据：cad-integration-audit/autoload-fix-final-publish.log、startup-entry-fix.json、gcad-after-fix.lsp、post-fix-plugin-comparison.json。全链路回归不宣布完成。

## 最终同步（2026-09-16）
用户确已关闭交互CAD；残留的是本任务启动的隐藏测试进程27852。核对启动时间、完整命令行、脚本路径、无窗口后，仅结束该自有测试进程（此前正常关闭失败），未结束用户图纸进程。再次确认无CAD进程后，调用修正后的安装器同步当前release三件套并更新gcad.lsp。Success=true、Ready=true，三件套SHA256全部一致，证据sync-final-installed.log、final-installed-hashes.json。无重建或release替换。新会话实际自动加载仍待DWGTRANSLATORPING确认；不再启动隐藏CAD测试会话。

## 自动加载宿主验收通过（2026-09-16 10:08，中国标准时间）
用户按要求重新打开浩辰、不手动NETLOAD、执行DWGTRANSLATORPING后反馈“OK了”。本地同时读取到新的成功标记：status=ok|platform=GstarCAD|runtime=v4.0.30319|utc=2026-09-16T02:08:04.4760994Z。标记文件时间晚于最终插件同步日志，已复制至cad-integration-audit/ping-confirmed-final.txt，不复用早先成功标记。

本机浩辰CAD2024的插件自动加载验收关闭为通过。范围仅为本机新会话插件加载和健康命令；不等同于APP→CAD完整翻译调度、真实云翻译或其他CAD版本均通过。回写算法未改动。用户无须继续重复关闭CAD。

## 当前盘点补充（2026-09-16）
原始盘点证据目录当前已不存在。已使用只读工具tools/Get-ProjectInventory.ps1生成当前快照：artifacts/install-default-20260916-082921/remaining-acceptance/current-inventory。共8691个文件、56个显式工程项；5条ProjectReference目标存在。11项自动未知分类已人工复核：后端10项KEEP，根零字节settings.json仍待确认。该快照不是移动前原始证据，不能据此重建所有历史移动判断。详细当前状态见docs/architecture/governance-current-status.md。

## 内嵌 CAD 插件旧副本（2026-09-16）

REVIEW REQUIRED：`src/DwgTranslator.App/Embedded/CadPlugin/DwgTranslator.Cad.dll` 和同目录 `cad-platform.txt`。

APP 工程改为从本次 CAD 构建输出嵌入完整私有依赖，不再引用这两个手工副本。旧 DLL 不自动删除：保留作为历史版本溯源材料，后续结合跨任务 Git 审核确认归档/删除。正常发布的 `release/CadPlugin` 布局及自定义插件路径优先规则不变。本变更不涉及 CAD 图纸回写算法。

---

# 本轮获准清理执行结果（2026-09-16，22:22 CST）

本节是用户确认“开始吧”之后的实际执行记录，更新前面的待执行状态；历史章节不代表本轮范围。范围仅为获准 SAFE 缓存、正式交付门禁及本任务证据归类，不是业务重构或全仓库无用代码清零。

## 1. 清理前发现与执行保护

初始只读盘点 17136 文件，详细分类见 artifacts/cleanup-audit-20260916-confirmation/files.csv、deletion-classification.csv 和 scope.json。源码/测试/资源/工具原已基本集中，不为目录美观重复迁移。旧 UI、CAD DLL、数据库状态和历史任务资料不能仅按名称删除。

执行前检测到另一个发布流程占用 .desktop-publish.lock，等待其完成后才获取锁清理；未结束任何他人进程。该流程先将 release 更新至 2.1.1+ui.20260916-221152.a3f6f1f，本轮以它作为替换前安装版。没有运行中的 release APP，未强杀或关闭用户 CAD。

逐项检查批准名单、Git 跟踪状态、绝对路径边界、每级重解析点和文件大小。没有撤销此前未提交工作，没有修改 UI、业务源码、支付/会员/CAD 算法、项目路径或 .gitignore。

## 2. 删除文件与原因

- 删除 1022 个未跟踪 obj 编译缓存，共 15002595 字节（14.31 MiB），不是删除 DLL、源码、JSON 或整个 bin/obj。
- 逐文件路径、删除前 SHA256、字节数及原因：artifacts/cleanup-audit-20260916-confirmation/deletion-results.csv。这是本报告的完整删除明细附件；其中 Status=Deleted 才是实际删除。
- 1 个缓存大小已改变，未删除：src/DwgTranslator.Core/obj/Release/net8.0/DwgTranslator.Core.assets.cache。
- 正常构建后上述缓存中的 40 个已再生（核对时点）；这是预期行为，不反复删除已验证构建的产物。本轮删除字节数不是最终磁盘净节省量。
- 正式发布的既有有界保留流程另清理 3 个过期交付对象，共 223333996 字节：artifacts/publish-20260916-221152、artifacts/release-backup-20260916-221152、artifacts/DwgTranslator-win-x64-GstarCAD-20260916-221152.zip。原因：新候选/包已验证安装，原安装版已进入新的唯一回滚目录；执行既有用户数据保护检查，不泛化清理 artifacts。

## 3. 移动文件

仅归类本轮安装回归生成的唯一隔离夹具：

artifacts/installer-test-00407eabb1c54d6b932124580cb791be
→ artifacts/cleanup-audit-20260916-confirmation/installer-regression

依据本轮日志中的唯一 FIXTURE 路径确认归属，测试退出后检查绝对边界及无重解析点再移动。夹具内测试清单的旧绝对路径是历史证据，不宣称移动后可原样重跑。

没有移动正式代码、资源、用户数据或其他任务文件。五页面截图复制到本任务 screenshots 目录，不移动共享历史截图目录。

## 4. 保留的危险文件与 REVIEW REQUIRED

- release：正式本地运行版；便携 settings.json、glossaries/mechanical_zh_en.json、prompts/deepl_context.txt 与清理执行前哈希相同，详见 portable-data-verification.csv。
- CAD 源码、SDK 引用、生成插件和私有依赖：嵌入、复制、发布及安装输入，必须保留。
- src/DwgTranslator.App/Embedded/CadPlugin/DwgTranslator.Cad.dll 和 cad-platform.txt：旧副本仍待历史用途确认；未删除。
- 根零字节 settings.json、%SystemDrive% 下数据库、.wrangler/cf-worker/.wrangler 及数据库 WAL/SHM：待审核，不作为构建垃圾。
- 其他任务 artifacts、release-next-*、其他 installer-test-*、恢复备份及唯一便携数据：未通用删除或搬迁。
- assets/glossaries/mechanical_zh_en.json、icons、prompts、Installer 及正式发布工具：保留；包内默认/可编辑术语副本有独立路径契约，不按哈希相同去重。
- cf-worker/node_modules 和后端资料不属于本轮 APP 清理范围。
- 本轮未证明全部旧源码无反射/动态资源引用，未执行全量密钥内容审计，因此不宣称“旧 UI 全删”“无秘密”或“所有未知文件均解决”。

## 5. 最终目录结构（主要层级）

```text
D:\DWGC2E\
├─ src\
│  ├─ DwgTranslator.App\
│  ├─ DwgTranslator.Core\
│  └─ DwgTranslator.Cad\
├─ tests\                 单测、UI、CAD、布局、恢复与构建验证
├─ assets\                icons、glossaries、prompts
├─ tools\                 正式构建、发布、维护入口
├─ docs\
├─ installer\
├─ cf-worker\             本轮未整理后端
├─ release\               当前正式安装版
├─ artifacts\
│  ├─ cleanup-audit-20260916-confirmation\
│  ├─ publish-20260916-221624\
│  ├─ DwgTranslator-win-x64-GstarCAD-20260916-221624.zip
│  ├─ release-backup-20260916-221624\
│  └─ 其他受保护任务证据及待审核文件
├─ .wrangler\             待审核
├─ %SystemDrive%\         待审核
├─ DwgTranslator.sln、Directory.Build.props
├─ dev.bat、publish.bat、.gitignore、AGENTS.md
├─ README.md、cleanup-report.md
└─ settings.json.example、settings.json（待审核）
```

## 6. Build 与回归验证

首次尝试因 PATH 默认 dotnet 无 SDK 而未能启动 Clean；未影响 release。仅对子进程前置 C:/Users/GQL/AppData/Local/Microsoft/dotnet，并设置该进程 DOTNET_ROOT，使用已安装 SDK 8.0.423 重试成功，未修改系统配置。

实际正式入口：tools/Publish-Desktop.ps1 -Clean；未传 BuildOnly 或 SkipTests。

| 门禁 | 本轮结果 |
|---|---|
| Clean | PASS |
| Restore | PASS |
| Build | PASS；UI 测试源码有既有 CS8602 警告，未顺手改无关代码 |
| Tests | PASS（已执行测试）；Core 379 通过、1 跳过、0 失败 |
| 安装数据保留回归 | PASS，65 项 |
| 任务进程中断恢复 | PASS |
| 图纸布局回归 | PASS |
| CAD 部署隔离回归 | PASS；不是宿主实际加载 |
| WPF UI 冒烟 | PASS；隔离数据、模拟 API |
| Publish | PASS；release 已替换并校验 SHA256 |
| 候选插件/资源/架构审核 | PASS；内嵌插件与发布负载一致 |
| 可执行图标、Shell 通知回归 | PASS |
| .gitignore 边界 | PASS，27 项 |
| 工程/资源结构求值 | PASS，46 项；该附加验证不运行构建 |

唯一跳过的 Core 测试：TaskTemporaryFileTests.CleanupDoesNotFollowSymbolicLinkCandidates，现有 Skip 声明为 Windows 缺少符号链接权限；不计入通过。

APP 基础验证范围：
- WPF 窗口启动、图纸翻译/批量任务/术语库/会员中心/设置五页面渲染：UI 冒烟通过。已查看本轮图纸翻译与设置截图；其中登录清理提示来自测试故障注入，不是读取真实用户状态。
- 术语资源加载、设置读写、队列/导入/输出相关行为：自动化覆盖通过；没有使用用户真实图纸完成在线翻译，未作手工 DWG/DXF 文件选择器完整验收。
- 账号与会员：仅模拟 API 场景通过；真实登录、会员权益、付款未验证，未进行真实支付。
- 插件源定位、内嵌依赖、发布负载：自动化验证通过。CAD 实际插件加载未验证（本轮）；未在真实 CAD 中翻译或执行健康命令。
- 日志/输出：本轮测试日志与输出使用隔离测试数据；没有用真实用户工作区做启动→翻译→退出的源码污染全链路验收。
- 五页面截图是测试宿主截图，不伪称最终单文件 EXE 的人工启动截图；本轮未另行人工启动最终安装 EXE。

## 7. 本地交付与有限保留

版本：2.1.1+ui.20260916-221624.a3f6f1f
安装程序：D:/DWGC2E/release/DwgTranslator.exe
SHA256：2BC4DF72F90D3FF576C027C09A2E5E084A8F4B1A10469A36B127284063CF67DA

本轮独立再次核对安装 EXE 与 build-info.json 一致，便携配置/术语/提示词三项哈希均保持。

最新候选：artifacts/publish-20260916-221624
最新安装包：artifacts/DwgTranslator-win-x64-GstarCAD-20260916-221624.zip
唯一回滚：artifacts/release-backup-20260916-221624（上一安装版 221152）

未上传网站下载、未开启购买、未进行真实支付、未创建 relaese。

## 8. 证据与例外

本任务证据统一置于 artifacts/cleanup-audit-20260916-confirmation：
- deletion-results.csv、safe-to-delete.csv、deletion-classification.csv：逐文件清单。
- publish-clean-sdk.log、execution-result.json、gate-summary.txt：最终正式发布及回归记录。
- project-structure-tests.log、gitignore-tests.log：附加结构和忽略边界验证。
- release-before-execution.csv、release-verification.json、portable-data-verification.csv：安装和数据哈希。
- moves.json、installer-regression/：本轮夹具归类证据。
- screenshots/：本轮五页面截图。
- after/：构建后的 metadata 快照（16330 文件，67 显式工程项；在归类本轮夹具前采集，不当作磁盘净差异证明）。

例外：既有 UI 冒烟工具仍写 artifacts/ui-smoke 共享输出及系统 TEMP 隔离数据。本轮没有为清理任务修改测试源码或删除共享旧图；只将本轮五张关键截图复制归类。失败启动日志保留用于 SDK 环境问题溯源；不是另留一个失败 APP 包。其他任务恢复资料保持原位。

结论：获准 SAFE 缓存清理、正式构建/自动化回归、local release 交付已完成；REVIEW REQUIRED 仍明确保留。缓存可再生，故不以“构建后没有 bin/obj”作为目标，也不把当前结果描述为全仓库所有历史杂物已清零。
