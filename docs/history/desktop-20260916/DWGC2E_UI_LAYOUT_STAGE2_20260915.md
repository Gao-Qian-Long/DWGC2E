# DWGC2E 第二阶段布局重构交付记录

日期：2026-09-15

## 范围与解释
本轮按附件已提供的内容整理布局，不增加业务功能。附件结束于“背景使用很淡的灰”，后续内容未提供。附件同时要求保留当前视觉方向和蓝色主色；以“不重新设计整个视觉风格”为本轮原则，保留上一版米白/暖灰/棕橙 Token，没有改为蓝色。

## 公共布局
- 新增 PageHeader：五页共享 76 DIP 标题区、22 DIP 半粗标题、13 DIP 说明、右侧页面操作和底部分割线；页面边距24。
- Workspace.Toolbar / Workspace.InfoStrip 复用既有主题；信息条40高，仅绑定已有真实套餐/额度。
- Section.Surface 改为透明分隔区域，避免白色小容器反复嵌套。
- 表格单元格真正应用12/4内边距和垂直居中；保留选中与键盘焦点。
- 修复单行输入框内容宿主重复内边距造成的文字裁切。
- 侧栏宽208，图标18，图标间距12；账号入口移至底部，仅展示账号与套餐，完整信息仍在会员中心。

## 五页变化
1. 图纸翻译：空队列主区域为较大拖入区，导入后隐藏并由队列表格占据；队列操作菜单移到表格标题旁；开始翻译在右侧，显式导出和输出目录为次级操作；日志默认收起，展开仍140高且保留复制/清空。
2. 批量任务：共享标题与筛选工具栏，既有任务详情抽屉、同页校对、保存/取消和返回状态继续保留。
3. 术语库：添加术语移至页面标题右侧；搜索/范围/导入导出在次级工具栏；保留冲突、云同步、编辑草稿、持久化词库。
4. 会员中心：标题固定，内容局部滚动；登录、真实套餐额度、订单和设备区域扁平化；未改支付行为。
5. 设置：共享标题，二级导航与内容间距24，分区标题16，表单标签统一16/8间距；显式保存、取消、校验与未保存保护未变。

## 文件清单（本轮增量；不代表清除此前未提交改动）
- D:\DWGC2E\src\DwgTranslator.App\Views\Controls\PageHeader.cs
- D:\DWGC2E\src\DwgTranslator.App\Themes\MainWindowStyles.xaml
- D:\DWGC2E\src\DwgTranslator.App\Themes\Metrics.xaml
- D:\DWGC2E\src\DwgTranslator.App\Views\MainWindow.xaml
- D:\DWGC2E\src\DwgTranslator.App\Views\Pages\TranslatePage.xaml
- D:\DWGC2E\src\DwgTranslator.App\Views\Pages\BatchTasksPage.xaml
- D:\DWGC2E\src\DwgTranslator.App\Views\Pages\GlossaryPage.xaml
- D:\DWGC2E\src\DwgTranslator.App\Views\Pages\AccountPage.xaml
- D:\DWGC2E\src\DwgTranslator.App\Views\Pages\SettingsPage.xaml
- D:\DWGC2E\tests\DwgTranslator.App.UiSmoke\Program.cs
- 本报告

纯UI修改为XAML样式、模板、布局；唯一新增产品C#是共享标题控件的描述依赖属性。未改ViewModel业务状态、API、数据库、计费、翻译核心或发布脚本。测试代码增加布局断言，不引入产品持久化状态。

## 验证结果
- 核心回归120/120通过；UI_SMOKE=PASS；CAD_PLUGIN_PACKAGE=OK；RELEASE_PACKAGE=OK。
- 新增验证：五页各只有一个可见76高标题、24页边距、空队列拖入区、导入后隐藏、紧凑窗口队列高度>=240、日志展开140高。
- 沿用校对保存/放弃、任务筛选/详情/返回、605任务虚拟化、术语与设置草稿、输出有效性、受控登录与支付状态测试。
- 自动化布局：1366×768@100%、1920×1080@100%、1920×1080@125%、2560×1440@150%对应的有效DIP尺寸，每种五页截图和标题断言通过。
- 上述为WPF布局与96DPI离屏截图，不是真实Windows物理DPI验收。实际系统DPI、全部键盘操作、真实CAD翻译/写回完整链路未验证。
- 人工查看了队列、会员和术语库截图；修复后确认输入文字完整、表格内边距生效。截图含受控会话测试产生的短时Toast。
- 日志：D:\DWGC2E\artifacts\ui-layout-stage2-delivery.log
- 截图：D:\DWGC2E\artifacts\ui-smoke

## 上一次安装记录（阻碍已于下文续交付中解除）
本轮较早的布局版本已通过完整发布流程并安装至唯一运行目录：
- D:\DWGC2E\release\DwgTranslator.exe
- 版本：2.1.1+ui.20260915-195320.a3f6f1f
- 实测SHA256：64E993A17FB2FA100D63AB2DAD0DF239EAC8F37EF4614833E0F0431E33CAD4BF，与build-info一致。
- 回滚目录：D:\DWGC2E\artifacts\release-backup-20260915-195320

最终输入框/表格修正后，再次执行不跳过测试的Publish-Desktop.ps1，回归和打包通过，但安装阶段文件被占用。确认release程序PID3444，于19:56:46启动。没有关闭用户程序，release版本及哈希保持上述较早版本。

最终候选 D:\DWGC2E\artifacts\publish-20260915-195615 **尚未安装，不能称为最新运行版**。发布失败留下release-next暂存目录；未在应用运行时清理或交换目录。关闭APP后应重新执行完整发布门禁，安装核验后使用既有清理流程清理临时产物并保留有限回滚。

没有发布官网下载包、启用购买或执行真实付款。


## 2026-09-15 续交付：最终修正版已安装

用户关闭APP后，重新执行完整 Publish-Desktop.ps1，未使用 BuildOnly 或 SkipTests。核心120/120、UI smoke、CAD依赖和安装包检查全部通过。

- 唯一运行文件：D:\DWGC2E\release\DwgTranslator.exe
- 安装版本：2.1.1+ui.20260915-200139.a3f6f1f
- SHA256：14AC4CCD6675FB6EA00656196D931FA2F21D75A30B6681CF91DF8DB857E3D1D2
- 已独立核对安装后exe哈希与build-info一致。
- 安装前记录的3个便携配置/词库/提示文件，安装后逐项哈希一致。
- 既有清理流程删除5项旧产物，只保留一份回滚：D:\DWGC2E\artifacts\release-backup-20260915-200139。
- 上次失败遗留的 D:\DWGC2E\artifacts\release-next-20260915-195615 不属于既有清理流程识别范围；额外清理请求被执行策略拒绝，暂存目录保留，不影响release运行，不将其视为安装版。
- 完整续交付日志：D:\DWGC2E\artifacts\ui-layout-stage2-delivery-resumed.log

此前“最终候选尚未安装”的状态已经解除；当前release包含输入框裁切与表格内边距最终修正。真实Windows DPI及真实CAD全链路仍未验证；未发布官网、启用购买或执行真实付款。
