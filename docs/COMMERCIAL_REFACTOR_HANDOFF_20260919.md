# DWGC2E 商用化重构交接报告

- **交接日期**：2026-09-19
- **工作区**：`D:\DWGC2E`
- **本次交接目标**：记录本轮已完成修复、验证证据、尚未完成事项和推荐接手顺序，供后续 AI 直接续作。
- **总计划**：`D:\DWGC2E\docs\COMMERCIAL_REFACTOR_PLAN_20260919.md`
- **安全说明**：本文不包含测试账号、密码、后台临时密钥、Provider 密钥或其他敏感值。

---

## 1. 当前结论

本轮已经完成两类工作：

1. 前序商用化 UI/交互重构已完成一轮落地并通过完整 UI smoke；
2. 本批完成了任务生命周期状态机、状态审计字段和 JSON Schema 迁移，并修复了暂停态可被普通流水线原因错误恢复的问题。

当前源码可以通过完整 Core 测试和 Solution 构建，但**尚未执行正式发布，也没有替换 `D:\DWGC2E\release`**。后续 AI 不应把当前源码状态描述为“已发布版本”。

---

## 2. 本轮已经修复的项目

### 2.1 翻译页布局与信息密度

主要涉及：

- `D:\DWGC2E\src\DwgTranslator.App\Views\Pages\TranslatePage.xaml`
- `D:\DWGC2E\src\DwgTranslator.App\Views\Pages\TranslatePage.xaml.cs`
- `D:\DWGC2E\src\DwgTranslator.App\ViewModels\MainViewModel.Translation.cs`
- `D:\DWGC2E\src\DwgTranslator.App\ViewModels\MainViewModel.WorkspaceUi.cs`

已完成：

- 增加任务关键指标摘要，用户可直接看到任务规模、状态和进度；
- 文件队列与设置区域重新分配空间，降低无意义空白；
- 适合并排展示的配置改为双排/双列组织，提高常用窗口下的信息密度；
- 底部主操作区固定并保持可达，避免内容滚动后找不到主要动作；
- 文件名、状态、进度和操作区域增加合理的最小宽度，减少文字与按钮裁切；
- 输出目录和相关操作重新组织，避免长路径挤压按钮；
- 删除依赖高频 `LayoutUpdated` 的动态高度补丁，降低布局抖动和重复测量风险。

### 2.2 批量任务页升级为任务中心

主要涉及：

- `D:\DWGC2E\src\DwgTranslator.App\Views\Pages\BatchTasksPage.xaml`
- `D:\DWGC2E\src\DwgTranslator.App\ViewModels\MainViewModel.Tasks.cs`

已完成：

- 增加七项任务摘要，常用状态可以快速查看；
- 工具栏改为更紧凑的双排布局；
- 常用宽度下消除不必要的横向滚动；
- 任务表格列宽重新分配，重要内容列获得弹性空间；
- 增加任务详情抽屉；
- 详情中增加生命周期时间线、错误和导出相关信息；
- 修复关闭详情抽屉后，DataGrid 选择变化又自动把抽屉打开的问题；
- 抽屉打开后设置合理焦点，关闭时恢复原焦点；
- 为虚拟化场景增加焦点恢复 fallback。

### 2.3 术语库列宽与可操作性

主要涉及：

- `D:\DWGC2E\src\DwgTranslator.App\Views\Pages\GlossaryPage.xaml`
- `D:\DWGC2E\src\DwgTranslator.App\Views\Pages\GlossaryPage.xaml.cs`
- `D:\DWGC2E\src\DwgTranslator.App\ViewModels\MainViewModel.Glossary*.cs`

已完成：

- 源词/目标词等主要文本列改为弹性列宽；
- 来源信息改为更紧凑的展示方式；
- 操作列设置最小宽度，避免按钮显示不全；
- 长内容使用省略/详情展示，避免一条数据撑坏整张表；
- 编辑抽屉的滚动、焦点和关闭恢复逻辑得到统一。

### 2.4 设置页结构、校验与反馈

主要涉及：

- `D:\DWGC2E\src\DwgTranslator.App\Views\Pages\SettingsPage.xaml`
- `D:\DWGC2E\src\DwgTranslator.App\Views\Pages\SettingsPage.xaml.cs`
- `D:\DWGC2E\src\DwgTranslator.App\ViewModels\MainViewModel.Settings.cs`
- `D:\DWGC2E\src\DwgTranslator.App\ViewModels\MainViewModel.SettingsPage.cs`

已完成：

