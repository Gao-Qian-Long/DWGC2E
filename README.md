# QLCAD（图纸翻译工作台）

面向机械工程 CAD 图纸的 Windows 桌面工具，可提取 DWG/DXF 文字、调用云端 AI 翻译，再把译文写入新图纸。写回有两种方式：本机直接写文件（无需 CAD）和由受支持 CAD 宿主插件处理。“离线”只指本机文件写回，不代表可以离线调用 AI 翻译。

## 仓库目录地图

日常只需要记住第一行那三个入口，其余是"按需查看"的实现细节。

| 目录 | 是什么 | 你多久会碰一次 |
|---|---|---|
| `src/` | 桌面程序源码，三个工程：`DwgTranslator.App`（WPF 界面）、`DwgTranslator.Core`（内核：API/翻译/任务/账户/更新）、`DwgTranslator.Cad`（装进 CAD 的写回插件） | 改功能时 |
| `installer/` | 安装与卸载：`QLCAD.iss`（Inno Setup 编译成 Setup.exe）、`Install.ps1`/`Uninstall.ps1`/`InstallTransaction.ps1`（免管理员安装、按清单精确卸载、失败回滚）、`安装.cmd`/`使用说明.txt`（给用户的入口与说明）。**对编译 APP 本身没有作用** | 动安装行为/安装目录/快捷方式时 |
| `tools/` | 交付链与运维脚本（发布、打包、安装器编译、包校验、品牌图标生成、截图取证）。分类索引见 [`tools/README.md`](./tools/README.md) | 发版时（`build-release.bat` 走的就是这里） |
| `tests/` | 测试与探针：哪些会在发版时拦你、哪些是手工跑，见 [`tests/README.md`](./tests/README.md) | 改完代码验证时 |
| `assets/` | 打包进程序的资源：`icons/`（品牌 SVG 与 ICO）、`glossaries/`（默认术语库）、`prompts/`（已退役提示词，仅作为"安装器要删除的历史路径"存在） | 换图标/改默认术语时 |
| `cf-worker/` | **另一个技术栈**：Cloudflare Worker + D1（登录、会员额度、翻译代理、支付、管理后台）。有自己的 `package.json`/`wrangler.toml`/`migrations`/`tests`，与桌面应用的构建互不影响 | 改后端时 |
| `release/` | **本机可运行版本**（`QLCAD.exe`），由 `build-release.bat` 产出并替换。位置被约定钉死，不要挪 | 只双击启动 |
| `artifacts/` | 本地产物：构建候选、安装包、交付记录、回滚备份、历次任务证据。**不入仓库**（见 `.gitignore`），保留策略见 AGENTS.md | 排障时看 |
| `upload/` | 上传网盘用的临时目录。**不入仓库** | 上传时 |

根目录那几个"散"文件其实各有约定位置：`DwgTranslator.sln`（工程入口）、`Directory.Build.props`（CAD SDK 定位，决定插件编译成 net48 还是 net8.0）、`settings.json.example`（配置模板，会被打进包，**包内那份必须与它逐字节一致**；本地 `settings.json` 不参与打包）、`build-release.bat`（构建并更新 release）、`build-setup.bat`（再投递安装包到 upload\）、`README.md`（本文）、`AGENTS.md`（协作与交付规则，不入仓库）。

## 用户安装与维护入口

用户只需最新 Setup.exe；安装器自动释放必需文件，不需要脚本或开发工具。维护者运行 build-setup.bat 后，从 artifacts/current-release.json 的 publicDirectory 取得网盘文件，不能直接上传个人 release 目录。

本机启动直接运行 release\QLCAD.exe；要更新它先跑 build-release.bat。详细管理规则见 [桌面发布手册](tools/desktop-release-management.md)。

## 本机已安装版本运行

