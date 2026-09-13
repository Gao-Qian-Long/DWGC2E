# DWGC2E APP 渐进式重构计划（长期基线）

> 本文档是 APP 端重构的执行基线。规则：**保证功能稳定 > 渐进重构 > UI 一致性 > 一次性完美架构**。
> 任何阶段都不推倒重写、不换技术栈、不升级运行时；每阶段结束必须可编译、可运行、可回归。

生成时间：2026-09-12（基于 `pro` 分支 `fbe038c` 之后的工作副本，已完成 Phase 1 与 Phase 2 的一部分）

---

## 一、当前技术栈（实测，不是推测）

| 层 | 工程 | 目标框架 | 产出 |
|---|---|---|---|
| 桌面端 | `src/DwgTranslator.App` | `net8.0-windows` | `WinExe`（WPF，自包含单文件发布） |
| 核心库 | `src/DwgTranslator.Core` | `net8.0` + `net48`（多目标，供 CAD 插件复用） | classlib |
| CAD 插件 | `src/DwgTranslator.Cad` | 随 `CadPlatform` 切换：浩辰CAD → `net48`，AutoCAD → `net8.0` | classlib（CAD 内 NETLOAD） |
| 测试 | `tests/DwgTranslator.Core.Tests` | `net8.0` | xunit |
| 工具 | `tools/LayoutRegression`、`tools/LicenseGenerator` | net8.0 | 控制台 |

关键依赖：`ACadSharp`（DWG/DXF 读写）、`ClosedXML`（Excel）、`CommunityToolkit.Mvvm`、`Microsoft.Extensions.DependencyInjection`、`Serilog`（Console+File）、`xunit`。

**当前不存在**：数据库（无 SQLite / EF）、任务引擎、任何后端接口（grep `SQLite|TaskManager|TranslationTask|IQueue` 零命中）。

离线环境注意：本机 `dotnet build` 必须加 `--no-restore`（NuGet 不可达），**新增 NuGet 依赖需要先在能联网的机器上 restore 并把包带入缓存**。

---

## 二、当前目录结构（源码行数，排除 bin/obj）

```
src/DwgTranslator.Core     62 文件 / 6202 行   Models · Services · Translation · Logging · Resources · Compatibility
src/DwgTranslator.App      58 文件 / 7473 行   Views(含 Pages) · ViewModels · Services · Themes · Converters
src/DwgTranslator.Cad      22 文件 / 4379 行   Commands · Extraction · Replacement
tests                       1 文件 /  110 行
tools                       2 文件 /  168 行
```

界面侧（Phase 1 已落地）：`Views/MainWindow.xaml`（Shell）+ `Views/Pages/{TranslatePage,BatchTasksPage,GlossaryPage,AccountPage,SettingsPage}.xaml` + `Themes/{ColorTokens,Icons,MainWindowStyles}.xaml`。

---

## 三、入口与依赖关系

- 入口：`App.xaml`（`StartupUri=Views/MainWindow.xaml`）→ `App.xaml.cs:OnStartup`（日志 → 配置 → `ConfigureServices` → 主窗口）；`MainWindow.xaml.cs:OnLoaded` 调 `InitializeAsync()` 与启动文件导入。
- DI（`App.xaml.cs:256-297`）：`ILogStore`、`ILocalizationService`、`IGlossaryService`、`IExcelService`、`IDwgReaderService`/`IDxfReaderService`（同一 `DwgReaderService` 类型）、`IDwgWriterService`/`IDxfWriterService`（同一 `DwgWriterService` 类型）、`IAutoCadInteropService`、`ILicenseService`、`IFormatCodeParser`，`MainViewModel` 为 `Transient`。
- 现状耦合（需要拆的目标）：

```
MainWindow ──► MainViewModel ──┬─► DwgReaderService      (CAD 解析)
                               ├─► DwgWriterService      (离线写回 / 文件输出)
                               ├─► AutoCadInteropService (在线写回)
                               ├─► TranslationService ──► DeepSeekClient ──► api.deepseek.com   ← UI 直连 AI
                               ├─► GlossaryService       (术语)
                               ├─► LicenseService        (授权)
                               └─► AppConfig（JSON 文件，%APPDATA%\DwgTranslator）
```

---

