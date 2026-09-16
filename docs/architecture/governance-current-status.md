# 工程治理当前状态（2026-09-16）

> 此页为历史阶段快照，不代表当前版本或当前测试结果。最新状态见 [当前交付报告](governance-current-delivery.md)，原始要求缺口见 [完成性核对](governance-completion-audit.md)。下文保留当时证据。

本页是当前状态索引。旧过程报告保留历史事实，不能将其中早期“未通过”或早期测试数量直接当作当前结论。仍有待验收与受阻实施项，不声明整个目标完成。2026-09-16用户最新决定：测试验收后续自行进行，当前先完成无需验收的整理工作；不再要求用户现在登录或操作CAD。

## 本轮新增可复核结果

- 便携安装/卸载：49项通过。真实执行生成的卸载器，覆盖预览无修改、路径越界、根目录不匹配、禁止登记用户配置、重复路径、非法哈希、文件锁预检、修改资源与未知文件保留、重复卸载。
- Inno：46项通过；从095749真实构建升级至095905真实构建。二者均为2.1.1，不把该结果宣传为不同语义版本的数据迁移验收。
- 新便携ZIP已生成：从已验证候选构建打包，未从本地release复制用户数据；包含Uninstall.ps1和修订后的说明。
- 使用新包在隔离目录真实安装（不创建快捷方式、不启动程序），EXE、插件和卸载脚本哈希匹配；随后实际卸载程序，配置保留。发布依赖验证通过。
- 本轮不编译APP、不替换release、不运行CAD、不部署网站、不做真实交易。
- release/DwgTranslator.exe当前SHA256仍为52DE397B96AE79B447ACC19B4C7BABA105CBA7F1D48E834633B057D5E5403C23。

## 证据

以下均相对 D:/DWGC2E/artifacts/install-default-20260916-082921/remaining-acceptance：

- safe-uninstall-final/result.log：49项结果和唯一夹具路径。
- inno-upgrade-final/results.json：46项结果、构建升级标识及隔离安装位置。
- delivery/package.log、verify.log、install.log、uninstall.log、results.json：新包的打包、依赖校验、安装/卸载和哈希结果。
- delivery/DwgTranslator-2.1.1-win-x64.zip：当前新增本地便携包，未上传公开下载。

CAD宿主成功证据位于同任务目录的 cad-integration-audit/ping-confirmed-final.txt；本机浩辰2024新会话自动加载已通过，不等于APP端到端业务全链路通过。

## 尚未关闭

| 项目 | 缺口 / 下一步 |
|---|---|
| APP→CAD→输出 | 真实桌面操作、实际翻译调度与输出内容需同一次流程证据；不以模拟API或插件PING替代 |
| 真实账号/会员/设备/云翻译 | 用户自行登录、使用可测试图纸；不读取或披露凭据，不进行真实支付 |
| 更新流程 | 路径静态与包验证不足以证明真实检查/更新全流程 |
| 安装失败恢复 | 便携复制不是原子事务；完整回滚仍未实现/验证，先前操作被策略阻止，不换方式绕过 |
| 卸载边界 | 已验证锁定文件；同目标运行进程和联接目录拒绝尚缺独立行为测试；快捷方式目前由用户手动清理 |
| 产物清理 | 本轮尝试清理本任务已被替代的合成夹具，被执行策略拒绝，未删除；不改工具重试，不记为完成 |
| 最终Git审查 | 安装相关tracked文件diff --check通过；整体工作区还有其他任务更改和未跟踪新文件，不等于全部差异已审核 |
| 早期盘点证据 | 原始目录仍缺失；已生成current-inventory当前快照作为补充，但不冒充移动前历史清单 |

## 保留与延期

- 按用户要求不改回写/布局算法，既有视觉观察不伪装为全部通过。
- LicenseGenerator、零字节根settings.json、本地IDE状态及其他任务验收产物仍不自动删除。
- UI旧职责边界已记录于overview.md，不强拆程序集、不引入新架构框架、不改变业务行为。
- 卸载保留用户数据、未知文件、修改资源、清单和辅助脚本；不承诺完整清空目录或中途故障原子恢复。
## 后续补充：当前清单与说明修正

