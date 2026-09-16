# DWGC2E 桌面端 UI/UX 重构交付报告

日期：2026-09-15

## 交付范围
- 保留 .NET 8 / WPF / MVVM、翻译核心、API、数据模型、计费规则和显式导出/写回流程。
- 五个页面统一为米白、暖灰、深灰、棕橙工程工具风格：图纸翻译、批量任务、术语库、会员中心、设置。
- 主窗口统一为顶部栏、208 DIP 侧栏、内容区和 30 DIP 状态栏；导航、账号摘要、就绪/任务状态、焦点、禁用、悬停、弹窗、Toast 使用共享主题。
- 任务详情为右侧 420 DIP 抽屉，校对在批量任务同页进入宽幅编辑区；增加保存/取消校对和离页保护。
- 翻译页支持空状态拖入区、队列表格、显式导出、有效输出后打开、折叠日志；批量任务支持搜索、状态/日期筛选、详情、重开、校对与虚拟化；术语库支持筛选、来源/方向/最近命中、冲突与抽屉编辑；会员页保留真实套餐/额度/到期、设备和受控购买；设置保留六个二级区、草稿校验、显式保存和关于入口。
- 订单列表改为表格，空订单单独反馈；二维码有效期、暂停购买、订单恢复及权益同步逻辑未改。

## 设计 Token
统一资源位于 `D:\DWGC2E\src/DwgTranslator.App/Themes/ColorTokens.xaml`、`Metrics.xaml` 和 `MainWindowStyles.xaml`。页面/表面/应用背景、文字层级、棕橙主色、危险色、边框、4–48 DIP 间距、34 DIP 控件、40 DIP 表格行、4 DIP 进度条和 4/6/8 DIP 圆角均集中管理。

## 界面状态代码
仅增加界面层派生状态：任务筛选视图、选中任务/详情抽屉、校对模式和未保存校对缓存、有效输出判断、日志摘要、术语范围筛选、窗口页面宿主尺寸计算。未修改公共业务接口、持久化结构、API 协议、翻译核心或计费规则。

## 验证
- 核心回归：120 通过，0 失败；最终发布日志：`D:\DWGC2E\artifacts\ui-refactor-delivery.log`。
- UI smoke：`D:\DWGC2E\artifacts/ui-refactor-final-smoke2.log`，`UI_SMOKE=PASS`；包含登录/过期、术语保存、任务筛选/详情/校对保存取消、有效输出、605 条队列、虚拟化、会员二维码/暂停/过期/付款恢复等受控接口场景。
- 截图目录：`D:\DWGC2E\artifacts/ui-smoke`，含五页、空/队列/详情/校对/术语编辑/设置关于和四组自动化尺寸矩阵截图。
- 四组尺寸检查采用 WPF 有效 DIP 尺寸：1366×768@100%、1920×1080@100%、1920×1080@125%、2560×1440@150%。当前 smoke 明确输出 `PHYSICAL_DPI_NOT_VALIDATED`；未宣称完成真实 Windows 多显示器物理 DPI 验收。

## 发布门禁
已通过发布门禁并更新唯一运行目录 D:\DWGC2E\release。发布使用现有 Publish-Desktop.ps1，未使用 BuildOnly 或 SkipTests。

- 版本：2.1.1+ui.20260915-190245.a3f6f1f
- 安装程序：D:\DWGC2E\release\DwgTranslator.exe
- SHA-256：3593099E9C2E16548BD163C24F06F1F8DF59AD0F0D1712AD2D416D7E27174430
- 安装后实际哈希与 build-info.json 一致；与上版对比的 3 个便携配置/词库/提示文件哈希一致。
- 包验证：CAD_PLUGIN_PACKAGE=OK、RELEASE_PACKAGE=OK。
- 初次发布使用 Windows PowerShell 5，现有清理脚本因 GetRelativePath 不可用而跳过清理。随后在 PowerShell 7 执行原有清理测试与清理流程，CLEANUP_TESTS=PASS，删除 3 份过期构建/包，现仅保留一个回滚目录 D:\DWGC2E\artifacts\release-backup-20260915-190245。
- 未创建 relaese；未发布官网包、未启用购买、未执行真实支付。


## Token 明细与公共样式

| 类别 | 实现值 |
|---|---|
| 背景 | 应用 #F3F0EA，页面 #F7F5F0，表面 #FCFBF8，次级 #EEEAE3 |
| 文本 | 主要 #1E1E1B，次要 #65615A；辅助及禁用改用 #777168 提高可读性（相对原方案 #969087 的明确调整） |
| 强调 | #B65C22，Hover #9F4E1C，浅强调 #F2E3D8 |
| 边框 | #DDD8CF / #E9E5DE / #C9C2B7 |
| 语义 | 成功 #4E765E，警告 #986E30，危险 #A34E49 |
| 字号 | 页面 24，区域 16，正文 14，表格 13，辅助 12 |
| 尺寸 | 按钮/单行输入 34，默认表格行 40，进度条 4，侧栏 208，状态栏 30，抽屉 420 DIP |
| 间距/圆角 | 4/8/12/16/20/24/32/40/48；控件 4，面板 6，弹窗 8 |

