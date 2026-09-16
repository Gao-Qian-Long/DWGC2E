# DWGC2E 工程治理当前交付报告

## 最新交付：输出路径安全修复（2026-09-16）

当前release为 **2.1.1+ui.20260916-221152.a3f6f1f**，SHA256 **8DEB8AAC4ACDE3ED0E35F99835EA8E90B2F2009744F779F50C7C5CA3F41A0070**。

已修复：overwrite拒绝批次已占用源/目标路径；重命名编号耗尽返回错误并跳过；备份编号耗尽（包括显式原图覆盖分支）返回错误并跳过。保留原有显式覆盖自身源文件和普通既有输出的配置行为，未修改CAD回写/布局算法。7项新增用例，路径专项18通过；正常发布完成Clean/Restore/Build/Publish，全Core379通过/1跳过/0失败，UI烟测及候选资源审计通过。已更新release，独立核对EXE哈希、settings.json不变及唯一回滚版本。人工验收按用户要求跳过，不计通过。

证据：artifacts/installer-safety-20260916-resumed/output-boundary-repro/publish-safety.log、resolver-safety.trx、installed-safety-verification.json。此项覆盖路径规划，不宣称所有写入竞态、磁盘备份行为或真实CAD已验收；安装事务回滚和其余治理审查仍未完成。


> 2026-09-16 最新执行口径：按用户明确要求，本任务跳过全部人工验收，交由用户后续自行测试；这些项目不再阻塞工程整理收尾，也不计为已通过。清单保留为后续测试交接，不要求用户现在登录、操作CAD或测试支付。自动化回归、UI烟测、构建/发布安全门禁仍必须执行。未实现的开发项（例如安装事务回滚）不属于人工验收豁免，继续单列。

核对日期：2026-09-16。本报告取代旧交付报告中的“当前”版本、数量与验收状态；历史报告保留作过程记录。

**结论：主体结构和多项安全改造已落地，整体任务仍未完成。真实业务验收由用户延期；未实现、未验证不计为通过。** 本轮已完成CAD依赖清单修复和统一APP本地发布；未修改回写算法、未上线网站或进行真实付款。

## 最新复核增量：输出边界与安全提交（2026-09-16）

已核对输出路径规划、批量源文件占用、重名策略、源文件保护和临时文件提交。57项迁移核对通过；OutputPathResolver、SafeFileCommit、实际DWG/DXF提交及批量规划共24项回归通过。后续复核确认测试只覆盖部分策略，overwrite跨源路径冲突及编号耗尽仍有边界，不能宣称全策略源文件保护。未修改图纸回写或布局算法，未运行真实CAD。详见 `artifacts/installer-safety-20260916-resumed/output-boundary-review/report.md`。
## 历史交付：术语本地存储职责分离（2026-09-16）

当时release：**2.1.1+ui.20260916-213643.a3f6f1f**，SHA256 **A7BEA6C1690DE0C9A6B0703D77AF15DD4A6B84B4EDC4639CCE8646E29AB01103**。术语编辑器的文件提交移入Infrastructure/Glossary/GlossaryFileStore，保持原序列化/替换和保存后重载顺序；新增4项隔离测试。完整Clean/Restore/Build/Publish通过，Core372通过/1跳过/0失败、UI与候选审计通过，3份便携文件不变，唯一回滚release-backup-20260916-213643。首次testhost环境失败未替换release，重试完整验证后才交付。详见 artifacts/installer-safety-20260916-resumed/ui-boundary-review/report.md。不是全UI分层或真实业务验收完成。

## 历史交付：配置未知字段保留（2026-09-16，21:22）

当时release：**2.1.1+ui.20260916-211935.a3f6f1f**，SHA256 **74BCA5FF9347EF918D699DDC13C982AEB76B26D238D88FAD8D96A73066F4DD02**。配置迁移、普通保存及清除凭据时保留未知JSON字段；不改变已有端点迁移规则，不改CAD算法。新增4例先失败后通过，配置组40通过；完整Clean/Restore/Build/Publish，Core368通过/1跳过/0失败、UI与候选审计门禁通过。3份便携文件不变，唯一回滚release-backup-20260916-211935。

详见 artifacts/installer-safety-20260916-resumed/configuration-preservation/report.md。真实业务人工验收仍延期，整体任务仍未完成。

## 历史交付：候选资源审计门禁（2026-09-16，20:50）

