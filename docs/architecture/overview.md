# DWGC2E 当前工程架构

## 实际依赖

- App（net8.0-windows WPF）→ Core（net8.0）。App/CadIntegration 是桌面 CAD 进程桥接。
- Cad 是真正加载进宿主的 DLL → Core 的对应目标。浩辰使用 net48 精简共享面；AutoCAD 2025+ 使用 net8.0。CAD SDK 程序集只用于宿主引用，不随产品任意复制。
- Core 不引用 App、Window、Page、MessageBox 或 WPF；CommunityToolkit.Mvvm 用于现有 observable 模型，不等于 WPF。Core 当前仍是混合共享程序集，不声称已经实现纯 Domain。
- 测试 → 被测工程；正式代码不得反向引用 tests。云端 cf-worker 独立部署，本次不改。

## 职责与新代码规则

UI 仅处理显示、输入、页面状态，调用应用服务；新增 API、配置持久化和业务编排不进入 View/Window code-behind。Application 承载用例，Infrastructure 封装外部网络、文件、账户存储；Drawing/Layout 是图纸算法；CadIntegration/Detection 与 Deployment 是桌面检测和插件部署。Models 与 Interfaces 不应反向依赖 UI。

当前 Api、Tasks、Translation、Logging 保留稳定 feature 目录，不为了层数重复搬迁。所有移动文件保留命名空间/程序集以避免序列化、反射、Serilog 分类和插件兼容性变化。可物理分目录，不要求批量改 using。

## 已知边界债务（未假装解决）

- BillingWindow 的记录读写、账号互斥由 Core/Infrastructure/Payments/PendingPurchaseStore 承担；持久化后下单、会话有效性复核、订单号回存及匹配已付款记录清理由 Core/Application/Payments/PurchaseCheckoutService 编排。窗口保留确认提示、恢复标识创建、锁生命周期、通道提示、列表/会员查询与二维码展示。旧 JSON、路径与下单顺序保持；这是一条用例的职责提取，不声明整个购买窗口已完成分层。
- MainViewModel 的配置、文件导入导出还有流程代码。已有 partial 按功能划分，后续针对具体职责渐进提取。
- TaskManager 约 1500 行、AcadWriterEngine 约 1055 行、WorkerApiClient 约 855 行。不能仅依行数拆分；先补场景测试再提取职责。
- 真正拆成纯 Domain/Infrastructure 程序集需要考虑 net48 CAD 精简编译面，非本次目录治理必要步骤。

## 路径契约

| 源文件 | 交付路径 |
|---|---|
| assets/icons/icon.ico | 内嵌 icon.ico，原 Pack URI 不变 |
| assets/glossaries/mechanical_zh_en.json | assets/default-glossaries/mechanical_zh_en.json；发布另保留 glossaries 兼容副本 |
| assets/prompts/deepl_context.txt | prompts/deepl_context.txt |
| settings.json.example | settings.json 默认模板 |
| Cad 项目输出及私有依赖 | CadPlugin/DwgTranslator.Cad.dll、DwgTranslator.Core.dll、cad-platform.txt 及私有运行依赖 |

用户配置/日志/词库默认在 %AppData%/DwgTranslator；账号任务/缓存/输出按 accounts 子目录隔离，购买恢复记录沿用 %LocalAppData%/DwgTranslator/payments。DWGC2E_DATA_DIR 供隔离测试使用。用户选择的图纸输出位置不改。源码根 settings.json 是本地未知零字节文件，不作为发布输入。release 中历史便携数据由发布保留，不自动删除用户数据。

## 构建与保护

唯一发布入口 tools/Publish-Desktop.ps1。测试通过才生成候选并替换 release；运行中或验证失败保持旧版。构建产物集中 artifacts，不新增 dist；bin/obj 仍是 SDK 标准中间输出。installer 只从明确指定、经过验证的 publish 候选取文件，不能把 release 中的用户数据打成分发包。

正式测试清单和命令见根 README。历史审查在 legacy-code-review.md，属于历史记录，不作为当前状态承诺。

## 当前依赖核对证据

2026-09-16对7个工程（Core双目标，共8份）执行MSBuild实际条目评估：所有Compile与6条ProjectReference目标存在，生产代码不反向引用tests，Core两个目标无直接WPF/WinForms依赖。详见 artifacts/installer-safety-20260916-resumed/architecture-audit/report.md。评估不是Build或全场景运行验收；UI职责债务仍按上文保留。

## 可重复的工程结构回归

`powershell -NoProfile -File tests/BuildPipeline/Test-ProjectStructure.ps1` 使用本机 .NET SDK 评估实际项目配置，不执行Build/Restore/Publish。覆盖Core的net8.0/net48及App的net8.0-windows：生产项目不引用tests、引用与Compile文件存在、不编译tests/artifacts/release，Core不启用WPF/WinForms或直接引用App/UI程序集；并核对默认配置、默认词库、提示词、图标的源路径、Link及发布复制约定。

这是一项窄范围结构回归，不证明Core所有类型均不间接使用UI、不覆盖反射和全部传递包依赖，也不代替CAD宿主、发布产物或业务验收。现另求值CAD的AutoCAD/GstarCAD两个条件分支，检查自动目标框架、Core引用、宿主SDK集合与Private=False、源码范围及GSTARCAD常量，共46项。只求值不编译、不启动宿主；真实SDK可用性与部署运行仍由对应测试/验收负责。

## 术语编辑的本地存储边界

MainViewModel.GlossaryEditor 保留编辑状态、数量/空值/冲突校验、确认提示和保存后刷新。账户语言路径保持原有规则；本地草稿的 JSON 写入、同目录临时文件替换和失败清理由 Core/Infrastructure/Glossary/GlossaryFileStore 负责。保存成功后仍按原顺序重载词库，再更新编辑基线，不改变云端同步或命中规则。

这只是具体存储职责的提取，不是术语模块全部分层完成：手动导出、云端编排和编辑规则仍有部分位于 ViewModel。没有新增 Repository/IoC 框架或拆分工程。4项隔离存储测试覆盖格式/元数据、空词库替换、锁文件和目标为目录时的保护；不等于真实用户权限和所有磁盘故障验收。