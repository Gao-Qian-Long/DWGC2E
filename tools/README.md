# 开发工具操作索引

本目录保留现有入口，不再建立重复的 scripts 目录。以下清单按当前源码说明用途，**不是已执行所有工具的证明**。路径相对仓库根目录；从根目录操作。正式自动测试在 `tests/`，手动截图和在线探针不能替代业务验收。

当前日常操作及网盘上传流程见 desktop-release-management.md。正式发布已包含 Setup 构建、隔离安装验收和 current-release.json 更新。

## 日常交付与内部辅助

| 文件 | 输入、输出和操作边界 |
|---|---|
| `Publish-Desktop.ps1` | 正式入口；构建并验证候选后更新 `release`，保留便携数据和一份回滚。正常交付不传 `-BuildOnly`；失败不得替换安装版。可能还原构建依赖，需要网络。 |
| `New-DesktopInstaller.ps1` | 从干净候选编译生产/隔离 Setup；完成新装/升级/修复/卸载验收后仅把生产 Setup 写入 public，不上传。 |
| `Get-DesktopPayload.ps1` | 严格验证候选运行文件白名单与全部文件哈希。 |
| `Get-DesktopSourceSnapshot.ps1` | 固定构建输入哈希，阻止构建过程中修改输入后仍替换 release。 |
| `Copy-DesktopUserData.ps1` | 按旧版 CAD 清单区分程序文件，保护所有未知便携文件，冲突拒绝。 |
| `New-ReleasePackage.ps1` | 输入明确的 `-PublishDir artifacts/publish-<timestamp>`；向候选加入安装引导及文档，生成目录/ZIP。建议明确 `-OutputDir`；不是只读校验，不打包个人运行目录。 |
| `New-DesktopReleaseManifest.ps1` | `-PublishDir -PackagePath -OutputPath`；核对后写本地发行清单，不上传、不授权公开发布。 |
| `Assert-CleanPackageInput.ps1` | 打包内部输入保护，拒绝把已安装 release 与个人数据作为干净候选打包。 |
| `Enter-DesktopPublishLock.ps1` | 发布互斥辅助，返回文件锁；调用方必须在 finally 中释放。存在锁文件不表示进程仍在运行。 |
| `Verify-ReleasePackage.ps1` | 检查候选资源、配置、插件；包含插件包校验，不运行 APP。 |
| `Verify-CadPluginPackage.ps1` | 检查插件、Core 及清单中的依赖/相对路径，不在 CAD 内加载 DLL。 |
| `Verify-ExecutableIcon.ps1` | 读取 EXE 图标资源并与标准 ICO 比较，不启动 EXE。 |
| `Refresh-DesktopShellIcon.ps1` | 通知 Windows Shell 刷新图标；不是图标生成器，不杀 Explorer、不清图标缓存。 |

根目录 `start.bat` 只启动当前 release；`publish.bat` 转发正式发布入口；`dev.bat` 仅在发布成功后启动 release。本地交付不等于网站下载更新、开放购买或真实支付验收。

## 盘点、维护与兼容入口

| 文件 | 输入、输出和操作边界 |
|---|---|
| `Get-ProjectInventory.ps1` | `-OutputDir` 必须为新目录；写 CSV 和范围说明。只提取显式构建声明，不求值 MSBuild、不删除源文件。 |
| `Verify-ProjectMoves.ps1` | 可选 `-ManifestPath -ResultPath`；对迁移记录与 Git 基线核对，输出证据，不移动文件。 |
| `Clean-DesktopArtifacts.ps1` | **有删除行为**；先预览 `-WhatIf` 并确认作用范围及保留规则。不得将其他任务产物、用户数据、恢复备份当垃圾。 |
| `Reset-FirstReleaseArtifacts.ps1` | **历史一次性清理，保留审计用途，不再运行**；固定首发基线与完成记录保护，不是日常清理入口。 |
| `Test-DesktopArtifactCleanup.ps1` | 保留的兼容入口，转发 `tests/BuildPipeline/Test-DesktopArtifactCleanup.ps1`；正式测试源码不在 tools 中重复维护。 |
| `Sync-BrandIcons.ps1` | **修改两个工作区的品牌资源**：本仓库 SVG/ICO，以及默认 `D:/DWGC2E_Website` 的 logo.svg/favicon.svg；写生成证据。仅在明确品牌变更时使用，不负责构建或部署。 |

## 人工截图工具

下列工具需要可交互桌面，会切换/置前窗口，运行前应停止操作同一界面。历史控件名称可能不覆盖当前 UI，生成 PNG 不代表页面验收通过。自动化环境的截图接口不可用时，不以这些旧脚本绕过当前工具限制。

| 文件 | 输入、输出和操作边界 |
|---|---|
| `Capture-AppWindow.ps1` | `-ProcessName -OutDir -Tag`；默认程序 DwgTranslator，输出 `%TEMP%/uicap`，会置前/恢复窗口。 |
| `Capture-AllPages.ps1` | 可选择页面和 `-NoLaunch`；默认启动 release，再切换页面调用截图，输出默认临时截图目录。不是无交互测试。 |
| `Capture-Dialogs.ps1` | 可 `-NoLaunch`；通过界面打开/关闭对话框并截图，会影响当前窗口状态。 |
| `Capture-DesktopReview.ps1` | 必填 `-Name`，建议明确 `-OutputDirectory artifacts/<task>`；可选窗口句柄。默认历史审核目录不适合新任务。 |

## 跨端与在线诊断

| 文件 | 输入、输出和操作边界 |
|---|---|
| `Test-CrossEndIntegration.mjs` | 默认仅检查本地 APP/Worker/官网配置（官网默认 `D:/DWGC2E_Website`）；显式 `--live` 才访问网络。可 `--website-root --output`；`--api-only` 要求 `--live`。线上检查为匿名读取/预检，不创建订单。 |
| `Test-CfApi.ps1` | 包装上述工具的 `--api-only --live`，**默认访问线上服务**。先确认 BaseUrl/WebsiteOrigin；不是离线测试。 |
| `Test-WorkerContract.ps1` | **线上匿名接口探针**，检查成功/未认证/方法拒绝响应，不验证登录账户或支付。 |
| `Test-CloudflareSetup.ps1` | **访问线上 Worker 并调用 npx wrangler secret list**；枚举秘密名称而非值，需要账户权限，npx 可能获取工具。不是自动登录、创建配置或部署入口。 |

本轮不运行线上诊断；不得把匿名可达性写成真实会员、翻译或付款通过，也不要把凭据放进报告。

## 子目录与保留理由

- `LicenseGenerator/`：当前 Program.cs 仅向错误输出提示离线签发已退休并返回 1，**不是可用的授权码生成器**。保留拒绝型兼容占位入口；不因它已退休就删除 Core/UI 内旧授权兼容代码。历史审查文档描述历史版本，不能当成现状；也不在本索引复制历史秘密内容。
- `LayoutRegression/`：目前仅有历史 bin/obj；正式源码已在 `tests/DwgTranslator.LayoutRegression/`。未在本轮删除生成目录，避免混淆旧证据与当前测试入口。

新增工具应在文件顶部标明用途、输入、输出及调用方式；修改本清单时同步检查实际根文件列表，不凭 test/debug 名称判断可删除。