当时release：**2.1.1+ui.20260916-204621.a3f6f1f**，SHA256 **6AD4931A38E1DAC99DB2AA535E7D09204DC7B4CA66DE8999AE18B3960BCE6114**。发布前强制检查新候选，不再依赖交付后人工运行资源审计。完整Clean/Restore/Build/Publish、Core364通过/1跳过/0失败、UI与既有门禁通过；资源审计36项、审计器8项正反用例通过。3份便携文件不变；唯一回滚release-backup-20260916-204621。

详情见 artifacts/installer-safety-20260916-resumed/publish-architecture-gate/report.md。此前202954隔离启动为历史证据；未作为当前版本真实业务验收。

## 历史交付：购买恢复用例分离（2026-09-16，20:33）

当时release为 **2.1.1+ui.20260916-202954.a3f6f1f**；SHA256 **6BE1B4E24F48D023F4E692427BCB7DB4DA765760E601297A8401B0B32303E0CB**。窗口中的恢复记录保存、复用标识下单、响应会话检查及匹配paid记录清理提取至 Core/Application/Payments/PurchaseCheckoutService，未改变调用顺序、支付通道/API/金额/会员规则。

新增9项服务隔离测试通过；正常Clean完整门禁交付，Core364通过/1跳过/0失败，既有支付恢复UI回归通过。独立哈希、3份便携数据、57项迁移、36项资源审计、32项结构评估通过。唯一回滚为 artifacts/release-backup-20260916-202954。此前201231是历史交付。

未做真实支付或CAD本轮宿主验收，不修改图纸回写算法。窗口仍有列表/会员查询与轮询职责，整体治理未完成。详细范围和证据：artifacts/installer-safety-20260916-resumed/purchase-service/report.md；39项现状见governance-completion-audit.md。

## 当前治理核对入口（2026-09-16，20:20）

39项状态已整理为单一现状表：governance-completion-audit.md。最新盘点13937项/59显式工程条目；10项已核实后端用途纳入规则后仅根settings.json仍UNKNOWN，23项工具回归通过。Git展开371路径仅完成分组，不是全语义审查。manual-acceptance.md已修正过时的“校对保存仅内存”描述。本轮没有APP构建或release替换，版本仍201231。详细证据见 inventory-classification/report.md。

下文交付增量按时间保留；历史“当前”版本和当时未完成项以本节、下一节及39项现状表为准。

## 最新交付增量：插件来源一致性（2026-09-16，20:15）

已修复环境安装页面与翻译对随包/旧同目录/自定义插件的优先级差异，共用一个选择入口；缺失时仍保留各自内嵌释放/开发查找后备，不扩大为全部加载路径已统一。未修改回写算法。

当前release为 **2.1.1+ui.20260916-201231.a3f6f1f**；EXE SHA256 **BB22F6663697586C0CF706CAFB217DE4502CF3C33C01032415DA30B66B634298**。正常Clean/Restore/Build/Publish、Core355通过/1跳过/0失败、受控UI及其余发布门禁通过。3份便携数据未变，57项迁移核对、新版本36项编译资源审计通过。唯一回滚为 artifacts/release-backup-20260916-201231。此前195051为历史交付，不再是当前版本。

证据与准确测试范围：artifacts/installer-safety-20260916-resumed/plugin-source/report.md。真实验收仍由用户延期；其余治理未完成项不因本次交付变为完成。

## 最新复核增量：编译资源与动态依赖（2026-09-16，20:04）

未修改产品源码或重新构建APP，release仍为195051。新独立元数据审计36项通过，工程评估32项通过；3种损坏资源/插件夹具均被审计器拒绝。Core/CAD直接UI依赖、21个XAML编译条目和4个内嵌CAD文件得到更强证据，但不是全部动态行为/传递依赖证明。详见dynamic-dependency-review.md及dependency-audit证据目录。

新发现未解决：环境安装页面优先配置的插件路径，实际翻译优先随包插件，旧配置可能导致二者来源不一致，需统一规则和回归。整体39项仍未完成；此项加入后续清单。其余原有未完成项不被本轮审计替代。

## 最新状态摘要（2026-09-16，19:53会话失效保护交付）

