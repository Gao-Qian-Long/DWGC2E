# 桌面发版与文件管理（2026-09-17）

## 一、用户到底下载什么

**只分发最新验证交付的 public 目录，主文件是 Setup.exe。**

正常执行根目录 publish.bat（或 tools/Publish-Desktop.ps1，不加 BuildOnly）。成功后读取 artifacts/current-release.json：

- publicDirectory：网盘上传目录，只含 Setup.exe、开始使用.txt、SHA256SUMS.txt。
- installerPath / installerSha256：本次真实安装器及 SHA256。
- installedExecutable / appSha256：本机 release 的程序及 SHA256。
- candidate、acceptanceReport、sourceSnapshot、rollbackDirectory：内部追溯与回滚资料，不上传。

不要按文件名猜“最新版”，不要重新上传历史同名安装器，不要把 release 打包上传（里面可能有个人配置）。artifacts 中的 ZIP 仅为内部便携候选，不是主下载。不要上传源码、测试截图、数据库备份、.git、账户配置或私钥。

安装器把必要资源释放到安装目录。主 APP 是自包含 win-x64，不要求终端用户安装 .NET 8、开发工具、运行 bat 或填写模型服务商 Key。账号登录/网络/额度、已安装的匹配 CAD 仍是业务前提。当前包的平台以 cad-platform.txt 和发行清单为准，GstarCAD 不能标成 AutoCAD 通用。

## 二、日常只有三个入口

| 入口 | 谁用 | 效果 |
|---|---|---|
| start.bat | 本机维护人员 | 只启动已验证 release，不构建、不下载依赖 |
| publish.bat | 发版人员 | 完整回归、构建、安装验收后更新 release，并打印上传路径 |
| dev.bat | 需要改完即运行的开发者 | 兼容原流程：调用正式发布，成功才启动；不是用户安装入口 |

保留这三个小入口有意义；不再另建含义重复的启动脚本。installer/安装.cmd 和 Install.ps1 属于受测的内部便携兼容通道，不在 Setup 主下载中提供；不是普通用户必须执行的步骤。

构建机需要 .NET 8 SDK、对应 CAD SDK、Inno Setup 6 及中文语言文件；这些是维护者的工具，不是用户安装条件。发布脚本会从 PATH 和用户目录寻找 SDK，避免系统只有运行时导致误报。

## 三、文件去留规则

| 范围 | 管理决策 |
|---|---|
| src、测试、项目文件、tools、installer | 保留源码和回归能力，不进入用户包 |
| assets/prompts/deepl_context.txt | 这是实际翻译服务所用运行时提示词，不是 AI 开发聊天垃圾；保留并封装到 Setup |
| assets/glossaries、assets/icons | 保留规范资源；安装器释放运行所需资源，图标编入 EXE |
| AGENTS.md / 开发代理规则 | 仅工作区规则，gitignore；不进入用户包，不因“AI”字样盲删 |
| docs/history/desktop-20260916 | 旧 UI 方案、旧交付记录、旧治理清单已归档；只作追溯，不当当前操作手册 |
| docs/architecture、docs/deployment | 保留现行架构、操作及安全边界；历史报告中的“当前”只指原日期 |
| cf-worker | 产品后端源码，不能因为桌面安装器不需要就删除；单独维护、单独授权部署 |
| bin、obj、node_modules | 可再生成的开发输出；不进安装器。首发期间不靠全删依赖目录节省小量空间 |
| artifacts | 构建/测试/盘点证据。只按已验证所有权清理，不按文件名含 test/backup 盲删 |
| release | 唯一本机可运行最新版。保护便携设置、词库、提示词及未知用户文件 |
| %APPDATA%/DwgTranslator、数据恢复备份 | 个人数据，不是构建垃圾；不得随源码清理或上传 |
| 根目录空 settings.json、字面量 %SystemDrive% 缓存目录 | 核对为空配置/错误路径缓存后可清除；不得清除真正系统缓存或 AppData |

初始全量盘点为 18,279 文件的路径/体积/规则分类，不代表逐字审读了所有内容；详见本地 artifacts/release-readiness-plan-20260916。二进制、依赖、测试输出不应伪称已逐行代码审查。

## 四、发布门禁与失败处理

1. 取得互斥锁、确认 release 未在用；有活动翻译/导出/未保存提示时不强制关闭。
2. 固定构建输入快照；运行安装保护、事务、启动入口、Core、任务中断、布局、CAD 部署与 UI smoke。
3. 构建干净自包含 candidate，验证图标/插件依赖/资源哈希。PDB 移到私有 symbols，不进 Setup。
4. 严格运行文件白名单拒绝额外私有配置、日志、未知 DLL。仅使用干净 candidate 编译生产 Setup。
5. 用隔离安装器验证新装、真实前后构建升级、修复、卸载、所有文件哈希及个人数据保留；隔离包不能上传。
6. 再查输入未变、release 未重新运行，复制个人文件；冲突拒绝，不悄悄覆盖。
7. 替换 release，验证已安装 EXE 哈希，原子更新 current-release.json。失败恢复旧目录；不把失败产物冒充最新版。
8. 清理已识别旧候选/包，保留一个上一版回滚；完成的旧 delivery 仅在所有文件与所有权清单一致时删除。

如果 APP 在运行，先正常退出。已授权正常关闭，但这不授权强杀、丢弃未保存作业。脚本默认拒绝运行中的 release，避免静默结束任务。

失败的 delivery、含未知或修改文件的旧目录、数据库恢复备份，清理器会保留供单独审查，并非应无限积累；每次处理后记录例外。

预览清理：powershell -NoProfile -File tools/Clean-DesktopArtifacts.ps1 -WhatIf。确认输出后不带 WhatIf 执行。它不是全盘垃圾清理器。

## 五、回滚与后续版本

current-release.json 的 rollbackDirectory 是上一已安装版本。发现回归先停止发网盘，正常退出 APP，保留现有问题版本、日志和个人数据，再人工恢复该目录。不要直接覆盖或删除数据库，尤其不要让旧程序覆盖新版数据库结构。

后续功能只改源码与规范资源，再走同一个 publish 入口。新增运行文件必须同步更新白名单、安装规则和测试；不要把整个工作区递归塞进 EXE。

## 六、上线前仍需人工确认的边界

本机隔离安装通过不等于全新 Windows VM、所有 CAD 版本和完整云端翻译链路认证。外发前建议在干净 Windows x64 + 目标 CAD 上做真实图纸导入、登录、翻译、导出和重启验证。未签名安装器可能被 Windows 提示，不能承诺无安全弹窗。签名后需重新记录安装器哈希并复验。

本流程只完成本地交付及获准的 Git pro 推送，不授权部署 Worker、修改官网购买开关、上传公开下载或真实支付测试。