复用 PageTitleStyle、PageSubtitleStyle、SectionTitleStyle、CaptionStyle、EmptyState.*、Button.*、DataGridHeaderStyle/DataGridCellStyle；新增 Section.Surface、Drawer.Surface、Focus.Outline；保留既有菜单、语言选择、授权、导出确认与 Toast 宿主，不引入第二套框架。Hover/Focus/Disabled/Selected 由公共样式管理；Loading/错误原因/成功状态由现有业务状态及轻量派生绑定呈现。

## 未验证与边界

1. 四组指定 Windows 物理 DPI、跨显示器拖动：未完成真实系统级逐项验收。截图矩阵仅证明相应 DIP 布局可创建和渲染，不等同于完整遮挡/点击区域审计。
2. 真实 CAD 多文件解析→在线翻译→取消/失败重试→写回→导出全链路：本次未执行真实环境端到端操作，核心回归与 UI smoke 不能替代该验收。
3. 全量键盘 Tab 顺序、所有弹窗和五页每一种错误/加载组合、手工剪贴板操作及超长译文持续编辑：尚未完成逐项人工验收。
4. 本次交付是五页重构实现及自动化验证版本，不将上述未验证项标为通过。校对保存表示保存到当前任务；只有显式导出才生成输出图纸。
5. 既有后端支付、数据库迁移、购买恢复与发布脚本修改均保留，不归类为本次 UI 新增业务逻辑。

## 本次修改文件分类

### 主题与纯视图
- `D:\DWGC2E\src\DwgTranslator.App\Themes\ColorTokens.xaml`
- `D:\DWGC2E\src\DwgTranslator.App\Themes\Metrics.xaml`
- `D:\DWGC2E\src\DwgTranslator.App\Themes\MainWindowStyles.xaml`
- `D:\DWGC2E\src\DwgTranslator.App\Views\MainWindow.xaml`
- `D:\DWGC2E\src\DwgTranslator.App\Views\Pages\TranslatePage.xaml`
- `D:\DWGC2E\src\DwgTranslator.App\Views\Pages\BatchTasksPage.xaml`
- `D:\DWGC2E\src\DwgTranslator.App\Views\Pages\GlossaryPage.xaml`
- `D:\DWGC2E\src\DwgTranslator.App\Views\Pages\AccountPage.xaml`
- `D:\DWGC2E\src\DwgTranslator.App\Views\Pages\SettingsPage.xaml`
- `D:\DWGC2E\src\DwgTranslator.App\Views\BillingWindow.xaml`

### 少量界面状态/事件调整
- `D:\DWGC2E\src\DwgTranslator.App\Converters\StatusToBrushConverter.cs`
- `D:\DWGC2E\src\DwgTranslator.App\ViewModels\MainViewModel.WorkspaceUi.cs`
- `D:\DWGC2E\src\DwgTranslator.App\ViewModels\DrawingFileItem.cs`
- `D:\DWGC2E\src\DwgTranslator.App\ViewModels\LogEntryViewModel.cs`
- `D:\DWGC2E\src\DwgTranslator.App\ViewModels\MainViewModel.GlossaryEditor.cs`
- `D:\DWGC2E\src\DwgTranslator.App\ViewModels\MainViewModel.Navigation.cs`
- `D:\DWGC2E\src\DwgTranslator.App\ViewModels\MainViewModel.SettingsPage.cs`
- `D:\DWGC2E\src\DwgTranslator.App\ViewModels\MainViewModel.ImportExport.cs`
- `D:\DWGC2E\src\DwgTranslator.App\ViewModels\MainViewModel.Translation.cs`
- `D:\DWGC2E\src\DwgTranslator.App\ViewModels\MainViewModel.Operations.cs`
- `D:\DWGC2E\src\DwgTranslator.App\Views\MainWindow.xaml.cs`
- `D:\DWGC2E\src\DwgTranslator.App\Views\Pages\TranslatePage.xaml.cs`
- `D:\DWGC2E\src\DwgTranslator.App\Views\Pages\BatchTasksPage.xaml.cs`
- `D:\DWGC2E\src\DwgTranslator.App\Views\BillingWindow.xaml.cs`

### 测试与报告
- `D:\DWGC2E\tests\DwgTranslator.App.UiSmoke\Program.cs`
- `D:\DWGC2E\docs\DWGC2E_UI_UX_REDESIGN_20260915.md`

工作区已有的 MainViewModel.Account.cs、AccountPage.xaml.cs、App 项目文件、Core BillingContracts/WorkerApiClient.Billing、支付测试和发布脚本保留；本报告不把它们全部记作本次 UI 改动。


最终复核：安装 SHA-256 匹配，便携文件逐项比较 3 项通过，回滚目录数量为 1；最终发布在 PowerShell 7 运行，清理成功，无跳过测试。