- **整体工程治理仍未全部完成。** 本轮已完成当前进程内旧凭据请求阻断、配置解锁后的凭据清理重试、仅术语/设置未保存时的工作区保护。
- 当前release：`2.1.1+ui.20260916-195051.a3f6f1f`；EXE SHA256 `1639F52E740CA6D4A81FCBF448CB7BDF726ED68952859E35B4040718C991C8EE`，已独立核对。上一版192900校对持久化功能保留。
- 正常Clean/Restore/Build/Publish全部通过：Core347通过/1跳过/0失败，安装65、布局21、CAD安装16、任务中断恢复7场景、受控UI烟测、包和图标验证通过。既有UI烟测CS8602警告保留；不扩大为真实账户/支付/CAD再次验收。
- 3份便携数据哈希未变；唯一回滚`artifacts/release-backup-20260916-195051`。57项迁移再次核验通过，SettingsStore的已审查变更指纹同步更新；不是全Git语义审查完成。
- 请求拒绝与定时清理限本进程；已经在途的请求不被撤回。始终锁定并退出时仍可能遗留磁盘凭据，不承诺跨重启阻断。已写入的新凭据不会被旧清理重试覆盖；不提供跨进程原子比较交换。
- 仍未完成：安装完整事务回滚、历史未知文件安全清理、混合Git全面审查，以及用户延期的真实业务/复杂图纸/视觉验收。未修改CAD回写算法、未上线网站、未进行真实付款。
- 当前证据：`artifacts/installer-safety-20260916-resumed/session-rejection/`（report、publish、installed-verification、source-hashes、moves-final）。以下为各自时点历史摘要，不覆盖本段。

## 历史状态摘要（2026-09-16，19:32校对持久化交付）

- **整体任务仍未全部完成；人工校对显式保存与恢复已实现并交付。** 当前release为`2.1.1+ui.20260916-192900.a3f6f1f`，EXE SHA256 `2FB090C0015978DED8A2C43136C811B25DDE6AFBB1959AE76DDF9B588F68CB59`，与build-info独立核验一致。
- 校对保存进入账号隔离的proofreading.json；提交成功才清dirty，失败保留编辑并阻止确认框继续退出。启动/切回账号重新读取图纸，核对文件哈希及实体身份后恢复译文与状态，几何来自真实图纸；不改变CAD回写/排版算法或TaskManager翻译检查点。
- Core专项16项、UI新增14项断言通过；另有两个独立进程的Core/真实DWG Reader保存恢复探针通过。UI启动测试是同进程新的服务与ViewModel实例，不扩大为完整GUI关闭重开或用户真实验收。
- 本轮正常`Publish-Desktop.ps1 -Clean`完成Clean/Restore/Build/Publish全部门禁：Core337通过/1跳过/0失败、安装65、布局21、CAD安装16、任务中断恢复探针、受控UI烟测和发布包验证通过。既有UI烟测CS8602警告仍存在。
- 3份便携文件与上一版哈希一致；唯一回滚`artifacts/release-backup-20260916-192900`；57条迁移重新核验通过，无未映射的已跟踪删除。不是全工作树语义审查通过。
- 仍未完成：鉴权失效且配置被锁时磁盘token自动清除/完整请求阻断；仅术语或设置dirty场景；安装完整事务回滚、历史未知文件清理、混合Git全面审查，以及用户延期的真实业务/图纸/视觉验收。校对是显式快照，不是未保存编辑自动保护或无限历史。
- 当前证据：`artifacts/installer-safety-20260916-resumed/proofreading-persistence/`（report、publish、source-hashes、installed-verification、moves、process-restart）。设计说明：`docs/architecture/proofreading-persistence.md`。
- 翻译阶段立即落盘与CAD恢复191304证据仍保留，但不能替代192900版本的真实CAD再次验收。下文为各自时点的历史记录，不覆盖本摘要。

## 1. 盘点与问题

最新只读盘点共11861文件，排除.git及两条已登记测试联接。统计是盘点时点，不含本报告新增文件，不能作为可删除数量。证据位于 `artifacts/installer-safety-20260916-resumed/final-review/inventory/`。

|分类|文件数|
|---|---:|
|PROJECT_CONFIGURATION|6|
|DOCUMENTATION|43|
|UNKNOWN|11|
|DEVELOPMENT_OR_BUILD_TOOL|22|
|GENERATED_OR_DEPENDENCY|6796|
|TEST|60|
|SOURCE|191|
|EMBEDDED_RESOURCE|2|
|LOCAL_DELIVERY_OR_USER_DATA|11|
|INSTALLER|5|
|BACKEND_SOURCE_OR_RESOURCE|50|
|LOCAL_STATE_REVIEW|20|
|RESOURCE|3|
|ARTIFACT_OR_EVIDENCE|4641|