## 四、CAD 核心模块（产品价值所在，原则上不动内部实现）

- 解析：`Core/Services/DwgReaderService`（ACadSharp 遍历实体）、`TextEntityFactory`、`TextExtractor`、`FormatCodeParser`
- 写回（离线）：`DwgWriterService`（临时文件 + `File.Move` 原子替换）、`DwgTextReplacer`、`CollisionDetector`、`DwgFontManager`/`FontMapper`
- 写回（进程内）：`Cad/Replacement/AcadWriterEngine`、`AvailableTextSpace`（走廊测量）、`CollisionDetector`（干涉/墨迹判定）、`TextReplacer`、`FrameDetector`、`VerticalTextLayout`
- CAD 内命令：`Cad/Commands/DwgTranslatorCommands`、`WritebackCommand`（配置驱动的会话写回）

---

## 五、翻译 API 调用位置（4 处，出口唯一是优点）

| 位置 | 说明 |
|---|---|
| `Core/Services/DeepSeekClient:36` | **唯一 HTTP 出口** `POST /v1/chat/completions` |
| `App/ViewModels/MainViewModel.Translation:216-222` | UI 侧自建 `HttpClient` + `DeepSeekClient`（**违反"UI 不直连 AI"**） |
| `App/ViewModels/SettingsViewModel:129-133` | 设置页"测试连接" |
| `Cad/Commands/DwgTranslatorCommands:131-133` | CAD 插件内自建（**同样需要走统一接口**） |

好消息：HTTP 出口只有 `DeepSeekClient` 一处，Phase 8 抽象 `IApiClient` 时改动面可控。

---

## 六、线程与异步现状

已有：
- `Task.Run`：导入、导出、Excel、连接测试、环境自检
- `SemaphoreSlim`：`TranslationService` 内 AI 并发受控（默认 `MaxTranslationConcurrency=12`）
- `DispatcherProgress<T>`：后台进度安全回 UI 线程
- `CancellationTokenSource`：翻译 / 导出各一个

缺口：
- **没有任务层**：多文件导入后是「串行 for 循环 + 单次翻译 + 单次导出」，没有 `TranslationTask`、没有本地 Worker 池、没有暂停/恢复
- **没有本地并发限制**：只限制了 AI 请求并发，未限制同时解析/写回的图纸数
- 没有任务持久化（崩溃/断网后无法续跑）
- 6 处 `async void` 事件处理器（异常依赖全局处理器兜底）
- `_exportCts` 存在竞争释放（见 `CODE_REVIEW.md` 复审结论）

---

## 七、配置与文件输出

- 配置：`AppConfig` → `%APPDATA%\DwgTranslator\settings.json`（API Key 经 DPAPI 加密；读取使用 `AppConfigJson.ReadOptions` 大小写不敏感）
- 术语表：`glossaries/mechanical_{源}_{目标}.json`（`ResolveGlossaryPath` 按方向查找）
- 日志：`%APPDATA%\DwgTranslator\logs\dwgtranslator-YYYYMMDD.log`；CAD 插件 `cad_plugin_*.jsonl`（已有 `CadLogReader` 在监控）
- 输出：离线 `DwgWriterService`（原子替换，不会半写）；在线 `AutoCadInteropService`（生成 .lsp/.scr → NETLOAD 批处理，或 COM 控制已打开 CAD）
- 命名：当前 `原名_translated.dwg` —— 与提示词要求的 `原名_zh.dwg` 不一致；重名会静默覆盖既有 `_translated` 文件（复审已列为缺陷）

---

## 八、可以直接保留（Phase 中不动内部实现）

1. 全部 CAD 读写与几何算法（ACadSharp 读写、走廊测量、字号搜索、干涉检测、字体映射、标签缩写）
2. 翻译流水线（去重、按语言对隔离的缓存、术语占位符、质量校验、重试）
3. 配置/日志体系（Serilog + ILogStore + CAD 日志读取）
4. 发布与安装体系（自包含发布、CadPlugin、安装脚本、环境自检与插件一键安装）
5. Phase 1 已完成的 Shell / 导航 / 主题 / 五页框架

---

## 九、需要渐进重构（对应提示词的硬性要求）