1. 打开 [`release`](./release/) 目录。
2. 双击 `QLCAD.exe`。
3. 首次运行后联网登录账户；AI 翻译由 Cloudflare Worker 管理，不需要在 APP 中填写 DeepSeek API Key。桌面 APP 是 Windows x64 自包含发布，目标电脑无需另装 .NET Runtime。
4. 可一次拖入多张 DWG/DXF：左侧按文件显示，双击图纸进入译文列表并直接编辑。
5. 勾选需要输出的图纸后点击“导出”，只选择一次输出文件夹即可批量生成 `*_translated.dwg/.dxf`，无需再次选择源文件。

> Windows 可能对从网络下载的程序显示安全提示；请确认文件来源为本仓库后再运行。

## 主要功能

- 提取 DBText、MText、尺寸、引线、块属性和表格文字
- 中文与英文互译，支持工程术语表
- 保留常用 MText 格式代码
- 自适应文字缩放与碰撞检测
- 支持不依赖 CAD 的本机 DWG/DXF 写回（ACadSharp）
- 支持浩辰 GstarCAD 2024 或 AutoCAD 2025+ 的宿主专用 `NETLOAD` 插件写回；写回在本机 CAD 进程中完成，不是云端修改图纸
- AI 翻译始终走云端服务；本机写回不等于离线 AI 翻译。手工编辑或已有译文可以不再请求翻译服务，直接本机写回
- 翻译缓存、日志查看与失败重试
- 界面按窗口宽度自适应：窗口可拖窄到 1024 DIP（更窄的工作区按屏幕收敛）；窄档时两栏上下堆叠、表格把次要列并进一格、指标带折行、工具栏换行，内容不会被裁掉、按钮不会被压掉；宽档（≥1366）排布保持不变。断点是单一事实来源（`Themes/Metrics.xaml` 的 `Size.Breakpoint*`），行为在 `Views/Controls/ResponsiveLayout.cs` 与 `ResponsiveTable.cs`

## 运行目录

```text
release/
├─ QLCAD.exe          # Windows 可运行程序
├─ settings.json              # 默认配置模板（不包含私人 API Key）
├─ assets/default-glossaries/ # 随包默认术语资源
├─ glossaries/                # 默认术语表兼容副本
└─ CadPlugin/                 # 当前发布包对应宿主的 NETLOAD 插件及平台清单
```

客户端提示词已退役：Release 构建不再编译 `Application/Translation/TranslationPrompt.cs`，`tools/Verify-ReleasePackage.ps1` 会拒绝任何 `prompts/` 目录，安装器还会删除遗留的 `{app}\prompts\deepl_context.txt`。不要把 `assets/prompts/` 重新塞回发布产物。