主要问题与处置：
- 原Services混放接口、模型、文件系统和CAD部署：按职责归档，保留程序集与稳定namespace。
- 正式资源/测试散落：集中至assets、tests，保留必要兼容入口。
- 构建产物与用户数据混用风险：候选产物统一artifacts；release仍是唯一正式本地运行目录。打包不从release读取用户配置。
- 安装、打包误覆盖风险：增加输入/输出、联接、哈希、运行实例和发布互斥检查；不声称具备任意失败的完整事务回滚。
- UI职责仍混合：购买记录持久化已抽至PendingPurchaseStore；BillingWindow网络/二维码编排、MainViewModel流程仍需逐步处理，不为整理而重写业务。
- 根目录仍有本地状态和未知文件；全工作区混合多个任务，未完成全部语义审查。
- 原始11001项盘点证据现已缺失：不能事后证明最初八阶段完整顺序，当前扫描不冒充初始扫描。

## 2. 当前目录结构

```text
DWGC2E/
├─ src/
│  ├─ DwgTranslator.App/       Views、ViewModels、CAD桥接、内嵌资源
│  ├─ DwgTranslator.Core/      Models、Interfaces、Application、Infrastructure
│  │                           CadIntegration、Drawing及现有共享模块
│  └─ DwgTranslator.Cad/       真正加载入CAD的插件
├─ tests/                     Core、UI、布局、CAD、构建与安装回归
├─ assets/                    icons、glossaries、prompts
├─ tools/                     开发、构建、发布、检查及兼容入口
├─ installer/                 便携/Inno安装源码与说明
├─ docs/                      架构、部署、验收文档
├─ cf-worker/                 独立云端/网站工程
├─ artifacts/                 发布候选、安装包、一个回滚版本、测试证据
├─ release/                   唯一本地运行版本（含需保留的便携数据）
├─ .wrangler/                 保留的本地状态
├─ %SystemDrive%/             待复核历史缓存，不是新目录规范
├─ .gitignore、AGENTS.md、Directory.Build.props、DwgTranslator.sln
├─ README.md、cleanup-report.md、dev.bat、publish.bat
└─ settings.json.example、settings.json（零字节待确认）
```

不创建空的Application等新工程或重复dist/scripts目录。Core仍是多目标共享库，不宣称纯Domain架构。安装默认D:\DWGC2E，无D盘回退C:\DWGC2E；不能安装覆盖当前源码目录。

## 3. 文件去向（完整57项）

固定基线及详细校验见 `project-moves.json`；最新结果 `final-review/moves.json` 全部通过，无未登记的已跟踪删除。52项纯搬迁、3项带已审核内容变化、1项兼容入口、1项资源去重；不是“删除57个文件”。下表路径相对工作区。