| 项 | 现状 | 目标 |
|---|---|---|
| 任务层 | 无 | `TranslationTask` + `TaskManager`（先内存，后持久化） |
| 状态机 | `TranslationStatus`（翻译维度） | 任务维度 10 态：Pending/Parsing/Extracting/Translating/LayoutOptimizing/Writing/Completed/Failed/Cancelled/Paused |
| 并发 | 仅 AI 并发 | 本地 Worker 数 + AI 并发数，两层独立限流 |
| UI 直连 AI | 是 | `IApiClient` / `ITranslationService`，UI 不认识 DeepSeek |
| 任务持久化 | 无 | SQLite 保存任务/日志/结果路径；启动检测未完成任务 |
| 输出安全 | 不覆盖源文件 ✓，重名会覆盖 | `_zh` 命名 + 重名策略（跳过/改名/显式覆盖）+ 可选自动备份 |
| 设置项 | 无保护开关 | 保护尺寸/公差/型号/术语优先 + 并发参数 + 启动/更新/内存优化 |
| 日志 | 单栏 | 任务日志 / 系统消息双页签 + 阶段化日志（解析→翻译→排版→写回） |
| 术语库 | 扁平条目 | 来源（用户/企业/系统）+ 优先级链 + 冲突处理 + 命中次数 |
| 会员/额度/设备 | 本地授权（可被绕过） | 预留接口，接 Cloudflare Worker + D1 后启用 |

---

## 十、阶段计划（每阶段独立可交付、可验证）

### Phase 1 — 浅色主题 + 主 Shell + 导航 ✅ 已完成
浅色工业风令牌（`#0F766E` 主色 / 石墨侧栏 / 卡片化）、左侧五项导航、账户条、底栏（状态 + 总进度 + 预计剩余时间 + 术语/授权/版本）、五个页面骨架。
验证：三项目 0 警告 0 错误；150% DPI 物理像素截图；UIA 实测五项导航可切换。

### Phase 2 — 图纸翻译页迁移（进行中）
- 已完成：页面结构（标题/说明/操作区/语言对/队列/统计卡/输出设置卡/日志卡）、统计卡接入 CAD 写回日志的两个真实指标（自动调整字高 / 解决干涉）。
- **待做（本轮下一步）**：主表由「文字条目表」改为**任务表**（# / 文件名 / 状态 / 文本数量 / 进度 / 耗时 / 操作）——按提示词"任务列表为中心"。文字条目表下沉到「批量任务」或点击某张图后查看。
- 验收：添加多张图纸 → 每行独立状态与进度；功能不退化（导入/翻译/导出全链路可跑）。

### Phase 3 — TranslationTask + TaskManager（内存版）
`Domain/TranslationTask`（Id/FilePath/FileName/Status/Progress/TextCount/TranslatedCount/Elapsed/Error/Priority/时间戳）+ `Application/TaskManager`（本地 Worker 池 → CAD 解析 → AI 请求池 → 写回，进度事件回 UI）。
验收：UI 不冻结、可取消、单文件失败不影响其他任务；现有单文件流程行为不变。

### Phase 4 — 多文件队列 + 双层并发 + 阶段日志
本地并发与 AI 并发分别可配；任务状态细化到 10 态；日志按 `[n/m] 文件名 阶段` 输出；异常与重试日志单列。
验收：20 张图纸并发跑，UI 全程可拖动、可查看日志、可取消。

### Phase 5 — 批量任务页
优先级（高/普通/低）、任务分组（按目录/自定义）、批处理规则（输出格式/路径/重名策略/失败重试次数/并发数）、异常与重试日志面板、清空已完成 / 重试失败 / 打开输出目录。

### Phase 6 — 设置页 + 文件安全 + 性能参数
常规（开机启动/自动检查更新/完成后打开目录）、翻译（源/目标语言 + 保护尺寸/公差/型号/术语优先）、文件（默认输出目录/输出格式/自动备份源文件/命名规则含预览）、性能（本地并发/AI 并发/失败重试/内存优化）、当前运行环境（OS/CPU/内存/磁盘/应用目录/数据目录，供客服排查）。

### Phase 7 — 术语库升级
来源与分类字段、优先级链（用户 > 企业 > 系统 > AI 默认）、冲突术语处理（候选 A/B + 建议，不静默覆盖）、命中次数与最近更新时间、批量导入/导出、导入记录。

