# 工程治理第三轮：收尾验收与未完成项

> 此页为历史阶段快照，不代表当前版本或当前测试结果。最新状态见 [当前交付报告](governance-current-delivery.md)，原始要求缺口见 [完成性核对](governance-completion-audit.md)。下文保留当时证据。

本轮日期：2026-09-16（本机时间）。状态：桌面整理和本地发布已交付；不能标记所有外部验收完成。

## 本轮改动

- installer/Install.ps1：修复 Windows PowerShell 5.1 下 SourceDir 默认值解析，安装源在脚本主体取 PSScriptRoot，不再取调用者工作目录。
- 修复缺失 EXE 时 Fail 调用传参错误：原先可能返回退出码 0，现在明确返回 2。真实包跨目录安装暴露此问题，已加入回归。
- 原卸载.cmd 的全局 taskkill 和整目录删除已撤掉。现在仅显示手动安全卸载提示，不执行删除，也不宣称已卸载。保留所有配置/数据/未知文件。这是安全降级，不是完成自动卸载器。
- installer/DwgTranslator.iss：已有便携 settings/prompts/glossaries 不覆盖且卸载保留；去除 startmenu 的 checked 标记，菜单项绑定 startmenu 选择。仅做静态修改，未编译验收。
- installer/使用说明.txt：同步便携手动卸载边界，不再承诺自动删除程序文件。
- tests/BuildPipeline/Test-Installer.ps1：13 项扩展到 19 项，新增安全卸载、Inno 数据保护、默认源目录、失败退出码。
- 本轮没有修改 APP/Core/CAD/Worker 业务代码，没有提交 Git，没有部署网站或后台。

## 验证结果

| 项目 | 结果与限制 |
|---|---|
| 安装回归 | 19/19 通过；隔离目录，无快捷方式，无真实用户数据 |
| Core | 125/125 通过 |
| 图纸布局 | 21/21 通过 |
| CAD 部署模拟 | 9/9 通过；不等于真实宿主加载 |
| UI smoke | 通过；受控接口，含登录、页面、会员购买窗口、设置、文件等现有断言 |
| UI 截图 | 本轮 70 张复制到证据目录 screenshots；WPF 渲染截图，非物理 DPI 验收 |
| Restore/Publish | 正常发布入口通过，编译 App、Core net8/net48、浩辰插件；1 条已有 UI smoke CS8602 警告 |
| Clean/全 Solution Build | 首轮已通过，本轮未再次 Clean/逐工程编译 LicenseGenerator；不混称本轮全 Solution Clean |
| 发布包依赖 | 通过 |
| 真实包隔离安装 | 通过：不传 SourceDir，从项目根运行打包后的脚本，目标为隔离目录 |
| 已安装真实 EXE 启动 | 通过：隔离数据目录，进程保持运行且生成日志；只关闭本轮测试进程 |
| EXE 哈希 | release、最终安装包、隔离安装结果三者一致 |
| 便携用户文件 | settings、prompt、glossary 与替换前备份哈希均一致 |
| 有界清理回归 | 通过；正式发布自动保留当前候选和一个回滚备份 |
| 跨端测试 | 24/24 通过，但其中 H01 是已知缺陷复现测试：不能把绿灯解释为缺陷已修复 |
| 后台扩展测试 | 194 项中 192 通过，2 项 workerd/D1 支付测试 fetch failed；单独重跑仍失败 |

后台的两项失败均在本地 Miniflare/D1 场景，不是生产支付请求。独立最小 D1 诊断未返回，停止了本轮诊断 node 与其子 workerd，没有关闭其他任务进程。尚不能断言原因已定位，更不能以“本地环境问题”直接豁免。

## 当前交付

- 正式 APP：D:/DWGC2E/release/DwgTranslator.exe
- 版本：2.1.1+ui.20260916-072524.a3f6f1f
- SHA256：AD0AE8D676C3E10853FC4862663308AF8F1498A89DC7F33E896D91DB3E8828D3
- 干净候选：D:/DWGC2E/artifacts/publish-20260916-072524
- 回滚备份：D:/DWGC2E/artifacts/release-backup-20260916-072524
- 最终安装包：D:/DWGC2E/artifacts/governance-final-20260916-072248/package-verified/DwgTranslator-2.1.1-win-x64.zip
- 证据：D:/DWGC2E/artifacts/governance-final-20260916-072248