|旧路径|新路径|性质|
|---|---|---|
|`src/DwgTranslator.Core/Services/IAutoCadInteropService.cs`|`src/DwgTranslator.Core/Interfaces/IAutoCadInteropService.cs`|move|
|`src/DwgTranslator.Core/Services/IDeepSeekClient.cs`|`src/DwgTranslator.Core/Interfaces/IDeepSeekClient.cs`|move|
|`src/DwgTranslator.Core/Services/IDwgReaderService.cs`|`src/DwgTranslator.Core/Interfaces/IDwgReaderService.cs`|move|
|`src/DwgTranslator.Core/Services/IDwgWriterService.cs`|`src/DwgTranslator.Core/Interfaces/IDwgWriterService.cs`|move|
|`src/DwgTranslator.Core/Services/IDxfReaderService.cs`|`src/DwgTranslator.Core/Interfaces/IDxfReaderService.cs`|move|
|`src/DwgTranslator.Core/Services/IDxfWriterService.cs`|`src/DwgTranslator.Core/Interfaces/IDxfWriterService.cs`|move|
|`src/DwgTranslator.Core/Services/IExcelService.cs`|`src/DwgTranslator.Core/Interfaces/IExcelService.cs`|move|
|`src/DwgTranslator.Core/Services/IGlossaryService.cs`|`src/DwgTranslator.Core/Interfaces/IGlossaryService.cs`|move|
|`src/DwgTranslator.Core/Services/ILicenseService.cs`|`src/DwgTranslator.Core/Interfaces/ILicenseService.cs`|move|
|`src/DwgTranslator.Core/Services/ILocalizationService.cs`|`src/DwgTranslator.Core/Interfaces/ILocalizationService.cs`|move|
|`src/DwgTranslator.Core/Services/ITranslationService.cs`|`src/DwgTranslator.Core/Interfaces/ITranslationService.cs`|move|
|`src/DwgTranslator.Core/Services/AutoCadDetector.cs`|`src/DwgTranslator.Core/CadIntegration/Detection/AutoCadDetector.cs`|move|
|`src/DwgTranslator.Core/Services/CadPluginInstaller.cs`|`src/DwgTranslator.Core/CadIntegration/Deployment/CadPluginInstaller.cs`|move-with-reviewed-change|
|`src/DwgTranslator.Core/Services/BatchExportPlanner.cs`|`src/DwgTranslator.Core/Application/Files/BatchExportPlanner.cs`|move|
|`src/DwgTranslator.Core/Services/GlossaryConflictDetector.cs`|`src/DwgTranslator.Core/Application/Glossary/GlossaryConflictDetector.cs`|move|
|`src/DwgTranslator.Core/Services/GlossaryService.cs`|`src/DwgTranslator.Core/Application/Glossary/GlossaryService.cs`|move|
|`src/DwgTranslator.Core/Services/SettingsBackedDeepSeekClient.cs`|`src/DwgTranslator.Core/Application/Translation/SettingsBackedDeepSeekClient.cs`|move|
|`src/DwgTranslator.Core/Services/TranslationFilter.cs`|`src/DwgTranslator.Core/Application/Translation/TranslationFilter.cs`|move|
|`src/DwgTranslator.Core/Services/TranslationPrompt.cs`|`src/DwgTranslator.Core/Application/Translation/TranslationPrompt.cs`|move|
|`src/DwgTranslator.Core/Services/TranslationQualityValidator.cs`|`src/DwgTranslator.Core/Application/Translation/TranslationQualityValidator.cs`|move|
|`src/DwgTranslator.Core/Services/TranslationService.cs`|`src/DwgTranslator.Core/Application/Translation/TranslationService.cs`|move|
|`src/DwgTranslator.Core/Services/WorkerTranslationService.cs`|`src/DwgTranslator.Core/Application/Translation/WorkerTranslationService.cs`|move|
|`src/DwgTranslator.Core/Services/AccountWorkspace.cs`|`src/DwgTranslator.Core/Infrastructure/Accounts/AccountWorkspace.cs`|move|
|`src/DwgTranslator.Core/Services/InstallationIdentityStore.cs`|`src/DwgTranslator.Core/Infrastructure/Accounts/InstallationIdentityStore.cs`|move|
|`src/DwgTranslator.Core/Services/MachineIdentifier.cs`|`src/DwgTranslator.Core/Infrastructure/Accounts/MachineIdentifier.cs`|move|
|`src/DwgTranslator.Core/Services/LicenseService.cs`|`src/DwgTranslator.Core/Infrastructure/Accounts/LicenseService.cs`|move|
|`src/DwgTranslator.Core/Services/SettingsStore.cs`|`src/DwgTranslator.Core/Infrastructure/Configuration/SettingsStore.cs`|move-with-reviewed-change|
|`src/DwgTranslator.Core/Services/LocalizationService.cs`|`src/DwgTranslator.Core/Infrastructure/Localization/LocalizationService.cs`|move|
|`src/DwgTranslator.Core/Services/DeepSeekClient.cs`|`src/DwgTranslator.Core/Infrastructure/Api/DeepSeekClient.cs`|move|
|`src/DwgTranslator.Core/Services/DeepSeekClientFactory.cs`|`src/DwgTranslator.Core/Infrastructure/Api/DeepSeekClientFactory.cs`|move|
|`src/DwgTranslator.Core/Services/DwgReaderService.cs`|`src/DwgTranslator.Core/Infrastructure/Files/DwgReaderService.cs`|move|
|`src/DwgTranslator.Core/Services/DwgWriterService.cs`|`src/DwgTranslator.Core/Infrastructure/Files/DwgWriterService.cs`|move|
|`src/DwgTranslator.Core/Services/ExcelService.cs`|`src/DwgTranslator.Core/Infrastructure/Files/ExcelService.cs`|move|
|`src/DwgTranslator.Core/Services/OutputPathResolver.cs`|`src/DwgTranslator.Core/Infrastructure/Files/OutputPathResolver.cs`|move|
|`src/DwgTranslator.Core/Services/SafeFileCommit.cs`|`src/DwgTranslator.Core/Infrastructure/Files/SafeFileCommit.cs`|move|
|`src/DwgTranslator.Core/Services/CadGeometryHelper.cs`|`src/DwgTranslator.Core/Drawing/Layout/CadGeometryHelper.cs`|move|
|`src/DwgTranslator.Core/Services/CadLabelCompactor.cs`|`src/DwgTranslator.Core/Drawing/Layout/CadLabelCompactor.cs`|move|
|`src/DwgTranslator.Core/Services/DwgBoundsEstimator.cs`|`src/DwgTranslator.Core/Drawing/Layout/DwgBoundsEstimator.cs`|move|
|`src/DwgTranslator.Core/Services/DwgCollisionDetector.cs`|`src/DwgTranslator.Core/Drawing/Layout/DwgCollisionDetector.cs`|move|
|`src/DwgTranslator.Core/Services/DwgFontManager.cs`|`src/DwgTranslator.Core/Drawing/Layout/DwgFontManager.cs`|move|
|`src/DwgTranslator.Core/Services/DwgFrameDetector.cs`|`src/DwgTranslator.Core/Drawing/Layout/DwgFrameDetector.cs`|move|
|`src/DwgTranslator.Core/Services/DwgTextReplacer.cs`|`src/DwgTranslator.Core/Drawing/Layout/DwgTextReplacer.cs`|move|
|`src/DwgTranslator.Core/Services/DwgTextScaler.cs`|`src/DwgTranslator.Core/Drawing/Layout/DwgTextScaler.cs`|move|
|`src/DwgTranslator.Core/Services/FontMapper.cs`|`src/DwgTranslator.Core/Drawing/Layout/FontMapper.cs`|move|
|`src/DwgTranslator.Core/Services/TextEnvelopeGeometry.cs`|`src/DwgTranslator.Core/Drawing/Layout/TextEnvelopeGeometry.cs`|move|
|`src/DwgTranslator.Core/Services/TextWidthEstimator.cs`|`src/DwgTranslator.Core/Drawing/Layout/TextWidthEstimator.cs`|move|
|`src/DwgTranslator.Core/Services/VerticalTextLayout.cs`|`src/DwgTranslator.Core/Drawing/Layout/VerticalTextLayout.cs`|move|
|`src/DwgTranslator.App/Services/AutoCadInteropService.cs`|`src/DwgTranslator.App/CadIntegration/AutoCadInteropService.cs`|move|
|`glossaries/mechanical_zh_en.json`|`assets/glossaries/mechanical_zh_en.json`|move|
|`prompts/deepl_context.txt`|`assets/prompts/deepl_context.txt`|move|
|`assets/icon.ico`|`assets/icons/icon.ico`|move|
|`tools/LayoutRegression/Program.cs`|`tests/DwgTranslator.LayoutRegression/Program.cs`|move|
|`tools/LayoutRegression/LayoutRegression.csproj`|`tests/DwgTranslator.LayoutRegression/LayoutRegression.csproj`|move|
|`tools/tests/Test-CadPluginInstaller.fsx`|`tests/CadIntegration/Test-CadPluginInstaller.fsx`|move-with-reviewed-change|
|`CODE_REVIEW.md`|`docs/architecture/legacy-code-review.md`|move|
|`tools/Test-DesktopArtifactCleanup.ps1`|`tests/BuildPipeline/Test-DesktopArtifactCleanup.ps1`|compatibility-entry|
|`src/DwgTranslator.App/icon.ico`|`assets/icons/icon.ico`|deduplicate|

