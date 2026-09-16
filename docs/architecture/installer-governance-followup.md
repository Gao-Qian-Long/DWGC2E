# 工程治理第二轮：安装链保护

日期：2026-09-16。承接首轮治理；本轮不改桌面业务、后台接口和 UI。

## 发现与修复

原 installer/Install.ps1 会覆盖安装目录中的 settings.json、词库、提示词，并在复制失败后尝试改名/删除旧文件重试。本轮：

- 安装前确认 EXE、配置、默认术语 JSON、提示词、CAD DLL 依赖和平台清单存在且非空，配置可解析。
- 拒绝源/目标目录重叠、路径链接及目标应用正在运行；不关闭用户程序。
- 重装保留已有 settings.json、词库及提示词；添加缺失默认文件，更新程序、CAD 私有依赖及内置 assets。
- 删除“复制失败就改名/删文件再重试”的分支，复制失败直接停止。
- 新增 NoShortcuts，配合 NoLaunch/NoPrompt 在独立目录验收；默认正常安装的快捷方式行为不变。
- 正式发布入口在其他测试之前先执行安装保护测试。

## 本轮文件

| 文件 | 改动 |
|---|---|
| installer/Install.ps1 | 前置检查、用户文件保留、失败即停、隔离验收选项 |
| tests/BuildPipeline/Test-Installer.ps1 | 新增 13 项隔离回归，失败日志及 fixture 保留在 artifacts |
| tools/Publish-Desktop.ps1 | 加入安装回归门禁 |
| README.md | 记录运行方式与保护边界 |
| cleanup-report.md | 追加本轮结果 |

## 验证

1. 首次安装带默认词库：通过。
2. CAD Core 私有依赖：通过。
3. 首次配置播种：通过。
4. 重装保留配置：通过。
5. 重装保留提示词：通过。
6. 重装保留词库：通过。
7. 重装更新 CAD 插件：通过。
8. 重装补充缺失默认文件：通过。
9. 缺失资源的包不修改已有安装：通过。
10. 非法 JSON 配置不修改已有安装：通过。
11. 源目标重叠拒绝：通过。
12. 锁定 EXE 不删除/改名：通过。
13. 不产生旧 EXE 改名 workaround：通过。

真实发布包另复制到独立安装目录，完整复制成功；EXE SHA256 与源一致：46F1CD558F901335F9C8035E0277ACAA879FCA10EBFB4B46611A4FA2FCE45AC4。测试均未创建快捷方式、未启动安装后的程序、未调用卸载脚本，未接触真实用户数据。

旧路径全文扫描有 13 处匹配：均为保留的运行时 glossaries/prompts 路径，或新 assets 源路径的后缀；没有发现完整旧源码路径的失效引用。该扫描不等于穷尽动态加载语义。

## 交付与边界

本轮只改安装/验证脚本，未重新编译 APP，也未替换 release；继续使用首轮已验证 2.1.1+ui.20260916-005804.a3f6f1f。更新安装脚本的本地安装包：

D:/DWGC2E/artifacts/project-governance-followup-20260916-0707/package/DwgTranslator-2.1.1-win-x64.zip

只保存在本地，没有上传或公开发行。真实包安装输出：D:/DWGC2E/artifacts/project-governance-followup-20260916-0707/installed-real-package。

Inno Setup 编译及真实安装、真实 CAD 宿主回写、生产登录/支付仍未验证。此次锁定文件检查是文件锁场景，未额外启动生产进程验证运行中判定。普通复制安装不是全目录事务升级：中途磁盘/权限故障仍可能造成部分资源已更新；本轮取消危险删文件重试，但不承诺完整原子回滚。原卸载脚本未改且未运行，尚需单独审查其按进程名终止行为。

此前被策略拒绝删除的旧 tools/LayoutRegression/bin、obj 未再次尝试；待确认历史文件不删。本輪无源文件删除、无 Git 提交、无后台部署。

## 证据

- baseline-status.txt 和原脚本副本：续做前快照。
- installer-tests.log：13 项回归结果；fixture 子目录包含每次子进程日志。
- package.log / package-verify.log：更新安装包和依赖校验。
- real-install.log / real-install-result.txt：真实发布包在隔离目录安装与哈希。
- old-path-audit.json：旧路径匹配及人工复核依据。
- results.json：结构化结果。

## 2026-09-16 补充：Inno 隔离安装验收已通过

此前“尚未安装/卸载验收”状态由本节更新：正式与隔离分支均编译成功，隔离首次安装、同版本修复、卸载合计 40/40；便携安装链复测 19/19。最终编译日志无 Warning/Error。真实 settings.json/术语库、正式卸载注册项和 release EXE 未变化。仅测试分支安装器被执行，正式分支输出未运行且未签名。跨版本升级、事务失败回滚以及真实 CAD/账号/支付未覆盖；不能据此宣布全部治理完成。未 Build APP、替换 release 或部署后台。

复测方法与完整边界见 docs/architecture/inno-installer-acceptance.md；最终证据位于 D:\DWGC2E\artifacts\inno-acceptance-20260916-082013\final\results.json。
## 2026-09-16 当前交付补充
便携安装/安全卸载行为回归49/49通过，更新后的便携包已在隔离目录完成真实安装、哈希比对与卸载保留配置验收。Inno两个实际构建间升级46/46通过，不宣称跨语义版本迁移或原子失败回滚通过。当前证据及剩余项统一见 governance-current-status.md。本轮没有APP构建、release替换或生产部署。
