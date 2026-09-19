# 开发工具目录（2026-09-16）

当前桌面操作以 desktop-release-management.md 为准；以下为工具索引。

本目录保留一套 tools 入口，不再创建含义重复的 scripts。下表按功能列出主要脚本；子目录中的独立工程、测试夹具不按文件名当垃圾清除。所有命令从仓库根目录执行，实际参数以脚本 param 为准。

## 本地交付与安装包

| 脚本 | 用途、输入与输出 | 风险 / 边界 |
|---|---|---|
| Publish-Desktop.ps1 | 默认全门禁后构建、生成并安装验证Setup、发布候选/内部ZIP并更新release/current-release.json；Clean增加Solution清理还原构建 | 会替换本地APP，保留数据及回滚；不授权线上发行；日常不得SkipTests |
| New-DesktopInstaller.ps1 | 干净PublishDir + 新OutputDir；编译生产/隔离安装器并验收，输出public与私有receipt | 不上传；隔离包永不分发 |
| Get-DesktopPayload.ps1 | 严格运行文件白名单及哈希 | 拒绝额外私有文件或未审查依赖 |
| Get-DesktopSourceSnapshot.ps1 | 构建前后输入哈希快照 | 不导出私密内容；输入变动阻断替换 |
| Copy-DesktopUserData.ps1 | 旧release到受控release-next | 保留所有未知文件；冲突拒绝 |
| Enter-DesktopPublishLock.ps1 | 内部互斥锁，输入WorkspaceRoot，返回FileStream | 调用者finally释放；文件存在不等于锁被占用 |
| Assert-CleanPackageInput.ps1 | 验证直接位于artifacts的时间戳候选并返回元数据 | 拒绝把用户release目录当分发输入；不是数字签名 |
| Verify-ReleasePackage.ps1 | 输入PublishDir，检查配置、资源、CAD依赖 | 本地检查，不启动APP |
| Verify-ExecutableIcon.ps1 | 输入ExecutablePath、可选IconPath；读取PE资源比对每个标准ICO帧，不执行候选 | 正式交付在打包前强制运行；只读检查 |
| Refresh-DesktopShellIcon.ps1 | 输入已替换release的ExecutablePath；通知Shell刷新图标缓存 | 交付后置步骤；结束Explorer、全局清缓存、改注册表都不做 |
| Sync-BrandIcons.ps1 | 可选WebsiteRoot、EvidenceDirectory；从APP徽标生成网站SVG与多分辨率ICO | 不在交付链中；默认写入工作区外的网站仓库目录，需显式指定路径后再运行 |
| Verify-CadPluginPackage.ps1 | 输入PluginDir，检查插件与私有依赖 | 不加载CAD，不证明宿主兼容性 |
| New-ReleasePackage.ps1 | 输入PublishDir，可指定InstallerDir/OutputDir/SkipZip；写安装目录及ZIP | 选择新的OutputDir，保留已有输出；不执行安装 |
| New-DesktopReleaseManifest.ps1 | 输入PublishDir、PackagePath、OutputPath；写本地校验清单 | 不上传，不启用公开更新或强制更新 |
| Clean-DesktopArtifacts.ps1 | 有界清理已识别旧候选与包，保留当前及回滚、独有用户数据 | 有删除操作；先用-WhatIf预览，不作为通用历史垃圾删除器 |
| Reset-FirstReleaseArtifacts.ps1 | 首发基线的一次性历史迁移 | 仅作历史审计保留，不重跑，不移除完成/版本保护 |

根目录publish.bat仅转发正式交付；start.bat只启动release；本地dev.bat先交付成功再启动release。没有第二套发布逻辑。

## 离线盘点与回归入口

| 脚本 | 用途、输入与输出 |
|---|---|
| Get-ProjectInventory.ps1 | 输入新的OutputDir，输出文件元数据/分类/显式引用；不删除源码，不证明反射依赖 |
| Verify-ProjectMoves.ps1 | 依据搬迁清单及Git基线验证，输出ResultPath；不修改Git索引 |
| Test-DesktopArtifactCleanup.ps1 | 兼容入口，转发tests/BuildPipeline的正式隔离测试；不另维护测试实现 |

正式回归源代码归tests；带Test名称的在线诊断不因名称被当作自动化离线测试。

## 需要外部环境的诊断

| 脚本 | 运行环境和影响 |
|---|---|
| Test-CfApi.ps1 | 调用Node跨端探针并强制live/api-only；会联网，默认目标是已部署API；应显式传入获准的测试地址 |
| Test-WorkerContract.ps1 | 公共及未登录接口HTTP检查，会联网；不测试真实登录、额度和付款 |
| Test-CloudflareSetup.ps1 | 公共HTTP检查及npx wrangler secret list；需要网络和Cloudflare账号权限，只请求Secret名称，不读取值，不部署 |
| Test-CrossEndIntegration.mjs | 默认检查本地跨端配置；--live才加网络请求；--output写报告。--api-only必须配合--live。警告不是发行验收 |

这些探针不列入无网络保证的单元测试。不要因为命名为Test便对生产执行；本次编写目录说明没有执行它们。

## 交互式截图

Capture-AllPages.ps1、Capture-Dialogs.ps1、Capture-AppWindow.ps1、Capture-DesktopReview.ps1共4项，详见[截图工具说明](capture-tools.md)。前两项会导航或开关对话框，不是只读诊断。默认发现APP已运行就拒绝启动；不会强制结束用户进程。

## 维护规则

- 新脚本顶部记录用途、输入、输出、调用方式和联网/删除/安装副作用。
- 临时探针进入具体artifacts任务目录；有复用价值的工具才进入tools。
- 生产交付只用Publish-Desktop；不跳过门禁后把候选当最新release。
- 不在清理脚本中加入模糊名称的递归删除，不清除未知资源、运行数据和其他任务证据。

## 盘点工具路径回归

`Get-ProjectInventory.ps1 -OutputDir` 的相对路径现在按PowerShell当前位置解析，而非宿主进程目录。正式隔离测试：`powershell -NoProfile -File tests/BuildPipeline/Test-InventoryPaths.ps1 -EvidenceDir artifacts/<new-task-directory>`。仅使用合成仓库，覆盖相对/绝对/方括号路径及已有报告保护；不会将合成统计当成实际项目盘点。