- 修复部分设置区域缺少明确 Grid 行定义造成标题、字段重叠的结构性问题；
- 重新组织设置分区，减少“开发配置项堆叠”的观感；
- 增加输入校验和明确状态反馈；
- 增加推荐值恢复入口；
- 增加输出目录可写性验证；
- 保存栏与表单宽度、可达性得到统一；
- 明确保存成功、失败和未保存状态。

### 2.5 滚轮、触控板与嵌套滚动

主要涉及：

- `D:\DWGC2E\src\DwgTranslator.App\Views\MainWindow.xaml.cs`
- 各页面的滚动容器与 DataGrid 交互代码

已完成：

- 支持高精度触控板的累计滚动 delta；
- DataGrid、抽屉和页面主滚动容器之间增加边界传递；
- 表格内部可以滚动时优先滚表格；
- 表格到顶/到底后，滚轮继续传递给父页面；
- 降低“鼠标放在表格上页面完全滚不动”的概率；
- 避免多层 ScrollViewer 长期争抢同一滚轮事件。

### 2.6 任务审计字段与导出路径可靠性

主要涉及：

- `D:\DWGC2E\src\DwgTranslator.Core\Tasks\TranslationTask.cs`
- `D:\DWGC2E\src\DwgTranslator.Core\Tasks\TaskManager.cs`
- `D:\DWGC2E\src\DwgTranslator.Core\Tasks\ITaskManager.cs`
- 翻译项目和导出定位相关实现

已完成：

- 增加 `UpdatedAt`；
- 增加 `ReviewCompletedAt`；
- 增加 `LastExportedAt`；
- 增加 `RetryCount`；
- 增加审计元数据标准化逻辑；
- `ReadyForReview` 状态在重启后不再错误退回 `Pending`；
- 校对、导出、重试、暂停、恢复、取消、进度更新和失败均更新时间；
- 重试时清理旧校对/导出结果，同时保留可复用的翻译 checkpoint；
- 导出路径按“任务缓存 → 项目导出历史 → 旧兼容字段”三级解析；
- 最近历史文件失效时可以回退到仍存在的最近成功导出；
- 成功打开历史导出后回填任务侧快速缓存。

### 2.7 正式任务状态机

新增：

- `D:\DWGC2E\src\DwgTranslator.Core\Tasks\TranslationTaskStateMachine.cs`

修改：

- `D:\DWGC2E\src\DwgTranslator.Core\Tasks\TranslationTask.cs`
- `D:\DWGC2E\src\DwgTranslator.Core\Tasks\TaskManager.cs`

已完成：

- 新增稳定的 `TranslationTaskTransitionReason` 原因码；
- 新增统一的 `CanTransitionTo(...)` 和 `TransitionTo(...)`；
- 用明确转换矩阵限制任务状态流转；
- 非法状态跳转抛出 `InvalidOperationException`，不静默篡改状态；
- 状态变化统一更新：
  - `PreviousStatus`
  - `LastTransitionReason`
  - `StatusChangedAt`
  - `UpdatedAt`
  - `SchemaVersion`
- `TaskManager` 生产代码中的任务状态写入已迁移到状态机；
- 搜索 `src` 后未发现生产代码直接执行 `.Status = TranslationTaskStatus...`；
- `ResetForResume()` 已通过 `RecoveredAfterInterruption` 原因进入 `Pending`；
- 修复暂停态漏洞：`Paused` 不能再被 `PipelineStarted`、`ExtractionStarted` 等普通流水线原因隐式恢复，只允许显式用户恢复、取消或失败路径离开暂停态。

注意：

- `TranslationTask.Status` 目前仍保留 public setter，用于兼容 JSON 反序列化和大量现有测试对象初始化器；
- 生产代码写入已经收口，但 setter 的语言级封装尚未完成，列在后续任务中。

### 2.8 JSON 任务 Schema 迁移与损坏隔离

主要涉及：

- `D:\DWGC2E\src\DwgTranslator.Core\Tasks\JsonTaskStore.cs`

已完成：

- 当前任务记录 Schema 版本升级为 `2`；
- 没有 `SchemaVersion` 的旧数组记录按 Schema 1 读取；
- Schema 1 自动升级为 Schema 2；
- 旧记录补齐：
  - `PreviousStatus`
  - `LastTransitionReason = LegacyMigration`
  - `StatusChangedAt`
