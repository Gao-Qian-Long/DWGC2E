# DWGC2E 工程治理：39项当前核对

## 最新交付：输出路径安全修复（2026-09-16）

当前release为 **2.1.1+ui.20260916-221152.a3f6f1f**，SHA256 **8DEB8AAC4ACDE3ED0E35F99835EA8E90B2F2009744F779F50C7C5CA3F41A0070**。

已修复：overwrite拒绝批次已占用源/目标路径；重命名编号耗尽返回错误并跳过；备份编号耗尽（包括显式原图覆盖分支）返回错误并跳过。保留原有显式覆盖自身源文件和普通既有输出的配置行为，未修改CAD回写/布局算法。7项新增用例，路径专项18通过；正常发布完成Clean/Restore/Build/Publish，全Core379通过/1跳过/0失败，UI烟测及候选资源审计通过。已更新release，独立核对EXE哈希、settings.json不变及唯一回滚版本。人工验收按用户要求跳过，不计通过。

证据：artifacts/installer-safety-20260916-resumed/output-boundary-repro/publish-safety.log、resolver-safety.trx、installed-safety-verification.json。此项覆盖路径规划，不宣称所有写入竞态、磁盘备份行为或真实CAD已验收；安装事务回滚和其余治理审查仍未完成。


核对日期：2026-09-16，20:50。此文件只记录本次核对现状，不再堆叠多个“最新状态”。原文已保存至 artifacts/installer-safety-20260916-resumed/inventory-classification/completion-audit-before-consolidation.md。

**整体目标未完成。** 有证据的结构治理、自动验证与交付不等于真实业务验收。用户已将人工验收延期；不要求现在提供账号、运行CAD或付款。

## 当前证据入口
- L：artifacts/installer-safety-20260916-resumed/root-state-review/：本地状态8文件元数据及保留判定；移除正式dev.bat过时忽略规则，27项只读Git忽略回归通过，无删除。
- V：artifacts/installer-safety-20260916-resumed/ui-boundary-review/：术语本地存储提取，新增4项测试；213643完整发布Core372通过/1跳过，UI/候选审计通过，安装哈希和便携数据独立核对。首次环境故障及重试日志分别保留。
- O：artifacts/installer-safety-20260916-resumed/output-boundary-review/：57项迁移核对通过；输出规划、批量冲突、实际DWG/DXF提交与安全提交24项回归通过。
- F：artifacts/installer-safety-20260916-resumed/configuration-preservation/：未知配置字段4例红绿回归、配置组40通过、211935完整发布；Core368通过/1跳过，UI/候选审计通过，便携数据不变。
- K：artifacts/installer-safety-20260916-resumed/configuration-review/：配置/地址/账号目录审查，既有程序集36项回归通过；区分路径移动与行为修改，并列出未来schema加旧端点的未知字段保留缺口。
- T：artifacts/installer-safety-20260916-resumed/tool-catalog/：23个根工具完整索引、源码哈希与退休授权入口审查；仅文档变更，不运行截图、线上探针或清理。
- R：artifacts/installer-safety-20260916-resumed/build-config-review/：5个构建文件逐项diff语义审查与哈希；结构回归扩为46项，含AutoCAD/GstarCAD配置求值，不冒充宿主构建或运行。
- B：artifacts/installer-safety-20260916-resumed/build-reference-inventory/：最终盘点14615文件、67显式构建声明；增加.props/.targets/.sln及Solution构建顺序，30项工具回归通过。只读声明提取，不求值MSBuild。
- G：artifacts/installer-safety-20260916-resumed/publish-architecture-gate/：204621完整交付；发布前候选资源审计36项、审计器8项正反用例、独立EXE及3份便携数据核对通过。
- S：artifacts/installer-safety-20260916-resumed/installed-startup/：历史202954安装版无账号隔离启动、62条默认术语、正常关闭；限定范围文件前后哈希无变化。窗口截图接口不可用，使用界面文本，非视觉或全业务验收。
- P：artifacts/installer-safety-20260916-resumed/purchase-service/：202954完整发布日志、已安装哈希及3份便携数据验证、57条迁移核对、36项编译资源审计、32项工程评估。
- I：artifacts/installer-safety-20260916-resumed/inventory-classification/：当前13937项文件元数据、59项显式工程条目、23项盘点工具回归；371条展开Git状态分组。数字是带时间的快照，后续报告本身会增加文件数。
- M：docs/architecture/project-moves.json：旧路径→新路径、混合改动审查哈希；与P/moves.json配合使用。
- C：cleanup-report.md：SAFE TO DELETE / REVIEW REQUIRED / KEEP；分类不是删除授权。
- A：docs/architecture/overview.md 与 dynamic-dependency-review.md：实际职责、动态入口与限制。
- U：docs/architecture/manual-acceptance.md：用户后续验收清单。