- tools/Get-ProjectInventory.ps1新增只读盘点工具，输出路径必须是新目录，不删除文件、不读取配置秘密值、不跟随联接目录。
- current-inventory/files.csv记录8691个当前文件；project-references.csv提取56个显式工程条目；5条ProjectReference的实际目标均存在。自动分类只是保守用途提示，不是死代码证明。
- 自动规则剩余11项已人工分级记录unknown-review.csv：10项是后端配置/SQL/工具/文档，KEEP；根settings.json核对为零字节，仍REVIEW REQUIRED，未删除。
- .git内部数据库不枚举；历史原始清单无法恢复，不将当前快照冒充重构前完整证据。
- 安装说明修正为当前账户登录/云翻译方式，移除过时的填写模型API Key步骤；保持实际业务代码不变。
- delivery-final/DwgTranslator-2.1.1-win-x64.zip为说明修正后的最新本地便携包；依赖验证通过，安装/卸载脚本及说明与源文件哈希一致，EXE与release一致。此前delivery的隔离安装/卸载验收仍适用于未变化的脚本和程序；本次仅文案更新。
- 旧delivery包尚未清除，产物清理仍受前述策略阻断，不声称符合最终有界保留目标。
- 工具与安装测试脚本语法解析通过，说明文件diff检查通过。未执行APP构建或改动回写算法。
## 用户延期验收后的交接整理

- README同步自动安全卸载的实际行为，移除“只显示手动提示”和旧19项数量；修正CAD平台判断以cad-platform.txt为准，而非假设包名含平台。
- governance-delivery.md汇总实际目录、发现问题、引用变更、已知删除/保留、历史构建结果、Git审阅边界与56条逐文件移动表。
- 56个搬迁目标逐项存在；仅tools/Test-DesktopArtifactCleanup.ps1旧入口保留兼容转发，其余55个旧路径不存在。
- manual-acceptance.md将真实登录、翻译、CAD输出、更新与安装边界验收交给用户后续安排，不计为通过。
- 本轮仅静态文件/引用与文档审阅，没有启动测试、构建、安装或卸载，没有启动APP/CAD，没有删除产物。
- 完整失败回滚、快捷方式安全自动清理及受策略阻断的产物清理仍为实施缺口，不因用户延期验收而改写为完成。
## 快捷方式清理实现（用户延期验收之后）

- installer/Install.ps1增加可选Shortcuts清单字段，只记录StartMenu/Desktop类型及SHA256，不接受清单任意外部路径；重装保留旧归属，-NoShortcuts不丢弃记录。
- 创建前检查路径重解析点，保留另一安装的链接、自定义参数链接、未登记或已修改链接，不抢占用户快捷方式。
- installer/Uninstall.ps1从当前用户固定目录推导链接路径，同时验证登记、哈希、TargetPath和空参数；预览不删除，正式执行前复查哈希和重解析点。旧清单无Shortcuts仍可使用。
- 此项从“未实施”改为“源码已实现，尚待验收”。仅完成语法解析和静态审阅，没有执行COM快捷方式创建/删除、安装或卸载，没有重新打包。
- delivery-final中的旧已验证包保持原样；最新源码安装说明与旧包因此存在明确版本差异。49/46项历史结果不覆盖新增快捷方式行为。待用户安排验收后再更新安装包，不静默把未验收脚本当正式交付。
## 源码目录安装保护

静态审阅发现默认D:/DWGC2E与本机源码根相同。便携Install.ps1已在任何目标写入前拒绝驱动器根、含DwgTranslator.sln或.git的目标；Inno PrepareToInstall在正式和隔离分支均拒绝同样的源码标记。默认选择规则不变，开发机需显式另选安装位置，不能把源码根当正式安装目标。安装引导和说明同步纠正“安装到用户目录”的旧描述。

仅修改源码并做PowerShell解析/差异检查，没有编译Inno或运行安装验收，既有包保持原样；这项保护与快捷方式变更均待后续验收和重新打包。完整失败回滚及历史产物清理的既有策略限制仍未解除，不尝试绕过。
## 打包编码修正

静态检查确认installer/安装.cmd第二行是chcp 65001，而New-ReleasePackage.ps1此前写成GBK。源码现统一为无BOM UTF-8，并仅为.cmd规范CRLF；说明仍UTF-8 BOM，PowerShell中文脚本保留BOM。没有执行打包或安装测试，旧已验证ZIP未修改。此修正仍需后续在Windows双击安装引导时验收，不把静态解析当作运行通过。

## 2026-09-16 12:28 收尾复核（只读核查，不执行延期验收）