注意：证据目录下早先 package/ 是复现默认安装源问题的中间包，不作为交付；只使用 package-verified/。早先 real-install.log、installed-hashes.txt 属于失败调查；最终成功证据为 real-install-final.log、installed-hashes-final.json、real-launch.json。

正式清理本轮移除了先前 005804 候选/旧回滚/旧 ZIP 共 222,926,508 字节；新的回滚备份包含替换前 release。没有删除未知代码、配置、历史 DLL；原先被策略拒绝的旧 tools/LayoutRegression/bin、obj 未再次处理。

## 未完成清单（明确保留，不伪装通过）

1. **完整安装事务回滚**：拟议的回滚写入命令被执行策略拒绝，未绕过或改工具重试。现有安装仍是逐文件复制，普通失败停止，不能保证中途磁盘/权限故障后整包恢复。需要后续获准的实现及故障注入验收。
2. **自动卸载**：已禁用危险动作，当前改为手动提示。要恢复自动卸载，需实现安装文件清单、路径约束、用户数据排除、同路径进程判断，再做独立测试。
3. **Inno 编译/安装/卸载**：PATH、常见安装位置及已安装应用记录未找到 ISCC；没有编译或运行 Inno 安装器。需要提供编译器，或者明确同意配置相应工具链。
4. **真实 CAD 宿主**：发现 D:/haochenCAD/浩辰CAD 2024/gcad.exe，文件版本 24.1.0.0111。但发现程序不等于授权可用，也不等于加载/回写通过。未修改真实 CAD 自动加载配置、未打开或覆盖用户 DWG。仍需隔离样图与可用宿主完成 NETLOAD、创建任务、写回/再打开验收。
5. **真实账号、会员、设备、更新、云翻译**：目前桌面验证使用受控接口；未使用生产凭据进行完整流程验收。
6. **真实支付**：工作区明确未授权，未发起付款或启用购买；只能使用批准的沙箱或由用户执行真实支付验收。
7. **本地 D1 两项失败**：cf-worker/tests/payment-d1.test.mjs 的回调并发/回滚和 scheduled recovery 测试仍失败；日志在 backend-tests.log 与 payment-d1-recheck.log。未修改同时进行中的后台业务工作。
8. **未知历史文件和架构债务**：仍按首轮 REVIEW REQUIRED 保留。窗口购买编排等不因本次工程治理强行重写；未来功能迭代按测试保护渐进拆分。

## 报告关系

首轮完整文件分类、移动映射、新目录树和删除理由仍见 cleanup-report.md 及首轮 CSV。第二轮报告保留历史事实；本文件覆盖其“当前版本”和未完成项状态。未虚构新的全项目迁移或重复移动清单。
## D1 阻塞解除（2026-09-16T08:06:40.2008142+08:00）

用户重启 v2rayN 后，在没有修改 Worker 业务代码、测试断言、数据库结构或依赖的情况下：
- payment-d1.test.mjs：2/2 通过。
- npm test：194/194 通过，0 失败、0 跳过。
- npm run typecheck：通过。

此前嵌套错误为本机 loopback connect EADDRINUSE；端点占用检查发现 xray 占用大量端点。重启后的复测支持端口资源耗尽诊断；这不是通过跳过测试或放宽断言得到的绿灯。此项已关闭，不需要修改支付业务代码或部署后台。真实支付验收仍未进行。

证据目录：D:\DWGC2E\artifacts\governance-resolution-20260916-080055
文件：payment-d1-after-restart.log、backend-after-restart.log、typecheck-after-restart.log、after-restart-result.json。


# Inno 工具链安装和编译验收

时间：2026-09-16T08:13:47.3596319+08:00

