# DWG Translator（DWGC2E）代码评审报告

评审对象：`D:\DWGC2E`（.NET 8 WPF + AutoCAD/浩辰CAD 插件，DWG/DXF 图纸文字翻译与回写工具）
分支/版本：`dev` @ `7e5e35b`
评审方式：逐文件阅读源码 + 四路并行分层深审（Core / Cad / App / 安全）+ 交叉验证关键结论 + 实测复现高风险项
编译验证：`dotnet build DwgTranslator.sln -c Debug --no-restore` → **成功，0 warning / 0 error**
（`--no-restore` 是本沙箱离线环境的限制，非代码缺陷；联网环境直接 `dotnet build` 即可）

图例：`CRITICAL` 会导致错误结果 / 崩溃 / 收入损失 · `HIGH` 高概率触发功能或安全问题 · `MEDIUM` 明确缺陷或可维护性负债 · `LOW` 整洁性

---

## 0. 结论速览

工程整体是**有真实设计意图的中等规模项目**：分层清晰（Core / Cad / App）、接口齐全、本地化资源 337 条且键与占位符经审计**完全一致**（无缺失键、无参数错配）、格式码解析用 `[GeneratedRegex]`、缓存/术语表有锁、写回走临时文件 + 原子 `File.Move`、`DispatcherUnhandledException`/`TaskScheduler.UnobservedTaskException` 都挂了处理器。

问题集中在五处：

1. **授权体系整体失效**——签发密钥就在客户端里，且本地授权文件可自造、可删除重置、可回拨时钟，门禁还是一个 JSON 布尔字段。
2. **已知的"界面卡死"根因没有被修掉**——进度回调仍是逐条同步 `Dispatcher.Invoke`，`.claude/workflows/fix-ui-freeze-and-ux.md:19` 明确要求改成 `BeginInvoke`，实际以一段注释"论证保留 Invoke"收场。
3. **回写"全有或全无"**——离线侧一条 handle 没匹配上就一个字节都不写；CAD 插件侧更宽（一个被**有意保留**的表格单元格也会让整张图回滚），且真实原因都不会呈现在界面上。
4. **在线回写链路有一组必现缺陷**——生成的 CAD 脚本未加引号、在 .NET 8 上用 `Encoding.Default`（实测 = UTF-8）写 ANSI 脚本、超时后仍删除会话目录。
5. **CAD 插件对宿主的防护不足**——只捕获 `CadRuntimeException`、部分命令把 IO 放在 try 之外、在主线程 `.GetAwaiter().GetResult()`、公共入口不加 `DocumentLock`；另有"报告成功但实际没改文字"（MLeader）与"健康检查命令改动用户图纸并重定向其保存路径"两处具体缺陷。

未发现：任何硬编码的 DeepSeek API Key（含 git 全历史，`git log --all -S "sk-"` 无命中）；证书校验绕过；畸形占位符/资源键。

**已核实并更正的两条误报：**
- `Views/LicenseDialog.xaml:59` 的 `ToolTip="输入 gql119871 或 DwgTranslator-P-XXXX..."` **不是**可用后门。实测 `LicenseService.Activate()`（`LicenseService.cs:150-203`）只接受 `DwgTranslator-P-` / `DwgTranslator-S-` 前缀，`gql119871` 会返回 `LicenseActivationInvalidFormat`。这是遗留开发备注（顺带泄漏作者账号）+ 误导用户的提示文案。
- `.claude/settings.local.json` **未被提交**（`git ls-files .claude` 为空，`.gitignore:29` 已忽略）。

**已定位并修复的具体缺陷**：用户报告的"部分中文没有翻译出来"经运行日志实证定位为 **C9**——45° 旋转标注的译文在排版拟合阶段被静默丢弃（`AvailableTextSpace.Measure` 对非 90° 旋转不测空间 + 旋转分支用退化搜索 + 拒绝时不打日志），且诊断日志本身因 `Log.FormatMessage` 正则缺陷而失效，导致该 bug 长期隐身。修复已实施并部署，详见 C9。

---

## 1. CRITICAL

### C1 授权体系可被完全绕过（四个独立缺口叠加）

| # | 位置 | 事实 |
|---|---|---|
| a | `src/DwgTranslator.Core/Services/LicenseCrypto.cs:13-22` vs `tools/LicenseGenerator/Program.cs:14` | 客户端把签发密钥分片拼装：`"DWG-Trans"+"lator-202"+"6-Secret-"+"Key-v1"`，与生成器常量**字节完全一致**。分片只是防字符串扫描，不是密码学保护。任何人都能离线为任意机器码签发永久授权。 |
| b | `LicenseCrypto.cs:70-72` + `Program.cs:127` | 校验和 `DeriveKey(data + SecretKey, 4)` 只有 **4 字节 / 8 个十六进制字符**，盐固定且公开（`LicenseCrypto.cs:38`）。PBKDF2 的 10 万次迭代被 32 位截断完全抵消。 |
| c | `LicenseService.cs:32-71` | `LoadLicense()` 直接信任 DPAPI 解出的 `LicenseInfo`，**从不重新校验** `ActivationCode` 或校验和。`ProtectedData` 用的是 `CurrentUser` 且无附加熵（`LicenseCrypto.cs:27,32`），同一 Windows 用户可自行写出一份 `license.dat`（`Type=Perpetual` + 自己机器码）→ `IsValid == true`。删除 `license.dat` 即恢复 3 次试用（`:36-52`）。订阅到期只与本地时钟比较（`Models/LicenseInfo.cs:48`）→ 回拨系统时间即永久有效。 |
| d | `MainViewModel.Translation.cs:21`、`ImportExport.cs:110`、`Settings.cs:281` | 所有门禁都是 `if (_config.LicensingEnabled && ...)`。该字段默认 `false`（`AppConfig.cs:15`），来源是用户可写的 `%APPDATA%\DwgTranslator\settings.json`（`App.xaml.cs:204-218`）→ 改一个 JSON 字段即可，连反编译都不需要。界面自己就标着 `"免授权版本"`（`MainViewModel.cs:134`）。 |

另有：`LicenseService.cs:212-218` 的"激活请求"只是本地拼字符串，全项目无任何服务端校验或联网激活路径。

**修复方向**：删除共享密钥，改用 Ed25519/RSA——私钥只留在离线签发环境，客户端只嵌公钥；校验和升级为 ≥128 位 MAC 并用恒定时间比较；`LoadLicense` 每次读取都验签 + 验机器绑定；本地授权文件只当缓存；试用状态移出用户可写区；门禁改为编译期能力开关（避免运行期布尔字段）。

### C2 翻译进度逐条同步 `Dispatcher.Invoke` → UI 线程成为串行瓶颈（"界面卡死"的真根因）

- `src/DwgTranslator.App/Services/DispatcherProgress.cs:15` — `dispatcher.Invoke(...)`，**同步阻塞**。
- 频率是**每条实体**，不是每个唯一文本：`Core/Services/TranslationService.cs:112` 在 `foreach (var entity in group)` 内部调用 `progress?.Report(result)`。
- 8000 条实体的图纸 ⇒ 最多 **8000 次跨线程阻塞往返**；最多 12 个 worker（`MainViewModel.Translation.cs:105`）同时停在 `Invoke` 里；每次回调还要执行（`MainViewModel.Translation.cs:71-98`）：两次 `ToUpperInvariant()`、`NormalizeSourcePath()`→`Path.GetFullPath`（`:158`）、3 个 `TextEntity` 属性写（触发 INPC → DataGrid 重绘）、`_consistencyService.CacheSize`、`Strings.Get(...)` 格式化。
- 因此 UI 线程变成不可并行化的串行点，无法绘制、无法响应输入。
- **该修法在项目自己的工作流里写明过**：`.claude/workflows/fix-ui-freeze-and-ux.md:19` 要求"把 `Dispatcher.Invoke` 换成 `BeginInvoke`"。实际做法是给 `Invoke` 加了一段自证注释（`DispatcherProgress.cs:5-9`），修复未落地。

同源放大器（同属一条 UI 线程路径）：

- `ViewModels/MainViewModel.Operations.cs:94-95` — `FilteredEntities.Clear()` + 逐条 `Add`，8000+ 次 `CollectionChanged`。触发点：导入（`ImportExport.cs:74`）、翻译结束（`Translation.cs:111`）**且在 finally 里又来一次（`:150-151`）**、全部阅览（`Operations.cs:21`）、导出（`ImportExport.cs:353`）、切换筛选（`Operations.cs:48`）。
- `ViewModels/MainViewModel.Config.cs:34-51` — `AutoCadDetector.DetectInstallation()` / `FindCadPlugin()`（含硬编码盘符探测、注册表卸载表扫描，`Core/Services/AutoCadDetector.cs:28-89`）**同步**跑在 `LoadConfig()` 里，而 `LoadConfig()` 跑在 `MainViewModel` 构造函数里（`MainViewModel.cs:112`），即 `MainWindow` 构造期间（`MainWindow.xaml.cs:14-17`）⇒ 启动期卡顿。设置向导打开时（`SettingsDialog.xaml.cs:23` → `SettingsViewModel.cs:47/206/212`）与 CAD 测试里（`:224`）还有同样的同步调用。
- `Views/LogViewerPanel.xaml.cs:41-45` — 开启自动滚动时每来一条日志就 `ScrollIntoView`（强制布局）。
- `ViewModels/LogViewModel.cs:118-127` — 每条日志：dispatcher 回调 + 逐条 Add + `RemoveAt(0)` + 全量 `RefreshLevelSummary()`（O(500)）；默认日志级别是 `Debug`（`App.xaml.cs:201`），所有条目都过 `UISink`（`:102`）⇒ 每行日志一次 O(500) UI 计算。

**修复方向**：worker 侧把结果放进锁保护队列，UI 侧用 `BeginInvoke` + `DispatcherTimer` 以 ≤10 Hz 合批刷新；`FilteredEntities` 改为后台构建后整体替换 `ItemsSource`（或用支持批量重置的集合）；硬件探测移到 `Task.Run` 并缓存结果（`SettingsViewModel.DetectAutoCadAsync` 已经是正确写法，直接复用）。

### C3 离线回写"全有或全无"中止，且用户拿不到真实原因

- `Core/Services/DwgWriterService.cs:104-106`：

  ```csharp
  if (translationMap.Count > 0 || result.FailCount > 0 || result.Errors.Count > 0)
      throw new InvalidOperationException("Writeback incomplete; output was not saved. Unmatched handles: " + ...Take(20));
  ```

  替换循环已经全部跑完，只因为一条 handle 没匹配上（或任一 `FailCount`/`Errors`），就**一个字节都不写**。999/1000 成功也全废。
- 异常被 `:135-141` 捕获后把 `SuccessCount = 0; FailCount = entities.Count;` 一起覆盖，真实计数被抹掉。
- 抛出的消息只进日志；界面只显示通用的 `MsgCadExportError`，且消息本身还按 `Take(20)` 截断。用户完全无法定位是哪条文字没匹配上。
- 容易产生未匹配的路径：`Core/Services/DwgTextReplacer.cs:104`（空译文直接 `continue`，但条目已进 map）、`:149-151`、`:171-175`（多内容单元格硬返回 `false`）。

**修复方向**：把严格模式改成显式开关（默认关）；默认保存部分结果，并把未匹配 handle 明细回传到界面/导出报告；`CadWriteResult` 保留真实计数，不因异常清零。

### C4 术语表空条目导致死循环 / OOM

- `Core/Services/GlossaryService.cs:68,94`：`while ((pos = text.IndexOf(entry.Source, searchStart, ...)) >= 0)` 且 `searchStart = pos + entry.Source.Length`。当 `entry.Source == ""` 时 `IndexOf("")` 返回 `searchStart`，`searchStart += 0` 永不推进。
- **已实测确认**（.NET 8）：

  ```
  iter=1 pos=0 searchStart(before)=0
  ...
  iter=5 pos=0 searchStart(before)=0
  LOOP DID NOT TERMINATE IN 5 ITERATIONS -> non-advancing (infinite)
  IndexOf("", 3) on len-3 string = 3
  ```

- `LoadGlossaryAsync`（`:27-45`）不做任何校验，且 `:38` 的 `e.Source.Length` 在 `"source": null` 时直接 NRE。
- 入口：`ImportGlossaryAsync` 对 xlsx 会过滤空行（`MainViewModel.Settings.cs:185`），但 **JSON 是直接 `File.Copy` 覆盖**（`:133`），手改过一个空条目的术语表就会让翻译 worker 空转并持续增长 `matches` 列表直到 OOM。

**修复方向**：加载期丢弃 `Source`/`Target` 为空或纯空白的条目；循环内加"位置未推进即 break"的保护。

### C5 `license.dat`/试用/时钟三处均已确认可绕过

见 C1(c)。这里单列是因为它即使不碰二进制也能利用：删 `license.dat` 重置试用、自写 `license.dat` 直接永久、回拨时钟让订阅不过期。

### C6 CAD 插件侧同样是"全有或全无"，而且触发面比离线更宽

`src/DwgTranslator.Cad/Replacement/AcadWriterEngine.cs:196` 依据 `errors.Count > 0` 抛出并回滚**整个事务**，而 `errors` 被两类"并非失败"的情况污染：

- `:394`（配合 `:129/193/196`）：表格单元格 `Contents.Count != 1` 时被**有意保留**（不翻译），却仍被追加进 `errors` ⇒ 标题栏里只要有一个公式单元格/多段落单元格，**整张图所有已翻译文字全部回滚**。
- `:275`（配合 `:196/249/567`）：`ReplaceEntity` 返回 `false`（内部任意异常、`:419` 译文为空、`:440/:464` 属性写入失败如 `AlignmentPoint`）会把手柄留在 `unprocessed` 里 ⇒ 一条坏实体毁掉整批。同样地 `AvailableTextSpace.Measure` 在 `:138` 抛 `InvalidOperationException("No clear text corridor")`（由 `:428-430` 调用）。