运行时的个人配置、日志、术语表和导出文件保存在 `%APPDATA%\DwgTranslator\`。私人 API Key 和运行日志不会提交到仓库。

## CAD 插件与依赖

`DwgTranslator.Cad.dll` 是随 APP 一起打包的 CAD 扩展程序集，配有 `DwgTranslator.Core.dll`、运行依赖和 `cad-platform.txt`。它调用宿主 CAD 的托管图形 API 完成写回；不是另一个独立软件，也不需要单独下载。当前交付支持浩辰 GstarCAD 2024（宿主使用 .NET Framework 4.8）或 AutoCAD 2025+（宿主使用 .NET 8）。插件包一次针对一个 CAD 平台构建，`cad-platform.txt` 必须与宿主一致；AutoCAD LT 不支持此插件。

普通用户运行发布版需要 Windows x64 和可联网的账号（仅使用 AI 翻译时需要网络）。主程序自包含，不要求另外安装 .NET Runtime。没有 CAD 也可以用“本机直接写回”；如果使用 CAD 插件写回，则必须安装受支持的 CAD 版本及匹配插件。碰撞避让取决于文字类型和图纸结构，块属性、复杂嵌套块和特殊字体需在 CAD 中复核。

1. 在 APP「更多 → 环境自检与安装」查看检测结果；插件随 APP 内置，可从该页面安装或修复（修复前需关闭 CAD）。
2. 核对 `CadPlugin/cad-platform.txt` 与 CAD 宿主一致；AutoCAD 与 GstarCAD 的插件 DLL 不通用。
3. 如需手动加载，在对应 CAD 中运行 `NETLOAD`，选择 `release\CadPlugin\DwgTranslator.Cad.dll`。
4. 通过 APP 选择“CAD 宿主插件写回”，或在 CAD 中运行 `DwgTranslateWrite` 命令。

## 从源码构建

源码构建环境：Windows、.NET 8 SDK；构建 CAD 插件还需要对应宿主的托管 SDK。当前支持 GstarCAD 2024（插件目标 .NET Framework 4.8）和 AutoCAD 2025+（插件目标 .NET 8）。发布包在构建机上确定 CAD 平台并将其写入 `cad-platform.txt`。

```powershell
.\tools\Publish-Desktop.ps1
```

发布 Windows x64 单文件版本：

```powershell
.\build-release.bat
```

交付约定：每次 APP 发布构建通过后，必须更新本地 `release/`，它是唯一的日常启动入口；不要仅交付 `artifacts/` 中的候选目录，也不要另建 `relaese/`。正常开发构建和交付统一使用 `tools/Publish-Desktop.ps1`（不加 `-BuildOnly`），测试失败或应用正在运行时不覆盖旧版。设置与词库保留，旧构建按保留策略自动清理。仅当用户明确要求隔离构建时才使用 `-BuildOnly`。本地更新不等于公开发行或支付验收通过。

如果 AutoCAD SDK 不在默认位置，请设置 `AUTO_CAD_DIR`，或创建本地且不提交的 `Directory.Build.props.user`。

## 源码结构

```text
src/DwgTranslator.Core/   核心模型、服务与翻译流程
src/DwgTranslator.App/    WPF 桌面界面
src/DwgTranslator.Cad/    宿主专用 CAD 插件
tools/LicenseGenerator/   已退休离线授权的拒绝型兼容入口
tests/                    自动化单元、UI、布局、CAD 部署与构建流程回归
```

## 注意事项

- 图纸对象冲突时，程序会记录冲突对象；请检查导出结果和应用内日志。
- 建议保留原始图纸，不要直接覆盖唯一副本。
- `UI布局意见.md`、`功能开发.md`、`建议.md`、`BUG反馈.md`、`CLAUDE.md` 等开发记录仅保留在本地，不随发布程序上传。



## 工程导航与验证

工具用途、输入输出和副作用见 [开发工具操作索引](tools/README.md)；在线诊断、截图、历史清理与正常发布入口已分别说明。

`assets` 是集中源资源；`tests` 是正式测试；`tools` 统一保留构建/发布/包校验和诊断入口，避免再增加一套同义 scripts；`installer` 是安装源码；`artifacts` 是可忽略的产物及验证证据。资源复制保持既有安装相对路径。


tools/Test-CfApi.ps1、Test-CloudflareSetup.ps1、Test-WorkerContract.ps1 是需要外部环境的诊断探针，不是自动执行的离线单元测试；使用前确认测试环境，不能把真实支付当冒烟测试。


默认配置只编辑 `settings.json.example`；个人配置由程序写入应用数据目录，不提交 token、API key、真实订单或用户词库。`Directory.Build.props.user` 可覆盖本机 CAD SDK 目录，已加入条件导入且忽略入库。默认优先检测 AutoCAD，否则浩辰；可显式设置 `AUTO_CAD_DIR` 或 `GSTAR_CAD_DIR`。


本地构建依赖 .NET 8 SDK，不仅是 Runtime；如果系统 dotnet 显示无 SDK，而 SDK 安装在用户目录，可仅在当前终端设置：


```powershell
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
dotnet --list-sdks

# 完整清理交付：同一互斥锁内运行 Clean / Restore / Build / 全部门禁 / Publish，更新 release。
.\tools\Publish-Desktop.ps1 -Clean

# 日常增量交付：仍执行全部回归门禁，成功后更新 release。
.\tools\Publish-Desktop.ps1

# 单独编译 CAD（需要本机安装的匹配 SDK）
dotnet build src/DwgTranslator.Cad -c Release -p:CadPlatform=GstarCAD