- 用户授权安装 Inno Setup。本轮安装 6.7.3，与现有 6.x 脚本对应；官方同时提供 7.x，本轮不迁移主版本。
- 下载使用显式当前命令代理 http://127.0.0.1:10806，排除 localhost/127.0.0.1/::1；没有修改永久代理、TUN 或 PATH。
- 官方下载页指向 jrsoftware/issrc GitHub release；安装文件 Windows Authenticode 为 Valid，发布者 Pyrsys B.V.。SHA256 见 download-verification.json。
- 当前用户安装目录：C:\Users\GQL\AppData\Local\Programs\Inno Setup 6。安装退出码 0，编译器确认 6.7.3。未购买授权或执行付费操作。
- 初次编译发现缺少 ChineseSimplified.isl；从官方翻译索引指向的 jrsoftware/issrc 仓库获取，文件声明支持 6.5.0+，仅含语言文本段。文件和哈希保存在本证据目录，并补充至工具 Languages 目录。未修改项目安装脚本。
- 随后现有 installer/DwgTranslator.iss 编译通过（退出码 0）。输入是与当前 release/build-info.json 哈希一致的干净 publish 候选；候选版本 2.1.1+ui.20260916-080934.a3f6f1f。本轮没有编译 APP 或替换 release；其他任务已更新的版本没有回退。
- 输出：D:\DWGC2E\artifacts\inno-toolchain-20260916-081055\output\DwgTranslator_Setup_2.1.1.0.exe
- 输出 SHA256：CFD6799E782BAE4F771602E9BC25447538968F307CE855128EA9A629063B8BB1
- 输出安装包签名：NotSigned。工具安装文件签名有效不代表生成的产品安装包已签名。

## 边界

已关闭：缺少 Inno 编译器、现有安装脚本未编译验证。
尚未关闭：产品 Setup.exe 的实际安装/升级/卸载验收。此次没有运行生成的 Setup.exe，没有写产品安装注册项、快捷方式或真实用户数据，没有发布网站下载。
完整安装回滚、安全自动卸载、真实 CAD/账号/支付等其他项目状态不因编译通过而改变。

证据目录：D:\DWGC2E\artifacts\inno-toolchain-20260916-081055

## 2026-09-16 补充：Inno 隔离安装验收已通过

此前“尚未安装/卸载验收”状态由本节更新：正式与隔离分支均编译成功，隔离首次安装、同版本修复、卸载合计 40/40；便携安装链复测 19/19。最终编译日志无 Warning/Error。真实 settings.json/术语库、正式卸载注册项和 release EXE 未变化。仅测试分支安装器被执行，正式分支输出未运行且未签名。跨版本升级、事务失败回滚以及真实 CAD/账号/支付未覆盖；不能据此宣布全部治理完成。未 Build APP、替换 release 或部署后台。

复测方法与完整边界见 docs/architecture/inno-installer-acceptance.md；最终证据位于 D:\DWGC2E\artifacts\inno-acceptance-20260916-082013\final\results.json。
## 2026-09-16 默认安装目录与 CAD 续验
最终默认 D:\DWGC2E，没有 D 盘时 C:\DWGC2E；Inno 原地升级保持旧路径。便携 21/21、Inno 40/40、新包校验通过。CAD 已启动但控制接口仍异常，未完成回写验收。详见 docs/architecture/install-directory-followup.md。

## CAD 受限宿主验收更新（2026-09-16）
用户手动执行插件命令后，PING成功；合成测试文字回写6/6、内容6/6；详细审计可用范围6/6、新增文字重叠0。原边界限制测试仍为0/6，不抹去；与利用周边空间策略存在规则差异。单行最小字高3.5→1.4，视觉可读性待验收。未覆盖真实云翻译、APP全链路、自动加载或复杂生产图纸。证据：D:\DWGC2E\artifacts\install-default-20260916-082921\cad-host-20260916-085409\status.md 及 layout\audit-request.txt.report。

## CAD 视觉验收更新（2026-09-16）
14份PDF成功导出和渲染，DWG哈希未变，7组对照已检查。英文完整可见，但原图3处单行中文显示问号、2处译文字高缩至40%、倾斜多行逐词换行。视觉验收保持待确认，不因几何6/6而全部关闭。完整记录与对照：D:\DWGC2E\artifacts\install-default-20260916-082921\cad-host-20260916-085409\visual\visual-review.md、index.html。本轮未改业务或release。

