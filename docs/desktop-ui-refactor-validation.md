# 桌面 UI 重构：实现与验收记录

## 已实现
- 主窗口五页导航；全局登录入口、内嵌账号表单、内嵌设置和术语编辑。
- 设置五个分类、保存/放弃/离页保护、就地错误与环境检查。
- 会话验证状态、云端操作登录门禁、登录返回而不自动启动任务。
- 配置版本迁移、旧配置备份、按字段原子保存，避免覆盖新会话。
- 术语草稿、搜索/冲突筛选、导入导出及显式保存；语言切换加载保护。
- 任务参数配置快照接口；发布暂存、包校验、备份与失败回滚。

## 本轮实际结果（2026-09-14，本机时区）
- 核心测试：77/77 通过。
- WPF UI smoke：通过。使用隔离数据目录和可控 API，不使用真实凭据或翻译额度。
- 覆盖：五页导航、旧配置迁移、离线令牌不可信、配置异常、登录失败/成功、登录返回但不翻译、过期会话、设置保存/放弃/写入失败、术语保存和数量、确认窗口关闭语义。
- 暂存发布包依赖校验通过。正式打包 EXE 可启动，实际无障碍树显示五页导航、登录账号按钮及构建版本。
- 暂存构建：2.1.0+ui.20260914-005356.235a0db（包含当时未提交的工作树改动）。
- 暂存 EXE SHA256：3A69C1FF8E955821B943ABC86030708E4C1CA833268DF989C9E5D310B0E39B78。

## 尚未通过，不得宣称全部交付
- 旧 release 进程 PID 22400 没有主窗口但仍占用文件；未强制终止。
- 当前 release 仍是先前存在启动资源错误的构建。修正构建位于暂存目录，尚未替换最终路径。
- 实际桌面截图接口返回不支持接口，点击接口返回缺少坐标几何。仅取得真实窗口控件树，不能代替视觉与鼠标验收。
- artifacts/ui-smoke 下为运行中 WPF 控件渲染图，不是物理桌面截图。窗口受当前桌面工作区限制，尺寸矩阵文件名不证明相应分辨率/DPI已通过；需在实际 1366×768、1920×1080 和各缩放下重新检查。
- 真实账户登录、每个设置的重启及实际行为、完整 CAD 工作流、导入冲突和所有键盘交互仍需人工回归。

## 复现与发布
在仓库根目录执行：

~~~powershell
dotnet test tests/DwgTranslator.Core.Tests/DwgTranslator.Core.Tests.csproj -c Release
dotnet run --project tests/DwgTranslator.App.UiSmoke/DwgTranslator.App.UiSmoke.csproj -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Publish-Desktop.ps1 -BuildOnly
.\publish.bat
.\dev.bat
~~~

BuildOnly 不替换 release。普通发布先检测目标进程、测试、独立构建、依赖验证，再更新 release；保留备份。dev.bat 只在发布成功后启动准确的 release 路径。

测试可通过 DWGC2E_DATA_DIR 指定隔离目录；普通使用不设置此环境变量。不要将测试数据目录用于真实客户数据。

远程只跟踪源码、测试、脚本与文档；release、artifacts、日志、本地设置和敏感信息不提交。停止跟踪 release 不会删除本地运行文件。
