# 混合工作区改动审核边界

快照：2026-09-16 14:10（本地）。基线：a3f6f1f66e7884cdcd21ae88de9b1f9e3d1ab9ad。

逐文件状态、用途分组、当前SHA256见 artifacts/installer-safety-20260916-resumed/change-review/working-tree-inventory.csv 与同名JSON。清单覆盖已跟踪变动及展开后的未跟踪文件，不包含Git忽略的产物/私人数据，不等于全磁盘盘点。后续文件变化会使快照失效。

| 审核组 | 快照路径数 | 审核重点 |
|---|---:|---|
| 已映射搬迁 | 113 | 57条manifest的旧/新路径；包含去重、兼容入口和3条混合修改，不是113个纯移动文件 |
| 构建/安装治理 | 29 | 路径边界、数据保留、失败恢复、互斥、打包输入验证 |
| 工程/资源/文档 | 30 | Project引用、资源路径、默认配置、文档与实际证据一致性 |
| 桌面行为及测试 | 85 | UI交互、任务恢复、账户隔离、对应回归；不能混作目录整理 |
| 网页及云端 | 30 | 字体、后台留言、API/支付/调度等独立任务的行为变化 |

总计287个路径；这是审核路由，不代表全部语义已通过。

## 已发现必须单独审核的行为修改

- API默认域名与迁移逻辑：settings.json.example、AppConfig、ApiClientFactory及配置视图逻辑。
- 任务切换/清空和未保存校对的离开保护：MainViewModel多处流程。
- TaskManager 与 JsonTaskStore：输出路径治理与账户切换保护、损坏状态恢复交织，须按代码块拆审。
- WorkerApiClient.Transport：旧账户请求隔离，不是路径移动。
- App工程及MainViewModel.Environment：内嵌CAD完整依赖与按哈希安全释放，是交付可靠性变更；不是回写算法修改。
- cf-worker配置的分钟调度需独立审核；基线PAYMENTS_ENABLED已为true，不得声称本轮启用了支付，也未获准线上部署。

## 建议审核/提交顺序（本轮未暂存、未提交）

1. 搬迁及紧邻的Project/资源路径修复，保持旧新成对；混合改动不能整文件草率归入纯搬迁。
2. 构建/发布/安装脚本与其测试及说明；打包输入保护作为独立可审核补丁。
3. 桌面行为修改与对应测试，区分数据目录治理和其他可靠性改进。
4. 网页/云端独立审核，明确部署及付款权限边界。
5. 最终文档根据稳定状态更新，再运行完整门禁；不要git add .。

git diff --check当前仍失败：云端、页面XAML、UI测试、SafeFileCommitTests有少量空白问题。详细输出在change-review/diff-check.txt。未批量格式化或改写其他任务文件。当前无权把完整工作区宣布“无业务行为变化”或“全部审查通过”。

## 后续具体审查发现（2026-09-16）

New-ReleasePackage路径操作曾将方括号当作通配符，真实复现后已修复；Windows PowerShell 5.1打包回归24项通过，ZIP13条目字节验证通过。见artifacts/installer-safety-20260916-resumed/package-literal-paths/report.md。本项是局部代码审查与修复，不上调其他文件的审查状态。


## 2026-09-16：任务/账号持久化局部审查

逐块核对任务保存、损坏记录保留、账号目录/会话隔离及默认输出路径；重新构建Core测试，82通过、1权限相关跳过。受审9文件的限定范围与哈希见 artifacts/installer-safety-20260916-resumed/task-account-review/review-evidence.json；断言、证据强度和缓存尽力而为语义边界见同目录report.md。未构建APP、未替换release、未改变业务或回写算法；不能据此宣布完整工作区审查完成。


## 2026-09-16：正式交付禁止跳过测试

发现Publish-Desktop的SkipTests可进入release替换，已在构建前拒绝非BuildOnly的跳过测试调用。真实隔离子进程20项通过：两类拒绝无产物副作用、隔离诊断仍受锁约束、既有互斥/释放全部通过。证据 artifacts/installer-safety-20260916-resumed/release-gate-policy/report.md。没有APP构建、release替换或业务代码改动；不冒充完整交付重验。

## 2026-09-16：API配置迁移局部审查
新增v0/v1自定义服务器地址全链路保留测试，连同相关测试32项通过。审查及范围见 artifacts/installer-safety-20260916-resumed/config-review/report.md。未修改产品业务逻辑，不宣称生产域名或全部设置UI已验收。

## 2026-09-16：发布验证器合法路径修复
Verify-ReleasePackage在[review]目录真实失败后已修复LiteralPath；两验证器同步按PowerShell当前位置解析相对输入。独立PS5.1回归4项通过，包含假密钥和无效平台仍拒绝。详见 artifacts/installer-safety-20260916-resumed/release-verifier/report.md。未构建APP、未替换release。

## 2026-09-16：本地更新清单路径修复
修复PowerShell相对路径解析，正式回归11项通过；依据磁盘当前160406候选与ZIP完成清单验证。详见 artifacts/installer-safety-20260916-resumed/manifest-paths/report.md。不授权公开下载或强制更新；本轮没有APP构建/release替换。