## 用户决策及运行路径审计（2026-09-16）

用户明确要求保留现有回写算法，后续再考虑调整。因此此前视觉记录保留作为观察证据，排版/字高算法修改从本轮治理阻塞项中移出；这不将原始包围框0/6改记为通过，也不代表已完成复杂生产图纸验收。

本轮仅增加 RuntimeResourcePathTests 五项回归测试，未修改 APP/Core/CAD 业务源码、未构建 APP、未替换 release、未执行真实支付或生产接口请求。

- Core 全套测试：150/150通过，0失败、0跳过。新增覆盖插件优先使用 CadPlugin 子目录、兼容同目录插件、用户提示词优先、内置文件回退、缺失提示词安全兜底。插件夹具是从未加载的文本文件，不冒充宿主加载测试。
- 初次新增测试出现2项路径分隔符断言失败，已修正测试夹具为规范绝对路径；业务代码未修改。最终编译无新增平台警告。
- release 发布文件校验：RELEASE_PACKAGE=OK、CAD_PLUGIN_PACKAGE=OK。不是本轮重新发布，也不是完整启动验收。
- APP 默认数据根仍为 %APPDATA%\DwgTranslator（可由 DWGC2E_DATA_DIR 显式覆盖）；日志、设置、账号任务与缓存使用数据目录。安装目录改为 DWGC2E 不迁移既有数据目录，以免丢失用户状态。
- 术语库：账号目录优先，回退程序 assets/default-glossaries。提示词：用户数据目录优先，程序 prompts 次之，最后内置兜底。没有因源码资源集中而更改已发布相对路径。
- CAD 插件定位首先使用程序 CadPlugin，其次兼容同目录；历史开发输出回退保留，不贸然删除。已发布包依赖验证通过，但不等同于 APP→CAD 调度及自动加载全链路通过。
- 更新检查：设置页面调用版本接口，清单请求不携带 Authorization，失败可回退 Worker 版本接口。当前确认的是手动版本检查路径；未证明自动检查开关触发后台检查，也未实现或验证自动下载替换。此次不新增更新功能，不将静态检查记为联网成功。

证据：D:/DWGC2E/artifacts/install-default-20260916-082921/path-contract-audit/（core-path-final.trx、core-path-final.log、release-verification.log、release-hash.json）。

剩余验收与决策项：APP→CAD全链路和自动加载；真实账号/会员/更新联网验收（不进行真实支付）；跨版本安装及故障恢复；便携自动卸载安全实现；历史未知文件逐项确认。此前执行策略拒绝的安装事务回滚操作不绕过重试。当前不宣布全部工程治理完成。

本轮检测到 release EXE SHA256 为 9CAA33DF63353DFE6E772D9CE490899A67BCBCB9BEB6E694C9E4DFE69F3009A5，与早先080934基线记录不同。本轮未写入release，具体替换来源尚未核实；因此上述包校验仅针对当前磁盘版本，不把早先基线的安装/启动验收自动套用到当前版本，也不进行回滚覆盖。

## CAD集成审计补充（2026-09-16）
隔离安装/修复/卸载回归扩充并按实际检查数量统计：13/13通过，补充保留非托管文件与原始备份检查。真实CAD只读检查发现Support/DwgTranslator中的Cad、Core DLL与当前release不一致，平台和自动加载配置存在，但Ready=false。未向运行中的CAD替换DLL；需保存关闭CAD后更新安装副本并在新会话验收。此前手动NETLOAD成功不等于自动加载副本已更新。本轮没有修改算法、业务代码或release。证据：D:/DWGC2E/artifacts/install-default-20260916-082921/cad-integration-audit/report.md及plugin-hashes.json。