当前安装版：**2.1.1+ui.20260916-213643.a3f6f1f**，D:/DWGC2E/release/DwgTranslator.exe。
SHA256：A7BEA6C1690DE0C9A6B0703D77AF15DD4A6B84B4EDC4639CCE8646E29AB01103。
最新交付将术语本地存储移出ViewModel，完整构建并更新release；配置未知字段保留已包含；候选审计门禁及购买用例分离保留，仍不是全部UI分层完成。

## 逐项核对
| # | 要求 | 当前判断与证据/缺口 |
|---|---|---|
|1|八阶段顺序|原始盘点证据缺失，不能追证最初全部执行顺序。当前盘点、设计、移动、引用修复、构建、受控回归和报告有证据；真实回归未全部完成。|
|2|不破坏业务|现有全门禁P通过；混合工作区含其他行为修复，不能声称整个diff只有目录变化。真实完整行为一致性待U。|
|3|完整盘点|B最终快照14615文件、67显式构建声明，含共享构建输入和Solution顺序；跳过.git数据库及联接。声明未求值，不是全部动态依赖证明。|
|4|垃圾识别|C分级保留；生成目录和证据目录不等于可整目录删除，历史处置仍未完成。|
|5|文件分类|I有保守分类，10项后端用途已核实并纳入规则，未知仅根settings.json（0字节）。路径分类不等于逐文件死代码判定。|
|6|合理目录|src/tests/assets/tools/installer/docs/artifacts与用户指定release已存在；未强拆为模板中的众多项目。|
|7|UI业务分离|购买恢复持久化/Checkout/已付款清理由PurchaseCheckoutService编排，ViewModel按功能组织；窗口列表/会员查询和轮询等仍未完全分离，A明确债务。|
|8|Application用例|Core/Application按业务组织，保留稳定程序集；未为架构创建过量抽象。|
|9|Core无UI依赖|P工程评估和程序集元数据无直接App/WPF/WinForms依赖，不能扩展为全传递包/反射纯Domain证明。|
|10|Infrastructure外部系统|已有Configuration/Files/Accounts/Payments/Proofreading等职责目录；UI部分编排仍有债务。|
|11|CAD独立|App/CadIntegration为桌面桥接，独立Cad工程供宿主加载；包与内嵌依赖P检查通过。|
|12|运行数据|AppData及账号隔离已有契约；release保留便携数据。S实际启动/退出在限定范围无文件变化；所有业务场景仍未验收。|
|13|发布与源码分离|artifacts统一候选/包/证据，release为用户要求的运行入口，唯一回滚；历史证据/夹具处置未全部完成。|
|14|正式测试集中|正式测试在tests，旧工具兼容入口保留；独立审计工具有明确运行入口。|
|15|DWG样例|根目录无DWG/DXF；各测试/证据中的图纸保留，不把未知样例移动或删掉。|
|16|配置与秘密|默认模板、忽略与候选文本审查见configuration-security-review.md；不是所有历史/二进制零秘密保证。|
|17|统一命名|按功能整理目录，保留历史命名空间、程序集和数据目录兼容；未做全项目改名。|
|18|死代码|C与动态入口审查仅列候选；旧授权工具等仍未证明可删，保留。|
|19|DLL安全|插件/私有Core/宿主SDK职责已有A和P证据；没有仅凭无using删除DLL。|
|20|资源|图标/默认词库/提示词集中assets，Pack URI和发布Link保持；P核对21个XAML编译资源及4项内嵌CAD资源。|
|21|gitignore|L移除正式dev.bat过时规则，27项实际Git规则回归通过；本地配置/依赖/产物继续排除，不冒充穷尽路径或已跟踪秘密清除。|
|22|构建脚本|Publish-Desktop为正规交付入口，dev/publish兼容入口保留，脚本职责及副作用由T和tools/README.md逐项说明；历史授权入口确认为拒绝型占位，不删除未知工具。|
|23|README|开发、构建、发布、CAD、数据/配置和审计入口已说明；作为开发文档保留。|
|24|架构文档|A覆盖真实目录、依赖方向、路径契约及未完成边界，不宣称纯分层已全部实现。|
|25|不过度设计|保持3个生产工程，无新增DDD/CQRS/IoC框架；本轮只增加购买用例服务，不强拆程序集。|
|26|引用路径|P构建、发布、资源和迁移检查通过；插件文件来源优先级已统一，缺失时内嵌/开发后备仍有差异，不能声称所有动态路径已验证。|
|27|Namespace|移动尽量保留现有namespace/x:Class/程序集，P构建通过；动态序列化兼容仍有实际验收边界。|
|28|模块职责|功能目录及partial分工已落地；TaskManager/BillingWindow等债务仍在，不为达标大改核心算法。|
|29|非机械拆分|未按行数拆文件或创建通用垃圾桶工具类，CAD回写算法按用户要求保持。|
|30|根目录干净|主体清晰，但%SystemDrive%、.wrangler、零字节settings.json仍保留；不能判为完全收尾。|
|31|删除清单|C三档清单存在；本轮无删除、移动或运行迁移。|
|32|完整Build|V正常-Clean完成Clean/Restore/Build/Publish：Core372通过/1跳过/0失败、UI和其余发布门禁通过；Inno专项为历史独立证据，未冒充当前重跑。|
|33|核心回归|受控UI、默认资源、CAD部署、任务恢复等有自动证据；真实启动交互/登录/会员/翻译/CAD/输出/设置/支付窗口/更新由U逐项验收。|
|34|运行内容不回源码|S实际启动/正常退出验证：源码与release限定范围无新增/删除/内容变化，运行数据在隔离目录。残留本地目录未清，全场景证明仍缺。|
|35|Git可审查|M57项通过；I展开371路径分组；R已逐项审查5个构建文件并绑定哈希，但不是全diff语义审核。全局diff --check还有7处已记录空白问题；未格式化、暂存或提交其他改动。|
|36|正式模块保护|桌面/翻译/CAD/术语/云端/账号/会员/支付订单/设备/设置/输出/日志/更新保留；未按测试命名删除正式模块。|
|37|根目录可理解|README与实际目录可定位源码/测试/工具/发布；遗留根本地状态例外仍需处理。|
|38|最终报告|当前交付报告、M、C、A、P、U涵盖移动/删除理由/保留/引用/构建/回归；这是进行中报告，不把未验收写成完成。|
|39|最终原则|结构和交付可维护性已有改善；未知历史处理、UI职责债务、安装事务回滚、全量变更审查与真实验收仍未完成。|