- 网站字体专项独立完成本地交付：16页、120组页面/状态/视口检查、240张截图；51项浏览器回归、22项静态/客户端测试。证据和当前源文件SHA256汇总在 artifacts/website-typography-20260916/delivery-manifest.json。不代表桌面工程全目标完成，也不代表生产业务验收。
- 当前release已被其他工作更新：实际ProductVersion为2.1.1+ui.20260916-120540.a3f6f1f，EXE SHA256为D2540F60DB60F26203347FC45446212A1F07D5DD6BBD4124C4402E161716CC14。本文前述095905版本/hash仅是历史记录，不再是当前release。本轮没有构建、替换或回滚release；不能把旧回归记录套用于当前版本。
- tests/BuildPipeline/Test-UninstallSafety.ps1仍不存在，新增快捷方式/源码目录保护的独立行为验证不得标为通过。用户延期的安装与真实业务验收仍延期。
- 完整安装失败回滚、已记录的受阻历史产物清理、真实APP/CAD/云端业务链路仍未关闭。未重试此前被拒绝的操作，未自动安装、卸载、删除或发布。

## 2026-09-16：恢复隔离安装验证

- 本轮证据统一位于 artifacts/installer-safety-20260916-resumed/report.md。
- 补齐便携卸载 .git 源码保护，并把便携自动回归从49项扩展至61项，全部通过。
- 最新安装向导源码正常/隔离编译成功；40项隔离安装、同版本修复及卸载检查通过，真实配置、生产注册表和测试期间release未变。
- 以固定版本2.1.1+ui.20260916-123614.a3f6f1f生成本地候选包，执行包内脚本隔离安装/卸载，程序哈希一致、设置保留。其他任务仍可能更新release，因此不将此包宣称为永久最新版本。
- 未构建APP、未替换release、未上线下载、未做真实付款。事务性安装回滚、便携COM快捷方式完整矩阵及真实业务人工验收仍未完成；具体范围见本轮报告。
## 补充：便携快捷方式与旧清单兼容验证

- tests/BuildPipeline/Test-PortableShortcuts.ps1 新增正式测试：解析并执行生产 New-Shortcut 函数，使用真实 Windows COM，但所有链接只落入指定 artifacts 子目录；不执行安装器顶层逻辑，不接触真实桌面/开始菜单。
- 10项检查通过：创建目标/空参数/归属哈希，已登记链接重装更新及记录去重，用户修改、自定义参数、另一安装目标、未登记链接全部保留。
- tests/BuildPipeline/Test-Installer.ps1 增加 -NoShortcuts 重装保留两项归属及哈希、无 Shortcuts 字段的旧清单预览兼容；全套结果由61项增加到65项，全部通过。
- 证据：portable-shortcuts/results.json、portable-shortcuts.log、ownership-regression.log。
- 测试运行方式：powershell -NoProfile -ExecutionPolicy Bypass -File tests/BuildPipeline/Test-PortableShortcuts.ps1 -ResultDir <源码artifacts内尚不存在的结果目录>。
- 本次仅新增测试与文档，没有修改生产安装脚本或APP，上一轮候选包仍对应当时脚本。
- 未将函数级COM测试冒充整套卸载快捷方式端到端验收：固定用户目录派生、卸载删除及联接目录等完整组合仍待完成；事务性回滚和人工业务验收仍未完成。
## 补充：便携快捷方式卸载端到端检查

新增 tests/BuildPipeline/Test-PortableShortcutUninstall.ps1。它复制当前正式卸载脚本并启动真实子进程；只为该子进程设置隔离 APPDATA，父进程及 Windows 用户目录配置不变。清单仅含 StartMenu，不含 Desktop；链接全部由 Windows COM 创建在本任务 artifacts 目录。

30 项检查全部通过，覆盖8个场景：登记且未修改链接删除、用户修改链接保留、不同目标链接保留、自定义参数链接保留、未登记链接保留、重复清单拒绝、锁定链接拒绝、预览无更改。各场景均验证用户设置不变，重复/锁定场景验证整个测试目录文件内容不变。

证据：shortcut-uninstall/results.json、shortcut-uninstall.log 和8个子进程日志。已核对结果中的正式卸载脚本SHA256与当前源码一致。

范围：此结果是真实卸载进程及 StartMenu 路径分支的端到端验证，不是仅函数模拟；仍不证明 Desktop 已知目录映射、重解析点竞态或事务性故障恢复。未碰真实桌面/开始菜单，未运行APP，未发布新安装包。
## 补充：运行实例与根路径联接保护

新增 Test-InstallerPathSafety.ps1，18项检查通过（path-safety/results.json、path-safety.log）。测试复制本机Node到独立假安装目录，确认同目标进程存活后运行正式安装/卸载脚本：均由运行实例检查拒绝，安装文件哈希不变且未结束测试进程；自然退出后重装成功。进一步验证目标根联接、包根联接、经联接目录运行卸载脚本均拒绝，物理目标文件不变。测试未启动真实APP或CAD。