## 已关闭CAD后的插件同步（2026-09-16）
用户确认已关闭后，进程检查未发现gcad/acad。先将真实安装的三件套及acaddoc.lsp备份到cad-integration-audit/installed-plugin-before-sync，再调用现有CadPluginInstaller.Install同步release插件。安装成功、Inspect Ready=true，三件套安装后哈希全部与release一致，acaddoc.lsp文本未变化。未修改算法或任何业务源码，未重建APP、未更换release。

自动加载运行验证仍未通过：启动独立CAD批处理会话，脚本仅DWGTRANSLATORPING及退出，不显式NETLOAD、不打开用户图纸。70秒观察内未产生新ping，进程仍存活且没有可选的CAD窗口；旧08:53:38标记没有被当作新成功证据。未降低安全设置、未强杀进程。需用户可见的正常CAD新会话确认启动状态并执行DWGTRANSLATORPING，不能把Ready=true等同于宿主实际加载成功。启动/观察/正常关闭尝试记录见autoload-launch.json、autoload-result.json、test-process-close.json。

## 未知命令反馈及启动入口修复（2026-09-16）
用户在新会话输入DWGTRANSLATORPING提示未知命令，实际自动加载验收失败，不以文件就绪覆盖此结果。检查本机浩辰Support/gcad2024doc.lsp，内含LoadGcadLsp，明确通过findfile/load执行gcad.lsp；原安装器统一写acaddoc.lsp。最终改为发现gcad.exe时写gcad.lsp，AutoCAD仍用acaddoc.lsp。旧文件不自动删除。首轮gcaddoc.lsp方案已被本机可验证的gcad.lsp入口替代，不作为最终交付。

正式隔离安装测试新增平台入口及旧文件保留：16/16；Core164/164；布局21/21；便携安装21/21；UI_SMOKE=PASS（模拟API）。UI冒烟测试已有CS8602警告，未趁机修改无关UI代码。按正式Publish-Desktop工作流发布并更新D:/DWGC2E/release，最终版本2.1.1+ui.20260916-095905.a3f6f1f，SHA256=52DE397B96AE79B447ACC19B4C7BABA105CBA7F1D48E834633B057D5E5403C23。已校验安装EXE哈希、保留便携数据和有界回滚备份。未修改回写/布局算法。

真实CAD当前打开，未覆盖运行中DLL；仅将此前已校验的本程序自动加载段写入Support/gcad.lsp，保留已有内容和原acaddoc.lsp。未修改安全变量。新会话的自动加载仍待用户重启后执行DWGTRANSLATORPING确认。此前隐藏测试进程27852未强杀，仍需关注；当前用户图纸进程未关闭。

证据：cad-integration-audit/autoload-fix-final-publish.log、startup-entry-fix.json、gcad-after-fix.lsp、post-fix-plugin-comparison.json。全链路回归不宣布完成。

## 最终同步（2026-09-16）
用户确已关闭交互CAD；残留的是本任务启动的隐藏测试进程27852。核对启动时间、完整命令行、脚本路径、无窗口后，仅结束该自有测试进程（此前正常关闭失败），未结束用户图纸进程。再次确认无CAD进程后，调用修正后的安装器同步当前release三件套并更新gcad.lsp。Success=true、Ready=true，三件套SHA256全部一致，证据sync-final-installed.log、final-installed-hashes.json。无重建或release替换。新会话实际自动加载仍待DWGTRANSLATORPING确认；不再启动隐藏CAD测试会话。

## 自动加载宿主验收通过（2026-09-16 10:08，中国标准时间）
用户按要求重新打开浩辰、不手动NETLOAD、执行DWGTRANSLATORPING后反馈“OK了”。本地同时读取到新的成功标记：status=ok|platform=GstarCAD|runtime=v4.0.30319|utc=2026-09-16T02:08:04.4760994Z。标记文件时间晚于最终插件同步日志，已复制至cad-integration-audit/ping-confirmed-final.txt，不复用早先成功标记。

本机浩辰CAD2024的插件自动加载验收关闭为通过。范围仅为本机新会话插件加载和健康命令；不等同于APP→CAD完整翻译调度、真实云翻译或其他CAD版本均通过。回写算法未改动。用户无须继续重复关闭CAD。