## 剩余工作与执行边界
1. 安装器完整事务式升级回滚未实现，不能用安装测试通过代替。此前被阻止的操作不换工具重试。
2. 输出路径与安全提交已由O核对部分场景；overwrite跨源路径冲突和编号耗尽尚未验证/修复，不能视为全策略安全；历史/未知文件的处置仍需审查，尤其本地状态、独有恢复数据和验收证据；不因体积大或名称可疑删除。
3. Git其余行为改动需按分组和文件哈希逐块审查；空白问题另列，不靠全项目格式化掩盖。
4. K发现的配置未知字段丢失已由F修复并发布；保留JSON语义值，不等于所有未来schema语义均兼容。
5. UI职责渐进分离，不改CAD算法，不为目录好看拆新程序集。
6. 用户真实验收延期，后续按U执行，无需现在提供密码或进行真实支付。

以上未完成项保持未完成；盘点工具现30项通过，B补齐共享构建输入与Solution声明，但不提高未覆盖业务验收的证据强度。


## 输出边界隔离复现补充（2026-09-16）
证据：artifacts/installer-safety-20260916-resumed/output-boundary-repro/report.md。5个规划场景中，skip/rename普通跨源冲突安全，overwrite跨源冲突、重命名编号耗尽、备份编号耗尽3个缺口已复现，尚未修复（不再仅为静态怀疑）。仅规划，未调用写图或实际覆盖。原正式测试未设置同目录，现修正这一夹具条件，重新编译后11项输出路径测试通过；不代表上述3项安全通过。没有生产代码变更、APP构建或release替换。