- 高于客户端支持版本的未来 Schema 记录被隔离，不强行反序列化；
- 单条未来版本或损坏记录不会导致其他健康任务全部不可见；
- 原始异常 JSON 继续通过 `.recovery-*` 机制保留，便于人工恢复；
- 保存格式仍维持原 JSON 数组，避免在本批同时破坏既有文件、崩溃恢复和兼容测试。

### 2.9 新增状态机测试

新增：

- `D:\DWGC2E\tests\DwgTranslator.Core.Tests\TranslationTaskStateMachineTests.cs`

覆盖：

- 新任务 Schema 版本和 Created 审计；
- 正常流水线状态转换；
- 非法跳转不修改任务；
- 暂停后只有显式恢复原因才能回到原阶段；
- 终态通过显式重试回到 Pending；
- 旧 JSON Schema 自动迁移；
- 未来 Schema 行隔离且保留恢复证据。

---

## 3. 验证结果

### 3.1 状态机/TaskManager/JsonTaskStore 专项测试

执行：

```powershell
dotnet test tests/DwgTranslator.Core.Tests/DwgTranslator.Core.Tests.csproj `
  --no-restore `
  --filter "FullyQualifiedName~TranslationTaskStateMachineTests|FullyQualifiedName~JsonTaskStoreTests|FullyQualifiedName~TaskManager"
```

结果：

- **77 passed**
- **0 failed**
- **0 skipped**

### 3.2 完整 Core 测试

执行：

```powershell
dotnet test tests/DwgTranslator.Core.Tests/DwgTranslator.Core.Tests.csproj --no-restore
```

结果：

- **507 passed**
- **0 failed**
- **1 skipped**
- 跳过项：`TaskTemporaryFileTests.CleanupDoesNotFollowSymbolicLinkCandidates`

### 3.3 完整 Solution 构建

执行：

```powershell
dotnet build DwgTranslator.sln --no-restore
```

结果：

- **构建成功**
- **0 errors**
- **7 warnings**

当前警告均不是本批阻断错误，主要为：

- 已废弃 `TranslationTask.OutputPath` 兼容字段仍被 App 显示层引用；
- UI smoke 测试代码已有的可空引用警告。

### 3.4 UI 验证证据

前序完整 UI smoke 已通过，最终截图位于：

- `D:\DWGC2E\artifacts\commercial-refactor-20260919\ui-smoke-full-final`

其中包括常用窗口尺寸、不同 DPI、翻译页、批量任务页、任务抽屉、术语库、设置页、账号页、支付窗口、公告、Toast、工作区大数据量等截图。

本批状态机修复不改变 XAML，因此完成状态机后未重复执行整套截图生成；如后续继续改 UI，必须再次运行 UI smoke。

---

## 4. 当前发布状态

**当前源码尚未发布。**

`D:\DWGC2E\release` 仍是此前验证版本：

- ProductVersion：`2.1.1+ui.20260919-075732.dbe2b59`
- SHA-256：`B5A80243F7B8A53415864707D978024661754BAB4156777738F353878C71B9E6`

本次没有运行发布脚本，也没有替换 release。

若后续确定正式交付，必须遵守：

1. 先运行回归与 UI smoke；
2. 正常关闭正在运行的 APP；
3. 使用 `D:\DWGC2E\tools\Publish-Desktop.ps1`，不得带 `-BuildOnly`；
4. 保留便携用户设置和数据；
5. 验证安装后 EXE 哈希；
6. 只保留最新候选和一个上一版回滚备份；
7. 如果 release 被占用或验证失败，保留旧 release，不得把 artifacts 中的孤立构建冒充最新版。

---

## 5. 尚未修复/尚未完成的事项

以下均未在本批实现，后续 AI 不应误报为已完成。

### P0-1：异步操作统一防重复提交

建议优先检查：

- `D:\DWGC2E\src\DwgTranslator.App\Views\BillingWindow.xaml.cs`
- `D:\DWGC2E\src\DwgTranslator.App\Views\EnvironmentCheckPanel.xaml.cs`
- `D:\DWGC2E\src\DwgTranslator.App\Views\Pages\AccountPage.xaml.cs`
- `D:\DWGC2E\src\DwgTranslator.App\Views\MainWindow.xaml.cs`
- `D:\DWGC2E\src\DwgTranslator.App\Views\Pages\GlossaryPage.xaml.cs`
- `D:\DWGC2E\src\DwgTranslator.App\ViewModels\MainViewModel.Projects.cs`

已知现状：