**修复方向**：把"有意保留"与"真正的失败"分开（前者进结果明细/`preserved` 列表，不进致命 `errors`）；逐实体失败默认记账并提交成功项，严格中止改为可选开关。

### C7 CAD 插件只捕获 `CadRuntimeException`，其他异常直接逃出命令方法进宿主

- `Commands/DwgTranslatorCommands.cs:91`、`Extraction/TextExtractor.cs:66`、`Replacement/TextReplacer.cs:74` 都只 `catch (CadRuntimeException)`（按平台在 `TextExtractor.cs:14-16` 起别名）。
- 但被保护的方法体里做的是文件 IO、`JsonSerializer.Deserialize`（`DwgTranslatorCommands.cs:249`）、EPPlus/HTTP、`Convert.ToInt64(handle,16)`（`TextReplacer.cs:94`）、硬转型（`TextExtractor.cs:152`、`TextReplacer.cs:187/192/219`）。普通 `IOException` / `InvalidCastException` / `NullReferenceException` 会穿透 `[CommandMethod]` 到达宿主。
- 另有一批命令把 IO 放在 try/catch **之外**：`VerifyLayoutCommand.cs:99`/`:161`、`LayoutFootprintCommand.cs:40`、`LayoutPlotCommand.cs:70`、`HealthCheckCommand.cs:23`/`:32`（`File.WriteAllText`、`Directory.CreateDirectory` 在 try 外）。

对以 `NETLOAD` 常驻 CAD 进程的插件来说，这是最直接的"把 AutoCAD 带崩"路径。**修复方向**：命令方法体统一 `catch (System.Exception)`（保留 CAD 异常做补充信息），IO 全部移入受保护区。

### C8 交互式回写会把译文写到无关实体上（未校验来源图纸 + 未保存图纸共用空 docKey）

- `Commands/DwgTranslatorCommands.cs:194-211`（配合 `TextReplacer.cs:94-96`）：交互式回写导入任意 Excel，并把其中的 handle 直接在当前活动数据库里解析，**从不比对 `entity.SourceFilePath`**。而 handle 只在单张图纸内唯一 ⇒ 译文会静默落到毫不相关的实体上。
- `:75` 与 `:108` 用 `doc.Database.Filename ?? doc.Name` 作 docKey。未保存的新图纸 `Filename` 是 `""`（不是 `null`），`??` 永不触发 ⇒ 所有未保存图纸共享 docKey `""`，实体集合互相污染（`_extractedEntitiesByDoc` 是进程级静态字典，见 Medium 段）。

**修复方向**：改写前校验 `SourceFilePath == doc.Database.Filename`；用 `string.IsNullOrEmpty` 判断；以 `Database`/`Document` 实例身份为键。

### C9 【实测定位】旋转标注的译文被静默丢弃，图纸交付后仍是中文

**现象**：用户交付的 `衡直_投料站_01(1)_translated.dwg` 中，`冷机温度`、`急停`、`停止指示`、`运行指示`、`故障报警`、`蜂鸣器` 等 20 条标注仍是中文，而同一张图上 `热机温度 → Heat Engine Temperature` 等 370 条已正常翻译，且导出报"成功"。

**证据**（`%APPDATA%\DwgTranslator\logs\cad_plugin_20260910_195406.jsonl`）：
```
WRN  Layout fit rejected 2FFDE: 急停; height=2.5; width=27.6274068040628
WRN  Layout fit rejected 3031A: 冷机温度; height=2.5; width=27.6274068040628
WRN  Original text preserved after layout rejection for handles: 2FE64, 2FFDE, 302AD, 302AE, ... 309B6
INF  Measured envelope verification passed for 370 replacements
INF  AcadWriter: saved DWG to C:\Users\GQL\Desktop\衡直_投料站_01(1)_translated.dwg
```
`急停 → Emergency Stop`、`冷机温度 → Chiller temperature`、`蜂鸣器 → Buzzer` 在 `translation_cache.json` 里都有正确且不长的译文 —— **问题不在翻译，而在排版拟合策略**。

**根因（三处叠加）**：

1. `Replacement/AvailableTextSpace.cs:90-93` —— 非 90° 倍数的旋转文字**根本不测量可用空间**：
   ```csharp
   bool vertical = characterColumn || Math.Abs(Math.Cos(rotation)) < .001;
   if (!vertical && Math.Abs(Math.Sin(rotation)) >= .001) return original;   // 45° 走这里
   ```
   于是 `originalBounds` 就是**原文字自己的包围盒**，紧接着 `IsInsideOriginalEnvelope` 要求译文必须塞进那个紧贴的框 —— 中文短、英文长，必然要大幅缩小。
2. `Replacement/AcadWriterEngine.cs:723-755`（改前）—— 旋转分支每轮只按 `ratio` 猜一次并 `Clamp(ratio, 0.20, 0.98)`，**最坏一步把高度打到 20%**；要么卡在偏好阈值之上不动、要么直接越过它。而轴对齐分支（`:688`）走的是**区间 `[0.70·h₀, h₀]` 的 14 步二分搜索**，总能取到"能放下的最大高度"，所以 370 条轴对齐/宽矩形标注全部成功，45° 标注成批失败。
3. `AcadWriterEngine.cs:734`（改前）`if (text.TextHeight < originalHeight*.70) return false;` —— **这条路径一行日志都不打**，直接丢弃译文还原原文。这正是日志中 2FFDE/3031A 只有"拒绝"结论、没有任何原因行，而其它拒绝行（`:253`）都带明细的原因。

**为什么一直没被发现**：`Cad/Log.cs:68` 的 `FormatMessage` 正则 `\{[A-Za-z_]\w*\}` 匹配不到 `{OldW:F3}` 这类带格式说明符的占位符 → `string.Format` 抛异常 → 走兜底分支，把**模板原文 + 参数转储**写出去。日志里第一条 `category="Envelope fit MText {Handle}: width {OldW:F3}->..."` / `message="2FE2F [54.88, 18.08, 3, 2.23]"` 就是这个后果。全项目 21 处带 `:F1/:F2/:F3/:P0` 的日志（`AcadWriterEngine` 11 处、`LayoutOptimizer` 4 处、`CollisionResolver` 5 处）全部因此失效。

**已实施的修复**：
- `AcadWriterEngine.cs` 旋转分支改为与轴对齐分支一致的**"求最大可行高度"二分搜索**（16 步，区间 `[floor, ceiling]`），存在解就一定能取到；
- 拟合唱词新增 `WritebackConstants.PreferredFitHeightRatio = 0.70`（干净拟合）与 `AbsoluteMinFitHeightRatio = 0.40`（可接受的缩放下限）：低于 0.70 但 ≥0.40 时**接受译文并记 Warning**（图变成英文，字号偏小可复核），低于下限才放弃原文，且**必定带原因日志**；DBText 路径同样处理；
- `Original text preserved after layout rejection (...)` 现在把 **handle 与原文一起**打出（`2FFDE=急停; 3031A=冷机温度`），不再是光秃秃的 handle 列表；
- `Cad/Log.cs` 正则改为 `\{[A-Za-z_]\w*(:[^}]*)?\}` 并在替换中保留格式说明符，21 处日志恢复正常输出（已用 .NET 8 实测验证新旧行为）。

**实测验证**：`dotnet build` 全解决方案 0 warning / 0 error；日志格式化修复用 .NET 8.0.29 复现脚本对比（旧正则 → 模板+参数转储；新正则 → `Envelope fit MText 2FFDE: accepted shrunk height 2.500->1.250 (50%); width 27.630->9.400`，且无占位符的模板行为不变）。**未在真实 CAD 宿主中做端到端回归**（本机无 AutoCAD/GstarCAD 会话），需重跑该图纸确认。

**第二轮（修复首次部署后重跑，暴露第二条拒绝路径）**：新日志 `cad_plugin_20260910_200522.jsonl` 显示第一轮修复**完全生效**——`Layout fit rejected` 由 64 条降为 **0 条**（132 条 DBText + 79 条 MText 拟合成功、26 条走新增的"接受缩小"分支、11 条满高度即通过），日志格式化也恢复（`Envelope fit MText 2FFDE: accepted shrunk height …`）。但失败数由 20 升到 **36**，且 36 条**全部**来自另一条路径：

```
WRN  Rendered interference rejected 38C0E; original entity restored: CONFLICT=38C0E,302AE|KIND=TEXT|OBSTACLE=((6026.09,8260.34,0),(6048.24,8279.84,0))
INF  Measured envelope verification passed for 370 replacements
```

**根因**：`AcadWriterEngine.cs:154-185` 的"渲染干涉复检"用**世界轴对齐墨迹 AABB**（`AvailableTextSpace.FindIntersections`，容差 0.01）判定重叠，而 45° 旋转文字的墨迹 AABB 是被放大过的正方形。从新日志提取全部 49 条冲突明细后，结构一目了然：y≈8260 那一排是**连杆式成对相邻冲突**（`2FE64↔309B6↔309B7↔309B8↔309B9↔309BA↔309BB↔309BC`、`309EC↔314BE`、`30D3E↔30DF2` 等），每个墨迹 AABB 宽 27–30 单位、行距约 25 单位 ⇒ **真实重叠仅 1–4 单位**（另有 11 条 `KIND=GEOMETRY`，是 4.4 单位的指示灯图形被标签压到）。而第一轮修复让译文"尽量大"地填满各自的原框，相邻两框因此互相侵入 —— 复检发现冲突后直接 `CopyFrom(OriginalSnapshot)` 还原原文，于是又变回中文。**这是"修好一处、撞上另一处"，不是第一轮修复失效。**

**第二轮修复**（`AcadWriterEngine.cs:187-202` + 新增 `TryShrinkUntilClear`）：
- 复检发现冲突时**先尝试继续缩小**（高度与 MText 换行宽度**等比**缩放，步长 0.85，下限同为 `AbsoluteMinFitHeightRatio`），缩到不再重叠就**保留译文**并记 Warning（`resolved by shrinking {Handle} in {Steps} step(s)`），只有缩到下限仍无法分离才还原原文 —— 即"还原成中文"从首选项降级为最后手段。
- 等比缩放保证每行字数与行数不变（字形与换行宽度同比例缩小），因此只是整体块变小，不会因换行变化反向长高。
- 每次缩小后 `AvailableTextSpace.Refresh(tr)` 重建障碍缓存（否则刚缩小的实体仍按旧尺寸参与判定，冲突看起来"无法解决"）；且在缩小**之前**先复检一次——同排邻居的缩小可能已经顺带解决本行，避免无谓缩小（记 `cleared by a neighbouring shrink`）。
- 汇总行改为 `Measured envelope verification passed for {Count} replacements ({Shrunk} separated by shrinking, {Preserved} kept original)`，一眼可见还剩多少条没翻译。
- 缩小只会让实体的世界 AABB 单调变小且不移动锚点，因此**不会引入新的重叠**，是安全的方向性操作。

**遗留（本次未改，属更深层设计问题）**：`AvailableTextSpace.Measure` 对非 90° 旋转直接 `return original`，是"信封=原文自身框"的根源；`FindIntersections` 对旋转文字用轴对齐 AABB 判定，是这 36 条 1–4 单位假/近重叠的根源。正解分别是"在文字局部坐标系测量走廊"与"对旋转文字做定向矩形相交（SAT）"。本次两级修复（接受适度缩小 + 干涉时继续缩小）已把"还原成中文"压缩到只有真正放不下时才发生，但没有消除上述两处几何近似。

### C10 【实测定位】DBText 被横向压扁（`WidthFactor` 下限写成了绝对值）

**现象**：端子表（90° 竖排表头）里 "Terminal Description" / "Item No." / "Wire Number" 等文字明显扁平。

**证据**（`cad_plugin_20260910_201108.jsonl`，132 条 DBText 拟合事件，数值从日志的参数转储中还原）：

| 宽度因子保真度 W1/W0 | 条数 |
|---|---|
| ≥0.95（未变形） | 13 |
| 0.80–0.95 | 39 |
| <0.80（肉眼可见压扁） | **80** |
| <0.65（严重压扁） | **41** |

典型样本：`311DB [0.75, 0.682, 4.5, 4.5]`、`31B59 [0.75, 0.423, 4, 4]` —— 源图本来就是 `WidthFactor = 0.75` 的压缩表格字，拟合又把它压到 **0.423（原值的 56%）**，而**高度完全没动**（4→4）。即"宁可横向压扁，也不肯等比缩小"。

**根因**：`AcadWriterEngine.FitDbTextToOriginalEnvelope` 的下限是
```csharp
double minimumWidth = Math.Min(startWidthFactor, .40);   // 绝对值 0.40
```
取"原始宽度因子"与"绝对 0.40"的较小者 ⇒ 对源图 0.75 的字允许压到 0.40（损失 47% 字宽）而没有任何相对约束。低于该下限时才用 `text.Height *= desiredWidth/minimumWidth` 等比补偿，但下限太低，等比分支几乎不会被触发。

**修复**：下限改为**相对源 aspect** —— 新增 `WritebackConstants.MinWidthFactorRetention = 0.90`，`minimumWidthFactor = startWidthFactor × 0.90`；超过这个幅度就不再压字宽，改由**等比缩小高度**承担（三处：循环内、接受判定、最终判定）。按上表核算，此前被压到 0.40/0.75（=0.53）的最坏样本，改造后宽度因子止于 0.675，余量由高度 0.66× 吸收 —— 仍高于 40% 高度下限，因此**不会因此丢译文**，只是文字等比变小、不再变形。该常量可调（→1.0 完全不压；越小保留越多高度）。

