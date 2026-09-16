# 最终交付记录（2026-09-16）

本记录是当前交付摘要。旧 review/status 文档保留过程与失败记录；与本记录冲突的旧版本号、旧发布阻断，以本记录列出的实物和证据为准。它不是对所有真实场景测试通过的声明。

## 1. 本轮范围与责任

- 用户明确：下载链接后续由管理后台动态维护，本任务不再上传或修改公开下载地址。
- 用户明确：真实测试由用户后续自行进行，不再要求用户现在提供账号、密码或生产测试环境。
- 本轮只完成两项工程收尾：图标发布检查接入、交付记录整理。没有改动翻译、计费、CAD写回或排版核心逻辑，没有重新构建APP、替换release、部署网站/Worker。
- “以后在管理后台动态维护下载链接”是用户接手的后续事项，不表示本任务已经实现了该管理后台功能。

## 2. 当前本地交付

| 项目 | 当前结果 |
|---|---|
| 本地可运行APP | `D:\DWGC2E\release\DwgTranslator.exe` |
| APP版本 | `2.1.1+ui.20260916-192900.a3f6f1f` |
| APP SHA256 | `2FB090C0015978DED8A2C43136C811B25DDE6AFBB1959AE76DDF9B588F68CB59` |
| 便携包 | `D:\DWGC2E\artifacts\DwgTranslator-win-x64-GstarCAD-20260916-192900.zip` |
| 统一图标安装器 | `D:\DWGC2E\artifacts\icon-cache-20260916\installer\DwgTranslator_Setup_2.1.1.0.exe` |
| 安装器 SHA256 | `95ABD57C74B3C0263FDBC23DE3856725E1DEF69710A3F3BCC7FAA9F867BA4E8E` |

安装器文件名采用EXE数字文件版本2.1.1.0；所含APP对应上列192900构建，不以文件名推断构建时间。来源证据为 `D:\DWGC2E\artifacts\icon-cache-20260916\installer-source.json`。

## 3. 图标发布检查已接入

统一资源是 `D:\DWGC2E\assets\icons\icon.ico`，以APP界面橙棕色D图标为准。

正常运行 `D:\DWGC2E\tools\Publish-Desktop.ps1` 时：

1. 构建前运行 `Test-InstallerBrand.ps1`，检查APP、安装器、卸载列表和卸载快捷方式的图标配置；失败中止。
2. 构建后、压缩打包和替换release之前运行 `Verify-ExecutableIcon.ps1`，把候选EXE中的9种尺寸图标与统一ICO逐帧逐字节比较；失败中止。仅作为资源读取，不执行候选APP。
3. 在同一候选阶段运行 `Test-DesktopShellIconRefresh.ps1 -ExecutablePath <候选EXE>`，检查错误路径拒绝、重复通知及EXE不被改写；不依赖旧release存在，因此首次安装也可运行。
4. 安装成功后保留原有定向Shell图标刷新。刷新失败警告，不回滚已验证可用的APP，也不强杀资源管理器或删除全局缓存。
5. `Test-InnoInstaller.ps1` 每次编译出的正式/隔离安装器也调用同一个EXE资源校验器，图标不一致即停止验收。

正常交付仍禁止跳过原安装保护/Core/布局/CAD/UI门禁。网站品牌检查仍由 `D:\DWGC2E\tests\BuildPipeline\Test-BrandIcons.cjs` 独立负责，不把另一仓库与浏览器依赖强加给桌面首次发布。

### 本轮实际验证

- 当前已安装APP及安装器：9帧图标一致，校验通过。
- 品牌配置检查：通过。
- Shell刷新测试：对候选EXE通过，3个无效路径拒绝、重复调用、文件哈希不变。
- 新增 `Test-BrandPublishGate.ps1`：有效图标通过，故意不一致ICO及无效EXE拒绝；发布步骤顺序、失败退出检查、发布/安装器脚本语法通过；release哈希不变。
- 门禁证据：`D:\DWGC2E\artifacts\icon-cache-20260916\publish-gate\results.json`。
- 安装/修复安装/卸载既有隔离验收45项通过：`D:\DWGC2E\artifacts\icon-cache-20260916\install-acceptance\results.json`。它不是跨版本升级或生产用户安装验收。
- 本轮未重新执行完整APP构建发布；以上为实际检查器运行与接线结构检查，不冒充一次新的完整发布。

## 4. 网页与云端交付边界

- 网页logo/favicon已在此前最小范围部署中统一。该次62文件与线上部署核对证据：`D:\DWGC2E\artifacts\brand-unification-20260916\production\production-verification.json`；不是本轮重新部署。
- APP默认及旧产品默认API地址已改为自定义域名；不强制修改用户另行配置的其他自定义地址。
- 网页术语云端保存、冲突保护和历史只读合并等既有实施记录见 `CROSS_END_REVIEW_PLAN_20260916.md` 第8节及其后续更新。真实账号往返由用户验收，不以匿名检查替代。
- 2026-09-16 19:35只读探测：自定义API健康、匿名账号/设备/订单/术语/历史401、CORS通过；网页代理健康及私有响应no-store通过。
- 当次发行检查仍显示：官网1.0.0、云端2.1.0且下载地址为空，与本地2.1.1不同。这是明确交给用户后续管理的下载/版本配置状态，没有伪装成已对齐或擅自上线。
- 证据：`D:\DWGC2E\artifacts\icon-cache-20260916\cross-end-release-audit.json` 与 `website-proxy-release-audit.json`。检查中现有购买开关为true，本任务未开启、关闭、下单或付款。

## 5. 用户后续验收，不作为本轮工程待办

- 真实APP登录、云端术语上传/下载、网页历史与账号隔离的生产往返。
- 用户实际CAD图纸、复杂实体、执行中断、磁盘满等环境及边界场景。
- Windows当前资源管理器窗口是否已重绘旧缓存图标；Shell资源检查通过不等于直接观察了用户窗口。
- 实际邮件、支付和公开下载发行均不在本轮执行范围。

结论：本轮两项工程收尾完成。保留以上边界，不宣称整个产品所有真实环境都已验收，也不把用户接手的测试/下载再次列为代理阻塞。