## 4. 删除与保留

- 确认的非生成资源删除：旧 `src/DwgTranslator.App/icon.ico` 重复副本，字节一致性由移动校验确认；集中资源保留。
- 源文件旧路径消失属于搬迁，不能计为死代码清理。
- bin/obj由Clean流程管理；过期发布候选由现有有界清理器管理。最新发布保留一份回滚。不补造历史逐文件删除清单，具体以相应发布清理日志为准。
- 本次报告整理没有删除任何源文件、未知文件、测试夹具或恢复证据。
- UNKNOWN的11项中，10项已人工判为KEEP：cf-worker下CF_BACKEND_CONTRACT.md、ops/RELEASE_20260915.md、ops/retention-preview.sql、package.json、package-lock.json、schema.sql、tools/schema-preflight.mjs、tools/verify-remote-schema.mjs、upgrades/0003_payments_from_legacy.sql、wrangler.toml。这是说明、依赖、部署/迁移和验证工具，不能按未分类删除；未执行迁移或部署。
- 剩余未知为根settings.json（零字节），保留。历史授权工具、旧内嵌插件DLL、动态宿主依赖、.wrangler、本地缓存、截图和其他任务证据均按cleanup-report.md分级保留；“未证明无用”不是删除理由。

## 5. 引用与职责调整