**同轮附带修复**：`Log` 的 `(category, message, args)` 重载构成**重载解析陷阱** —— `Log.Information(模板, 字符串handle, 数字…)` 会被编译器绑定到该重载（第二参数是 string，比 `params object?[]` 更 specific），于是"模板"进了 `category`、`message` 变成 handle，输出退化成 `"311DB [0.75, 0.68, 4.5, 4.5]"`。这正是上一轮"正则修好了却仍读不出 DBText 数值"的原因，也是该 bug 长期隐身的原因。已把两个重载改名为 `InformationCategorized` / `DebugCategorized`（仅 2 处调用点，同时把 `{0}/{1}` 位置占位符改为命名占位符），此后 `Information(template, string, …)` 只可能绑定到正确的 params 重载。

---

## 2. HIGH

### H1 生成的 CAD 脚本编码与引号错误 → 在线回写必现失败

- `src/DwgTranslator.App/Services/AutoCadInteropService.cs:334-342` 写批处理 `.scr`：
  `File.WriteAllLines(scriptPath, [ "_.NETLOAD", cadDllPath.Replace('\\','/'), ... ], System.Text.Encoding.Default)`
- **实测**：.NET 8.0.29 下 `Encoding.Default` = `utf-8` / `cp65001`（不是 ANSI）。而 CAD 的脚本/LISP 引擎按系统 ANSI 代码页解析 ⇒ 路径含非 ASCII 时乱码，`NETLOAD` 拿到错路径。
  同源问题：`:307` 的 `File.WriteAllText(lspPath, lspContent)` 也是无 BOM 的 UTF-8。
  触发条件很现实：`%TEMP%\DwgTranslator\<id>` 含 Windows 用户名（中文用户名常见），以及插件被放在中文目录时。
  注意修复陷阱：**.NET 8 上直接 `Encoding.GetEncoding(936)` 会抛 `NotSupportedException`**（实测），必须 `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` 并引用 `System.Text.Encoding.CodePages` 包。
- 引号缺失：`.scr` 是逐行喂给 CAD 命令行的，路径**没有任何引号包裹**。装到 `C:\Program Files\...` 或任何含空格目录时，CAD 会把路径按空格切成两个参数 → `NETLOAD` 失败或误执行下一条命令。`.lsp`（`:295-306`）只做了反斜杠转义，未处理双引号/换行，同样可被路径内容打断语句。
- **同样的问题在第二处**：`ViewModels/SettingsViewModel.cs:242-249` 生成"测试 CAD 集成"脚本时，既用了 `Encoding.Default`（同样是 UTF-8），又直接把 `CadPluginPath` 裸写进脚本行（`:244-245`）不加引号。两处请一起修，并加"含空格路径 + 中文路径"的回归用例。

### H2 配置包的 `settings.json` 会让全新安装进入"配置加载失败"状态

链路：`installer/DwgTranslator.iss:38` 打包的是**仓库根目录**的 `settings.json`（该文件被 `.gitignore:35` 忽略，本机上是 **0 字节**）→ `[Code]` 段 `:70-71` 在 `ssPostInstall` 把它复制到 `%APPDATA%\DwgTranslator\settings.json`（因为目标不存在）→ 之后 `App.xaml.cs:113-122` 发现文件已存在，**不再复制**随包发布的合法模板。

于是 `MainViewModel.LoadConfig()`（`MainViewModel.Config.cs:27-28`）对空字符串执行 `JsonSerializer.Deserialize<AppConfig>("")` → 抛 `JsonException` → 被外层 `catch` 吞掉（`:88-92`），**整段配置初始化被跳过**，后果：

- `AutoCadDetector` 安装路径/CAD 插件路径自动解析不再执行（`:34-52`）；
- API Key 的 DPAPI 解密不再执行（`:63`）；
- `ExportDirectory`/`LogDirectory`/`GlossaryPath` 路径解析、`Directory.CreateDirectory` 不再执行（`:65-76`）；
- `RefreshGlossaryData()` 不再执行（`:78`）；
- **持久化翻译缓存不再创建**（`:81-82`），`_consistencyService` 停留在构造函数里的纯内存实例（`MainViewModel.cs:110`）⇒ README 宣传的"翻译缓存"静默失效；
- 界面显示"配置加载失败 — 请检查设置文件"。

同时 `tools/Verify-ReleasePackage.ps1:29` 只断言 `licensingEnabled -eq $false`，**不校验 `deepSeekApiKey` 是否为空**；而 `AppConfig.cs:64-65` 为兼容旧版会静默接受明文 Key ⇒ 在已配置的开发机上打出的包会把明文 Key 一起发出去。

（注：`publish.bat` 那条链路用的是 csproj 里 `settings.json.example` 的链接副本，是合法的；出问题的只有安装器这条链路。两条包装链路行为不一致本身就是缺陷。）

### H3 图纸内容与模型返回默认明文落盘

- 默认 `MinimumLogLevel = "Debug"`（`AppConfig.cs:39`、`App.xaml.cs:192/201`、`Cad/Log.cs:18`）。
- 落盘内容：`DeepSeekClient.cs:56`（模型返回前 100 字符）、`:42`（完整 API 错误体）、`TranslationConsistencyService.cs:86-88,96-98,110-112`（原文与译文，**每条缓存新增一条 Debug**）、`Cad/Replacement/AcadWriterEngine.cs:253-255`（`textEntity.PlainText` 原始图纸文字）。
- 位置：`%APPDATA%\DwgTranslator\logs`，保留 14 天（`App.xaml.cs:96-101`），另有 CAD 侧 `cad_plugin_*.jsonl`（`Cad/CadFileLogger.cs:112`）。
- 机械图纸文字属客户敏感资料。**建议**：默认 `Information`，把 payload 日志放到显式开关后；`translation_cache.json`（`MainViewModel.Config.cs:81`，同样是明文原文/译文）提供加密或退出选项；界面上明确告知文字会发送到 DeepSeek（目前只在 resx 里提了 API Key，见 `Strings.resx`）。

### H4 网络与 API 客户端健壮性

- `MainViewModel.Translation.cs:216-219`、`SettingsViewModel.cs:131-133`、`Cad/Commands/DwgTranslatorCommands.cs:127-128`：`BaseAddress` 直接取用户可编辑的 `DeepSeekBaseUrl`（`AppConfig.cs:18`），**不校验 scheme**，紧接着就附加 `Authorization: Bearer <key>`。改成 `http://` 即把 Key 以明文发给任意主机。证书校验本身干净（全项目无 `ServerCertificateCustomValidationCallback` / `ServicePointManager` 覆写）。
- `Core/Services/DeepSeekClient.cs:36-57`：不区分状态码（401/402/429/5xx 全塌成一个通用 `HttpRequestException`）；无 `max_tokens`；**忽略 `finish_reason`** ⇒ 被长度截断的译文会被当作成功结果写回图纸；`HttpResponseMessage` 未 `Dispose`；`ReadFromJsonAsync` 的 `JsonException` 未处理（代理返回 HTML 时会变成逐条实体失败）。
- `Core/Services/TranslationService.cs:361-383`：重试是固定 `2^i` 秒、**无抖动、不读 `Retry-After`**，而 12 个 worker 会同步重试（惊群）；429 后只重试 3 次。
- 叠加后果：**API Key 无效（401）时，会对每个唯一文本重试 4 次**——8000 条图纸 → 约 3.2 万次注定失败的请求。应按状态码把不可重试错误（401/402/400）立即冒泡。

### H5 关闭窗口即释放，可能截断输出图纸

`Views/MainWindow.xaml.cs:22` — `Closing += (s,e) => vm.Dispose();`：无确认、无 `e.Cancel`、不等待进行中的操作。`MainViewModel.Dispose`（`MainViewModel.cs:137-150`）取消 CTS 并 `Dispose` 掉 `_httpClient`，而 `Task.Run(() => _dwgWriterService.WriteTranslations(...))`（`ImportExport.cs:317`）或 CAD 批处理进程（`AutoCadInteropService.cs:356-403`）可能正在写文件 ⇒ 输出图纸被截断 + 后台线程抛 `ObjectDisposedException`，且没有任何"存在未保存译文"的提示。

### H6 在线回写超时后仍删除会话目录

`AutoCadInteropService.cs:150-174`：`WaitForCompletion`（`:417-461`）超时上限是 `120 + 实体数/15`，夹在 [120, 600] 秒。超时只是"放弃等待"，**不会向 CAD 发送任何取消命令**，CAD 侧插件可能仍在写回；而 `finally`（`:173-174`）立刻删掉 `sessionWorkDir`（内含 `writeback_config.json` 与 `doneSignalPath`）⇒ 插件随后写完成信号必然失败。大图纸必然命中。

### H7 Excel 导出/导入没有授权门禁

`ViewModels/MainViewModel.ImportExport.cs:371-389` — `ExportExcelAsync` / `ImportExcelAsync` 都不检查 `CanExecuteOperation()`，而 `TranslateAsync`（`Translation.cs:21`）和 `ExportDwgAsync`（`ImportExport.cs:110`）都检查。等于授权启用后仍可无授权把全套译文导出成 Excel，产品价值被完整取走。

### H8 回写期 O(n²) 全文重扫

- `Core/Services/DwgCollisionDetector.cs:114,183-217` + `CadGeometryHelper.cs:74-127`，由 `DwgTextReplacer.cs:219,241` **逐实体**调用 ⇒ 每条被替换的文字都重扫整个集合、递归重算 Insert 的 AABB，且无缓存。
- `Core/Services/DwgFrameDetector.cs:108` 的 `doc.BlockRecords.FirstOrDefault(...)` 被 `DwgWriterService.cs:75,83,95` 对模型空间、每个布局、每个块定义分别重跑 ⇒ 复杂度约 C×N×B。
- **建议**：每份文档构建一次空间索引（网格或 R-tree）+ 一次块名字典。

### H9 术语表导入"先覆盖、后校验"

`MainViewModel.Settings.cs:125-155`：JSON 分支在 `:133` 直接 `File.Copy(dialog.FileName, targetPath, overwrite: true)`，**在校验内容之前**就覆盖了正在使用的 `mechanical_zh_en.json`；随后 `LoadGlossaryAsync`（`:153`）解析失败，旧术语表已经没了（无备份、无"临时文件—校验—替换"）。另外该命令把错误信息硬编码成中文（`:159`），且导入/切方向后**不清空翻译一致性缓存**。

### H10 翻译一致性缓存键不含语言方向

- `Core/Translation/TranslationConsistencyService.cs:31,46,79,219-222`：键是 `text.Trim()`（仅规范化换行），**没有源语言/目标语言**，且 `FlushCache`/`LoadCache`（`:135-190`）跨会话持久化。
- `MainViewModel.Settings.cs:66` 切换翻译方向时只刷新术语表，**不清缓存**。
- 影响修正（重要）：我复核了 `MainViewModel.Translation.cs:174-190` 的调用点——缓存命中后仍要过 `TranslationQualityValidator.IsAcceptable`，而该校验按目标语言要求/禁止 CJK，因此**方向错配的缓存值多数会被判不合法并移除**，主要后果是缓存抖动与一次多余 API 调用，而不是"把英文写进中文图纸"。仍应修：键改为 `src|tgt|text` 并带版本号，方向切换和术语表导入后清缓存。

### H11 未转义的 LISP / NETLOAD 路径（供应链卫生）

`AutoCadInteropService.cs:285-311` 与 `Core/Services/AutoCadDetector.cs:178-222`：`FindCadPlugin` 会依次探测 `appDir\CadPlugin`、`appDir`、相对 `publish` 目录、**开发期 bin 目录**（`net48`/`net8.0`、Debug/Release），找到就把该 DLL `NETLOAD` 进 CAD。安装器用 `PrivilegesRequired=lowest` + `{autopf}`（`DwgTranslator.iss:14,23`）⇒ 目录对当前用户可写。风险限于同一用户自身权限，但两个现实问题成立：(1) 生产包会加载开发树里**陈旧/不匹配**的插件（版本错配）；(2) 放入同名 DLL 即被执行。另外 `SettingsViewModel.cs:237-249` 会把 `AppConfig.CadPluginPath` 里任意路径 NETLOAD 进新起的 CAD 进程。**建议**：只信任随包发布路径 + 校验哈希/签名。

### H12 依赖漏洞审计被关闭

`publish.bat:15`（`-p:NuGetAudit=false`）与 `src/DwgTranslator.App/DwgTranslator.App.csproj:54`（`NuGetAudit=false`）⇒ 每次发布构建都不产出漏洞数据。版本本身都是固定引用的（好事），但没有 `Directory.Packages.props` 与 `packages.lock.json`，还原不可复现。建议移除这两处开关并加 `NuGetAuditMode=all`。

### H13 MLeader 引线文字**静默不翻译，却被记为成功**

`src/DwgTranslator.Cad/Replacement/TextReplacer.cs:159-175`：

```csharp
var originalHeight = mLeader.MText?.TextHeight ?? DefaultTextHeight;   // 每个 mLeader.MText 读取都返回一个"游离副本"
if (mLeader.MText != null) {
    mLeader.MText.Contents = entity.TranslatedText...;   // 改的是副本，从未 mLeader.MText = ...
    MapTextStyle(mLeader.MText.TextStyleId, db);         // 又是另一个副本
}
return new EntityReplaceResult { Success = true, ModifiedEntity = mLeader.MText, ... };
```

`MLeader.MText` 的 getter 返回**游离副本**（setter 才写回），因此引线文字从未被真正修改，方法却返回 `Success = true` 并被计入 `result.SuccessCount`——即"报告成功、实际没改"。对照 `AcadWriterEngine.cs:557` 用的是正确写法 `mLeader.MText = leaderText`。同时两个副本都没 `Dispose`（`:161` 与 `:162/168/173` 各自 new 一个）。该路径经 `DwgTranslatorCommands.cs:211` 的交互式回写命令可达。

### H14 CAD 插件的原地覆盖写会失败（output == source）