> 2026-09-16 最新执行口径：按用户明确要求，本任务跳过全部人工验收，交由用户后续自行测试；这些项目不再阻塞工程整理收尾，也不计为已通过。清单保留为后续测试交接，不要求用户现在登录、操作CAD或测试支付。自动化回归、UI烟测、构建/发布安全门禁仍必须执行。未实现的开发项（例如安装事务回滚）不属于人工验收豁免，继续单列。

## 空白审查补充（2026-09-16）
已对5个受影响源文件和cf-worker/schema.sql做窄范围空白清理，仅移除行尾空格及多余末尾空行；非空白字符核对保持一致。git diff --check现为0，记录见 artifacts/installer-safety-20260916-resumed/whitespace-review/。未做全项目格式化，未执行APP发布。


## 安装晚期文件锁修复（2026-09-16）
安装器新增所有待覆盖文件的写入前检查；隔离复现从主程序已变更变为全文件不变。正式安装回归70项通过，证据见artifacts/installer-safety-20260916-resumed/installer-failure-audit/report.md。不是完整事务回滚，断电/复制中故障/快捷方式等仍未关闭；仅源码修复，未更新已发布安装包。历史拒绝的原始反馈未找到，不再将其泛化为当前缺少用户授权。

## 2026-09-16 收尾更新
- 安装事务已实现并纳入正式发布门禁：写入前快照、哈希校验、异常回滚、进程中断后的下一次恢复、个人配置保护及恢复证据保留；隔离事务测试16场景通过。
- 正式发布 `2.1.1+ui.20260916-225348.a3f6f1f` 已更新 `D:\DWGC2E\release`，安装后哈希与 `release/build-info.json` 一致；便携数据3类路径核对保持一致；保留 `artifacts/release-backup-20260916-225348` 作为单一回滚版本。
- 最终安装包：`artifacts/installer-safety-20260916-resumed/final-delivery/DwgTranslator-2.1.1-win-x64.zip`；包含 `InstallTransaction.ps1`。打包输入回归已通过。
- 项目结构46项、迁移清单57项、核心测试383通过/1跳过、安装70项、事务16项、Bootstrap13项、UI自动烟测及发布门禁通过；人工验收按用户要求延期，不作为本轮阻塞。
- Inno 安装入口仍由独立验收清单管理；本轮事务 helper 针对便携 `Install.ps1`，不声称改造 Inno 的物理断电事务能力。
- 工作区仍含其他任务的网页/云端行为改动及未逐项语义审核内容；本轮不据此声称全仓库“仅目录变更”。