- csproj资源链接/复制路径、Core net48显式Compile、测试程序集定位及兼容入口随搬迁调整；运行时插件目录契约保持。
- 工程评估覆盖7工程、Core双目标共8份评估；实际6条ProjectReference目标存在。盘点的57个显式XML条目不是57条ProjectReference。评估范围与时间点见architecture-audit，源文件数量不冒充本次最新数量。
- Core无直接WPF/WinForms依赖，生产工程不反向引用tests；不据此宣称所有动态反射已证明。
- CAD内嵌载荷取当前构建输出并验证哈希；DLL/私有依赖仍按发布契约保留。GstarCAD自动加载入口修正属于既有功能修复，不是纯移动；未改图纸回写算法。
- PendingPurchase模型/存储独立，原账号哈希、锁、JSON形状、异常处理和原子替换语义保持；受控11项存储测试及UI持久化门禁有证据。
- 默认配置/日志/账号工作区进入AppData/DwgTranslator；支付记录进入LocalAppData/DwgTranslator/payments，保留既有隔离数据目录覆盖机制。不为品牌名强迁移现有用户数据。
- 打包仅接受当前工作区的干净publish候选，输出限artifacts；拒绝release输入、额外个人资源、联接和覆盖既有包。直接手工ISCC尚不受同一输入守卫强制约束。

## 6. 构建及自动验证

当前实际release版本：**2.1.1+ui.20260916-160406.a3f6f1f**。
EXE SHA256：`26174377BADD5686D96A8C5F62BA3437A000E9564419BD7693D2CEE79D601634`，本次重新核对与build-info一致。

|项目|结果与范围|
|---|---|
|完整Clean / Restore / Build|135439轮通过，解决方案6工程；证据clean-delivery/publish.log。这不是160406版本重新Clean的证明。|
|最新常规Build / Publish|160406轮成功且更新release；日志artifacts/cross-end-completion-20260916/app-process-recovery/publish-final.log。|
|最新Core Test|259通过、1跳过、260总计；符号链接测试因主机权限跳过。|
|最新发布门禁|安装65、布局21、CAD部署16、任务中断检查、UI_SMOKE通过。|
|Inno|135439轮普通/隔离编译及45项安装修复卸载检查通过；不冒充160406安装包已重新做同一套验证。|
|购买记录存储|新增11项通过，交付版本已包含；不访问真实付款。|
|最新打包路径专项|24项通过，package-literal-paths/verified-final/results.json；不构建APP。|
|交付数据|settings、默认用户词库、提示词3份便携文件哈希不变；一个release-backup-20260916-160406。|
|警告|已有UiSmoke Program.cs:270 CS8602未消除；不能写零警告。|

除完整路径注明的跨任务日志外，本节证据均相对 `artifacts/installer-safety-20260916-resumed/`。各测试覆盖不同范围，不相加宣传为业务验收总数。

## 7. 业务回归状态

|业务|自动/已有证据|实际验收|
|---|---|---|
|启动、页面、购买窗口|受控UI smoke通过|真实用户环境整体验收延期|
|登录、会员、设备|接口/隔离环境门禁不等于真实账号验证|待用户验收|
|图纸添加、术语库、默认JSON|自动测试及发布资源检查|真实图纸和用户术语待验收|
|CAD检测、DLL定位、自动加载|部署检查；此前用户确认GstarCAD2024加载成功|完整宿主翻译仍待验收|
|翻译任务、输出、日志|受控任务/文件路径测试|真实图纸全流程、输出效果待验收|
|设置读取保存|配置路径/受控UI证据|用户设置重启保留待验收|
|更新|发布/路径契约检查|实际更新流程待验收|
|订单/支付|隔离持久化与UI检查|未执行真实付款，不列为自动任务|

## 8. 尚未完成及下一步

1. 便携安装任意中途失败的完整事务回滚尚未实现；此前实施被执行策略拒绝，不换工具绕过。
2. 历史夹具/缓存递归收尾尚未做；正常发布有界清理已经工作，不应将两者混为“全部清理被阻止”。保留恢复证据、其他任务内容及未知文件。
3. 跨任务完整Git语义审查未完成。当前diff --check仍有7处空白问题（后端、两页XAML和两份测试）；未擅自格式化其他任务文件，未暂存或提交。
4. UI仍有业务编排；若继续拆分，应选有可验证等价性的边界，不大改支付流程和回写算法。
5. 原始盘点缺失、跨版本安装和全部桌面重定向/竞态场景没有完备证据，不追认通过。
6. 用户延期的真实业务验收见manual-acceptance.md。当前不需要用户重新操作CAD来完成文档收尾。

原始39项逐条状态见governance-completion-audit.md；本报告不把“多数可自动检查完成”改写成“全部完成”。

## 后续增量：方括号安装包路径（2026-09-16）

打包审查复现并修复方括号目录被解释为通配符的问题；实际24项正式回归通过，另验证ZIP全部13文件字节哈希。详细证据：artifacts/installer-safety-20260916-resumed/package-literal-paths/report.md。此记录替代上文21项打包专项的最新数量；上文其他测试和版本范围不变。未重新构建APP或修改release。