`Replacement/AcadWriterEngine.cs:52-71`：当 `outputFilePath == sourceFilePath` 时，`:69` 的 `File.Delete` 在 `db` 仍通过 `ReadDwgFile` 持有源文件时执行 ⇒ `IOException`，被 `:75` 捕获，整个作业失败并给出误导性信息。另 `:65` 用 `DwgVersion.Current` 另存会强制把输出升到最新版本。**修复方向**：先存临时文件 → 释放 `Database` → 再移动/替换；保留源图纸版本。

### H15 块展开递归没有 visited 集合 → 指数级展开 + 宿主卡死

`Replacement/AvailableTextSpace.cs:50-58`（`depth < 8`）+ `CollisionDetector.cs:84-118` + `AcadWriterEngine.cs:157-167`：块展开**没有 visited 集合**（而 `Extraction/TextExtractor.cs:40/107` 有），自引用块或被广泛复用的块会被反复展开；每个障碍 MText 还会被 `Explode` 成字形克隆（`CollisionDetector.cs:88`），且障碍集合在逐实体后处理循环里**被重复测量**（`:157-167`）。这是数千个标注的图纸上"CAD 卡死"的路径。

顺带：`AcadWriterEngine.cs:36` 的 `ExhaustiveCollisionEntityLimit = 400` **全项目无引用**（grep 确认），即 `:32-35` 注释里承诺的"有界快速路径"根本不存在，昂贵扫描永远执行。

### H16 net48（浩辰CAD）侧的 JSON 处理有硬上限且行为不一致

`Commands/WritebackCommand.cs:80-87`（配合 `DwgTranslator.Cad.csproj:41`）：`#if NETFRAMEWORK` 分支用 `JavaScriptSerializer`，其默认 `MaxJsonLength` ≈ 2 MB，远低于真实回写配置；且没有 `PropertyNameCaseInsensitive`，而 net8 分支（`:83-86`）有。⇒ 大作业、或大小写不同的 JSON，在 AutoCAD/net8 上能跑、在浩辰CAD/net48 上只报 `"Command error: …"`（`:149`）。**修复方向**：两个 TFM 用同一个序列化器（System.Text.Json 或 Newtonsoft），或显式提高 `MaxJsonLength`。

### H17 宿主版本与目标框架不匹配：AutoCAD 2021-2024 的插件根本无法 NETLOAD

`Directory.Build.props:13-16` 的探测列表包含 `D:\autocad2021\AutoCAD 2021`，而 `:20-23` 只要发现 AutoCAD（`CadPlatform != 'GstarCAD'`）就固定 `CadPluginTargetFramework = net8.0`，`DwgTranslator.Cad.csproj:4-5` 也据此产出 `net8.0`。但 **AutoCAD 2021-2024 是 .NET Framework 4.8 宿主**（2025 起才是 .NET 8）⇒ 为这些版本构建出的程序集在宿主里无法加载。`DwgTranslator.Cad.csproj:2` 的注释"AutoCAD builds retain the .NET 8 target"正是这个错误假设。README 声称支持 "AutoCAD 2021-2026"，与实现不符。**修复方向**：在 `Directory.Build.props` 里把宿主版本映射到 TFM（2021-2024 → net48，2025+ → net8.0）；顺带注意 `:22-23` 算出的 `CadPluginTargetFramework` **没有任何项目消费**（csproj 自己重算），改它不会有任何效果。

### H18 公共入口在无 `DocumentLock`、无文档上下文保证的情况下改数据库

`Replacement/AcadWriterEngine.cs:94` 与 `Replacement/TextReplacer.cs:34-40` 都是 `public` 入口，直接修改调用方传入的 `Database`，既不加 `DocumentLock` 也不检查是否存在文档上下文（`TextReplacer.ReplaceAll` 由 `DwgTranslatorCommands.cs:211` 调用）。任何从 WPF 线程、`DocumentManager` 事件或非模态对话框发起的调用都会在无宿主锁的情况下改库。另外 `LayoutPlotCommand.cs:28-29` 打开文档并重设 `MdiActiveDocument`，**既不还原原文档也不关闭它**。**修复方向**：在公共 API 内部要求/获取 `DocumentLock`（或显式声明前置条件），绘图命令里关闭并还原文档。

### H19 在 CAD 主线程上用 `.GetAwaiter().GetResult()` 阻塞

`Commands/DwgTranslatorCommands.cs:85`、`:123`、`:140-143`、`:163`、`:207` 直接同步等待 HttpClient/EPPlus/翻译任务。AutoCAD 安装了自己的同步上下文，因此这些服务内部任何未加 `ConfigureAwait(false)` 的 `await` 都会造成**宿主死锁**；最坏情况主线程冻结数分钟（"无响应"）。**修复方向**：放到 worker 任务上执行，只把简短的 `Editor.WriteMessage` marshal 回主线程；或在 Core 中统一保证 `ConfigureAwait(false)`。

---

## 3. MEDIUM

**功能缺陷**

- 实时搜索实际不生效：`Views/Controls/FilterStatsBar.xaml:69` 用 `UpdateSourceTrigger=PropertyChanged` 绑 `SearchText`，但全项目**没有** `OnSearchTextChanged` 分部方法（只有 `OnFilterStatusTextChanged`，`Operations.cs:46`）⇒ 打字只改属性不过滤，只有回车（`FilterStatsBar.xaml.cs:15-22`）才生效；`ApplySearchCommand`（`Operations.cs:57-58`）没被任何控件绑定。
- 导入后看不到已导入内容：`Views/Controls/TranslationDataGrid.xaml:161` 的 `EmptyStatePanel` 是不透明遮罩且是 Grid 最后一个子元素；`TranslationDataGrid.xaml.cs:49-59` 在 `TranslatedCount == 0 && !IsProcessing` 时显示它 ⇒ 导入完立即盖住整个表格（且无关闭入口），用户无法在翻译前核对/修改提取结果。该遮罩还会随每次 `TranslatedCount` 变化重算（`.xaml.cs:29-33`）。
- 多岛 MText 内容丢失：`Core/Services/TranslationService.cs:307-351`，译行数与原岛数不相等时，除最长岛外**全部清空**（`:350`）；`:324-335` 的前缀分支用 Ordinal `StartsWith` 比较修剪后的首岛，较脆。
- 质量校验过严：`Core/Services/TranslationQualityValidator.cs:19-26`，ZH→EN 时只要译文含任何 CJK 即判失败 ⇒ 图号/型号等**应当保留**的中文会被重试 4 次后硬失败（`TranslationService.cs:197`），丢掉本可用的结果；同时未知目标语言直接 `return true`（过宽）。
- 术语表方向错写：术语表编辑/导入始终写固定文件 `mechanical_zh_en.json`（`MainViewModel.Settings.cs:125`、`Config.cs:139`、`GlossaryManagerDialog.xaml.cs:62`），而编辑列表来自当前方向（`Settings.cs:93`）⇒ EN→ZH 模式下的编辑会覆盖 ZH→EN 词条（影响为推断）。`GlossaryManagerDialog.xaml.cs:57-59` 静默丢弃含空白行、保存前不 `CommitEdit`（正在编辑的单元格丢失）。
- `ExcelService`：表头一半本地化一半硬编码英文（`:28-34`）；工作表名硬编码 `"Translations"`（`:79`），改名即抛英文异常；`Columns().AdjustToContents()`（`:59`）对大表极慢且**不响应取消**；原文直接写入单元格，推断 ClosedXML 会把以 `=` 开头的内容当**公式**（表格公式注入），单条超 32,767 字符也会整表导出失败。
- 导出模式对话框单击即开跑：`Views/Controls/ExportModeDialog.xaml.cs:44-57`，一次 `MouseLeftButtonDown` 就 `DialogResult = true`，直接启动数分钟的 CAD 回写，无二次确认、无防重复点击。
- 多文件导出要用户重新用文件对话框选源图纸（`ImportExport.cs:233-241`），而不是下拉选择，交互奇怪。
- `MainViewModel.Translation.cs:111-112` 与 `:150-151` 对同一次翻译做了**两遍** `ApplyFilter()` + `UpdateStatistics()`（各 O(n)）。
- `ExcelService` 之外，`SettingsViewModel.cs:226-293` 的 CAD 测试也硬编码中文（`"正在启动CAD并验证插件…"` 等），异常消息直出给用户。

**并发 / 线程**

- `Core/Logging/CadLogReaderService.cs:115` 在 `StreamReader` 预读之后取 `stream.Position` ⇒ 跳过未读/半行内容（日志查看器缺行/串行）；`_filePositions`/`_processedFiles`（`:18-19`）被定时器和公开方法 `ScanForNewEntries` 无锁并发访问（`Dictionary` 非线程安全）；`Dispose`（`:134`）不等待进行中的轮询。
- `MainViewModel.Translation.cs:100-105`：后台线程读 `_config`（`BatchSize`/`MaxRetryCount`/`MaxTranslationConcurrency`），而 `Settings()` 保存时会在 UI 线程替换 `_config`（`Settings.cs:222`）⇒ 竞态。
- `Services/AutoCadInteropService.cs:119,146,240,279`：COM 附加与 `SendCommand` 跑在线程池 MTA 线程上（调用方 `ImportExport.cs:309` 包了 `Task.Run`），而 AutoCAD 自动化是 STA 单线程服务（推断：`RPC_E_WRONG_THREAD` 与长时间停顿风险）。建议用专用 STA 线程。
- `Core/Services/LicenseService.cs:30,40,119-121,161,190`：`CurrentLicense` 把可变实例暴露到锁外；`Activate`/`LoadLicense` 在锁外替换 `_license`；`ConsumeTrialUse` 在锁内递减却**在锁外** `SaveLicense`（非原子 `File.WriteAllBytes`）⇒ 可能丢计数或写坏文件。`:76` 的 `_license == null` 判断是死代码（字段在 `:22` 已初始化）。
- `Core/Logging/InMemoryLogStore.cs:28-38`：`capacity / 10` 在 capacity ≤ 9 时为 0 ⇒ 永不裁剪（无界增长）；`EntryAdded?.Invoke` 在锁外触发，订阅者抛异常会打断日志调用点。

**可维护性 / 架构**

- **本地化绕过约 30 处**（Core 有 337 条资源键，界面却大量硬编码）：`MainViewModel.cs:134`、`ImportExport.cs:132/134/135/158-160/178/278`、`Settings.cs:139/150/159/170`、`SettingsViewModel.cs:226-293`、`TranslationDataGrid.xaml.cs:42-55`、`LogViewModel.cs:82-83/96/193`、`GlossaryManagerDialog.xaml.cs:122/127`、`AutoCadInteropService.cs:355`、`MainWindow.xaml:141`、`TitleBarControl.xaml:81`、`HelpDialog.xaml:38-111`（约 50 条）、`LogViewerPanel.xaml:52-117`（另有整套硬编码深色配色 `#1E1E1E`/`#2D2D2D`/`#3C3C3C` 出现在浅色窗口里）。
- `Converters/LocalizeExtension.cs:31-38` 在 XAML 加载时**一次性**解析键并返回纯 `string`，注释里承诺的运行时切换语言没有实现；`:33` 的 `Args == null` 分支不可达。
- `Core/Services/CadLabelCompactor.cs:12-31` 把**某一个具体项目**的中文标签（`共 页`/`处数`/`制图`/`旧底图总号`/`日光灯`/`真空泵`/`主机料筒`/`共挤电流测量`）连位置式 `key.Substring(3)` 硬编码进通用库，且只在目标语言为 EN 时生效（`TranslationService.cs:98`，且该处用了大小写敏感的 `== "EN"`）。应下沉为术语表数据。
- `DwgWriterService.cs:112-116` 静默把 AC1021 输出改写成 AC1024；`:162-181` 备份在 `.bak` 已存在时永久跳过（陈旧备份），备份失败只 `Warning` 后继续；`:125` `File.Move(overwrite: true)` 没有 `output == source` 的兜底判断（UI 层有拦，`ImportExport.cs:275-282`，但服务层没有）。
- `Core/Services/DwgReaderService.cs:56-64,113` 全模型空间和所有布局共享一个 `visitedBlocks`（多布局引用同一块时只枚举一次，丢失按布局归属）；`IsXref` 在 `:162,187,208,229,245,289` 恒为 `false`，而 `IsExternalReference` 已算出（`:141-142`）⇒ 模型字段失效，`TranslateAsync` 里的 `!e.IsXref` 过滤是空操作。
- `Core/Services/DwgFontManager.cs:19-37`：无论是否被引用都给每个输出文档加入 `Arial`/`Helvetica`（或 `SimHei`）文字样式（样式表污染），`:47-52` 的 `isShx` 分支对这几个名字不可达。
- `Core/Services/TranslationService.cs:126-129` 的 `catch (AggregateException)` 不可达（`await Task.WhenAll` 直接抛第一个异常）⇒ 一处失败整批中止，与 `"Some translation tasks failed"` 的意图不符。
- `Core/Services/LocalizationService.cs:24-28,52-61` + `ILocalizationService.cs:7,20,24`：`LanguageChanged` 的 `add`/`remove` 为空（订阅者永不收到通知）、`SetLanguage` 是 `[Obsolete]` 空实现、`SetLanguageInternal` 是死代码，注释仍在承诺运行时切换与 `Strings.en-US.resx`。
- `Core/Resources/Strings.cs:53-58` 只捕获 `MissingManifestResourceException`，模板/参数不匹配时的 `FormatException` 会抛到调用方；`:21` 的 `_resourceManager ??=` 非线程安全。（注：我逐键比对过全部 337 个资源键与调用点，**当前无缺失键、无占位符数量错配**，此处只是潜在脆弱点。）
- MVVM 泄漏三处：ViewModel 直接改窗口（`MainViewModel.Settings.cs:20-24` 改 `Application.Current.MainWindow.Topmost`，与 `MainWindow.xaml:11` 的 `Topmost` 局部值和 `TitleBarControl.xaml:146` 的绑定构成双真源）；ViewModel 构造并 `ShowDialog` 对话框（`ImportExport.cs:294`、`Settings.cs:93/210/243/257`）；`MessageBox.Show` 出现在 15+ 个 VM 位置。
- `App.xaml.cs:247-250`：`IDwgReaderService`/`IDxfReaderService` 与 `IDwgWriterService`/`IDxfWriterService` 是 4 个单例架在 2 个具体类型上 ⇒ 两个实例、状态/缓存分裂；`ImportExport.cs:34` 会同时解析两个 reader。
- `App.xaml.cs:169-218`：启动期把 `settings.json` 读取+反序列化**三次**（`ReadConfiguredLogLevel`、`ReadLicensingEnabled`、`:142`），`Config.cs:27` 再来一次；`Config.cs:57` 还在 UI 线程同步写盘。解析一次、传 `AppConfig` 即可。
- `MainViewModel.Translation.cs:83` 的 `Interlocked.Increment(ref completedCount)` 是多余的，相邻的 `TranslatedCount++` 之所以安全**仅因为** `DispatcherProgress` 恰好把它调度到 UI 线程（`DispatcherProgress.cs:14-15`）——请显式记录这个不变量，或干脆去掉。
- `ExportModeDialog.xaml.cs:66-75` 直接 `new SolidColorBrush` 硬编码 `Colors.LightGray`/`#1976D2`，不走颜色令牌。
- `MainWindowStyles.xaml:7` 声称"所有颜色都经 `ColorTokens.xaml` 的 DynamicResource"，但 `:108/201/412/430` 硬编码了 `#B91C1C`/`#D1FAE5`/`#F3F4F6`/`#E5E7EB`；`ColorTokens.xaml:46` 的 `Brush.Info` 与 `:10` 的 `Brush.Primary` 重复；6 个约 40 行的按钮模板（`:19/57/92/305/346/389`）只差画刷。