两条测试联接指向同一新夹具内目标，Path/Target均记录在results.json；未删除它们，后续清理不得递归跟随联接。此结果不覆盖并发路径替换竞态、所有嵌套联接组合、Desktop路径映射或安装事务回滚。

manual-acceptance.md已修正此前“新增源码/快捷方式/进程保护尚未测试”的过时表述，按实际覆盖范围勾选，不把局部测试扩大为全部验收通过。本轮无生产代码改动、无APP构建、无release更新。
## 补充：Inno磁盘根目录保护及源码目标拒绝

- installer/DwgTranslator.iss 增加 IsDriveRoot 与正式 PrepareToInstall 拒绝逻辑，避免选择 C:\ 或 D:\ 作为安装位置。默认 D:\DWGC2E / C:\DWGC2E 规则不变。
- 隔离编译版在实际安装流程运行相同的判定函数，验证两个盘根、D:、C:/及两个正常子目录；没有对真实磁盘根执行安装。
- Test-InnoInstaller 增加 .sln/.git 源码标记拒绝验证：标记置于隔离app目录，正式源码检查返回明确原因，拒绝前后所有安装文件的路径与哈希不变。
- 修改后正常与隔离安装器均编译成功，45项测试通过。证据：inno-path-guards-final/results.json、verified-hashes.json、同目录完整日志。测试使用固定程序版本2.1.1+ui.20260916-124619.a3f6f1f，不声称为其他任务后续发布的永久最新版本。
- 第一次编辑因混合换行精确匹配失败，没有写入源码；inno-path-guards目录是未改动脚本的40项结果，不能作为新保护证明。以final目录45项为本轮权威结果。
- 未执行正式安装器，只运行隔离安装器；真实配置、生产卸载注册表及测试期间release保持不变。没有APP构建或网站上传。磁盘根测试是判定函数在编译安装器中的验证，不夸大为真实盘根端到端安装。
## 39项完成性核对（2026-09-16）

详见 docs/architecture/governance-completion-audit.md。本次直接刷新只读清单10012项、56个显式工程条目，5条ProjectReference实际目标均存在；当前release 2.1.1+ui.20260916-124619.a3f6f1f哈希与元数据一致，对应发布日志Core195/195、CAD16/16及UI冒烟通过。README修正正常构建入口和过时的49项/快捷方式尚未测试表述。

整体仍未完成：初始完整盘点证据缺失、部分UI职责债务、历史缓存/夹具收尾、完整事务回滚、部分安装边界、真实业务验收和跨任务Git整体审查均不得被安装测试结果替代。后续以完成性核对中的明确缺口推进，不继续把单纯增加测试数量当作收尾。
## 补充：CAD内嵌备用插件交付修复

修复旧内嵌DLL/缺少Core依赖、只按长度判断版本和直接截断旧文件问题。现在随构建嵌入完整私有插件输出，释放时校验SHA256并逐文件安全替换，回写算法未修改。旧源码二进制保留为待确认项。

本轮完整正常发布通过：安装65、Core226、布局21、CAD部署16、UI冒烟（含新增备用插件15项）。134135交付成功后，其他活动任务发布134303；已确认134303日志包含本轮新检查通过，未覆盖回旧版本。核查时当前release为2.1.1+ui.20260916-134303.a3f6f1f。另已从该版本干净候选生成本地安装ZIP，12文件哈希验证通过，未上传。

详细报告及逐项证据：artifacts/installer-safety-20260916-resumed/embedded-plugin/report.md。该结果不代表安装器完整事务回滚、历史清理或真实业务验收完成。

## 补充：发布入口互斥

正式Publish-Desktop入口现通过Enter-DesktopPublishLock在测试/构建前获取工作区独占文件句柄，并在finally释放。新增隔离真实进程测试13项通过，验证竞争拒绝早于构建、无候选/release副作用、正常退出和失败后可再次获取、残留标记可重用及不同工作区互不干扰。未构建APP、release仍为134303且哈希匹配。证据：artifacts/installer-safety-20260916-resumed/publish-lock/report.md及verified/results.json。该锁不冻结源码或保护绕过入口的手工命令，不将其表述为安装事务回滚。

## 补充：完整Clean交付链已验证

Publish-Desktop新增-Clean入口，在同一发布互斥锁内完成当前解决方案6工程Clean/Restore/Build，再执行全部门禁与Publish。135439版本已更新release，哈希核对通过，3个便携文件不变，回滚1份。结果：Core236、任务中断7、布局21、CAD16、安装保护65、UI冒烟均通过；正式/隔离Inno编译成功，隔离安装修复卸载45项通过。构建有1条UI测试CS8602警告、0错误，未隐瞒警告。原39项审计第32行已据当前直接证据更新；不代表第33项真实业务验收通过。详细报告：artifacts/installer-safety-20260916-resumed/clean-delivery/report.md。