## 当前交付再核对（160406）
本轮读取实际release元数据并校验EXE，3份便携文件与唯一回滚副本哈希一致；57项搬迁重新验证通过，无未映射的已跟踪删除。证据：artifacts/installer-safety-20260916-resumed/delivery-160406/。此轮未构建APP；160406来自另一任务，完整Clean和Inno仍仅引用各自历史版本。

后续局部检查：云端244项通过、API配置32项通过、发布验证器4项通过、本地清单11项通过，分别见cloud-review、config-review、release-verifier、manifest-paths证据目录。它们不能替代完整业务验收。

## 最新增量核验：订单契约与后续交付（2026-09-16 16:34）

本节更新上文历史“当前”版本范围：本轮修复桌面合法订单状态白名单遗漏 cancelled/refunded，新增12例测试（详情/混合列表可读，非pending付款许可拒绝）。并非纯目录变更，不改回写算法或云端支付规则。修复前27通过/2失败，修复后29全部通过。

本轮Publish-Desktop.ps1 -Clean完整成功：162914，Core271通过/1跳过，安装65、进程中断7、布局21、CAD部署16、受控UI smoke通过；Clean/Restore/Build/Publish均通过。未重新执行Inno专项。

随后另一任务交付163249，本次读取release/build-info.json与EXE校验，当前版本2.1.1+ui.20260916-163249.a3f6f1f，SHA256 B94C384E53C1742D3C29C7628D2C244CD925F2AF4C8477CCCF3C3C17CCD45C4D。3份便携文件与本轮发布前哈希一致，只保留一个rollback。当前版本来自cad-runtime/reader-fix-publish.log；本轮全Clean证据不可冒充163249的Clean证据。

详细记录：artifacts/installer-safety-20260916-resumed/billing-review/report.md、publish.log、delivery-verification.json。另一任务真实CAD流水线日志仍有“Real CAD interop path not proven”，不据此声称宿主全链路验收通过。订单终态文案仍待后续处理，整体治理和真实业务验收未全部完成。

## 截图辅助工具安全修复

两份旧截图工具默认强制终止APP，已改为发现已有实例就拒绝默认启动，显式NoLaunch才复用；不再强杀。新增模拟进程回归10项通过（Windows PowerShell 5.1），用途及操作风险见docs/deployment/capture-tools.md，证据见artifacts/installer-safety-20260916-resumed/capture-tool-review/。本轮未构建APP、未操作真实窗口，不更新release版本声明。

## 盘点脚本输出路径修复

复现并修复Get-ProjectInventory相对输出路径使用进程工作目录的问题，避免Push-Location后报告写错位置。Windows PowerShell 5.1合成仓库4项回归通过，保留修复前失败证据；见artifacts/installer-safety-20260916-resumed/inventory-path-review/report.md。只改工具路径解析，不构建APP、不替换release、不重新定义实际盘点数量。

## 2026-09-16：更新入口与全Core增量复核
更新入口7项模拟HTTP测试通过，覆盖匿名清单、API回退、自定义路径、取消和无配置；保留空对象不回退等现有语义边界。随后全Core回归288通过/1失败/1跳过，新增流水线测试在OutputPath非空断言失败，单独复跑亦失败；已向相关任务同步，未改算法或掩盖测试。详情 artifacts/installer-safety-20260916-resumed/update-review/report.md。实际release仍163249且EXE哈希匹配，本轮未构建APP或替换release。此前发布成功不代表当前工作树全测试通过。

## CAD私有依赖清单修复已入源码，待统一发布
已补生成清单、依赖安装/自检、哈希保护卸载及发布包清单验证；说明见docs/deployment/cad-payload-manifest.md。专项19+16+4+7项通过，最新全Core314通过/1跳过/0失败；上文旧流水线红灯已被当前测试结果更新，但相关实现属另一任务。本轮无APP构建/发布，release仍163249。移动校验另有DwgReaderService和两条icon内容发生变化，当前不能重复声称57项全通过，未擅自审核这些其他任务修改。证据：artifacts/installer-safety-20260916-resumed/cad-payload-review/。


## 输出边界隔离复现补充（2026-09-16）
证据：artifacts/installer-safety-20260916-resumed/output-boundary-repro/report.md。5个规划场景中，skip/rename普通跨源冲突安全，overwrite跨源冲突、重命名编号耗尽、备份编号耗尽3个缺口已复现，尚未修复（不再仅为静态怀疑）。仅规划，未调用写图或实际覆盖。原正式测试未设置同目录，现修正这一夹具条件，重新编译后11项输出路径测试通过；不代表上述3项安全通过。没有生产代码变更、APP构建或release替换。