- 登录已有 `_submitting` 和 ViewModel 状态保护；
- Billing 已有 `_busy` 和 `Run(...)`；
- CAD 插件安装/卸载主要依赖按钮禁用，建议增加原子 guard；
- 术语页多个 `async void` 入口依赖 ViewModel flag，尚未形成统一入口保护；
- 文件拖放缺少独立 import gate；
- 项目自动保存会取消前一次 debounce，但缺少代际检查和可等待关闭。

验收标准：

- 连续快速点击不会启动两次网络、导入、安装或保存操作；
- 按钮禁用不是唯一防线，业务入口也有原子 guard；
- 取消、窗口关闭、异常后 guard 必须可靠释放；
- 对高风险入口增加单元测试或 UI 自动化测试。

### P0-2：发布包敏感信息扫描门禁

尚未实现：

- Prompt 明文扫描；
- Provider/API Key/Worker Secret 扫描；
- 账号密码模式扫描；
- `.pdb`、源代码、source map、开发配置、内部模板扫描；
- 将扫描门禁集成到 `D:\DWGC2E\tools\Publish-Desktop.ps1`；
- 安全门禁正向/反向测试；
- 可审计的扫描报告。

建议实现方式：

1. 建立明确 allowlist，而不是只靠少量 deny pattern；
2. 对发布目录、单文件 EXE 解包结果、安装包内容分别扫描；
3. 检测高熵字符串和常见凭据格式；
4. 对已知安全测试样本验证“必须阻断”；
5. 扫描失败时发布脚本必须停止，不能仅打印 warning；
6. 报告不得把命中的完整密钥再次输出到日志。

### P0-3：Worker 生产部署闭环

进入 Worker 目录前必须先阅读：

- `D:\DWGC2E\cf-worker\AGENTS.md`

本地源码已有 Provider CRUD、AES-GCM、权重、熔断、故障转移、Prompt/routing profile 和审计基础，但尚未完成：

- 生产 D1 migration；
- 当前源码 `wrangler deploy`；
- 线上 `/v1/version` 与本地提交对应性验证；
- Provider probe；
- 权重路由远程验收；
- 熔断和故障转移远程验收；
- APP 发起真实翻译并成功回写；
- 线上日志敏感信息检查。

在这些验证完成前，不得声称“后台自定义模型配置和负载均衡已经正式上线”。

### P1-1：收紧任务状态字段封装

当前 `TranslationTask.Status` 保留 public setter，只是生产写入已经全部迁移到状态机。

后续可选择：

- 独立持久化 DTO；或
- private/internal setter 配合 `[JsonInclude]`；或
- 自定义 JsonConverter。

要求：

- 不能破坏旧 JSON；
- 不能让 UI/ViewModel 绕过状态机；
- 应增加反序列化、迁移和非法业务写入测试。

### P1-2：JSON 到 SQLite 的真正持久化演进

当前仍是单文件 JSON 数组。尚未实现：

- SQLite 任务表；
- 状态转换历史表；
- 导出历史、错误记录和 checkpoint 的事务一致性；
- JSON → SQLite 首次迁移；
- 迁移失败回滚；
- 并发读写与崩溃恢复测试。

不要直接删除 JSON 支持。建议先增加双读/一次性迁移和可回滚备份，再切换默认存储。

### P1-3：导出事务与磁盘可靠性

尚未完整实现：

- 导出前磁盘空间检查；
- 输出目录权限和占用检查；
- 临时文件写入后原子替换；
- 同名冲突策略的完整测试；
- 写回途中崩溃后的残留清理；
- 部分成功时的结构化错误报告。

### P1-4：ViewModel 继续拆分

`MainViewModel.*.cs` 虽然通过 partial 分文件，但本质仍是聚合式超大 ViewModel，继续承担导航、账号、任务、术语库、设置、更新、支付、环境检查和 UI 状态。

建议后续按页面拆为：

- `TranslatePageViewModel`
- `TaskCenterViewModel`
- `GlossaryPageViewModel`
- `SettingsPageViewModel`
- `AccountPageViewModel`
- 独立服务：导航、通知、对话框、后台任务、项目自动保存

不要一次性大爆炸重写；先提取接口和可独立测试的业务服务，再逐页迁移。

### P2：安装包、签名和最终商用发布

尚未完成：

- 代码签名；
- 更新包签名；
- 安装、覆盖升级、降级阻断、回滚、卸载全流程验证；
- 正式更新 `D:\DWGC2E\release`；
- 最终发布报告；
- 新包 SHA-256 和回滚证据；
- 网站下载发布；
- 购买开关或真实支付测试。

注意：更新本地 release 不代表允许发布网站下载、开放购买或执行真实支付。