# 从明确的干净发布候选生成安装包，不以 release 用户目录为输入
.\tools\New-ReleasePackage.ps1 -PublishDir "artifacts/publish-<时间戳>" -OutputDir "artifacts/installer-<时间戳>"
# 装有 Inno Setup 6 时：
ISCC /DPublishDir="D:\DWGC2E\artifacts\publish-<时间戳>" installer/QLCAD.iss
```

UI 冒烟使用隔离数据和受控接口，不等同于真实登录、付款或宿主 CAD 图纸验收。LicenseGenerator 当前仅提示离线签发已退休并返回失败，保留兼容入口，不生成授权码。

架构与依赖规则见各 csproj 与 Directory.Build.props。逐文件整理及验证：历史清单。

### 安装链回归

`powershell -NoProfile -File tests/BuildPipeline/Test-Installer.ps1` 在独立 artifacts 目录验证首次安装、重装保留用户文件、不完整包、坏配置和锁定文件保护；不启动程序、不创建快捷方式、不操作真实用户数据。正式发布入口会先运行该检查。

一键安装支持 `-NoShortcuts`（默认仍创建快捷方式）。隔离验收使用 `-NoLaunch -NoPrompt -NoShortcuts -TargetDir <独立目录>`。重装保留已有 settings.json、词库与提示词，更新程序、CAD 私有依赖和内置 assets。复制失败立即停止，不再尝试删除旧文件。它不是全目录事务式升级器；磁盘故障等仍可能需要从备份恢复。

便携安装包的 `卸载.cmd` 调用 `Uninstall.ps1`，输入 `UNINSTALL` 后仅移除安装清单登记且哈希未改变的程序文件。个人配置、词库、提示词、导出、日志、未知文件及已修改资源保留；不会终止进程或递归清空安装目录。可用 `-Preview` 预览。快捷方式仅在已登记、哈希未改变、指向本安装且没有自定义参数时清理。

2026-09-16恢复验证结果：便携安装/卸载65项、快捷方式创建函数10项、StartMenu真实卸载进程30项、运行实例/根路径联接18项、最新Inno安装/修复/卸载及路径保护45项。各组范围不同，不相加当作业务验收。Desktop本机实际卸载已有8场景27项验证，但不覆盖全部重定向环境；便携安装中途故障事务回滚仍未完成；既有跨构建升级也不等于跨语义版本迁移。可重复命令和范围见 安装测试说明，最新证据见 `artifacts/installer-safety-20260916-resumed/report.md`。

### 首次安装目录（2026-09-16）

Inno 与便携安装脚本默认选择 D:\QLCAD；D:\ 不可用时选择 C:\QLCAD。Inno 可手动选择目录，升级保持原路径；便携脚本以显式 -TargetDir 为准，不自动发现或迁移旧安装。默认目录无写入权限时应选择其他目录，不绕过权限。运行数据位置不因安装默认值而改变。

### 本轮治理交接




- 只读重新盘点：`powershell -NoProfile -File tools/Get-ProjectInventory.ps1 -OutputDir "artifacts/<本任务>/inventory-<新时间戳>"`。输出文件分类与显式工程依赖，不据名称自动删除文件。
- 未经逐项审查不要执行 `git add .`：当前工作区同时包含桌面UI、后端与本次整理变更。移动文件在未暂存时可能显示旧路径删除、新路径未跟踪，不能只提交其中一半。
### 核对工程搬迁清单（不构建）

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Verify-ProjectMoves.ps1 -ResultPath artifacts/governance-review/moves.json
```

结果路径必须为尚不存在的artifacts文件。检查以 `tools/project-moves.json` 的固定Git基线为准，核对资源去重、文件去向及3项已单独审查的内容变动；不修改源码、Git暂存区或release。不替代业务测试，后续有意修改已登记文件时须重新审查而非直接刷新哈希。
配置提交前可运行 `powershell -NoProfile -ExecutionPolicy Bypass -File tests/BuildPipeline/Test-SourceConfiguration.ps1`：核对默认配置没有非空凭据、发布输入不读取本地settings、Git忽略与Worker秘密边界。该检查不读取本地秘密、不构建、不访问网络。
### 发布并发保护