**CAD 插件层**

- **文档化的"策略 1-5 碰撞求解管线"实际从未运行**：`Replacement/CollisionResolver.cs:26-453`、`FrameDetector.cs:18-229`、`LayoutOptimizer.cs:226-278`（`OptimizeDBText`）、`CollisionDetector.cs:19-33/58-64/140-180`、`ColliderCollector.cs:14-83` 经 grep 确认不可达；只有 `LayoutOptimizer.OptimizeMText` 被调用，且仅作用于 MLeader 那个游离副本（`AcadWriterEngine.cs:550`）。引擎自己的包络拟合还与 `Core/Services/DwgCollisionDetector.cs` 重复实现。注意 `CollisionResolver.cs:442` 甚至会"先建 MText 再删 DBText"，与按 handle 索引的映射直接冲突。**建议**：删除，或有意接线。
- **尺寸阈值两套**：`AcadWriterEngine.cs:607/656/734/763` 硬编码 **70%** 高度下限，而 `LayoutOptimizer.cs:33` 与 `CollisionResolver.cs:345` 用 `WBC.MinHeightRatio = 0.85`；epsilon 也不一致（`:791` 容差 `0.01` vs `:607` 的 `1e-8`）⇒ 同一张图纸走 CAD 在线与离线回写会得到不同的最小字号。
- **进程级可变静态状态**：`Commands/DwgTranslatorCommands.cs:41-42` 的 `_config` 与 `_extractedEntitiesByDoc` 无同步，且**从不随文档关闭清理**（没有挂 `DocumentManager.DocumentDestroyed`）⇒ 长会话内存泄漏，且后续命令可能拿到陈旧实体。同类问题见 `FrameDetector.cs:24-29`（`MinFrameArea`/`MaxFrameArea` 公开 setter）与 `Log.cs:18-28`。
- **每次求包络都临时改写进程级 `HostApplicationServices.WorkingDatabase`**（`CollisionDetector.cs:67-74`）：虽有 `finally` 还原，但非线程安全，且每次作业要改数千次宿主全局状态。同时障碍缓存在 `AvailableTextSpace.cs:13-14/36` 按 `(Transaction, owner)` 缓存，而 `AcadWriterEngine.cs:221` 在"改文字"那一趟**之前**就准备好了障碍（`:430` 的走廊测量用的正是改之前的老几何）。
- **"请求文件"格式重复实现且索引不安全**：`VerifyLayoutCommand.cs:112-118`（`lines[0]/[1]/[2]`）、`LayoutFootprintCommand.cs:21/26`（`Take(3)` 之后又取 `lines[3]` ⇒ 文件只有 3 行时 `IndexOutOfRangeException`，被 `:39` 捕获后报成通用 ERROR）、`LayoutPlotCommand.cs:36`（`Skip(2)`），三者都写 `<input>.report`。`LayoutPlotCommand.cs:46` 的 `papers.First()` 在空出图设备列表时抛异常，`:42` 硬编码 `"DWG To PDF.pc3"`，`:58` 每个区域各建一个发布引擎。
- **枚举/区域设置**：`LayoutOptimizer.cs:53`、`AcadWriterEngine.cs:483`、`TextReplacer.cs:133` 把经 Excel/JSON 往返的整数直接强转 `LineSpacingStyle` 而不校验范围（写入端 `TextEntityFactory.cs:73`）⇒ 越界值被 CLR 接受并产生非法行距样式；`Log.cs:68-72` 用 `string.Format` 在 `CurrentCulture` 下重写消息模板 ⇒ 逗号小数区域的机器上 `{H:F2}` 会渲染成 `2,50`，破坏日志解析；正则重写还会误伤形似占位符的字面花括号。
- **`AssemblyResolve` 版本无关绑定**：`Commands/Initializer.cs:37/82-87` 只要简单名相同就返回**任意**已加载程序集，可能把 Serilog/EPPlus 绑到 AutoCAD 自带的老版本 ⇒ 宿主内 `MissingMethodException`；该处理器只在 `Terminate`（`:63`）里摘除，而 `NETLOAD` 永远不会调用它 ⇒ 整个会话期常驻。**建议**：限定到已知程序集+版本白名单。
- **`DWGTRANSLATORCREATETEST` 会改动用户当前图纸并改写其保存目标**（`Commands/HealthCheckCommand.cs:37-69`）：它向活动图纸的模型空间追加一个 `DBText "Hello Motor"`（`:54-61`），无确认、无 `DocumentLock`，随后 `:66` `doc.Database.SaveAs(drawingPath, DwgVersion.Current)` 把活动文档的文件名**重定向到** `%TEMP%\DwgTranslator\online_writeback_source.dwg` ⇒ 用户之后 Ctrl+S 会存进临时文件而不是原图纸。已确认应用内的"测试 CAD 集成"走的是安全的 `DWGTRANSLATORPING`（`SettingsViewModel.cs:246`），所以危害限于手工调用该命令的人；但"健康检查命令会改我的图"本身应当移除或改成在全新图纸上执行。另 `:54-63` 的 `using var text` 在 `tr.Commit()` 之后才释放，Autodesk 推荐在 `Commit` 前释放（推断：当前良性，但并非所有宿主都合法）。

---

## 4. LOW / 整洁性