## 补充：打包输入保护与混合改动审核边界

New-ReleasePackage新增实际候选路径/版本/哈希/默认配置资源验证，拒绝个人词库与安装目录输入，保留build-info并禁止覆盖已有ZIP，共用发布锁。最终16项打包检查、中文入口13项通过；未构建APP或替换release，当前135929哈希与元数据匹配。证据：artifacts/installer-safety-20260916-resumed/package-input/report.md。

展开后的Git快照287个路径已分为5组，57条搬迁重新验证；分组不是全量语义审查，剩余空白问题未批量修改。详见docs/architecture/change-review-boundaries.md及change-review/working-tree-inventory.csv。完整事务安装回滚、历史清理和用户延期的真实业务验收仍未完成。

## 补充：Core与项目依赖边界核对

对7个工程/Core双目标进行了8份MSBuild实际条目评估；Compile与6条ProjectReference目标均存在，生产工程无测试反向依赖，Core双目标没有直接WPF/WinForms依赖。补齐默认配置、日志、账户输出和支付恢复记录的路径来源说明，未把静态检查当作全部运行场景验收。Core.csproj只修正误导性的历史架构注释，未更改编译或业务行为。详见artifacts/installer-safety-20260916-resumed/architecture-audit/report.md。

## 补充：购买窗口的文件持久化职责已移出

BillingWindow的pending恢复JSON、账号哈希路径、排他文件锁及安全落盘已抽至Core/Infrastructure/Payments/PendingPurchaseStore，数据模型PendingPurchase保留旧JSON契约。下单顺序/渠道/幂等键/查询和权益行为未重写。新增11项隔离单元测试通过，完整受控购买窗口恢复冒烟通过。

本任务发布首次被另一任务的活动锁拒绝，未绕过；随后对方正常发布142413已包含本轮变更及回归，已核对release哈希一致、三份便携文件不变、回滚仅一份。全量Core257通过/1跳过；UI测试原CS8602警告仍存在。详见artifacts/installer-safety-20260916-resumed/billing-storage/report.md。窗口仍有网络编排，不宣称彻底分层或真实付款已验收。

## 补充：打包输出路径统一

New-ReleasePackage输出统一按调用者PowerShell位置解析为绝对路径，并限制在本工作区artifacts内；不再允许src、release和工作区根作为输出位置。Assert-CleanPackageInput同步修正相对路径解析。新增5项连同原16项共21项通过，包括真实Push-Location后的相对输出打包。只改工具/测试/文档，未构建APP或替换release；详细证据artifacts/installer-safety-20260916-resumed/package-paths/report.md。


## 2026-09-16：安装包方括号目录修复

真实复现路径含[review]时误报缺少CAD插件。New-ReleasePackage使用LiteralPath与ZipFile，正式回归增加3项，共24项通过；另对ZIP全部13条目逐一验证SHA256一致。证据：artifacts/installer-safety-20260916-resumed/package-literal-paths/report.md。本轮无APP构建或release替换，不代表整体Git审查完成。


## 2026-09-16：任务/账号持久化局部审查

逐块核对任务保存、损坏记录保留、账号目录/会话隔离及默认输出路径；重新构建Core测试，82通过、1权限相关跳过。受审9文件的限定范围与哈希见 artifacts/installer-safety-20260916-resumed/task-account-review/review-evidence.json；断言、证据强度和缓存尽力而为语义边界见同目录report.md。未构建APP、未替换release、未改变业务或回写算法；不能据此宣布完整工作区审查完成。


## 2026-09-16：正式交付禁止跳过测试

发现Publish-Desktop的SkipTests可进入release替换，已在构建前拒绝非BuildOnly的跳过测试调用。真实隔离子进程20项通过：两类拒绝无产物副作用、隔离诊断仍受锁约束、既有互斥/释放全部通过。证据 artifacts/installer-safety-20260916-resumed/release-gate-policy/report.md。没有APP构建、release替换或业务代码改动；不冒充完整交付重验。

## 2026-09-16：云端语义审查补充
订单/支付本地回归76项通过，新增已有数据迁移保留与重复结算保护测试2项通过；基础云端测试72项通过、TypeScript类型检查通过。新增测试见 cf-worker/tests/populated-migration.test.mjs，完整边界见 docs/architecture/cloud-semantic-review-20260916.md。未部署线上Worker/D1，未进行真实支付，不能据此宣称云端业务验收完成。