### Phase 8 — API Client 抽象
`IApiClient`（LoginAsync/GetProfileAsync/GetSubscriptionAsync/GetUsageAsync/TranslateAsync/BindDeviceAsync/CheckVersionAsync）；请求结构化（source_lang/target_lang/items[{id,text,height,rotation,layer}]）；**Phase 8 之前先保证"UI 不直连 DeepSeek"**（把 HttpClient 收进 Infrastructure，CAD 插件同样改为通过统一接口）。

### Phase 9 — Cloudflare Worker + D1 接入
客户端 → Worker → D1 → AI Gateway → 模型；会员/额度/设备由服务端裁决；`latest.json` 自动更新检查。

### Phase 10 — 登录 / 会员 / 额度 / 设备
不强制启动登录：打开可用、点"开始翻译"时才要求登录；官网与 APP 共用同一账号与额度。

### Phase 11 — 安全增强
Native/NativeAOT 关键模块、混淆、请求签名、设备指纹（服务端裁决）。**注意**：现有本地授权（硬编码共享密钥 + 32 位校验和）在接入后端前不得作为最终权限依据。

---

## 十一、风险与决策点（需要拍板）

| # | 决策 | 影响 |
|---|---|---|
| 1 | 页面 1 主表改为任务表（提示词已明确），文字条目表下沉 | 决定 Phase 2 收尾形态 |
| 2 | 输出命名 `原名_zh.dwg`（替代 `_translated`）+ 重名策略 | 影响既有用户习惯与既有输出文件 |
| 3 | 数据目录是否由 `%APPDATA%\DwgTranslator` 改为 `DWGC2E` | 需迁移逻辑，否则老用户配置/缓存丢失 |
| 4 | SQLite 依赖本机离线无法 restore —— 先在联网机引入，或任务持久化先用 JSON | 决定 Phase 3/4 的持久化实现 |
| 5 | AI 并发默认值下调（12 → 自动/1/2/3） | 影响速度与 API 成本/封控风险 |
| 6 | 后端未就绪期间的会员/额度只做 UI + 接口预留（不伪造在线状态） | 与提示词 §32 一致 |

---

## 十二、禁止事项（本计划全程遵守）

不做 CAD 预览器；不做网页 Landing 风格；不做深色/赛博朋克；不做大量动画；不推倒重写；UI 线程不跑 CAD 解析；不为每个文件起无界线程；不一次发无界 AI 请求；不默认覆盖源 DWG；不把 DeepSeek Key 写进客户端；不在 UI 里直接调 AI；不用本地 `IsVip` 当最终权限；不保存明文 Token；不因后端未完成而阻塞客户端重构。

---

# 实施进度（重构目标第 14 轮更新：任务层接入 UI）

## 本轮完成：任务层接入 UI（计划里"唯一剩余工作项"）✓

主链路已从「多文件串行 + UI 自己 new TranslationService 直连 AI」迁到 `ITaskManager`：

| 计划项 | 落点 | 验证 |
|---|---|---|
| ① 任务表由任务层驱动（状态 / 进度 / 耗时 / 优先级） | `ViewModels/DrawingFileItem.cs`（`AttachTask` + 任务优先读值，无任务时才回退旧推导） | **运行实测**：真实图纸导入后行显示「等待中 · 5,460 文本」，队列跑完变「已完成 · 100%」 |
| ② 开始 / 取消 / 重试改走任务层 + 订阅四个事件回 UI | `ViewModels/MainViewModel.Tasks.cs`、`MainViewModel.Translation.cs` | **运行实测**：`[0/6]…[6/6]` 阶段日志进日志面板，状态栏显示当前阶段与总进度 |
| ③ 启动询问「检测到 N 张未完成任务，是否继续？」 | `MainViewModel.Tasks.cs::ResumePendingTasks`（`PendingFromLastRun`） | **运行实测**：`tasks.json` 有未完成任务时启动即询问；选「继续」直接续跑并沿用上次写回方式 |
| ④ 命名 / 重名策略在任务引擎内同样生效 | `TaskManager.PrepareDestinations` / `ResolveOutputPath`（已走 `BatchExportPlanner`） | 编译 + 既有 11 个文件安全单测 |
| ⑤ UI 不再认识 DeepSeek | 新增 `Core/Services/SettingsBackedDeepSeekClient.cs`、`DeepSeekClientFactory.cs`；`MainViewModel` 删掉 `HttpClient` / `DeepSeekClient` 字段与 `EnsureDeepSeekClient`；设置页「测试连接」也改走工厂 | **7 个新单测**（缺 Key / 地址非法 / 调用时读配置 / 提示词优先级） |
| 写回方式透明化 | 「开始翻译」前复用 `ExportModeDialog` 选离线 / AutoCAD | **运行实测**：对话框弹出（810×660），选择结果出现在 `任务层运行参数：ZH → EN … 写回方式 "AutoCad"` |
| 导出不再重复写回 | `ExportDwgAsync` 跳过已由任务层写回的图纸并说明原因 | 编译 |
| 设置页性能区三个下拉接线 | `MainViewModel.RuntimeConfig.cs`（写穿 `TaskManagerOptions` + 落盘；任务层持有的就是同一实例） | 编译（改完立即生效，无需重启） |
| 「开机启动」真接线 | `Services/StartupRegistration.cs`（HKCU Run 项；失败回滚并提示；读取时与当前 exe 路径比对） | 编译（绿色版换目录后不会假勾） |