## 2026-09-16：UI离开保护与任务恢复局部审查
核对取消保护及160406恢复提示断言。另发现校对保存仅有当前会话内存证据，未见对应落盘，不得宣称重启恢复人工译文已实现。未改业务或回写算法，范围及源码哈希见 artifacts/installer-safety-20260916-resumed/ui-boundary-review/。

## 2026-09-16：术语库局部语义审查

核对默认资源/账号词库优先顺序、语言上下文、云端revision保护、草稿保存及三方合并，Core专项32项通过。详见artifacts/installer-safety-20260916-resumed/glossary-review/report.md及源码哈希。明确记录加载失败先清内存、本机落盘和加载非一体事务、写响应仅比较原译文等限制；不将测试通过扩张为所有失败路径安全，不改翻译/回写算法，不构建APP。

## 2026-09-16：更新入口与全Core增量复核
更新入口7项模拟HTTP测试通过，覆盖匿名清单、API回退、自定义路径、取消和无配置；保留空对象不回退等现有语义边界。随后全Core回归288通过/1失败/1跳过，新增流水线测试在OutputPath非空断言失败，单独复跑亦失败；已向相关任务同步，未改算法或掩盖测试。详情 artifacts/installer-safety-20260916-resumed/update-review/report.md。实际release仍163249且EXE哈希匹配，本轮未构建APP或替换release。此前发布成功不代表当前工作树全测试通过。

## 2026-09-16：CAD私有依赖交付审查
隔离模拟GstarCAD安装复现：源包包含额外私有依赖时，安装/自检仍只复制并校验固定3文件，可返回Ready但遗漏额外文件。当前浩辰release恰为3文件，不据此声称现有宿主已加载失败；AutoCAD完整私有依赖未实测。嵌入回退是逐文件替换，不是整包事务；旁置主DLL存在时也不会自动补齐缺失依赖。证据及精确范围见artifacts/installer-safety-20260916-resumed/cad-payload-review/report.md。本轮无生产改动和发布，此安装契约缺口仍待修复。

## CAD私有依赖清单修复已入源码，待统一发布
已补生成清单、依赖安装/自检、哈希保护卸载及发布包清单验证；说明见docs/deployment/cad-payload-manifest.md。专项19+16+4+7项通过，最新全Core314通过/1跳过/0失败；上文旧流水线红灯已被当前测试结果更新，但相关实现属另一任务。本轮无APP构建/发布，release仍163249。移动校验另有DwgReaderService和两条icon内容发生变化，当前不能重复声称57项全通过，未擅自审核这些其他任务修改。证据：artifacts/installer-safety-20260916-resumed/cad-payload-review/。

## 2026-09-16 18:24：统一本地交付已完成

经其他任务冻结确认后，正常Publish-Desktop.ps1 -Clean已通过全部门禁并更新D:/DWGC2E/release：2.1.1+ui.20260916-182234.a3f6f1f。EXE SHA256=7574D0A42B933370549FC1CBFD13DBC0B69513074513B967103B8AE0D94531A4。
Clean/Restore/Build/Publish/包验证通过；安装保护65项、Core314通过/1跳过、布局21项、CAD安装16项、UI受控API冒烟与任务持久化进程中断检查通过。源图标与CAD依赖清单已进入构建；没有修改回写算法或部署网站。
旧release的3个便携文件逐个哈希一致，唯一回滚为artifacts/release-backup-20260916-182234。正常保留策略移除3项旧构建/备份/包，不代表历史未知数据已清理。
迁移核验57项通过，reader读取副本及两处icon映射已记录经审查内容变化；不代表混合工作树全部语义审完。整体目标仍未完成，完整安装事务回滚、历史状态清理和延期真实业务验收仍保留。
证据：artifacts/installer-safety-20260916-resumed/cad-payload-review/{final-publish.log,installed-verification.json,moves-final-reviewed.json}。本段更新上文“源码未发布/三条映射未审”时点状态，历史记录不删除。
## 2026-09-16：启动配置边界局部审查
新增SettingsMigrationSafetyTests六项，连同SettingsStore与资源路径测试20项通过。覆盖首次复制后的迁移、已有备份保护、备份创建失败、读取成功但替换被锁时的原文件保护和临时文件清理、损坏输入与未来schema不降级。生产代码和release未改变，无APP构建。初次两项测试假设纠正及未证明边界见 artifacts/installer-safety-20260916-resumed/startup-config-review/report.md；不扩大为实际启动、跨进程并发或全工作树审完。

## 2026-09-16：账号动作校对Cancel保护已交付
显式登录/退出加校对确认，8项WPF Cancel断言通过。正常发布190101，Core320通过/1跳过，完整门禁通过，release哈希及便携数据核验一致。自动鉴权失效路径和Save跨账号/重启持久化仍有待验证缺口，不扩大为全部编辑保护已完成。范围及证据见 artifacts/installer-safety-20260916-resumed/account-proofreading-review/report.md。


## 2026-09-16：自动失效dirty校对保护交付
190730已通过正常发布门禁并更新release，Core320通过/1跳过；新增UI两场景20断言，验证可写/锁定配置时撤销登录状态且保留校对实体、dirty和账号任务存储。它不等于重启持久化或全部网络请求阻断。生产范围仅Account.cs；TaskManager及回写算法未改。详见 artifacts/installer-safety-20260916-resumed/expired-proofreading-review/report.md。