- `Views/LicenseDialog.xaml:59` 的 `ToolTip="输入 gql119871 或 DwgTranslator-P-XXXX..."`：**已实测确认不是可用激活码**（`Activate()` 会返回 `LicenseActivationInvalidFormat`）。属遗留开发备注 + 泄漏作者账号 + 误导用户，删除即可。
- 死代码：`Converters/StatusToBrushConverter.cs:11-52` 与其样式 `MainWindowStyles.xaml:11` 全项目未引用（表格用的是 `TranslationDataGrid.xaml:120-143` 的内联触发器）；未引用样式还有 `SectionHeader:131`、`CardBorder:139`、`InputLabel:149`、`DescriptionText:156`、`StatsBadge:229`、`ToolbarCard:248`、`ContentCard:258`、`ToolbarDivider:295`（与 `RibbonDivider:428` 字节重复）、`StatInlineText:436`、`CompactToolButton:443`、`CompactPrimaryButton:450`、`DialogSaveButton:124`、`DialogCancelButton:125`。
- `Services/AutoCadInteropService.cs:45` 的字段 `_comObjects` **从未被写入**（实际用的是按调用创建的 `localComObjects`，`:88/119/131/172`）⇒ `Dispose()` 什么也不释放，`IDisposable` 链路是空转。
- `Views/LogViewerPanel.xaml:177-193`：时长样式默认 `Collapsed`（`:182`），唯一触发器在 `{x:Null}` 时也设 `Collapsed`（`:184-186`）⇒ 时长**永远不可能显示**；为此写的 `DurationToVisibilityConverter`（`LogViewerPanel.xaml.cs:68-79`，键 `DurationVis`，`:11`）从未被引用。
- `Core/Models/WritebackConstants.cs:40,51,56,66` 未被使用（`FrameToleranceRatio` 在 `DwgFrameDetector.cs:233-234` 被重新硬编码为 `0.005`）；`DwgCollisionDetector.cs:175-181` 的 `ScaleDownToAvoidCollisions` 注释声称被 `DwgTextReplacer` 调用（不实）；`DwgFrameDetector.cs:32-70`、`TextWidthEstimator.cs:124-159`（注释声称两条写回路径都调用它——离线 MText 实际从未重排）、`DwgReaderService.cs:355-356`、`DwgReaderService.cs:308-317`（`:266` 已提前跳过）、`FontMapper.cs:70-78` 均为死代码；`when entity is not CadMText` 之类的守卫也是死的（`ACadSharp.Entities.MText.BaseType` 是 `Entity` 而非 `TextEntity`）。
- 格式码正则重复两份：`DwgReaderService.cs:28-30` 与 `Translation/FormatCodeParser.cs:14-22`（漂移风险）。`DwgReaderService.cs:119` 的 `Flags & 2`、`:365` 只数 `\P` 而 `:29` 正则同时接受 `\p`；`DwgFrameDetector.cs:24/192`、`DwgTextScaler.cs:32-33/110-111`（对 `HeightCapRatio == 1.0` 的夹取是空操作）等魔法数；`TextWidthEstimator.cs:226-230` 的 `HasCjkChar` 只看 `sb[0]`（等同失效）。
- `Services/AutoCadDetector.cs:32-33` 把开发机路径 `D:\haochenCAD\浩辰CAD 2024` 写进了探测列表。
- `dev.bat:14-15` 无条件 `taskkill /F /IM acad.exe /T` / `acadlt.exe`，会强杀用户正在使用的 AutoCAD 并丢失未保存工作。
- 工作区残留未跟踪文件/目录：`%SystemDrive%\`（Windows 缓存 `.db`，某次命令拼错的产物）、`src\DwgTranslator.App\DwgTranslator.App_*_wpftmp.csproj`（泄漏 `C:\Users\GQL\...` 路径）。建议删除并把这两个模式加进 `.gitignore`。
- 界面细节：`Views/MainWindow.xaml:95` 的 `x:Name="LogPanel"` 未使用；`SettingsDialog.xaml:20` 第 3 行从未使用；`MainWindow.xaml:120-128` 在 `Border.Loaded` 上启动 `RepeatBehavior="Forever"` 的动画，即使 `IsProcessing=false` 折叠了该元素也永不停止（持续占用渲染）；`Views/` 下**没有任何** `AutomationProperties.*`（工具栏与标题栏都是自定义模板的 emoji 按钮，配合 `WindowStyle="None"` 无边框窗口，读屏软件基本不可用）。
- `SettingsDialog.xaml:84` 的 `ResultText` 与 VM 属性同名易混；`GlossaryManagerDialog.xaml.cs:81` 用 `Debug.WriteLine` 记录保存失败，与其他对话框统一用 Serilog 不一致。
- `Converters/BoolToVisibilityConverter.cs:38` 的 `InverseBoolConverter` 放在文件名不符的类里，且 `ConvertBack`（`:43-44`）做的是取反而不是 `DoNothing` ⇒ 用于 TwoWay 不安全。
- `App.xaml.cs:24` + `:254`：`ILicenseService` 可能解析出 `null!`（设计器/测试场景 `OnStartup` 未跑）⇒ `MainViewModel.cs:133` NRE，而不是一个清晰的失败。
- `MainViewModel.cs:81-91` 无参构造函数是 Service Locator，并带 `?? new ...` 静默降级；在 `App.xaml.cs:260` 注册了 `AddTransient<MainViewModel>()` 之后它在生产路径上是死代码。
- `publish.bat:31,38` 用 `-ExecutionPolicy Bypass`（仓库脚本常见做法，此处仅记录）。

**CAD 插件层**

- `Cad/Log.cs:23` 与 `:52-56`：`Log.Editor` 全解决方案**从未被赋值**（grep 确认）⇒ `OutputToCommandLine` 分支是死代码，诊断只进日志文件/`Debug`。另外逐实体的 `Log.Information`（`AcadWriterEngine.cs:208/610-612/736-738`）让日志量与标注数量线性增长。**建议**：在 `Initializer`/各命令里赋值 `Log.Editor`（并随文档切换清空）或删掉该属性；把逐实体日志降为 `Verbose`。
- `Cad/Replacement/AcadWriterEngine.cs:87` 调用 `Log.Debug("AcadWriterEngine", "Failed to remove temporary DWG {0}: {1}", ...)`，但 `Log.cs:68` 的 `FormatMessage` 只认命名占位符 ⇒ 参数被原样追加、字面 `{0}` 留在输出里。**同类根因已被 C9 一并修复**：`FormatMessage` 的正则原本连 `{OldW:F3}` 这种带格式说明符的命名占位符也匹配不到，导致全项目 21 处日志退化成"模板 + 参数转储"。**仍建议**把该处的 `{0}/{1}` 改成命名占位符（正则只认 `[A-Za-z_]\w*`），或让 `FormatMessage` 同时支持位置式占位符。
- 死参数/死字段：`IsInsideOriginalEnvelope(...originalHeight)` 的 `originalHeight` 未使用（`AcadWriterEngine.cs:789`）；`ColliderCollector.cs:16/32` 的 `MaxRecursionDepth`/`depth` 从不参与判断（它根本不递归）；`Compat.cs:1-9` 名为版本兼容垫片，实际只剩一个 `Clamp`。
- `Cad/Replacement/ColliderCollector.cs:46/62/80` 用 `System.Diagnostics.Debug.WriteLine`（Release 下不可见）而不是 `Log`。
- 空引用与状态码未校验：`VerifyLayoutCommand.cs:31/105`、`LayoutPlotCommand.cs:23`、`LayoutFootprintCommand.cs:17` 直接解引用 `MdiActiveDocument.Editor` 而不判空（与 `DwgTranslatorCommands.cs:50/63/104/183`、`WritebackCommand.cs:52` 的做法不一致）；`.StringResult` 未检查 `PromptStatus`（`VerifyLayoutCommand.cs:32`、`LayoutFootprintCommand.cs:17`）⇒ 用户按 ESC 会得到空串和误导性的"文件不存在"提示。
- `Cad/Extraction/TextEntityFactory.cs:130` 与 `:134` 两次读取 `mLeader.MText`，每次 get 都产生一个新的游离 `MText` 且都不释放（正确写法见 `AcadWriterEngine.cs:540` 的 `using`）⇒ 原生对象泄漏量与引线数量成正比。相关：`TextReplacer.cs:251` 用 `styleId.GetObject(OpenMode.ForWrite)` 而不是调用方的 `Transaction`（对照 `AcadFontApplier.cs:60-90`），混用隐式与显式事务可能让文字样式改动未被提交或抛 `eWasOpenForWrite`，即字体映射静默失效（推断具体失败模式；写法分歧是事实）。
- `Cad/Extraction/TextEntityFactory.cs:179-186`：表格单元格实体一律以 `TextStyleName = "Standard"`、`Height = (double)(cell.TextHeight ?? 2.5)`、`Position = (0,0,0)` 构造 ⇒ 所有单元格都按 "Standard" 做字体映射，且几何信息不可验证。
- `Cad/Replacement/AcadWriterEngine.cs:544-559`：MLeader 的拟合走 `AlignInsideEnvelope`（`:768-787`）会对游离 MText 做 `TransformBy` 平移，然后在 `:557` 赋回 ⇒ 被"挪动"的引线标注可能偏离箭头（推断）。**建议**：MLeader 只缩不挪。

**已确认可靠、无需改动的部分（避免误伤）**：基于文件的回写把源图作为 side database 打开（`AcadWriterEngine.cs:52-54`）、写临时文件后原子移动（`:62-70`）、`CollisionDetector.cs:115` 会释放 `Explode` 产物、CAD/GstarCAD 的 API 引用都正确标了 `<Private>False</Private>`（`csproj:22-44`）——这条路径的宿主安全性主干是站得住的，问题只在 C6 的中止语义与 H14 的原地保存。

---

## 5. 架构观察

1. **Core 并不"纯"**：`Core` 同时依赖 ACadSharp、ClosedXML 与 `CommunityToolkit.Mvvm`，且 `Models/TextEntity.cs:13` 直接继承 `ObservableObject`（文件里 `:9-12` 已有 TODO）。这正是"逐条 `Report()` 变成 8000 次 UI 回调"的结构性原因——UI 关注点渗进了核心模型。csproj 的 TODO 说明作者已意识到，拆出 Infrastructure 层是收益最高的结构改造。
2. **写回是一堆静态方法**（`DwgFontManager`/`DwgFrameDetector`/`DwgCollisionDetector`/`DwgTextReplacer`/`DwgTextScaler`/`DwgBoundsEstimator`），统一以 `CadDocument` 作参数而不是注入协作者 ⇒ 无法隔离测试，也是"反复全文重扫"的根因。
3. **排版策略散落在三处**（常量、三个文件里的字面量、`CadLabelCompactor` 代码）⇒ 一个 `WritebackPolicy` 值对象即可消除漂移。
4. **`DwgWriterService.WriteTranslations`（`DwgWriterService.cs:18-152`）把编排、备份策略、版本转换、失败语义塞进一个 150 行方法**，这正是 `:104-106` 那个"全有或全无"判断难以安全放宽的原因。
5. **`IsProcessing` 是唯一的重入保护**：设置了它的命令有保护，但模态对话框类命令（`MainViewModel.Settings.cs:206/90/241/255`）既不设置也不检查；目前只靠"`Application.Current.MainWindow` 非空 + 模态窗口禁用 Owner"兜住——这与所有 `Owner =` 赋值用的是同一个假设。
6. **DI 生命周期基本正确**（服务单例 + VM Transient），但容器会在 `App.xaml.cs:267` 二次 Dispose 已在 `MainWindow.Closing` 里 Dispose 过的 VM（当前幂等所以无害，`Dispose` 一旦不幂等就会出问题）。
7. **双平台（AutoCAD / 浩辰CAD）构建是"二等公民"**：`DwgTranslator.Cad.csproj:46-51` 在 net48 下直接 `Compile Remove` 掉 `Commands/DwgTranslatorCommands.cs` 与整个 `Extraction/**`，于是浩辰CAD 侧没有交互式提取/翻译，而那些文件里的 `#if GSTARCAD` 别名（`DwgTranslatorCommands.cs:26`、`TextExtractor.cs:14-16`）**根本没机会被编译**——典型的"版本条件代码静默失效"。同时 `System.Web.Extensions` 只给 GstarCAD 加（`:39-45`），一旦按 H17 把 AutoCAD 也切到 net48，`CadFileLogger.cs:21`/`WritebackCommand.cs:19` 的 `#if NETFRAMEWORK` 分支会缺引用。**建议**：TFM 与平台矩阵写进一处（`Directory.Build.props`）并让 CI 至少各构建一次，避免"在 A 上能编、在 B 上编不过"。

---

## 6. 建议的修复顺序

**第 1 批（安全与收入，可独立合并）**
1. 授权改非对称签名：Ed25519/RSA，客户端只留公钥；校验和 ≥128 位 + 恒定时间比较；`LoadLicense` 每次验签并校验机器绑定；删除 `license.dat` 不可重置试用；防时钟回拨（单调水位线）；门禁改成编译期开关。
2. `DeepSeekBaseUrl` 强制 `https://` 才能在附加 Bearer 头之前放行。
3. `tools/Verify-ReleasePackage.ps1` 增加 `deepSeekApiKey` 必须为空的断言；`installer/DwgTranslator.iss:38/70-71` 改为打包 `settings.json.example`。
4. 移除 `NuGetAudit=false`（`publish.bat:15`、`App.csproj:54`）。

**第 2 批（必现功能性缺陷）**
5. `AutoCadInteropService` 的 `.scr`/`.lsp`：路径全程加引号 + 用 `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` 后的 ANSI（或 GBK）写出，并加非 ASCII/含空格路径的回归用例。
6. `GlossaryService.MatchTerms` 空源条目防护（加载期过滤 + 循环内不推进即 break）。
7. `DwgWriterService` 的"全有或全无"改为默认保存部分结果 + 未匹配明细回传界面，严格模式作为可选开关。
8. 关闭窗口：存在未导出译文时确认；取消 → 等待进行中的写回 → 再 Dispose。
9. 在线回写超时后**不删**会话目录（延迟清理），并对超时给出"CAD 可能仍在写回"的明确提示。
10. 术语表 JSON 导入改为临时文件—校验—原子替换。

**第 2.5 批（CAD 插件宿主安全，建议优先于第 3 批）**
11. 命令方法体统一 `catch (System.Exception)`；把所有 IO 移入 try/catch（`VerifyLayoutCommand`、`LayoutFootprintCommand`、`LayoutPlotCommand`、`HealthCheckCommand`）。
12. `AcadWriterEngine.cs:196` 的中止语义：把"有意保留"（`:394` 的表格单元格）与逐实体失败从致命 `errors` 里分离，默认提交成功项。
13. 修 `TextReplacer.ReplaceMLeader`（赋回 `mLeader.MText` + `using` 释放副本）并把返回值真实性纳入成功计数。
14. 块展开加 visited `ObjectId` 集合；障碍集合移出逐实体循环；落实或删除 `ExhaustiveCollisionEntityLimit`。
15. `AcadWriterEngine.cs:52-71` 原地保存改为"临时文件 → 释放 Database → 替换"，并保留源图纸版本。
16. `Directory.Build.props` 按宿主版本映射 TFM（AutoCAD 2021-2024 → net48）；统一两个 TFM 的 JSON 序列化器（含 `MaxJsonLength`/大小写）。
17. 公共入口加 `DocumentLock`；`LayoutPlotCommand` 关闭并还原文档；`DWGTRANSLATORCREATETEST` 改在全新图纸上执行或删除。
18. CAD 侧 `.GetAwaiter().GetResult()` 改为 worker 执行 + 仅 marshal `WriteMessage`。
19. 线程/生命周期：`Log.Editor` 赋值或删除、`_extractedEntitiesByDoc` 随文档关闭清理、`AssemblyResolve` 白名单化。

**第 3 批（性能，直接对应"界面卡死"）**
20. `DispatcherProgress` 合批 + `BeginInvoke`（≤10 Hz），worker 侧只入队。
21. `ApplyFilter` 改为后台构建 + 整体替换 `ItemsSource`；去掉重复的 `ApplyFilter()/UpdateStatistics()` 调用。
22. 启动期硬件/注册表探测移到 `Task.Run` 并缓存。
23. 日志面板：增量维护级别计数、节流滚动；默认日志级别改 `Information` 并给 payload 日志加开关。
24. 回写期空间索引 + 块名字典，消除 O(n²)/C×N×B。

**第 4 批（一致性与整洁性）**
25. 把约 30 处硬编码字符串收进 resx；实现或删除 `LocalizeExtension` 的运行时语言切换；`HelpDialog`/`LogViewerPanel` 配色接入 `ColorTokens`。
26. 删除死代码（`StatusToBrushConverter`、13 个未用样式、`_comObjects`、`WritebackConstants`、未接线的 `TextWidthEstimator.ReflowTextToWidth`、不可达的 CAD 碰撞求解管线等），合并重复按钮模板，统一尺寸阈值常量。
27. 清理 `%SystemDrive%\` 与 `*_wpftmp.csproj` 残留，`dev.bat` 不再无条件杀 acad。
28. 翻译缓存键加入语言方向 + 版本；方向切换与术语表导入后清缓存。
29. 补一个最小测试工程（`tests/` 在 README 里被提到但不存在）：优先覆盖 `FormatCodeParser`、`TranslationConsistencyService`、`GlossaryService.MatchTerms`（含空条目）、`DwgWriterService`/`AcadWriterEngine` 的失败语义、`AutoCadInteropService` 与 `SettingsViewModel` 的脚本生成（含空格/中文路径）。

---

## 7. 本轮功能变更与实测记录

### 7.1 新增：拖拽多文件导入

需求是"把多个文件拖进窗口直接翻译"，原先只能走 `OpenFileDialog`。实现方式是把导入抽成**一个核心入口 + 三个调用方**，避免三份重复逻辑：

| 调用方 | 入口 |
|---|---|
| 工具栏「导入 DWG」按钮 | `ImportDwgAsync()` → `ImportCadFilesAsync(paths)` |
| **拖拽**（窗口任意位置） | `MainWindow.OnPreviewDrop` → `ImportDroppedFilesAsync(paths)` |
| **命令行/「打开方式」** | `App.StartupFiles` → `MainWindow.OnLoaded` → `ImportDroppedFilesAsync(paths)` |

- `MainViewModel.ImportExport.cs`：新增 `ImportDroppedFilesAsync`（按扩展名分流 DWG/DXF 与 XLSX，无法识别的类型会明确报出而不是静默忽略）、`ImportCadFilesAsync`、`ImportExcelFilesAsync`、`IsSupportedFile`；原 `ImportDwgAsync` / `ImportExcelAsync` 只保留选文件对话框。
- `MainWindow.xaml`：`AllowDrop="True"` + `PreviewDragOver/PreviewDragLeave/PreviewDrop`，并新增拖拽提示层 `DropOverlay`（半透明遮罩 + 卡片提示"松开鼠标即可导入"+ 文件数与文件名预览）。
- `MainWindow.xaml.cs`：只认 `DataFormats.FileDrop`，非文件载荷（例如表格里选中文字拖动）原样放行，不影响 DataGrid 自身的文本拖拽；`async void` 仅用于事件处理器。
- 新增 resx 键 6 条（`DropHintTitle/Desc/Files/FilesPartial`、`StatusDropUnsupported/StatusDropNothing`），未硬编码文案。

**为什么提示层要 `IsHitTestVisible="False"` 且尺寸绑定窗口**：前者保证拖拽事件不被遮罩吞掉；后者是因为这条工具栏本身比窗口宽（截图里「日志」按钮已被裁切），栅格会被撑得比可视区更宽，若按栅格居中，卡片会偏右下并被窗口边缘切掉 —— 首次截图实测到了这个现象，故改成绑定 `ActualWidth/ActualHeight`。

### 7.2 顺带修复：导入后看不到已导入的行

`TranslationDataGrid.xaml.cs:UpdateEmptyState` 原先在 `TotalCount>0 && TranslatedCount==0 && !IsProcessing` 时也显示不透明覆盖层，于是**导入完成后整个表格被"共 N 条文字待翻译"盖住**，用户无法核对刚导入了什么（拖拽导入后尤其突兀）。已改为仅在 `TotalCount == 0` 时显示。实测截图确认：导入两个 DXF 后 12 行正常可见。

### 7.3 实测记录（可复现）

样例：`%TEMP%\dwgdrop\sheet_A.dxf`（7 个 DBText，含 热机温度/冷机温度检测/停止指示/运行指示/故障报警/蜂鸣器/急停）、`sheet_B.dxf`（5 个，含 中文+英文+单位混合）。两个文件 handle 故意重叠（24/26/27），用于验证多文件按 `SourceFilePath` 作用域。

| 验证项 | 方法 | 结果 |
|---|---|---|
| DXF 可被本项目读取器解析 | `dotnet fsi` 直接引用构建产物中的 `DwgTranslator.Core.dll` + `ACadSharp.dll` 调用 `DwgReaderService.ExtractFromFile` | 12 个实体，handle/类型/状态正确，UTF-8 中文无损 |
| 多文件导入（与拖拽同一路径） | 以命令行参数启动已发布的自包含 exe | 状态栏 `总数 12`；两个文件实体合并显示（`24/26/27/28/29/B/C` + `22/24/25/26/27`） |
| 拖拽提示层渲染 | 临时置 `Visibility="Visible"` 截图后还原 | 三行文案与"即将导入 2 个文件：sheet_A.dxf、sheet_B.dxf"全部正确，resx 键与 `{conv:Localize}` 均生效 |
| 导入后行可见（7.2 修复） | 同上截图 | 12 行正常显示，无遮挡 |
| 编译 | `dotnet build DwgTranslator.sln -c Debug` / `-c Release` | 0 warning / 0 error |
| 发布包 | `tools\Verify-ReleasePackage.ps1` | `RELEASE_PACKAGE=OK` |

**两张截图**（临时文件，不在仓库内）：`%TEMP%\dwgdrop\drop_overlay2.png`（拖拽提示层）、`%TEMP%\dwgdrop\final_import.png`（多文件导入后 12 行可见）。

### 7.4 环境问题（非代码缺陷，但会让"打不开"）：框架依赖 exe 解析不到运行时

用户报"一直跳出 *You must install .NET Desktop Runtime*"。实测本机：

```
dotnet --list-runtimes  ->  8.0.29 全部装在 C:\Users\GQL\AppData\Local\Microsoft\dotnet\shared\...
$env:DOTNET_ROOT        ->  空
HKLM/HKCU \SOFTWARE\dotnet\Setup\InstalledVersions\x64\InstallLocation  ->  不存在
C:\Program Files\dotnet ->  不存在
```

即：运行时装在**用户目录**，而 apphost 探测运行时的三个途径（`DOTNET_ROOT`、注册表 `InstallLocation`、默认安装目录）**全部缺失**，因此 `src\...\bin\Debug\...\DwgTranslator.exe` 这类**框架依赖**产物会间歇性启动失败并弹出该对话框（实测 4 次里失败 2 次），Windows 应用日志中留下 `Failed to resolve hostfxr.dll [not found]. Error code: 0x80008083`。

两种解法（任选其一）：
1. **用自包含的 `release\DwgTranslator.exe`**（仓库里那份 75 MB 单文件，自带运行时，不需要任何环境配置）—— 推荐，也是本次实测所用的方式。
2. 若希望 `bin\Debug` 产物 / `dotnet run` 生成的 exe 也能双击运行，把用户级环境变量补上：
   `setx DOTNET_ROOT "%LOCALAPPDATA%\Microsoft\dotnet"`（生效后需重开终端）。本次实测在会话内设置该变量后，框架依赖 exe 立刻稳定启动。

### 7.5 第三轮：几何精确化 + 尺寸策略（针对"还有中文"和"字号忽大忽小"）

`cad_plugin_20260910_202747.jsonl` 的实测：

```
Layout fit rejected 2781: 描述; height=2.5; width=1      ← 4 条新回归
Layout fit rejected 278B: 批准; height=2.5; width=1
Original text preserved after layout rejection (8): 2781=描述; 278B=批准; 6A4D=描述; 6A57=批准;
                                                    30312=停止指示; 30314=运行指示; 31A29=…; 32023=…
Envelope fit DBText 31B59: accepted shrunk height 4.000->2.509 (63%)   ← 端子表表头
Envelope fit DBText 31B89: accepted shrunk height 4.500->2.639 (59%)
```

**（a）我上一轮引入的回归**：把宽度因子下限从 0.40 提到 0.90 后，标题栏窄格里的 `描述`/`批准`（DBText，`WidthFactor=1.0`）既不能压扁、又缩不下高度，于是被判定失败还原中文。改用**两级兜底**：第一档 `MinWidthFactorRetention`（保持比例），失败才用第二档 `FallbackWidthFactorRetention=0.40`（历史行为）。**"还原成中文"只在两档都放不下时才发生。**

**（b）尺寸策略（回答"明明可以放大点"）**：DBText 不能换行，英文比中文长时只有两个旋钮，而渲染长度 ∝ `字高 × 字宽因子`：

| 策略 | 结果（以所需缩减 0.59 为例） | 观感 |
|---|---|---|
| 只用宽度因子 | 字高 100%、字宽 59% | 扁（用户上一轮反馈） |
| 只用字高 | 字高 59%、字宽 100% | 小（用户这一轮反馈） |
| **先允许适度压缩再补高度** | 字宽 65%、字高 **91%** | 接近原尺寸、轻微变窄 |

已改成第三种：`MinWidthFactorRetention = 0.65`（允许 35% 压缩，余量再交给高度）。该常量是唯一旋钮：调小→更大但更扁，调大→不变形但更小。

**（c）45° 斜排标注的几何精确化**（两处耦合改动，针对"还是失败 8 条"里的 `停止指示`/`运行指示`/`柜门急停开关反馈`/`投料站除尘风机故障`）：

1. `AvailableTextSpace.Measure` —— 原先非 90° 倍数旋转**直接放弃测量**、拿"原文自身包围盒"当允许区域。现改为**在文字自身坐标系里测量走廊**（`MeasureRotated`）：该坐标系下文字退化为水平、同角度邻居的矩形保持轴对齐（因此是精确的），直接复用轴对齐的走廊规则，最后映射回世界坐标。关键效果：45° 一排标签的邻居在自身坐标系里偏移 `(u=+17.7, v=−17.7)`，**落在阅读行带之外**（`t < cLo − margin`），于是不再阻塞走廊 —— 横向上恢复出约 50 单位的可用长度，英文可在**接近原字高**下单行排下。轴对齐那条路径按原逻辑**逐行照搬**（U/V 映射、frame-line inset、共页规则均保持一致），因为它承担着当前 430 条正常替换。
2. `AvailableTextSpace.FindIntersections` —— 干涉判定原为**世界轴对齐 AABB**，而 45° 标签的墨迹 AABB 是对角正方形，相邻两框必然互相压住 1–4 单位（这正是前两轮 20→36 条被"缩小/还原"的根源）。现在当双方矩形都可重建时改用**定向矩形 SAT 相交**（`CollisionDetector.QuadsOverlap`，`(q−p)` 边法线作分离轴，容差 0.01 视为不撞）；其余情况（DBText、几何障碍）保持原 AABB 行为。新增 `CollisionDetector.TryGetOrientedCorners` 从 MText 的 `Location/Direction/Normal/Attachment/ActualWidth/ActualHeight` 重建渲染矩形（复用 `MeasureInk` 的同一套 attachment 数学）。

**为避免新回归做的两处加固**：
- `MeasureRotated` 里走廊塌缩时**捕获异常并回退到 `original`**（即旧行为）。否则旋转文字会新出现抛异常路径，而 `ReplaceEntity` 未捕获 → 异常会冒到 `WriteTranslationsToDatabase` 外层，把**整次回写**判为失败；轴对齐路径的抛异常是原有行为，保持不变。
- `MeasureRotated` 的尺寸绑定窗口/提示层 `IsHitTestVisible=False` 等前几轮的加固同样保留。

**验证**：全解决方案 Debug/Release 均 0 warning / 0 error；发布包 `Verify-ReleasePackage.ps1` → `RELEASE_PACKAGE=OK`；二进制级确认 `MeasureRotated`/`TryGetOrientedCorners`/`QuadsOverlap`/`ComputeCorridor`/`BoxCorners`/`MinWidthFactorRetention`/`FallbackWidthFactorRetention` 均在部署产物中，旧的静默路径 `precise fallback required` 已消失。

**仍未验证的部分（必须说明）**：这些几何改动**无法在本机用真实 CAD 宿主回归**（无 AutoCAD/浩辰CAD 会话），只能靠源码推演 + 编译 + 二进制确认。风险已被两条设计约束限制：走廊只会**变宽松**（宽松的最坏后果是重叠，而重叠会被干涉复检的"继续缩小"兜住，仍会翻译出来，不会退回中文）；SAT 相对 AABB 只会**减少假重叠**（矩形 ⊆ 其 AABB，故不会产生新的误判）。

### 7.6 第四轮：把"固定优先级贪心"换成"多次尝试取最优尺寸"（采纳用户建议）

用户建议：*"不能采取多次尝试取最合适的大小的算法吗？不过大也不过小，我可以等待这个迭代的过程。"* —— 这个方向是对的，而且比 7.5 的"两个旋钮依次用尽"更正确。已把三处拟合全部改为**搜索 + 择优**。

**（a）数学依据**：DBText 不能换行，渲染长度 ∝ `字高 × 字宽因子`。若需要的长度缩减为 `f`，则可行解的边界是 `h·w = f`；在 (h, w) 空间里**离原尺寸 (1,1) 最近的点正是 `h = w = √f`** —— 也就是"等比缩放到刚好放得下"。所以"取最合适的大小"有唯一且优雅的解：**最大等比缩放**。以 `f = 0.59` 为例：√0.59 = **0.77** → 字高与字宽同时 77%（不变形、尺寸尽可能大），而不是此前的"字高 59%"或"字宽 59%"。

**（b）DBText：三级搜索**（`FitDbTextToOriginalEnvelope` 重写）
1. 原尺寸能放下 → 原样保留（不动任何属性）；
2. 否则**二分求最大等比缩放**（16 步，区间 `[AbsoluteMinFitHeightRatio, 1]`，谓词在缩放上单调，故二分有效）→ 这就是"不过大也不过小"的解；
3. 只有连最小等比缩放都放不下时，才**二分求最大可行的字宽因子**（保留下限 `FallbackWidthFactorRetention=0.40`）—— 压缩是最后手段，因为留中文比压缩更糟。

每次尝试都从原始位置重新开始（否则 `AlignInsideEnvelope` 的位移会在候选之间累积）。日志会报告尝试次数：`uniform scale to {Ratio:P0} of the original size after {Tries} tries (aspect preserved)`；全部失败则明确报 `no fitting size found after {Tries} tries`。约 19 次尝试，DBText 的测量很便宜。

**（c）MText：四次尝试全跑、择优**（`FitMTextToOriginalEnvelope`）
原实现是 `\W ∈ {1.0, 0.9, 0.8, 0.75}` **取第一个可行即返回**，"第一个可行"可能明显小于"稍微压缩一点就大得多"的方案 —— 这正是"为什么这个字比旁边小那么多"的来源。现改为**四次全部评估、保留字高最大的那个**（字高在 1% 内视为相同，则优先压缩更少的），再把胜者重跑一次以落到确定状态。

**（d）干涉复检：二分替代 15% 递减**（`TryShrinkUntilClear`）
原实现每轮按 0.85 递减，直到恰好不撞为止（精度只有 15%）。现改为**对缩放做二分**求"最大的不重叠尺寸"（谓词同样单调）—— **精度更高且次数更少**（约 10 次 vs 最多 12 次）。

**（e）清理**：`MinWidthFactorRetention` 已无引用，删除；`FallbackWidthFactorRetention` 保留为压缩下限。

**代价**（用户已表示可以等待）：DBText 约 19 次廉价测量（`GeometricExtents`）；MText 由"命中即停"变为最多 4 倍候选评估（每次含字形级测量，是这三处里最贵的）；干涉复检反而更省。失败路径的日志会打印实际尝试次数，便于后续按图纸规模调参。

**验证**：全解决方案 Debug/Release 0 warning / 0 error；`Verify-ReleasePackage.ps1` → `RELEASE_PACKAGE=OK`；二进制确认 `uniform scale to` / `after {Tries} tries` / `best of {Tries} layout tries` / `no fitting size found` 均在部署的 `DwgTranslator.Cad.dll` 中，`MinWidthFactorRetention` 已从 `DwgTranslator.Core.dll` 消失。

**仍未验证**：与 7.5 相同 —— 无真实 CAD 宿主可回归，只能源码推演 + 编译 + 二进制确认。所有二分都依赖"谓词在尺寸上单调"这一前提（更小的文字不会比更大的更难放下），该前提在固定包络下成立。

### 7.7 第五轮：用 ACadSharp 直读用户图纸定位"尺寸不对"（两处已修）

第四轮后：`434 replacements (26 separated by shrinking, 3 kept original)`，中文只剩 3 条（`冷机运行反馈`/`柜门急停开关反馈`/`投料站除尘风机故障`，都是长标签），但用户反馈"尺寸还是不太合适"。**不再靠日志猜，直接用 ACadSharp 读源图纸与输出图纸对照**：

```
SOURCE   DBText h=205608 rot=90  height=4.00 wf=0.75  text=48芯航空插头
OUTPUT   DBText h=205608 rot=90  height=1.79 wf=0.75  text=48-core aviation plug     ← 45%
SOURCE   MText  h=197394 rot=40  h=2.50 w=27.63  text=停止指示
OUTPUT   MText  h=197394 rot=40  h=1.11 w=6.32  {\W0.75;Stop indication}            ← 44%
```

两条结论（都被数据证实）：

**（a）旋转 MText 的换行宽度用错了来源** —— `FitMTextCandidate` 的旋转分支把换行宽度设为"旋转后 AABB 的边长 × 0.96"（`originalLength = boxW`），而 45° 文字的 AABB 是对角正方形，与阅读方向无关。于是 `停止指示` 的换行宽度只有 **6.32**，把 "Stop indication" 硬拆成两行、字高压到 44%。而第一轮几何修复其实**已经算出了真正的走廊长度**（约 48 单位），只是没被用来换行。
**修复**：`AvailableTextSpace.Measure` 增加 `out double readingLength`（走廊在阅读方向上的长度，轴对齐与旋转两条路径都填），一路传到 `FitMTextCandidate`，旋转分支改用 `readingLength` 作为换行宽度。预期 `停止指示` 由 44% → **接近原字高、单行**。

**（b）允许区域此前会比"原文自己占的地方"更紧** —— `48芯航空插头` 的格子本身比中文还窄（中文墨迹约 21 单位、格子约 15.5 单位），**中文原本就是越格画的**；而走廊被格线夹到 15.5，于是英文被压到 45%。译文最多占"原文本来就占的那块"才合理。
**修复**：新增 `WidenToSourceFootprint(measured, sourceFootprint)` —— 返回的允许区域取 `max(走廊, 原文足迹)`。由于原文足迹在多数情况下本来就落在走廊内，**这条规则只对"原文自己就越界"的情况生效**，等价于"最坏也不比原文更紧"，因此对已正常的 430 条不产生行为变化（走廊 ⊇ 原文足迹时并集不变）。预期 `48芯航空插头` 由 45% → **约 61%**（`34.6·s ≤ 21 ⇒ s = 0.61`）。

**仍无法用几何解决的部分（需用户决策）**：`48芯航空插头` 是 **DBText**，不能换行。英文是中文的 3 倍长时，等比缩放的数学最优解就是 `√f`（这里 f≈0.61 → 78%；受格子长度限制实际 61%）。要再大只有两条路：
1. **把这类 DBText 提升为 MText 以便换行**（同一事务内新建 MText、`Erase` 原 DBText；需同步维护 `ReplacedEntityInfo` 的 ObjectId 与"还原"路径——还原不能再走 `CopyFrom`，必须先删 MText 再 `Erase(false)` 恢复 DBText）。预期长标签可提升 2–3 倍，但**会改变图纸实体类型**，且本机无法回归测试，因此等用户确认再做。
2. **术语表给出标准短写**（如 `48芯航空插头 → 48C avia. plug`、`停止指示 → Stop`）——零风险、即时生效，也是制图惯例；但翻译缓存会优先命中旧译文，需清空 `%APPDATA%\DwgTranslator\translation_cache.json` 才生效。

**验证**：全解决方案 Debug/Release 0 warning / 0 error；`release\` 通过 `Verify-ReleasePackage.ps1`（`RELEASE_PACKAGE=OK`）；二进制确认 `WidenToSourceFootprint` 已进入部署的 `DwgTranslator.Cad.dll`（92160 → **92672** 字节）。`release\DwgTranslator.exe` 因用户正在运行而未能替换——本轮 App 代码未改动，无功能影响。

### 7.8 第六轮：搭起本地回归闭环，并据此定位"尺寸"问题的真实约束

**关键突破：本地就能跑插件的完整回写**（浩辰CAD 批处理 + 插件 + ACadSharp 读回），不再依赖用户反复重跑：

```
gcad.exe /nologo /b run.scr
  run.scr: _.NETLOAD → release\CadPlugin\DwgTranslator.Cad.dll → _.DwgTranslateWrite → <config.json> → _.QUIT → _N
config : {sourceDwgPath, outputDwgPath, doneSignalPath, cnToEn, sessionId, entities:[{handle, entityType,
          rawText, plainText, translatedText, status:2, height, originalHeight, rotation, widthFactor,
          textStyleName, mTextRectangleWidth, position:{x,y,z}}]}
验证   : 读回 test_out.dwg 逐条比对字高（ACadSharp）
```

配好脚本后一次运行即可验证任何算法改动（`%TEMP%\cadtest\`）。**注意两件事**：① ACadSharp 的 `Handle.ToString()` 是**十进制**，而应用/插件用 `ToString("X")`（十六进制）—— 前几轮我拿十进制句柄搜日志，得出"日志里没有该实体"的错误结论，那些 handle 其实一直在（如 `205669` 实际是 `32365`）；② 成功回写会**自动删除配置文件**，重跑必须先重新生成。

**这轮据此得到的结论（都用实测数字，不再靠推断）**：

1. **表格长表头 51% 是该约束下的数学最优，不是算法没用**。`uniform scale to 51%` 反推可用长度 ≈18 单位；`Terminal Description`（20 字符）满尺寸需 35 单位，源中文仅 12.8 单位；同排的 `Wire Number`（11 字符 ≈19 单位）刚好差一点 ⇒ **97%**。所以"旁边大、这条小"来自**文本长度与格子尺寸**，等比缩放 `√f` 已是最优。要更大只能：换更短的标准短写（`Terminal Desc.` ⇒ 约 73%）或把 DBText 提升为 MText 以折行（此列垂直可用仅 ≈7.8 单位，最多 2 行，收益约 +20% 却要改实体类型，不划算）。
2. **斜排标签偏小的真因**：`Measure` 的走廊在密集旋转簇里会被规则**压回"原文自身足迹"**（世界轴对齐的障碍——指示灯方框/图框线——在文字自身坐标系里膨胀成菱形包围盒，压到阅读行带上）。此时换行宽度也被迫退回"旋转后 AABB 边长"（实测 `2FFDE` 的宽度仅 6.58，导致 `Buzz/er` 这类断词），字高被压到 53%。
   **修复**：把"走廊塌缩"的判定从"抛异常"扩展为"走廊未大于原文足迹"，此时沿阅读方向放宽一个自身长度，`readingLength` 同步给出，真冲突交由干涉复检二分仲裁（`TryShrinkUntilClear`）。
3. **实测效果（本地，23 实体定向子集）**：`急停` 53% → **71%**；`冷机运行反馈` 由"还原中文" → **满尺寸 3.00 成功写入**；表格那批不变。**全子集回归（297 实体）**：`295 replacements, 0 kept original`，缩放分布 51%×6 / 多数 70–100%，无新增还原。
   **未验证**：等价于真实 434 条的完整集合（我的全量筛选把带 `\W` 的 MText 排除了，因此恰好漏掉了旋转标签那批）——下一步应把 `\W` 包裹的实体也纳入回归集。
4. **顺带修掉一个真实缺陷**：`WritebackCommand` 的 `catch` 原本只 `WriteMessage`（批处理控制台是隐藏的）且 `SignalDone` 不带 detail ⇒ 批处理失败**完全静默**。现改为写插件日志 + 把异常消息并入 done 信号（正是靠它我才发现前一轮"配置畸形"其实是我自己生成的 JSON 少了一个 `}`）。

---

## 附录：本次已验证 / 已复核的结论

| 结论 | 验证方式 |
|---|---|
| 解决方案可编译 | `dotnet build DwgTranslator.sln -c Debug --no-restore` → 0 warning / 0 error（本机 `CadPlatform=GstarCAD` → 插件目标 `net48`） |
| 资源键与占位符完好 | 解析 `Strings.resx` 337 个键，比对全部 `Strings.Get("KEY", args...)` 调用点 → 无缺失键、无参数数量错配 |
| 空术语表条目死循环 | 在 .NET 8.0.29 上执行 F# 复现脚本 → `IndexOf("", 0)` 返回 0 且 `searchStart` 不推进，5 次迭代仍停在 `pos=0` |
| `Encoding.Default` 在 .NET 8 上是 UTF-8 | 在 .NET 8.0.29 实测：`Encoding.Default = utf-8 cp65001`；中文路径写出为 UTF-8 字节（`231,148,168`）；`Encoding.GetEncoding(936)` 直接抛 `NotSupportedException` |
| `gql119871` 不是可用激活码 | 读 `LicenseDialog.xaml.cs:66-85` + `LicenseService.cs:150-203`：只有 `DwgTranslator-P-`/`-S-` 前缀被接受，其余返回 `LicenseActivationInvalidFormat`；全项目仅此一处出现该字符串 |
| `.claude/` 未提交 | `git ls-files .claude` 为空；`.gitignore:29` 忽略 |
| 无 API Key 泄漏 | 安全审计对全历史 `git log --all -S "sk-"` 与三个发布二进制的直接扫描：无 API Key 形态匹配；`release/settings.json:3` 为空 |
| 单 handle 未匹配即中止 | 读 `DwgWriterService.cs:104-106` 与 `DwgTextReplacer.cs:52-58/102-111/134-155`：确认成功路径会从 map 移除，因此剩余计数即未匹配数，任何一条即抛 |
| `EmptyStatePanel` 会盖住已导入行 | 读 `TranslationDataGrid.xaml:161-183`（不透明 `Brush.Surface`，Grid 末子元素）+ `.xaml.cs:36-64`（`TotalCount>0 && TranslatedCount==0 && !IsProcessing` 时 Visible） |
| 实时搜索未生效 | grep `SearchText` 全 `App` 项目：只有 `Operations.cs:86/88` 的读取，无 `part void OnSearchTextChanged`；`ApplySearchCommand` 无 XAML 绑定 |
| 安装器 settings.json 链路 | 读 `installer/DwgTranslator.iss:36-40,57-77` + `App.xaml.cs:113-122` + `MainViewModel.Config.cs:21-92`：确认 0 字节文件会被复制到 `%APPDATA%` 并导致 `LoadConfig` 整体跳过 |
| 进度回调逐条触发 | 读 `TranslationService.cs:93-113`（`Report` 在 `foreach (var entity in group)` 内）+ `DispatcherProgress.cs:12-16` |
| 工作区残留 | `git status` → `?? %SystemDrive%/`；`src\DwgTranslator.App\DwgTranslator.App_*_wpftmp.csproj` 存在且未跟踪 |
| MLeader 引线文字静默不改却计成功 | 读 `Cad/Replacement/TextReplacer.cs:159-175`：`mLeader.MText` 每次读取都是游离副本，只改副本、从未赋回，却 `return Success = true`；对照 `AcadWriterEngine.cs:557` 的正确写法 `mLeader.MText = leaderText` |
| CAD 插件中止语义（C6） | 读 `AcadWriterEngine.cs:129/193/196/275/394/419/440/464/567`：`errors` 同时承载"有意保留的表格单元格"与真实失败，`:196` 依 `errors.Count > 0` 回滚整个事务 |
| `DWGTRANSLATORCREATETEST` 改动用户图纸 | 读 `Cad/Commands/HealthCheckCommand.cs:37-69`：向活动图纸模型空间追加 DBText（`:54-61`）后 `:66` 对活动 Database 执行 `SaveAs` 到 `%TEMP%`；并 grep 确认应用内"测试 CAD 集成"调用的是安全的 `DWGTRANSLATORPING`（`SettingsViewModel.cs:246`） |
| AutoCAD 宿主机版本与 TFM 不匹配 | 读 `Directory.Build.props:13-23`（探测列表含 `D:\autocad2021\AutoCAD 2021`，而 `CadPlatform=AutoCAD` 一律产出 `net8.0`）+ `DwgTranslator.Cad.csproj:2,4-5` + README "AutoCAD 2021-2026"；AutoCAD 2025 之前是 .NET Framework 4.8 宿主 |
| CAD JSON 序列化器跨 TFM 不一致 | 读 `Cad/Commands/WritebackCommand.cs:80-87`：net48 分支 `JavaScriptSerializer`（默认 `MaxJsonLength` ≈ 2 MB、无 `PropertyNameCaseInsensitive`）vs net8 分支；`CadFileLogger.cs:20-29/94-98` 同样分叉 |
| CAD 死代码范围 | grep 全解决方案确认 `CollisionResolver`/`FrameDetector`/`LayoutOptimizer.OptimizeDBText`/`ColliderCollector`/`ExhaustiveCollisionEntityLimit`/`Log.Editor`/`_comObjects` 均无引用 |
| C9：旋转标注译文被丢弃 | 解析 `%APPDATA%\DwgTranslator\logs\cad_plugin_20260910_195406.jsonl`（264 行）：64 条 `Layout fit rejected`、20 条 `Rendered interference rejected`、1 条 `Original text preserved after layout rejection for handles: ... 20 个 handle`、`Measured envelope verification passed for 370 replacements`、`saved DWG to ...衡直_投料站_01(1)_translated.dwg`；其中 `2FFDE: 急停`、`3031A: 冷机温度` 与用户截图中未翻译的标注逐一对上 |
| C9：译文本身正确 | `%APPDATA%\DwgTranslator\translation_cache.json` 中 `急停 => Emergency Stop`、`冷机温度 => Chiller temperature`、`蜂鸣器 => Buzzer`、`故障报警 => Fault Alarm` 均在，说明缺陷在排版而非翻译 |
| C9：旋转分支无诊断输出 | 对 `2FFDE`/`3031A` 全量过滤日志：只有 `Layout fit rejected` 结论，没有任何原因行（对比轴对齐路径的 `Envelope fit ...` 有明细），与改前 `:734` 静默 `return false` 一致 |
| C9：日志格式化缺陷 | `Log.FormatMessage` 旧正则 `\{[A-Za-z_]\w*\}` 匹配不到 `{OldW:F3}` → `string.Format` 抛异常 → 兜底输出模板+参数转储，与日志首行 `category`=模板 / `message`=`2FE2F [54.88, 18.08, 3, 2.23]` 完全吻合；新正则用 .NET 8.0.29 实测：旧 → 转储，新 → `...2FFDE: accepted shrunk height 2.500->1.250 (50%)...`，且无占位符模板行为不变（无回归） |
| C9：修复后编译 | `dotnet build DwgTranslator.sln -c Debug --no-restore` → 0 warning / 0 error；`DwgTranslator.Cad -c Release` → 0/0，产物 `bin\Release\net48\DwgTranslator.Cad.dll` 已刷新到 `release\CadPlugin\` |

**已知环境限制（非代码缺陷）**：本沙箱无外网，NuGet 还原失败（`NuGet.targets(745,5): Value cannot be null. (Parameter 'path1')`），故所有构建验证均带 `--no-restore`，使用仓库已有的 `obj/project.assets.json` 与全局包缓存；未执行 `dotnet publish`、未运行 WPF 应用、未在真实 AutoCAD/浩辰CAD 中做 NETLOAD 与回写端到端验证。上述涉及运行时行为的结论均已通过源码路径或 .NET 8 实测脚本确认，推断项已标注"推断"。