顺带修掉的两个真实缺陷（都会让"迁移后界面看起来正常、实际跑不通"）：

1. `ITranslationService` 从未注册 → `DirectApiClient` 构造失败 → `IApiClient` 静默退化成"未配置的 WorkerApiClient"（模式显示错、能力对不上）。现已注册，并与任务层共用同一个一致性缓存实例。
2. 任务层原先靠 `ActivatorUtilities` 现搭 `TranslationService`，缺 `IDeepSeekClient` 时会在解析 `ITaskManager` 时抛异常；现在由 Core 提供配置感知客户端，**Key 在调用时读取**——"先启动程序、再填 Key"不再需要重启。

全量回归：`dotnet build DwgTranslator.sln --no-restore` **0 错误 0 警告**；`dotnet test` **34/34 通过**（含 4 个任务层流水线集成测试：命名规则 / 故障隔离 / 重名不覆盖 / 运行参数规范化）；`artifacts/publish` 自包含发布成功并同步 `release/`；五页 UIA 截图巡回通过。

## 用户已能实际感受到的变化 ✓

1. 导出按配置命名（`motor.dwg` → `motor_zh.dwg`），重名**默认跳过并提示**（不再静默覆盖），**永不覆盖源文件**，可开启写回前备份
2. 设置页开关真正生效并落盘（保护尺寸/公差/型号/术语优先 + 自动检查更新/完成后打开目录/写回前备份/内存优化/开机启动）
3. 术语库统计为真实数据（总条目/用户/企业/系统/冲突待确认）
4. 「开始翻译」一步到位：解析 → 翻译 → 排版 → 写回，队列化处理，**单张图纸失败不影响其它图纸**
5. 任务表显示真实阶段（解析中 / 提取文本 / 翻译中 / 排版优化 / 写回中 / 已完成 / 失败 / 已取消）与实时进度、耗时；状态栏显示当前阶段
6. 没跑完的队列可续跑（重启后询问）；「重试失败」只重跑失败项
7. 本地并发 / AI 并发 / 失败重试次数在设置页可改，**改完立即生效**（无需重启；落盘后下次启动仍是该值）

## 剩余工作（按优先级）

1. **Phase 4 收尾**：暂停 / 恢复按钮接 `PauseAll` / `ResumeAll`（任务层已实现，页面还没有按钮）；日志面板「任务日志 / 系统消息」双页签尚未分流
2. **Phase 7 术语库**：批量导入导出、命中次数、冲突候选 A/B（`GlossaryConflictDetector` 已就绪，页面待接）
3. **Phase 8 / 9**：Worker 翻译链路 —— 需要一个 `IApiClient.TranslateAsync` → `ITranslationService` 的适配器；接通前 worker 模式在点「开始翻译」时明确提示不可用（已实现，避免整批 401）
4. **CAD 插件**：`Core.csproj` 的 net48 目标需显式 `Compile Include` + Serilog / System.Text.Json 引用才能用 `IApiClient`，插件内的自建 DeepSeek 调用仍是老路径
5. **Phase 11 安全**：本地授权（共享密钥 + 32 位校验和）在接入后端前不得作为最终权限依据
6. 任务持久化换 SQLite（需在能联网的机器上引入包；`ITaskStore` 接口已就位，换实现不动上层）