---

## 6. 推荐后续执行顺序

建议后续 AI 按以下顺序工作，每完成一批就单独验证，避免把 UI、后台部署和发布安全混在一个不可回滚的大改动中。

### 第 1 批：异步防重与关闭可靠性

1. 梳理所有 `async void` UI 入口；
2. 建立统一原子 guard/异步命令策略；
3. 完成拖放导入、CAD 安装、术语操作、自动保存防重；
4. 增加连续点击、取消、窗口关闭测试；
5. 运行 App/Core 测试和 UI smoke。

### 第 2 批：发布包安全门禁

1. 定义禁止进入客户端的资产和字符串；
2. 编写扫描器；
3. 编写必过/必失败样本测试；
4. 集成 `Publish-Desktop.ps1`；
5. 只做 `-BuildOnly` 验证时不要更新 release；
6. 生成脱敏安全报告。

### 第 3 批：Worker 生产闭环

1. 阅读 `cf-worker/AGENTS.md`；
2. 备份并迁移生产 D1；
3. 部署当前 Worker；
4. 验证版本、Provider probe、权重、故障转移；
5. 使用 APP 完成真实翻译和回写；
6. 检查日志无 Prompt、密钥和用户隐私泄露。

### 第 4 批：持久化与导出可靠性

1. 设计 SQLite schema 和迁移策略；
2. 保留 JSON 恢复路径；
3. 完成原子导出、磁盘和权限检查；
4. 增加断电/崩溃/并发恢复测试。

### 第 5 批：正式发布

1. 完整回归；
2. 完整 UI smoke；
3. 发布包安全门禁；
4. 安装/升级/回滚/卸载验收；
5. 使用不带 `-BuildOnly` 的发布脚本更新 release；
6. 记录版本、哈希、备份和回滚证据。

---

## 7. 接手注意事项

- 工作树包含大量尚未提交的既有改动，**禁止 `git reset --hard`、`git clean` 或覆盖式还原**；
- 不要把其他任务的修改当作垃圾清理；
- 临时日志、截图和脚本应放入 `artifacts` 下单独的任务目录，不要散落根目录；
- 不要删除用户数据、便携设置、数据库恢复备份或其他活动任务文件；
- 新增状态必须同步更新：状态机矩阵、原因码、UI 文案、筛选统计、JSON/SQLite 迁移和测试；
- 任何 Worker 远程操作前先确认环境与账号，部署后必须做线上版本对应性验证；
- 任何报告和日志都不得写入用户提供过的账号、密码或后台密钥；
- 当前配色不是本阶段重点，优先处理可靠性、信息架构、操作闭环和商用发布门禁。

---

## 8. 本批核心文件清单

状态机与持久化：

- `D:\DWGC2E\src\DwgTranslator.Core\Tasks\TranslationTaskStateMachine.cs`
- `D:\DWGC2E\src\DwgTranslator.Core\Tasks\TranslationTask.cs`
- `D:\DWGC2E\src\DwgTranslator.Core\Tasks\TaskManager.cs`
- `D:\DWGC2E\src\DwgTranslator.Core\Tasks\JsonTaskStore.cs`
- `D:\DWGC2E\src\DwgTranslator.Core\Tasks\ITaskManager.cs`
- `D:\DWGC2E\tests\DwgTranslator.Core.Tests\TranslationTaskStateMachineTests.cs`

UI 主页面：

- `D:\DWGC2E\src\DwgTranslator.App\Views\Pages\TranslatePage.xaml`
- `D:\DWGC2E\src\DwgTranslator.App\Views\Pages\BatchTasksPage.xaml`
- `D:\DWGC2E\src\DwgTranslator.App\Views\Pages\GlossaryPage.xaml`
- `D:\DWGC2E\src\DwgTranslator.App\Views\Pages\SettingsPage.xaml`
- `D:\DWGC2E\src\DwgTranslator.App\Views\MainWindow.xaml.cs`

参考与证据：

- `D:\DWGC2E\docs\COMMERCIAL_REFACTOR_PLAN_20260919.md`
- `D:\DWGC2E\artifacts\commercial-refactor-20260919\ui-smoke-full-final`

---

## 9. 交接状态

- 当前批次“任务状态机与 JSON Schema 迁移”：**已完成**；
- 专项测试：**通过**；
- 完整 Core 测试：**通过**；
- Solution 构建：**通过**；
- 正式发布：**未执行**；
- 下一推荐任务：**异步操作防重复提交**，随后完成**发布包敏感信息扫描门禁**。