`tools/Publish-Desktop.ps1` 从第一项验证开始直到发布/清理结束持有工作区独占锁。第二个发布进程会在构建前直接拒绝，不等待并覆盖另一任务的结果。锁文件为 `artifacts/.desktop-publish.lock`；文件存在本身不代表仍被占用，以操作系统打开的文件句柄为准，正常结束或进程退出后可以再次获取。不要删除锁文件来强行解锁，也不要在发布时另行执行绕过入口的构建、清理或源码修改。

`tools/Enter-DesktopPublishLock.ps1` 是发布入口的配套脚本，需一并保留。可执行 `powershell -NoProfile -ExecutionPolicy Bypass -File tests/BuildPipeline/Test-DesktopPublishLock.ps1`，在隔离目录中验证真实进程互斥、无构建副作用、失败释放和残留标记重用；不会重建APP。此保护不冻结源码、不锁住任意手工 dotnet 命令，也不承诺父进程异常退出后仍存活的构建子进程已自动结束。

### 安装包输入约束

`tools/New-ReleasePackage.ps1` 只接受本工作区 `artifacts/publish-yyyyMMdd-HHmmss` 干净候选，不接受 `release`。脚本核对构建版本、EXE哈希、默认配置/资源，并拒绝额外个人资源；模板变更后请重新通过正常发布流程生成候选。已有安装包与暂存目录不会被覆盖，请使用 artifacts 下的新输出目录。相对路径按调用者当前 PowerShell 目录解析；源码、release 和工作区根目录不能作为安装包输出位置。打包与发布共享工作区锁；包内包含 `build-info.json`，但这不代替签名或线上发布授权。


### 正式交付不得跳过回归门禁

`Publish-Desktop.ps1 -SkipTests`（含与 `-Clean` 同用）会在构建前拒绝，不能跳过回归测试后替换release。正常交付不传该参数。`-BuildOnly`仅在用户明确要求不更新release的隔离诊断时使用；即使诊断跳过测试，也仍受发布互斥锁保护。本规则不授权以隔离产物代替最新安装版本。

### 截图辅助工具安全规则

`tools/Capture-AllPages.ps1`、`tools/Capture-Dialogs.ps1` 不再强制结束正在运行的 APP。检测到已有实例时默认拒绝启动；只有显式 `-NoLaunch` 才复用窗口。复用仍会切换页面/对话框，须先保存工作。用途、参数与风险见 截图工具说明。隔离回归入口：`tests/BuildPipeline/Test-CaptureLaunchSafety.ps1`，不操作真实用户窗口。

### 构建与诊断工具导航

工具目录与副作用说明覆盖tools根目录的26个脚本，区分本地交付、离线验证、联网诊断、交互截图及一次性历史清理。维护者应先确认目标环境，不要把线上探针当离线测试运行。

工程结构回归：`powershell -NoProfile -File tests/BuildPipeline/Test-ProjectStructure.ps1`。仅评估Core双目标及App的源文件、依赖边界和资源映射，不构建APP，不改release；详细范围见架构文档。

CAD私有依赖交付契约、清单与隔离回归见 cad-payload-manifest。2026-09-16已通过统一完整构建并交付本地release（182234）；真实CAD/业务验收仍待用户后续进行，

### 编译资源与动态依赖审计

动态依赖复核记录CAD宿主入口、内嵌插件、WPF资源及运行路径。独立入口 `tests/ArchitectureAudit/ArchitectureAudit.csproj` 读取既有编译产物与release，不构建APP；正式发布在替换release前对新候选包强制执行同一审计；配套 `tests/BuildPipeline/Test-ArchitectureAudit.ps1` 包含8个用例，验证正常样本、候选选择和5种预期拒绝结果。运行方法、证据和未覆盖范围见文档。

### 忽略规则检查

运行 `powershell -NoProfile -File tests/BuildPipeline/Test-GitIgnore.ps1`，只读核对正式入口/资源可见和本地配置/构建产物被忽略。27个固定路径检查不等于全量秘密扫描，也不会自动暂存文件。新增大型DWG/XLSX夹具需单独检查后缀规则和入库必要性。