## 环境约束（影响方案选择）

- **离线**：无法引入新 NuGet 包 → 任务持久化先用 JSON（`ITaskStore` 接口不变，换 SQLite 不动上层）
- **`Core.csproj` 的 net48 目标 `EnableDefaultCompileItems=false`**：`Api\*.cs` / `Tasks\*.cs` 目前只参与 net8.0 编译 → 浩辰插件（net48）要用 `IApiClient` 需改 csproj
- 无法联网验证 Worker → 两种客户端已就绪，等你部署后把基地址填入 `apiMode` + `apiBaseUrl` 即可切换
- 无本地样例图纸（`%TEMP%\cadtest\test_out_all.dwg` 是合成测试件，文本全是 `V`/`24`/`%N` 之类，会被翻译过滤器正确跳过）→ 端到端"真译文写回"仍需在真实图纸上验收

---

# UI 第二轮优化（Design System 统一）进度

> 目标：不新增功能、不动核心算法，只把现有界面收敛成"有层级、克制、统一"的工程软件风格。

## 已完成并验证 ✓

| 批次 | 内容 | 验证 |
|---|---|---|
| 1 | **Design Token 落地**：`Themes/ColorTokens.xaml` 重排（主色 #1769E8，边框变浅 #E3EAF3，文字三级对比拉开）；新增 `Themes/Metrics.xaml`（字号 9 档 / 8pt 间距 / 控件尺寸 / 圆角 6·8·10 / 文本层级样式 / Badge·StatusDot·Toggle·FormRow·EmptyState 样式） | 编译 0 警告 |
| 1 | **公共组件收敛**：按钮 11 种 → Primary/Secondary/Tertiary/Danger/SecondaryDanger/Icon/IconSmall/Compact.*（旧键名保留 BasedOn 别名）；卡片 6 种 → Card/CardNoPad/Card.Inset/CardHeaderBar；表格 1 套（表头 38 / 行 44 / 字号 13）；隐式样式覆盖 TextBox·ComboBox·CheckBox·DataGrid·ProgressBar·ToolTip·ContextMenu·MenuItem·ScrollBar | 资源键差异审计：0 丢失、0 悬空引用 |
| 1 | 删除 3 个孤儿控件（MainToolBar / FilterStatsBar / TranslationDataGrid，0 引用）与未使用样式 | 编译 ✓ |
| 2 | **Header**：只保留「会员状态（真实授权文案）+ 用户下拉 + 窗口控制」；置顶/设置/环境自检/帮助收进下拉菜单；窗口按钮统一 46×40、关闭 hover 才变红 | UIA 实测：菜单项 = 会员中心/窗口置顶/设置/环境自检/帮助 |
| 3 | **侧栏**：导航项高度 40、图标/字号统一、页脚只留版本号 | 截图 ✓ |
| 4 | **图纸翻译页**：标题层级（24/13）+ 副标题独立成块；右栏改为 Summary Panel（3 个关键数字 → 分隔线 → 两个排版指标小行，不再 6 个 KPI 平铺）；输出设置改两列标签结构 + Toggle 开关（绑定 `OpenOutputFolderAfterExport`）；空状态三行文案统一；日志区降权 | 编译 + 截图 ✓ |
| 修 | **控件高度全项目统一**：59 处硬编码高度（24/26/28/30/32/34）→ 1 处（头部账号按钮 40，刻意）；工具栏一律 36，行内图标按钮 28，卡片内紧凑按钮 30 | UIA 实测：五页工具栏按钮全部 54 物理像素（=36 逻辑） |

## 本轮修掉的两类回归（教训记录下来）

1. **重写样式字典时丢了自定义模板** → 只设属性的 `ComboBox` 会退回系统 3D 外观（用户直接看出"像旧版"）。凡是"扁平化"过的控件（ComboBox/TextBox/ProgressBar/ScrollBar），模板必须整段保留。
2. **局部硬编码高度与样式层打架** → 同一工具栏里 Secondary=36 / Primary=38 / 暂停=30。规则：尺寸只在样式层定义；行内按钮用 `Button.IconSmall`，卡片内用 `Button.Compact.*`，工具栏只允许 36 一档。

## 剩余批次（按 §30 顺序）

- 5 批量任务页：主任务表占 55~60%、右栏 Summary 化、底部三卡等高、行内 `...` 菜单（§24）
- 6 术语库：统一 Filter Bar（唯一主按钮「新增术语」）、来源 Chip → Badge（用户蓝 / 企业绿 / 系统灰）、冲突淡橙提示条（§17/§18）
- 7 会员中心：首排「当前套餐 + 使用额度(进度条)」，二排 套餐详情/设备，三排 统计/订单；按钮权重 升级(蓝)/续费(次)/退出(危险文字)（§19）
- 8 设置页：左侧设置目录（常规/翻译/文件/性能/账号/关于）+ 右侧单类目；Form.Row 三段式；Toggle 与 Checkbox 分流（§20~§22）
- 9 Toast / Confirm 基础设施（§29；当前 26 处 MessageBox）

## 第二轮优化：5~9 批完成情况（补齐）

| 批次 | 内容 | 状态 |
|---|---|---|
| 5 批量任务页 | 行内两个占位图标按钮 → 单个 `···` 菜单（查看文本条目/打开输出文件/重试该任务/取消任务/查看日志，左键可展开）；6 个 KPI 数字统一 KpiNumberStyle/KpiLabelStyle；「快速操作」重卡片并入任务统计底部（Compact 按钮组）；标题层级统一 | ✓ 运行实测菜单项正确 |
| 6 术语库 | 来源 Chip 收敛到统一 Badge（用户蓝 / 企业绿 / 系统灰）；搜索框高度交回样式层；新增淡橙冲突提示条（绑定真实 `GlossaryConflictPendingCount`，无冲突自动隐藏） | ✓ 编译 + 运行 |
| 7 会员中心 | 卡片顺序确认为 账户信息+使用额度 → 套餐详情+设备管理 → 使用统计+订单记录；卡片标题/页面标题统一；「退出登录」改危险文字按钮（升级=主按钮、续费=次按钮） | ✓ |
| 8 设置页 | **左目录 + 右侧单类目**：左栏 176px 六个目录项（常规/翻译/文件/性能/账号/关于），右侧只显示选中类别（复用 `IndexToVis` 转换器）；目录项样式进公共字典 | ✓ 运行实测：切「关于」后 关于内容可见、常规内容隐藏 |
| 9 Toast / Confirm | 新增 `ToastHost` + `ToastService`（右下角堆叠、4 秒自动消失、无宿主时降级为日志）；新增主题化 `ConfirmDialog`；「清空工作区」与「未完成任务续跑」已改走确认框；**导出结果弹窗改 Toast**（详细清单进日志） | ✓ 编译 + 运行零错误 |

回归：`dotnet build --no-restore` **0 错误 0 警告**；`dotnet test` **34/34 通过**；五页 UIA 截图巡回通过、启动日志 0 错误。

**仍然开放的（下一轮）**：其余 ~24 处 MessageBox（导入错误、许可、设置保存失败等）继续按"结果→Toast / 阻塞前提→对话框"收口；批量任务页仍是设计稿静态行（未绑定真实任务数据）；会员中心数据仍为占位（等 Phase 10 后端）。
## 2026-09-12 批量页真实数据接线（后续审计）
- 替换静态 128 任务/示例文件/示例日志，任务表绑定 DrawingFiles，文本结果绑定 FilteredEntities。
- “条目”操作现在有对应文本结果表；当前为只读查看，编辑校对仍待实现。
- 原“暂停全部”实际执行取消，改为准确的“停止任务”，真实暂停/恢复仍待接线。
- 保留真实导入/开始/停止/重试/导出命令；不再展示没有命令的文件夹、分组及规则占位控件。这些需求仍保留在待办中，未视为完成。
- 构建 0 警告/0 错误，核心测试 34/34；结构检查证明两个真实集合绑定存在。尚未验证渲染效果与运行时绑定。
- 证据与可执行回滚：artifacts/batch-live-20260912/VERIFICATION.txt。整体 UI / 本地对接目标继续进行。
